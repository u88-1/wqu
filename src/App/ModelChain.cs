namespace GeminiMenu;

/// <summary>
/// Which failures justify moving to the next model of the SAME provider (never to another provider).
/// </summary>
public static class ModelFallback
{
    static readonly string[] KeyWords = { "api key", "api_key", "apikey", "leaked", "invalid key", "incorrect api key" };

    /// <summary>
    /// 403 usually means "this key may not use this model". It is a key problem only when the text says so.
    /// </summary>
    public static bool IsModelDenied(AppException e)
    {
        if (e.Kind != ErrKind.BadKey || e.Status != 403) return false;
        var text = ((e.Technical ?? "") + " " + e.Message).ToLowerInvariant();
        return !KeyWords.Any(text.Contains);
    }

    /// <summary>Rate limit, overload, timeout/network, deleted or unknown model, quota, or a model this key may not use.</summary>
    public static bool ShouldSwitch(AppException e) =>
        e.Kind is ErrKind.Overloaded or ErrKind.Quota or ErrKind.NotFound or ErrKind.Network || IsModelDenied(e);

    /// <summary>A bad or missing key fails the whole task at once, with a clear message.</summary>
    public static bool IsFatal(AppException e) =>
        e.Kind == ErrKind.NoKey || (e.Kind == ErrKind.BadKey && !IsModelDenied(e));

    /// <summary>"a, b\nc" to ["a","b","c"]. Empty items and duplicates are dropped.</summary>
    public static List<string> ParseList(string? text) =>
        (text ?? "").Split(new[] { ',', ';', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(x => x.Length > 0).Distinct(StringComparer.Ordinal).ToList();
}

/// <summary>
/// Circuit breaker, one per task: after <see cref="Threshold"/> failures in a row a model is marked unavailable,
/// so the following chunks do not waste time on it. Thread safe (parallel chunks).
/// A model that went down because of a TEMPORARY problem (rate limit, overload, network) is given a single probe
/// call once <see cref="RetryAfter"/> has passed; if it works it is back, if not it waits another period.
/// A model that is missing or not allowed for this key stays down for the rest of the task.
/// </summary>
public sealed class ModelHealth
{
    public static readonly TimeSpan DefaultRetryAfter = TimeSpan.FromSeconds(60);

    readonly object _gate = new();
    readonly Func<DateTime> _clock;
    readonly Dictionary<string, int> _streak = new(StringComparer.Ordinal);
    readonly Dictionary<string, string> _lastError = new(StringComparer.Ordinal);
    readonly Dictionary<string, DateTime> _downSince = new(StringComparer.Ordinal);
    readonly HashSet<string> _permanent = new(StringComparer.Ordinal);

    public int Threshold { get; }

    /// <summary>Pause before a temporarily failing model gets one more chance. <see cref="Timeout.InfiniteTimeSpan"/> = never.</summary>
    public TimeSpan RetryAfter { get; }

    public ModelHealth(int threshold = 3, TimeSpan? retryAfter = null, Func<DateTime>? clock = null)
    {
        Threshold = Math.Max(1, threshold);
        RetryAfter = retryAfter ?? DefaultRetryAfter;
        _clock = clock ?? (() => DateTime.UtcNow);
    }

    /// <summary>
    /// True while the model should be skipped. When the pause of a temporarily failing model is over this returns
    /// false ONCE (the probe) and the pause starts again, so parallel chunks do not all hit the model at the same time.
    /// </summary>
    public bool IsDown(string model)
    {
        lock (_gate)
        {
            if (!_downSince.TryGetValue(model, out var since)) return false;
            if (_permanent.Contains(model) || RetryAfter == Timeout.InfiniteTimeSpan) return true;
            var now = _clock();
            if (now - since < RetryAfter) return true;
            _downSince[model] = now;
            return false;
        }
    }

    public void Success(string model)
    {
        lock (_gate)
        {
            _streak[model] = 0;
            _downSince.Remove(model);
            _permanent.Remove(model);
        }
    }

    /// <param name="temporary">False for a model that is missing or not allowed: it will not be probed again.</param>
    /// <returns>True when this failure is the one that marked the model unavailable.</returns>
    public bool Failure(string model, string error, bool temporary = true)
    {
        lock (_gate)
        {
            _streak[model] = _streak.GetValueOrDefault(model) + 1;
            _lastError[model] = error;
            if (!temporary && _streak[model] >= Threshold) _permanent.Add(model);
            if (_streak[model] < Threshold) return false;
            var isNew = !_downSince.ContainsKey(model);
            _downSince[model] = _clock(); // a failed probe starts a new pause
            return isNew;
        }
    }

    public string? LastError(string model) { lock (_gate) return _lastError.GetValueOrDefault(model); }

    public List<string> DownModels() { lock (_gate) return _downSince.Keys.OrderBy(x => x, StringComparer.Ordinal).ToList(); }
}

/// <param name="Attempts">How many HTTP attempts the transport may make for this model.</param>
/// <param name="QuickSwitch">Another model is available: do not sit through long waits on rate limits.</param>
public sealed record ModelCall(string Model, int Attempts, bool QuickSwitch);

/// <summary>
/// Tries the models of one provider in order: (the transport already retries with a delay) then the next model.
/// Splitting a chunk in halves, the step after this one, stays in <c>Jobs</c>.
/// </summary>
public sealed class ModelChain
{
    readonly ModelHealth _health;
    readonly Action<string> _status;
    readonly Action<string> _log;
    readonly string _provider;
    // Gemini errors carry no Provider: the error page has dedicated texts for them.
    string? ErrProvider => _provider == "Gemini" ? null : _provider;

    public ModelChain(ModelHealth health, string provider, Action<string>? status = null, Action<string>? log = null)
    {
        _health = health;
        _provider = provider;
        _status = status ?? (_ => { });
        _log = log ?? (_ => { });
    }

    /// <param name="models">In priority order. "auto" and duplicates are ignored.</param>
    /// <param name="moreModels">Loaded lazily, only when the listed models are used up (for example model discovery).</param>
    /// <param name="maxModels">Upper bound on models actually attempted in one call.</param>
    public async Task<GenResult> RunAsync(IEnumerable<string> models, Func<CancellationToken, Task<IEnumerable<string>>>? moreModels,
        Func<ModelCall, CancellationToken, Task<GenResult>> call, int maxModels, CancellationToken ct)
    {
        var list = new List<string>();
        void Add(string? m)
        {
            if (!string.IsNullOrWhiteSpace(m) && m != "auto" && !list.Contains(m)) list.Add(m);
        }
        foreach (var m in models) Add(m);

        bool moreLoaded = moreModels == null;
        int idx = 0, attempted = 0;
        bool skippedDown = false;
        var errors = new List<string>();
        AppException? last = null;

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if (idx >= list.Count)
            {
                if (moreLoaded || attempted >= maxModels) break;
                moreLoaded = true;
                foreach (var m in await moreModels!(ct).ConfigureAwait(false)) Add(m);
                if (idx >= list.Count) break;
            }

            var model = list[idx++];
            if (_health.IsDown(model)) { skippedDown = true; continue; }
            if (attempted >= maxModels) break;
            attempted++;

            bool hasAlternative = idx < list.Count || !moreLoaded;
            if (attempted > 1)
            {
                _status($"עובר לדגם חלופי של {_provider}: {model}");
                _log($"{_provider}: switching to {model}");
            }

            try
            {
                var res = await call(new ModelCall(model, hasAlternative ? 3 : 5, hasAlternative), ct).ConfigureAwait(false);
                _health.Success(model);
                return res with { Model = model };
            }
            catch (AppException e) when (ModelFallback.ShouldSwitch(e))
            {
                last = e;
                var detail = e.Technical ?? e.Message;
                errors.Add($"[{model}] {detail}");
                if (_health.Failure(model, detail, temporary: e.Kind is not (ErrKind.NotFound or ErrKind.BadKey)))
                    _log($"{_provider}: {model} marked unavailable after {_health.Threshold} failures in a row");
            }
        }

        if (last == null)
        {
            if (skippedDown)
                throw new AppException(ErrKind.Overloaded, $"כל הדגמים של {_provider} סומנו כלא זמינים להמשך המשימה.",
                    technical: string.Join("\n", _health.DownModels().Select(m => $"[{m}] {_health.LastError(m)}"))) { Provider = ErrProvider };
            throw new AppException(ErrKind.Other, $"לא נמצא דגם זמין של {_provider}.") { Provider = ErrProvider };
        }
        throw new AppException(last.Kind, last.Message, last.Status, string.Join("\n", errors)) { Provider = last.Provider };
    }
}

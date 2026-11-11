using System.Net;
using System.Text;
using System.Text.Json.Nodes;

namespace GeminiMenu;

public sealed record GenResult(string Text, string? FinishReason);
public sealed record UploadedFile(string Name, string Uri, string MimeType);

public sealed class GeminiClient : IModelClient, IDisposable
{
    const string Base = "https://generativelanguage.googleapis.com";

    readonly HttpClient _http;
    readonly bool _ownsHttp;
    readonly AppConfig _cfg;
    readonly Action<string> _status;

    /// <summary>Overridable so tests do not really wait.</summary>
    public Func<TimeSpan, CancellationToken, Task> Delay { get; set; } = (t, ct) => Task.Delay(t, ct);

    public GeminiClient(AppConfig cfg, Action<string>? status = null, HttpClient? http = null)
    {
        _cfg = cfg;
        _status = status ?? (_ => { });
        _ownsHttp = http == null;
        _http = http ?? new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
    }

    public void Dispose()
    {
        if (_ownsHttp) _http.Dispose();
    }

    // Per-request timeouts (the HttpClient itself has none, so one hung request cannot block for 30 minutes).
    static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(2);
    static readonly TimeSpan UploadTimeout = TimeSpan.FromMinutes(10);
    static readonly TimeSpan GenerateTimeout = TimeSpan.FromMinutes(10);

    sealed record Reply(int Status, string Body, Dictionary<string, string> Headers);

    // ---------------------------------------------------------------- errors

    static string ExtractMessage(string body)
    {
        try
        {
            var m = (string?)JsonNode.Parse(body)?["error"]?["message"];
            if (!string.IsNullOrWhiteSpace(m)) return m!;
        }
        catch { /* not json */ }
        return body.Length > 400 ? body[..400] : body;
    }

    static AppException FromHttp(int status, string body)
    {
        var msg = ExtractMessage(body);
        var lower = (msg + " " + body).ToLowerInvariant();
        bool keyProblem = lower.Contains("api key") || lower.Contains("api_key") || lower.Contains("permission_denied");
        ErrKind kind = status switch
        {
            400 when keyProblem => ErrKind.BadKey,
            401 or 403 => ErrKind.BadKey,
            400 => ErrKind.BadRequest,
            404 => ErrKind.NotFound,
            429 => ErrKind.Quota,
            500 or 502 or 503 or 504 => ErrKind.Overloaded,
            _ => ErrKind.Other,
        };
        var tech = $"HTTP {status}: {msg}";
        // Daily quota will not recover by waiting a few seconds.
        if (body.Contains("PerDay", StringComparison.OrdinalIgnoreCase) || lower.Contains("per day")) tech += " [PerDay]";
        return new AppException(kind, msg, status, tech);
    }

    static bool Retryable(AppException e)
    {
        // "limit: 0" means this model has no free-tier quota at all: waiting will not help.
        if (e.Kind == ErrKind.Quota && (e.Technical ?? "").Contains("limit: 0", StringComparison.OrdinalIgnoreCase)) return false;
        if (e.Kind == ErrKind.Quota && (e.Technical ?? "").Contains("[PerDay]", StringComparison.Ordinal)) return false;
        return e.Kind is ErrKind.Overloaded or ErrKind.Quota or ErrKind.Network;
    }

    TimeSpan Backoff(int attempt)
    {
        double s = Math.Min(4 * Math.Pow(2, attempt - 1), 40);
        return TimeSpan.FromSeconds(s + Random.Shared.NextDouble() * 2);
    }

    // ---------------------------------------------------------------- transport

    async Task<Reply> SendAsync(Func<HttpRequestMessage> make, int maxAttempts, CancellationToken ct, TimeSpan? timeout = null)
    {
        int timeouts = 0;
        for (int attempt = 1; ; attempt++)
        {
            AppException failure;
            try
            {
                using var req = make();
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(timeout ?? DefaultTimeout);
                using var resp = await _http.SendAsync(req, cts.Token).ConfigureAwait(false);
                var body = await resp.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
                if (resp.IsSuccessStatusCode)
                {
                    var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var h in resp.Headers) headers[h.Key] = string.Join(",", h.Value);
                    return new Reply((int)resp.StatusCode, body, headers);
                }
                failure = FromHttp((int)resp.StatusCode, body);
            }
            catch (HttpRequestException e)
            {
                failure = new AppException(ErrKind.Network, "אין חיבור יציב לאינטרנט או ל-Gemini", technical: e.Message, inner: e);
            }
            catch (OperationCanceledException e) when (!ct.IsCancellationRequested)
            {
                failure = new AppException(ErrKind.Network, "הבקשה ל-Gemini ארכה יותר מדי זמן", technical: "timeout", inner: e);
            }
            catch (IOException e)
            {
                failure = new AppException(ErrKind.Network, "החיבור ל-Gemini נותק באמצע", technical: e.Message, inner: e);
            }

            Log.Write($"request failed (attempt {attempt}/{maxAttempts}): {failure.Technical ?? failure.Message}");
            // A request that timed out once usually will again: allow one retry, not five.
            if (failure.Technical == "timeout" && ++timeouts >= 2) throw failure;
            if (!Retryable(failure) || attempt >= maxAttempts) throw failure;

            var wait = Backoff(attempt);
            var why = failure.Kind == ErrKind.Quota ? "Gemini מגביל את קצב הבקשות" : failure.Kind == ErrKind.Network ? "בעיית תקשורת" : "Gemini עמוס כרגע";
            _status($"{why}. מנסה שוב בעוד {Math.Ceiling(wait.TotalSeconds)} שניות (ניסיון {attempt + 1} מתוך {maxAttempts})");
            await Delay(wait, ct).ConfigureAwait(false);
        }
    }

    HttpRequestMessage Req(HttpMethod method, string url)
    {
        var r = new HttpRequestMessage(method, url);
        r.Headers.Add("x-goog-api-key", _cfg.ApiKey);
        return r;
    }

    // ---------------------------------------------------------------- public API

    public static async Task<bool> ValidateKeyAsync(string key, HttpClient? http = null, CancellationToken ct = default)
    {
        using var own = http == null ? new HttpClient { Timeout = TimeSpan.FromSeconds(20) } : null;
        var client = http ?? own!;
        using var r = new HttpRequestMessage(HttpMethod.Get, $"{Base}/v1beta/models?pageSize=1");
        r.Headers.Add("x-goog-api-key", key);
        using var resp = await client.SendAsync(r, ct);
        return resp.IsSuccessStatusCode;
    }

    public async Task<UploadedFile> UploadAsync(string path, string mime, CancellationToken ct)
    {
        long len = new FileInfo(path).Length;
        if (len == 0) throw new AppException(ErrKind.FileProblem, "הקובץ ריק");

        var start = await SendAsync(() =>
        {
            var r = Req(HttpMethod.Post, $"{Base}/upload/v1beta/files");
            r.Headers.Add("X-Goog-Upload-Protocol", "resumable");
            r.Headers.Add("X-Goog-Upload-Command", "start");
            r.Headers.Add("X-Goog-Upload-Header-Content-Length", len.ToString());
            r.Headers.Add("X-Goog-Upload-Header-Content-Type", mime);
            r.Content = new StringContent("{\"file\":{\"display_name\":\"upload\"}}", Encoding.UTF8, "application/json");
            return r;
        }, 4, ct).ConfigureAwait(false);

        if (!start.Headers.TryGetValue("x-goog-upload-url", out var uploadUrl))
            throw new AppException(ErrKind.Other, "Gemini לא החזיר כתובת להעלאת הקובץ", technical: start.Body);

        var fin = await SendAsync(() =>
        {
            var r = new HttpRequestMessage(HttpMethod.Post, uploadUrl);
            r.Headers.Add("X-Goog-Upload-Offset", "0");
            r.Headers.Add("X-Goog-Upload-Command", "upload, finalize");
            var content = new StreamContent(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read));
            content.Headers.ContentLength = len;
            r.Content = content;
            return r;
        }, 3, ct, UploadTimeout).ConfigureAwait(false);

        var file = JsonNode.Parse(fin.Body)?["file"];
        var name = (string?)file?["name"];
        var uri = (string?)file?["uri"];
        if (name == null || uri == null)
            throw new AppException(ErrKind.Other, "תשובת ההעלאה מ-Gemini אינה תקינה", technical: fin.Body);

        var state = (string?)file?["state"] ?? "ACTIVE";
        var mimeOut = (string?)file?["mimeType"] ?? mime;
        for (int i = 0; state == "PROCESSING"; i++)
        {
            if (i > 200) throw new AppException(ErrKind.Overloaded, "Gemini לא סיים לעבד את הקובץ בזמן סביר");
            _status("Gemini מעבד את הקובץ...");
            await Delay(TimeSpan.FromSeconds(3), ct).ConfigureAwait(false);
            var poll = await SendAsync(() => Req(HttpMethod.Get, $"{Base}/v1beta/{name}"), 4, ct).ConfigureAwait(false);
            state = (string?)JsonNode.Parse(poll.Body)?["state"] ?? "ACTIVE";
        }
        if (state == "FAILED")
            throw new AppException(ErrKind.Unsupported, "Gemini לא הצליח לעבד את הקובץ (ייתכן שהפורמט אינו נתמך)");

        return new UploadedFile(name, uri, mimeOut);
    }

    public async Task DeleteFileAsync(UploadedFile f)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var r = Req(HttpMethod.Delete, $"{Base}/v1beta/{f.Name}");
            using var _ = await _http.SendAsync(r, cts.Token).ConfigureAwait(false);
        }
        catch { /* best effort; Google deletes uploads after 48h anyway */ }
    }

    /// <summary>Called with the model name after a successful call in auto mode (so it can be remembered).</summary>
    public Action<string>? ModelWorked { get; set; }

    static readonly System.Text.RegularExpressions.Regex FlashName =
        new(@"^gemini-(\d+(?:\.\d+)?)-flash(-lite)?(-preview[\w.-]*)?$", System.Text.RegularExpressions.RegexOptions.Compiled);

    static readonly System.Text.RegularExpressions.Regex ThinkingLevelModel =
        new(@"^gemini-([3-9]|\d{2,})(\.\d+)?-flash", System.Text.RegularExpressions.RegexOptions.Compiled);

    List<string>? _discovered;

    /// <summary>Asks the API which Flash models this key can use, best first (stable before preview, full before lite).</summary>
    public async Task<List<string>> DiscoverModelsAsync(CancellationToken ct)
    {
        if (_discovered != null) return _discovered;
        var found = new List<(string Name, double Ver, bool Lite, bool Preview)>();
        try
        {
            string? page = null;
            for (int guard = 0; guard < 10; guard++)
            {
                var url = $"{Base}/v1beta/models?pageSize=200" + (page == null ? "" : "&pageToken=" + Uri.EscapeDataString(page));
                var reply = await SendAsync(() => Req(HttpMethod.Get, url), 2, ct).ConfigureAwait(false);
                var root = JsonNode.Parse(reply.Body);
                if (root?["models"] is JsonArray ms)
                    foreach (var m in ms)
                    {
                        var name = ((string?)m?["name"] ?? "").Replace("models/", "");
                        var methods = m?["supportedGenerationMethods"] as JsonArray;
                        if (methods == null || !methods.Any(x => (string?)x == "generateContent")) continue;
                        var mt = FlashName.Match(name);
                        if (!mt.Success) continue;
                        found.Add((name, double.Parse(mt.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture), mt.Groups[2].Success, mt.Groups[3].Success));
                    }
                page = (string?)root?["nextPageToken"];
                if (string.IsNullOrEmpty(page)) break;
            }
        }
        catch (AppException) { /* listing is a convenience; fall back to known names below */ }

        _discovered = found
            .OrderBy(f => f.Preview).ThenBy(f => f.Lite).ThenByDescending(f => f.Ver)
            .Select(f => f.Name).Distinct().ToList();
        if (_discovered.Count == 0)
            _discovered = new List<string> { "gemini-3.5-flash", "gemini-3.5-flash-lite", "gemini-2.5-flash" };
        return _discovered;
    }

    /// <param name="parts">Gemini "parts" array. Built per call; this method serializes it once.</param>
    public async Task<GenResult> GenerateAsync(JsonArray parts, JsonObject generationConfig, bool disableThinkingWhenPossible, CancellationToken ct)
    {
        bool auto = string.IsNullOrWhiteSpace(_cfg.Model) || _cfg.Model == "auto";
        var queue = new Queue<string>();
        var tried = new HashSet<string>();
        void Enqueue(string? m)
        {
            if (!string.IsNullOrWhiteSpace(m) && m != "auto" && !tried.Contains(m) && !queue.Contains(m)) queue.Enqueue(m!);
        }
        if (auto) Enqueue(_cfg.ResolvedModel); else Enqueue(_cfg.Model);
        Enqueue(_cfg.FallbackModel);

        bool discovered = false;
        var errors = new List<string>();
        AppException? last = null;
        const int MaxModels = 4;

        while (tried.Count < MaxModels)
        {
            if (queue.Count == 0)
            {
                if (discovered) break;
                discovered = true;
                foreach (var m in await DiscoverModelsAsync(ct).ConfigureAwait(false)) Enqueue(m);
                if (queue.Count == 0) break;
            }
            var model = queue.Dequeue();
            tried.Add(model);
            try
            {
                var res = await GenerateOnceAsync(model, parts, generationConfig, disableThinkingWhenPossible, tried.Count == 1 ? 5 : 3, ct).ConfigureAwait(false);
                if (auto && model != _cfg.ResolvedModel) { try { ModelWorked?.Invoke(model); } catch { } }
                return res;
            }
            catch (AppException e) when (e.Kind is ErrKind.Overloaded or ErrKind.Quota or ErrKind.NotFound or ErrKind.Network)
            {
                last = e;
                errors.Add($"[{model}] {e.Technical ?? e.Message}");
                _status("עובר למודל חלופי של Gemini...");
            }
        }
        throw new AppException(last?.Kind ?? ErrKind.Other, last?.Message ?? "לא נמצא מודל זמין", last?.Status ?? 0, string.Join("\n", errors));
    }

    async Task<GenResult> GenerateOnceAsync(string model, JsonArray parts, JsonObject genCfg, bool noThink, int attempts, CancellationToken ct)
    {
        // Gemini 2.5 Flash: thinkingBudget = 0. Gemini 3.x Flash: thinkingLevel = "minimal" (default is medium/high,
        // which made a 3-minute transcription take minutes). If a model rejects it, retry once without it.
        bool levelModel = noThink && ThinkingLevelModel.IsMatch(model);
        string BuildBody(bool minimal)
        {
            var cfg = (JsonObject)JsonNode.Parse(genCfg.ToJsonString())!;
            if (noThink && model.Contains("2.5") && model.Contains("flash"))
                cfg["thinkingConfig"] = new JsonObject { ["thinkingBudget"] = 0 };
            if (minimal)
                cfg["thinkingConfig"] = new JsonObject { ["thinkingLevel"] = "minimal" };
            return new JsonObject
            {
                ["contents"] = new JsonArray { new JsonObject { ["parts"] = JsonNode.Parse(parts.ToJsonString()) } },
                ["generationConfig"] = cfg,
            }.ToJsonString();
        }

        Task<Reply> Post(string body) => SendAsync(() =>
        {
            var r = Req(HttpMethod.Post, $"{Base}/v1beta/models/{model}:generateContent");
            r.Content = new StringContent(body, Encoding.UTF8, "application/json");
            return r;
        }, attempts, ct, GenerateTimeout);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        Log.Write($"generate: model={model}, thinkingLevel={(levelModel ? "minimal" : "default")}");
        Reply reply;
        try
        {
            reply = await Post(BuildBody(levelModel)).ConfigureAwait(false);
        }
        catch (AppException e) when (levelModel && e.Kind == ErrKind.BadRequest)
        {
            Log.Write("model rejected thinkingLevel, retrying without it: " + (e.Technical ?? e.Message));
            reply = await Post(BuildBody(false)).ConfigureAwait(false);
        }

        var root = JsonNode.Parse(reply.Body);
        var usage = root?["usageMetadata"];
        Log.Write($"generate done in {sw.Elapsed.TotalSeconds:0}s: prompt={(int?)usage?["promptTokenCount"]}, thoughts={(int?)usage?["thoughtsTokenCount"]}, output={(int?)usage?["candidatesTokenCount"]}, finish={(string?)root?["candidates"]?[0]?["finishReason"]}");
        var cand = root?["candidates"]?[0];
        var sb = new StringBuilder();
        if (cand?["content"]?["parts"] is JsonArray ps)
            foreach (var p in ps)
            {
                if ((bool?)p?["thought"] == true) continue;
                var t = (string?)p?["text"];
                if (t != null) sb.Append(t);
            }

        var finish = (string?)cand?["finishReason"];
        var text = sb.ToString().Trim();
        if (text.Length == 0)
        {
            var block = (string?)root?["promptFeedback"]?["blockReason"];
            var why = block ?? finish ?? "לא ידוע";
            throw new AppException(ErrKind.Blocked, $"Gemini לא החזיר תוכן (סיבה: {why})", technical: reply.Body.Length > 600 ? reply.Body[..600] : reply.Body);
        }
        return new GenResult(text, finish);
    }
}

using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace GeminiMenu;

/// <summary>
/// One text-generation call. Gemini, Claude and OpenAI all sit behind this, so the editing and summary jobs
/// do not care which one runs them. Parts and config use the Gemini JSON shape the jobs already build
/// (text parts, inlineData for small PDFs, maxOutputTokens, responseMimeType/responseSchema).
/// </summary>
public interface IModelClient
{
    Task<GenResult> GenerateAsync(JsonArray parts, JsonObject generationConfig, bool disableThinkingWhenPossible, CancellationToken ct);
}

/// <summary>Shared HTTP, retry and error handling for the Claude and OpenAI clients.</summary>
public abstract class RemoteModelClient : IDisposable
{
    readonly HttpClient _http;
    readonly bool _ownsHttp;
    protected readonly Action<string> Status;

    /// <summary>Overridable so tests do not really wait.</summary>
    public Func<TimeSpan, CancellationToken, Task> Delay { get; set; } = (t, ct) => Task.Delay(t, ct);

    protected abstract string ProviderName { get; }

    protected RemoteModelClient(Action<string>? status, HttpClient? http)
    {
        Status = status ?? (_ => { });
        _ownsHttp = http == null;
        _http = http ?? new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
    }

    public void Dispose()
    {
        if (_ownsHttp) _http.Dispose();
    }

    protected static readonly TimeSpan GenerateTimeout = TimeSpan.FromMinutes(10);

    protected sealed record Reply(int Status, string Body);

    // ---------------------------------------------------------------- errors

    internal static string ExtractMessage(string body)
    {
        try
        {
            var err = JsonNode.Parse(body)?["error"];
            var m = err is JsonObject ? (string?)err["message"] : (string?)err;
            if (!string.IsNullOrWhiteSpace(m)) return m!;
        }
        catch { /* not json */ }
        return body.Length > 400 ? body[..400] : body;
    }

    protected AppException FromHttp(int status, string body)
    {
        var msg = ExtractMessage(body);
        var lower = (msg + " " + body).ToLowerInvariant();
        bool noMoney = lower.Contains("insufficient_quota") || lower.Contains("credit balance") || lower.Contains("billing") || lower.Contains("exceeded your current quota");
        ErrKind kind = status switch
        {
            401 or 403 => ErrKind.BadKey,
            402 => ErrKind.Quota,
            404 => ErrKind.NotFound,
            413 => ErrKind.BadRequest,
            429 => noMoney ? ErrKind.Quota : ErrKind.Overloaded,
            >= 500 => ErrKind.Overloaded,
            400 or 422 => ErrKind.BadRequest,
            _ => ErrKind.Other,
        };
        string text = kind switch
        {
            ErrKind.BadKey => $"{ProviderName} דחה את המפתח. בדוק אותו בהגדרות (מנועי AI ומפתחות).",
            ErrKind.Quota => $"נגמרה המכסה או היתרה בחשבון {ProviderName}.",
            ErrKind.NotFound => $"{ProviderName} לא מצא את הדגם שהוגדר. שנה את שם הדגם בהגדרות (מנועי AI ומפתחות).",
            ErrKind.Overloaded => $"{ProviderName} עמוס כרגע או שהגעת למגבלת קצב. נסה שוב בעוד כמה דקות.",
            ErrKind.BadRequest => $"{ProviderName} דחה את הבקשה: {msg}",
            _ => $"שגיאה מ-{ProviderName} ({status}): {msg}",
        };
        return new AppException(kind, text, status, body.Length > 600 ? body[..600] : body) { Provider = ProviderName };
    }

    // ---------------------------------------------------------------- http

    protected async Task<Reply> SendAsync(Func<HttpRequestMessage> makeRequest, int attempts, CancellationToken ct, TimeSpan timeout)
    {
        AppException? last = null;
        for (int attempt = 1; attempt <= attempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(timeout);
            TimeSpan? wait = null;
            try
            {
                using var req = makeRequest();
                using var resp = await _http.SendAsync(req, timeoutCts.Token).ConfigureAwait(false);
                var body = await resp.Content.ReadAsStringAsync(timeoutCts.Token).ConfigureAwait(false);
                int code = (int)resp.StatusCode;
                if (code is >= 200 and < 300) return new Reply(code, body);

                last = FromHttp(code, body);
                if (last.Kind is not (ErrKind.Overloaded or ErrKind.Network)) throw last;
                if (resp.Headers.RetryAfter?.Delta is { } ra) wait = ra;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException ex)
            {
                last = new AppException(ErrKind.Network, $"{ProviderName} לא ענה בזמן.", technical: ex.Message, inner: ex) { Provider = ProviderName };
            }
            catch (HttpRequestException ex)
            {
                last = new AppException(ErrKind.Network, $"אין חיבור ל-{ProviderName}. בדוק את האינטרנט.", technical: ex.Message, inner: ex) { Provider = ProviderName };
            }

            if (attempt == attempts) break;
            wait ??= TimeSpan.FromSeconds(Math.Min(30, 2 * Math.Pow(2, attempt - 1)));
            if (wait > TimeSpan.FromSeconds(60)) wait = TimeSpan.FromSeconds(60);
            Status($"{ProviderName} עמוס. מנסה שוב בעוד {Math.Max(1, (int)wait.Value.TotalSeconds)} שניות...");
            await Delay(wait.Value, ct).ConfigureAwait(false);
        }
        throw last ?? new AppException(ErrKind.Other, $"שגיאה לא ידועה מול {ProviderName}.") { Provider = ProviderName };
    }

    // ---------------------------------------------------------------- helpers shared by both providers

    /// <summary>True when the job wants a JSON answer (the summary jobs).</summary>
    protected static bool WantsJson(JsonObject cfg) => (string?)cfg["responseMimeType"] == "application/json";

    /// <summary>The Gemini jobs pass a response schema; other providers get it as an instruction in the prompt instead.</summary>
    protected static string? SchemaInstruction(JsonObject cfg)
    {
        if (!WantsJson(cfg)) return null;
        var schema = cfg["responseSchema"]?.ToJsonString();
        return "החזר אך ורק אובייקט JSON תקין אחד, בלי הסברים ובלי גדרות קוד, שמבנהו תואם לסכמה הבאה (שמות השדות באנגלית כפי שהם, והערכים בעברית):\n" + (schema ?? "{}");
    }

    static readonly Regex Fence = new(@"^```[a-zA-Z]*\s*|\s*```$", RegexOptions.Compiled);

    /// <summary>Removes a code fence and anything around the outermost JSON object.</summary>
    internal static string CleanJson(string text)
    {
        var s = Fence.Replace((text ?? "").Trim(), "").Trim();
        int a = s.IndexOf('{'), b = s.LastIndexOf('}');
        return a >= 0 && b > a ? s[a..(b + 1)] : s;
    }

    protected static int MaxTokens(JsonObject cfg, int fallback)
    {
        try { var v = (int?)cfg["maxOutputTokens"]; if (v is > 0) return v.Value; } catch { }
        return fallback;
    }

    protected static AppException Unsupported(string provider, string what) =>
        new(ErrKind.Unsupported, $"{what} נתמך רק עם Gemini. בחר ב-Gemini כמנוע העריכה והסיכום, או המר את המסמך לטקסט.") { Provider = provider };
}

// ====================================================================== Claude

public sealed class ClaudeClient : RemoteModelClient, IModelClient
{
    public const string DefaultModel = "claude-sonnet-5-5";
    internal const string Endpoint = "https://api.anthropic.com/v1";

    readonly string _key;
    readonly string _model;

    protected override string ProviderName => "Claude";

    public ClaudeClient(string apiKey, string? model = null, Action<string>? status = null, HttpClient? http = null) : base(status, http)
    {
        _key = (apiKey ?? "").Trim();
        _model = string.IsNullOrWhiteSpace(model) ? DefaultModel : model.Trim();
    }

    HttpRequestMessage Req(HttpMethod method, string url)
    {
        var r = new HttpRequestMessage(method, url);
        r.Headers.Add("x-api-key", _key);
        r.Headers.Add("anthropic-version", "2023-06-01");
        return r;
    }

    public static async Task<bool> ValidateKeyAsync(string key, HttpClient? http = null, CancellationToken ct = default)
    {
        var own = http == null;
        http ??= new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        try
        {
            using var r = new HttpRequestMessage(HttpMethod.Get, Endpoint + "/models?limit=1");
            r.Headers.Add("x-api-key", (key ?? "").Trim());
            r.Headers.Add("anthropic-version", "2023-06-01");
            using var resp = await http.SendAsync(r, ct).ConfigureAwait(false);
            return resp.IsSuccessStatusCode;
        }
        catch (HttpRequestException) { return false; }
        finally { if (own) http.Dispose(); }
    }

    internal static JsonArray BuildContent(JsonArray parts, string? schemaInstruction)
    {
        var content = new JsonArray();
        foreach (var p in parts)
        {
            if (p is not JsonObject o) continue;
            if (o["text"] != null)
                content.Add(new JsonObject { ["type"] = "text", ["text"] = (string?)o["text"] ?? "" });
            else if (o["inlineData"] is JsonObject d)
            {
                var mime = (string?)d["mimeType"] ?? "";
                if (mime != "application/pdf") throw Unsupported("Claude", "עיבוד קובץ מסוג " + mime);
                content.Add(new JsonObject
                {
                    ["type"] = "document",
                    ["source"] = new JsonObject { ["type"] = "base64", ["media_type"] = mime, ["data"] = (string?)d["data"] ?? "" },
                });
            }
            else
                throw Unsupported("Claude", "קובץ שהועלה לשרת");
        }
        if (schemaInstruction != null) content.Add(new JsonObject { ["type"] = "text", ["text"] = schemaInstruction });
        return content;
    }

    public async Task<GenResult> GenerateAsync(JsonArray parts, JsonObject generationConfig, bool disableThinkingWhenPossible, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_key)) throw new AppException(ErrKind.NoKey, "לא הוגדר מפתח API של Claude") { Provider = "Claude" };
        bool json = WantsJson(generationConfig);
        var body = new JsonObject
        {
            ["model"] = _model,
            ["max_tokens"] = MaxTokens(generationConfig, 8192),
            ["messages"] = new JsonArray
            {
                new JsonObject { ["role"] = "user", ["content"] = BuildContent(parts, SchemaInstruction(generationConfig)) },
            },
        }.ToJsonString();

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var reply = await SendAsync(() =>
        {
            var r = Req(HttpMethod.Post, Endpoint + "/messages");
            r.Content = new StringContent(body, Encoding.UTF8, "application/json");
            return r;
        }, 5, ct, GenerateTimeout).ConfigureAwait(false);

        var root = JsonNode.Parse(reply.Body);
        var sb = new StringBuilder();
        if (root?["content"] is JsonArray blocks)
            foreach (var b in blocks)
                if ((string?)b?["type"] == "text") sb.Append((string?)b["text"]);

        var stop = (string?)root?["stop_reason"];
        Log.Write($"claude done in {sw.Elapsed.TotalSeconds:0}s: model={_model}, in={(int?)root?["usage"]?["input_tokens"]}, out={(int?)root?["usage"]?["output_tokens"]}, stop={stop}");
        var text = sb.ToString().Trim();
        if (text.Length == 0)
            throw new AppException(ErrKind.Blocked, $"Claude לא החזיר תוכן (סיבה: {stop ?? "לא ידוע"})", technical: reply.Body.Length > 600 ? reply.Body[..600] : reply.Body) { Provider = "Claude" };
        if (json) text = CleanJson(text);
        return new GenResult(text, stop == "max_tokens" ? "MAX_TOKENS" : "STOP");
    }
}

// ====================================================================== OpenAI

public sealed class OpenAiClient : RemoteModelClient, IModelClient
{
    public const string DefaultModel = "gpt-5.6-sol";
    public const string DefaultTranscribeModel = "gpt-transcribe";
    internal const string Endpoint = "https://api.openai.com/v1";

    readonly string _key;
    readonly string _model;
    readonly string _transcribeModel;
    readonly string _language;

    protected override string ProviderName => "OpenAI";

    public OpenAiClient(string apiKey, string? model = null, string? transcribeModel = null, string? language = null,
        Action<string>? status = null, HttpClient? http = null) : base(status, http)
    {
        _key = (apiKey ?? "").Trim();
        _model = string.IsNullOrWhiteSpace(model) ? DefaultModel : model.Trim();
        _transcribeModel = string.IsNullOrWhiteSpace(transcribeModel) ? DefaultTranscribeModel : transcribeModel.Trim();
        _language = (language ?? "").Trim();
    }

    HttpRequestMessage Req(HttpMethod method, string url)
    {
        var r = new HttpRequestMessage(method, url);
        r.Headers.TryAddWithoutValidation("Authorization", "Bearer " + _key);
        return r;
    }

    public static async Task<bool> ValidateKeyAsync(string key, HttpClient? http = null, CancellationToken ct = default)
    {
        var own = http == null;
        http ??= new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        try
        {
            using var r = new HttpRequestMessage(HttpMethod.Get, Endpoint + "/models");
            r.Headers.TryAddWithoutValidation("Authorization", "Bearer " + (key ?? "").Trim());
            using var resp = await http.SendAsync(r, ct).ConfigureAwait(false);
            return resp.IsSuccessStatusCode;
        }
        catch (HttpRequestException) { return false; }
        finally { if (own) http.Dispose(); }
    }

    internal static JsonArray BuildContent(JsonArray parts, string? schemaInstruction)
    {
        var content = new JsonArray();
        foreach (var p in parts)
        {
            if (p is not JsonObject o) continue;
            if (o["text"] != null)
                content.Add(new JsonObject { ["type"] = "text", ["text"] = (string?)o["text"] ?? "" });
            else if (o["inlineData"] is JsonObject d)
            {
                var mime = (string?)d["mimeType"] ?? "";
                if (mime != "application/pdf") throw Unsupported("OpenAI", "עיבוד קובץ מסוג " + mime);
                content.Add(new JsonObject
                {
                    ["type"] = "file",
                    ["file"] = new JsonObject { ["filename"] = "document.pdf", ["file_data"] = $"data:{mime};base64,{(string?)d["data"]}" },
                });
            }
            else
                throw Unsupported("OpenAI", "קובץ שהועלה לשרת");
        }
        if (schemaInstruction != null) content.Add(new JsonObject { ["type"] = "text", ["text"] = schemaInstruction });
        return content;
    }

    public async Task<GenResult> GenerateAsync(JsonArray parts, JsonObject generationConfig, bool disableThinkingWhenPossible, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_key)) throw new AppException(ErrKind.NoKey, "לא הוגדר מפתח API של OpenAI") { Provider = "OpenAI" };
        bool json = WantsJson(generationConfig);
        // Reasoning models spend part of this budget on thinking, so leave room beyond what the job asked for.
        int tokens = Math.Min(MaxTokens(generationConfig, 8192) * 2, 32000);

        string BuildBody(bool lowEffort)
        {
            var o = new JsonObject
            {
                ["model"] = _model,
                ["max_completion_tokens"] = tokens,
                ["messages"] = new JsonArray
                {
                    new JsonObject { ["role"] = "user", ["content"] = BuildContent(parts, SchemaInstruction(generationConfig)) },
                },
            };
            if (json) o["response_format"] = new JsonObject { ["type"] = "json_object" };
            if (lowEffort && disableThinkingWhenPossible) o["reasoning_effort"] = "low";
            return o.ToJsonString();
        }

        Task<Reply> Post(string body) => SendAsync(() =>
        {
            var r = Req(HttpMethod.Post, Endpoint + "/chat/completions");
            r.Content = new StringContent(body, Encoding.UTF8, "application/json");
            return r;
        }, 5, ct, GenerateTimeout);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        Reply reply;
        try { reply = await Post(BuildBody(true)).ConfigureAwait(false); }
        catch (AppException e) when (disableThinkingWhenPossible && e.Kind == ErrKind.BadRequest
                                     && (e.Technical ?? "").Contains("reasoning_effort", StringComparison.OrdinalIgnoreCase))
        {
            Log.Write("model rejected reasoning_effort, retrying without it");
            reply = await Post(BuildBody(false)).ConfigureAwait(false);
        }

        var root = JsonNode.Parse(reply.Body);
        var choice = root?["choices"]?[0];
        var finish = (string?)choice?["finish_reason"];
        Log.Write($"openai done in {sw.Elapsed.TotalSeconds:0}s: model={_model}, in={(int?)root?["usage"]?["prompt_tokens"]}, out={(int?)root?["usage"]?["completion_tokens"]}, finish={finish}");
        var text = ((string?)choice?["message"]?["content"] ?? "").Trim();
        var refusal = (string?)choice?["message"]?["refusal"];
        if (text.Length == 0)
            throw new AppException(ErrKind.Blocked, $"OpenAI לא החזיר תוכן (סיבה: {refusal ?? finish ?? "לא ידוע"})", technical: reply.Body.Length > 600 ? reply.Body[..600] : reply.Body) { Provider = "OpenAI" };
        if (json) text = CleanJson(text);
        return new GenResult(text, finish == "length" ? "MAX_TOKENS" : "STOP");
    }

    /// <summary>Speech to text for one audio file (the chunks are short wav files, well under the 25 MB limit).</summary>
    public async Task<GenResult> TranscribeAsync(string path, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_key)) throw new AppException(ErrKind.NoKey, "לא הוגדר מפתח API של OpenAI") { Provider = "OpenAI" };
        var bytes = await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);
        if (bytes.Length > 24 * 1024 * 1024)
            throw new AppException(ErrKind.FileProblem, "קטע השמע גדול מדי לשליחה ל-OpenAI (מעל 25MB). הקטן את אורך הקטע בהגדרות.");

        var reply = await SendAsync(() =>
        {
            var form = new MultipartFormDataContent();
            var file = new ByteArrayContent(bytes);
            file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("audio/wav");
            form.Add(file, "file", Path.GetFileName(path));
            form.Add(new StringContent(_transcribeModel), "model");
            if (_language.Length > 0) form.Add(new StringContent(_language), "language");
            var r = Req(HttpMethod.Post, Endpoint + "/audio/transcriptions");
            r.Content = form;
            return r;
        }, 5, ct, GenerateTimeout).ConfigureAwait(false);

        var text = ((string?)JsonNode.Parse(reply.Body)?["text"] ?? "").Trim();
        // Silence is a legitimate result: the caller's sanity check decides what to do with it.
        return new GenResult(text, "STOP");
    }
}

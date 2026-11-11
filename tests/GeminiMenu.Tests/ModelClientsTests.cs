using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using Xunit;

namespace GeminiMenu;

/// <summary>Fake HTTP server: records every request (with its body) and answers from a script.</summary>
sealed class FakeHttp : HttpMessageHandler
{
    public sealed record Seen(string Method, string Url, Dictionary<string, string> Headers, string Body);

    public readonly List<Seen> Requests = new();
    readonly Queue<(HttpStatusCode Code, string Body)> _replies = new();
    (HttpStatusCode Code, string Body)? _fallback;

    public FakeHttp Reply(HttpStatusCode code, string body) { _replies.Enqueue((code, body)); return this; }
    public FakeHttp Always(HttpStatusCode code, string body) { _fallback = (code, body); return this; }
    public HttpClient Client() => new(this);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var headers = request.Headers.ToDictionary(h => h.Key.ToLowerInvariant(), h => string.Join(",", h.Value));
        var body = request.Content == null ? "" : await request.Content.ReadAsStringAsync(ct);
        Requests.Add(new Seen(request.Method.Method, request.RequestUri!.ToString(), headers, body));
        var (code, text) = _replies.Count > 0 ? _replies.Dequeue() : _fallback ?? (HttpStatusCode.OK, "{}");
        return new HttpResponseMessage(code) { Content = new StringContent(text, Encoding.UTF8, "application/json") };
    }
}

public class ModelClientsTests
{
    static JsonArray Text(string text) => new() { new JsonObject { ["text"] = text } };
    static JsonObject Cfg(int max = 1000) => new() { ["maxOutputTokens"] = max };

    const string ClaudeOk = """{"content":[{"type":"text","text":"שלום"}],"stop_reason":"end_turn","usage":{"input_tokens":3,"output_tokens":2}}""";
    const string OpenAiOk = """{"choices":[{"message":{"content":"שלום"},"finish_reason":"stop"}],"usage":{"prompt_tokens":3,"completion_tokens":2}}""";

    static ClaudeClient Claude(FakeHttp http, string model = "claude-test") =>
        new("sk-ant-key", model, http: http.Client()) { Delay = (_, _) => Task.CompletedTask };

    static OpenAiClient OpenAi(FakeHttp http) =>
        new("sk-key", "gpt-test", "transcribe-test", "he", http: http.Client()) { Delay = (_, _) => Task.CompletedTask };

    // ------------------------------------------------------------ Claude

    [Fact]
    public async Task Claude_SendsExpectedRequest_AndReturnsText()
    {
        var http = new FakeHttp().Reply(HttpStatusCode.OK, ClaudeOk);
        var res = await Claude(http).GenerateAsync(Text("ערוך את זה"), Cfg(4321), true, default);

        Assert.Equal("שלום", res.Text);
        Assert.Equal("STOP", res.FinishReason);
        var req = Assert.Single(http.Requests);
        Assert.Equal("https://api.anthropic.com/v1/messages", req.Url);
        Assert.Equal("sk-ant-key", req.Headers["x-api-key"]);
        Assert.Equal("2023-06-01", req.Headers["anthropic-version"]);
        var body = JsonNode.Parse(req.Body)!;
        Assert.Equal("claude-test", (string)body["model"]!);
        Assert.Equal(4321, (int)body["max_tokens"]!);
        Assert.Equal("user", (string)body["messages"]![0]!["role"]!);
        Assert.Equal("ערוך את זה", (string)body["messages"]![0]!["content"]![0]!["text"]!);
    }

    [Fact]
    public async Task Claude_MaxTokensStopReason_IsReportedAsMaxTokens()
    {
        var http = new FakeHttp().Reply(HttpStatusCode.OK, ClaudeOk.Replace("end_turn", "max_tokens"));
        var res = await Claude(http).GenerateAsync(Text("x"), Cfg(), true, default);
        Assert.Equal("MAX_TOKENS", res.FinishReason);
    }

    [Fact]
    public async Task Claude_JsonMode_AddsSchemaInstruction_AndStripsCodeFence()
    {
        var fenced = "{\"content\":[{\"type\":\"text\",\"text\":\"```json\\n{\\\"oneLine\\\":\\\"א\\\"}\\n```\"}],\"stop_reason\":\"end_turn\"}";
        var http = new FakeHttp().Reply(HttpStatusCode.OK, fenced);
        var cfg = new JsonObject
        {
            ["maxOutputTokens"] = 500,
            ["responseMimeType"] = "application/json",
            ["responseSchema"] = new JsonObject { ["type"] = "OBJECT" },
        };
        var res = await Claude(http).GenerateAsync(Text("סכם"), cfg, false, default);

        Assert.Equal("{\"oneLine\":\"א\"}", res.Text);
        var content = JsonNode.Parse(http.Requests[0].Body)!["messages"]![0]!["content"]!.AsArray();
        Assert.Equal(2, content.Count);
        Assert.Contains("JSON", (string)content[1]!["text"]!);
        Assert.Contains("OBJECT", (string)content[1]!["text"]!);
    }

    [Fact]
    public async Task Claude_InlinePdf_BecomesDocumentBlock()
    {
        var http = new FakeHttp().Reply(HttpStatusCode.OK, ClaudeOk);
        var parts = new JsonArray
        {
            new JsonObject { ["inlineData"] = new JsonObject { ["mimeType"] = "application/pdf", ["data"] = "QUJD" } },
            new JsonObject { ["text"] = "ערוך" },
        };
        await Claude(http).GenerateAsync(parts, Cfg(), true, default);
        var first = JsonNode.Parse(http.Requests[0].Body)!["messages"]![0]!["content"]![0]!;
        Assert.Equal("document", (string)first["type"]!);
        Assert.Equal("QUJD", (string)first["source"]!["data"]!);
        Assert.Equal("application/pdf", (string)first["source"]!["media_type"]!);
    }

    [Fact]
    public async Task Claude_UploadedFileReference_IsRejectedBeforeAnyRequest()
    {
        var http = new FakeHttp();
        var parts = new JsonArray { new JsonObject { ["fileData"] = new JsonObject { ["fileUri"] = "x" } } };
        var ex = await Assert.ThrowsAsync<AppException>(() => Claude(http).GenerateAsync(parts, Cfg(), true, default));
        Assert.Equal(ErrKind.Unsupported, ex.Kind);
        Assert.Equal("Claude", ex.Provider);
        Assert.Empty(http.Requests);
    }

    [Fact]
    public async Task Claude_BadKey_IsNotRetried()
    {
        var http = new FakeHttp().Always(HttpStatusCode.Unauthorized, """{"error":{"message":"invalid x-api-key"}}""");
        var ex = await Assert.ThrowsAsync<AppException>(() => Claude(http).GenerateAsync(Text("x"), Cfg(), true, default));
        Assert.Equal(ErrKind.BadKey, ex.Kind);
        Assert.Equal("Claude", ex.Provider);
        Assert.Single(http.Requests);
    }

    [Fact]
    public async Task Claude_OverloadedThenOk_Retries()
    {
        var http = new FakeHttp()
            .Reply((HttpStatusCode)529, """{"error":{"message":"Overloaded"}}""")
            .Reply(HttpStatusCode.TooManyRequests, """{"error":{"message":"slow down"}}""")
            .Reply(HttpStatusCode.OK, ClaudeOk);
        var res = await Claude(http).GenerateAsync(Text("x"), Cfg(), true, default);
        Assert.Equal("שלום", res.Text);
        Assert.Equal(3, http.Requests.Count);
    }

    [Fact]
    public async Task Claude_AlwaysOverloaded_GivesUpWithOverloaded()
    {
        var http = new FakeHttp().Always((HttpStatusCode)529, """{"error":{"message":"Overloaded"}}""");
        var ex = await Assert.ThrowsAsync<AppException>(() => Claude(http).GenerateAsync(Text("x"), Cfg(), true, default));
        Assert.Equal(ErrKind.Overloaded, ex.Kind);
        Assert.Equal(5, http.Requests.Count);
    }

    [Fact]
    public async Task Claude_UnknownModel_SaysToChangeTheModel()
    {
        var http = new FakeHttp().Always(HttpStatusCode.NotFound, """{"error":{"message":"model: nope"}}""");
        var ex = await Assert.ThrowsAsync<AppException>(() => Claude(http, "nope").GenerateAsync(Text("x"), Cfg(), true, default));
        Assert.Equal(ErrKind.NotFound, ex.Kind);
        Assert.Contains("הדגם", ex.Message);
    }

    [Fact]
    public async Task Claude_WithoutKey_FailsFast()
    {
        var http = new FakeHttp();
        using var client = new ClaudeClient("  ", http: http.Client());
        var ex = await Assert.ThrowsAsync<AppException>(() => client.GenerateAsync(Text("x"), Cfg(), true, default));
        Assert.Equal(ErrKind.NoKey, ex.Kind);
        Assert.Empty(http.Requests);
    }

    // ------------------------------------------------------------ OpenAI

    [Fact]
    public async Task OpenAi_SendsExpectedRequest_AndReturnsText()
    {
        var http = new FakeHttp().Reply(HttpStatusCode.OK, OpenAiOk);
        var res = await OpenAi(http).GenerateAsync(Text("ערוך"), Cfg(1000), true, default);

        Assert.Equal("שלום", res.Text);
        Assert.Equal("STOP", res.FinishReason);
        var req = Assert.Single(http.Requests);
        Assert.Equal("https://api.openai.com/v1/chat/completions", req.Url);
        Assert.Equal("Bearer sk-key", req.Headers["authorization"]);
        var body = JsonNode.Parse(req.Body)!;
        Assert.Equal("gpt-test", (string)body["model"]!);
        Assert.Equal(2000, (int)body["max_completion_tokens"]!); // room for reasoning tokens
        Assert.Equal("low", (string)body["reasoning_effort"]!);
        Assert.Equal("ערוך", (string)body["messages"]![0]!["content"]![0]!["text"]!);
    }

    [Fact]
    public async Task OpenAi_LengthFinish_IsReportedAsMaxTokens()
    {
        var http = new FakeHttp().Reply(HttpStatusCode.OK, OpenAiOk.Replace("\"stop\"", "\"length\""));
        var res = await OpenAi(http).GenerateAsync(Text("x"), Cfg(), true, default);
        Assert.Equal("MAX_TOKENS", res.FinishReason);
    }

    [Fact]
    public async Task OpenAi_ModelRejectingReasoningEffort_IsRetriedWithoutIt()
    {
        var http = new FakeHttp()
            .Reply(HttpStatusCode.BadRequest, """{"error":{"message":"Unsupported parameter: 'reasoning_effort'"}}""")
            .Reply(HttpStatusCode.OK, OpenAiOk);
        var res = await OpenAi(http).GenerateAsync(Text("x"), Cfg(), true, default);

        Assert.Equal("שלום", res.Text);
        Assert.Equal(2, http.Requests.Count);
        Assert.Contains("reasoning_effort", http.Requests[0].Body);
        Assert.DoesNotContain("reasoning_effort", http.Requests[1].Body);
    }

    [Fact]
    public async Task OpenAi_JsonMode_UsesJsonObjectFormat()
    {
        var http = new FakeHttp().Reply(HttpStatusCode.OK, """{"choices":[{"message":{"content":"{\"a\":1}"},"finish_reason":"stop"}]}""");
        var cfg = new JsonObject { ["maxOutputTokens"] = 100, ["responseMimeType"] = "application/json", ["responseSchema"] = new JsonObject() };
        var res = await OpenAi(http).GenerateAsync(Text("x"), cfg, false, default);

        Assert.Equal("{\"a\":1}", res.Text);
        var body = JsonNode.Parse(http.Requests[0].Body)!;
        Assert.Equal("json_object", (string)body["response_format"]!["type"]!);
        Assert.Null(body["reasoning_effort"]);
    }

    [Fact]
    public async Task OpenAi_InlinePdf_BecomesFilePart()
    {
        var http = new FakeHttp().Reply(HttpStatusCode.OK, OpenAiOk);
        var parts = new JsonArray { new JsonObject { ["inlineData"] = new JsonObject { ["mimeType"] = "application/pdf", ["data"] = "QUJD" } } };
        await OpenAi(http).GenerateAsync(parts, Cfg(), true, default);
        var first = JsonNode.Parse(http.Requests[0].Body)!["messages"]![0]!["content"]![0]!;
        Assert.Equal("file", (string)first["type"]!);
        Assert.Equal("data:application/pdf;base64,QUJD", (string)first["file"]!["file_data"]!);
    }

    [Fact]
    public async Task OpenAi_InsufficientQuota_IsQuotaAndNotRetried()
    {
        var http = new FakeHttp().Always(HttpStatusCode.TooManyRequests, """{"error":{"message":"You exceeded your current quota","code":"insufficient_quota"}}""");
        var ex = await Assert.ThrowsAsync<AppException>(() => OpenAi(http).GenerateAsync(Text("x"), Cfg(), true, default));
        Assert.Equal(ErrKind.Quota, ex.Kind);
        Assert.Equal("OpenAI", ex.Provider);
        Assert.Single(http.Requests);
    }

    [Fact]
    public async Task OpenAi_Transcribe_PostsMultipartWithModelAndLanguage()
    {
        var wav = Path.Combine(Path.GetTempPath(), "gm-test-" + Guid.NewGuid().ToString("N") + ".wav");
        File.WriteAllBytes(wav, new byte[] { 1, 2, 3, 4 });
        try
        {
            var http = new FakeHttp().Reply(HttpStatusCode.OK, """{"text":"שלום עולם"}""");
            var res = await OpenAi(http).TranscribeAsync(wav, default);

            Assert.Equal("שלום עולם", res.Text);
            var req = Assert.Single(http.Requests);
            Assert.Equal("https://api.openai.com/v1/audio/transcriptions", req.Url);
            Assert.Equal("Bearer sk-key", req.Headers["authorization"]);
            Assert.Contains("transcribe-test", req.Body);
            Assert.Contains("name=language", req.Body);
            Assert.Contains("he", req.Body);
            Assert.Contains(Path.GetFileName(wav), req.Body);
        }
        finally { File.Delete(wav); }
    }

    [Fact]
    public async Task OpenAi_Transcribe_EmptyResultIsReturnedNotThrown()
    {
        var wav = Path.Combine(Path.GetTempPath(), "gm-test-" + Guid.NewGuid().ToString("N") + ".wav");
        File.WriteAllBytes(wav, new byte[] { 1 });
        try
        {
            var http = new FakeHttp().Reply(HttpStatusCode.OK, """{"text":""}""");
            var res = await OpenAi(http).TranscribeAsync(wav, default);
            Assert.Equal("", res.Text);
        }
        finally { File.Delete(wav); }
    }

    [Fact]
    public void CleanJson_RemovesFenceAndSurroundingText()
    {
        Assert.Equal("{\"a\":1}", RemoteModelClient.CleanJson("```json\n{\"a\":1}\n```"));
        Assert.Equal("{\"a\":1}", RemoteModelClient.CleanJson("הנה התוצאה: {\"a\":1} בהצלחה"));
    }
}

public class DocxWriterTests
{
    static readonly XNamespace W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";

    static XDocument Document(byte[] docx)
    {
        using var zip = new ZipArchive(new MemoryStream(docx), ZipArchiveMode.Read);
        using var s = zip.GetEntry("word/document.xml")!.Open();
        return XDocument.Load(s);
    }

    static List<XElement> Paragraphs(XDocument d) => d.Descendants(W + "p").ToList();
    static string TextOf(XElement p) => string.Concat(p.Descendants(W + "t").Select(t => t.Value));
    static string StyleOf(XElement p) => (string?)p.Element(W + "pPr")?.Element(W + "pStyle")?.Attribute(W + "val") ?? "";

    [Fact]
    public void Build_ProducesAValidPackageWithAllParts()
    {
        var bytes = DocxWriter.Build("כותרת", "פסקה אחת.");
        using var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
        var names = zip.Entries.Select(e => e.FullName).ToList();
        Assert.Contains("[Content_Types].xml", names);
        Assert.Contains("_rels/.rels", names);
        Assert.Contains("word/document.xml", names);
        Assert.Contains("word/_rels/document.xml.rels", names);
        Assert.Contains("word/styles.xml", names);
    }

    [Fact]
    public void Build_HeadingsBecomeWordHeadings_AndParagraphsStayInOrder()
    {
        var text = "# פרק ראשון\n\n## נושא\n\nפסקה ראשונה.\nפסקה שנייה.\n\n### תת נושא";
        var ps = Paragraphs(Document(DocxWriter.Build(null, text)));

        Assert.Equal(new[] { "Heading1", "Heading2", "", "", "Heading3" }, ps.Select(StyleOf).ToArray());
        Assert.Equal(new[] { "פרק ראשון", "נושא", "פסקה ראשונה.", "פסקה שנייה.", "תת נושא" }, ps.Select(TextOf).ToArray());
    }

    [Fact]
    public void Build_EveryParagraphIsRightToLeft_AndTitleComesFirst()
    {
        var ps = Paragraphs(Document(DocxWriter.Build("שם המסמך", "שלום\nעולם")));
        Assert.Equal("Title", StyleOf(ps[0]));
        Assert.Equal("שם המסמך", TextOf(ps[0]));
        Assert.All(ps, p => Assert.NotNull(p.Element(W + "pPr")!.Element(W + "bidi")));
        Assert.All(ps.SelectMany(p => p.Elements(W + "r")), r => Assert.NotNull(r.Element(W + "rPr")!.Element(W + "rtl")));
    }

    [Fact]
    public void Build_ReviewMarkersAreHighlighted_AndTextIsKeptWhole()
    {
        var ps = Paragraphs(Document(DocxWriter.Build(null, "הוא אמר דבר [?] ואז הלך [לא ברור] הביתה.")));
        var p = Assert.Single(ps);
        Assert.Equal("הוא אמר דבר [?] ואז הלך [לא ברור] הביתה.", TextOf(p));
        var highlighted = p.Elements(W + "r").Where(r => r.Element(W + "rPr")!.Element(W + "highlight") != null).Select(r => r.Value).ToArray();
        Assert.Equal(new[] { "[?]", "[לא ברור]" }, highlighted);
    }

    [Fact]
    public void Build_SpeakerLabelIsBold()
    {
        var p = Assert.Single(Paragraphs(Document(DocxWriter.Build(null, "דובר 1: שלום לכולם"))));
        var runs = p.Elements(W + "r").ToList();
        Assert.Equal("דובר 1:", runs[0].Value);
        Assert.NotNull(runs[0].Element(W + "rPr")!.Element(W + "b"));
        Assert.Null(runs[1].Element(W + "rPr")!.Element(W + "b"));
    }

    [Fact]
    public void Build_EscapesXml_AndDropsControlCharacters()
    {
        var p = Assert.Single(Paragraphs(Document(DocxWriter.Build(null, "a < b & c > d \u0001\u0008 סוף"))));
        Assert.Equal("a < b & c > d  סוף", TextOf(p));
    }

    [Fact]
    public void Build_NoteIsAppendedAtTheEnd()
    {
        var ps = Paragraphs(Document(DocxWriter.Build(null, "גוף", "חלק 2 קצר מהמקור")));
        Assert.Equal("Note", StyleOf(ps[^1]));
        Assert.Contains("חלק 2 קצר מהמקור", TextOf(ps[^1]));
    }

    [Fact]
    public void SaveDocx_WritesNextToTheSourceFile_WithAFreshName()
    {
        var dir = Path.Combine(Path.GetTempPath(), "gm-docx-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var source = Path.Combine(dir, "שיעור.mp3");
            var result = new JobResult(JobKind.Transcribe, "שיעור.mp3", "", "טקסט פשוט", null, Title: "טקסט בסגנון ספר", MarkdownText: "## כותרת\n\nגוף [?]");
            var a = HtmlPages.SaveDocx(source, "בסגנון ספר", result);
            var b = HtmlPages.SaveDocx(source, "בסגנון ספר", result);

            Assert.Equal(".docx", Path.GetExtension(a));
            Assert.NotEqual(a, b);
            Assert.True(File.Exists(a) && File.Exists(b));
            var ps = Paragraphs(Document(File.ReadAllBytes(a)));
            Assert.Contains(ps, p => StyleOf(p) == "Heading2" && TextOf(p) == "כותרת"); // MarkdownText wins over PlainText
        }
        finally { Directory.Delete(dir, true); }
    }
}

public class ProviderConfigTests
{
    static string TempFile() => Path.Combine(Path.GetTempPath(), "gm-cfg-" + Guid.NewGuid().ToString("N") + ".json");

    [Fact]
    public void Keys_AreStoredEncrypted_AndRoundTrip()
    {
        var path = TempFile();
        try
        {
            new AppConfig { ApiKey = "gem-key-111", ClaudeKey = "claude-key-222", OpenAiKey = "openai-key-333", TextProvider = "claude", TranscriptionProvider = "openai" }.Save(path);
            var raw = File.ReadAllText(path);
            Assert.DoesNotContain("gem-key-111", raw);
            Assert.DoesNotContain("claude-key-222", raw);
            Assert.DoesNotContain("openai-key-333", raw);

            var back = AppConfig.Load(path);
            Assert.Equal("gem-key-111", back.ApiKey);
            Assert.Equal("claude-key-222", back.ClaudeKey);
            Assert.Equal("openai-key-333", back.OpenAiKey);
            Assert.Equal("claude", back.TextProvider);
            Assert.Equal("openai", back.TranscriptionProvider);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void PlainKeysInTheFile_AreEncryptedOnLoad_WithoutLosingOtherKeys()
    {
        var path = TempFile();
        try
        {
            // A saved Gemini key (encrypted) plus a Claude key typed by hand in plain text.
            new AppConfig { ApiKey = "gem-key-111", OpenAiKey = "openai-key-333" }.Save(path);
            var node = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            node["claudeKey"] = "hand-typed-claude";
            File.WriteAllText(path, node.ToJsonString());

            var loaded = AppConfig.Load(path);
            Assert.Equal("hand-typed-claude", loaded.ClaudeKey);

            var raw = File.ReadAllText(path);
            Assert.DoesNotContain("hand-typed-claude", raw);
            var again = AppConfig.Load(path);
            Assert.Equal("gem-key-111", again.ApiKey);
            Assert.Equal("hand-typed-claude", again.ClaudeKey);
            Assert.Equal("openai-key-333", again.OpenAiKey);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void UnknownProviders_FallBackToGemini_AndEmptyModelsGetDefaults()
    {
        var path = TempFile();
        try
        {
            File.WriteAllText(path, """{"textProvider":"bogus","transcriptionProvider":"claude","claudeModel":"","openAiModel":" "}""");
            var cfg = AppConfig.Load(path);
            Assert.Equal("gemini", cfg.TextProvider);
            Assert.Equal("gemini", cfg.TranscriptionProvider); // Claude cannot transcribe audio
            Assert.Equal(ClaudeClient.DefaultModel, cfg.ClaudeModel);
            Assert.Equal(OpenAiClient.DefaultModel, cfg.OpenAiModel);
            Assert.Equal(OpenAiClient.DefaultTranscribeModel, cfg.OpenAiTranscribeModel);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void OldConfigWithoutProviderFields_KeepsWorkingWithGemini()
    {
        var path = TempFile();
        try
        {
            File.WriteAllText(path, """{"apiKey":"old-plain-key","model":"auto"}""");
            var cfg = AppConfig.Load(path);
            Assert.Equal("old-plain-key", cfg.ApiKey);
            Assert.Equal("gemini", cfg.TextProvider);
            Assert.Equal("gemini", cfg.TranscriptionProvider);
            Assert.Equal("", cfg.BookPrompt);
        }
        finally { File.Delete(path); }
    }
}

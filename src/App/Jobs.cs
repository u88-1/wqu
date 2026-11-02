using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using System.Xml.Linq;

namespace GeminiMenu;

public enum JobKind { Transcribe, Summarize }

public sealed record JobResult(JobKind Kind, string FileName, string BodyHtml, string PlainText, string? Note);

public static class Jobs
{
    public static readonly string[] AudioExts = { ".wav", ".mp3", ".m4a", ".ogg", ".amr", ".flac", ".aac", ".wma", ".opus" };
    public static readonly string[] DocExts = { ".pdf", ".txt", ".docx", ".md" };

    /// <summary>Converts m4a/wma/amr to a temp wav. Set by the Windows app (NAudio). Returns null on failure.</summary>
    public static Func<string, string?>? AudioConverter { get; set; }

    const int MaxChars = 3_000_000;
    const long InlinePdfLimit = 12L * 1024 * 1024;

    static Jobs() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    public static async Task<JobResult> RunAsync(JobKind kind, string file, GeminiClient client, Action<string> status, CancellationToken ct)
    {
        if (!File.Exists(file)) throw new AppException(ErrKind.FileProblem, "הקובץ לא נמצא: " + file);
        var ext = Path.GetExtension(file).ToLowerInvariant();
        var name = Path.GetFileName(file);

        if (kind == JobKind.Transcribe)
        {
            if (!AudioExts.Contains(ext)) throw new AppException(ErrKind.Unsupported, "סוג הקובץ אינו קובץ שמע נתמך: " + ext);
            return await TranscribeAsync(file, ext, name, client, status, ct).ConfigureAwait(false);
        }
        if (!DocExts.Contains(ext)) throw new AppException(ErrKind.Unsupported, "סוג הקובץ אינו נתמך לסיכום: " + ext);
        return await SummarizeAsync(file, ext, name, client, status, ct).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------ transcription

    const string TranscribePrompt =
        "תמלל את ההקלטה המצורפת במלואה, מילה במילה, בשפה שבה נאמרו הדברים (דיבור בעברית יתומלל בעברית). " +
        "אל תסכם, אל תתרגם ואל תוסיף הערות משלך. " +
        "אם יש כמה דוברים, התחל כל תור דיבור בשורה חדשה עם התווית 'דובר 1:', 'דובר 2:' וכן הלאה. " +
        "קטע שאינו ברור סמן [לא ברור]. אם אין בהקלטה דיבור כלל, כתוב רק: [לא זוהה דיבור]. " +
        "החזר טקסט פשוט בלבד, בלי עיצוב, כותרות או הקדמה.";

    static string AudioMime(string ext) => ext switch
    {
        ".wav" => "audio/wav",
        ".mp3" => "audio/mp3",
        ".aac" => "audio/aac",
        ".ogg" or ".opus" => "audio/ogg",
        ".flac" => "audio/flac",
        ".m4a" => "audio/mp4",
        ".wma" => "audio/x-ms-wma",
        ".amr" => "audio/amr",
        _ => "application/octet-stream",
    };

    static async Task<JobResult> TranscribeAsync(string file, string ext, string name, GeminiClient client, Action<string> status, CancellationToken ct)
    {
        string upPath = file;
        string mime = AudioMime(ext);
        string? temp = null;

        if ((ext is ".m4a" or ".wma" or ".amr") && AudioConverter != null)
        {
            status("ממיר את ההקלטה לפורמט נתמך...");
            temp = AudioConverter(file);
            if (temp != null) { upPath = temp; mime = "audio/wav"; }
        }

        UploadedFile? up = null;
        try
        {
            status("מעלה את ההקלטה ל-Gemini...");
            up = await client.UploadAsync(upPath, mime, ct).ConfigureAwait(false);

            status("מתמלל... (הקלטות ארוכות לוקחות זמן)");
            var parts = new JsonArray
            {
                new JsonObject { ["fileData"] = new JsonObject { ["mimeType"] = up.MimeType, ["fileUri"] = up.Uri } },
                new JsonObject { ["text"] = TranscribePrompt },
            };
            var cfg = new JsonObject { ["temperature"] = 0, ["maxOutputTokens"] = 65536 };
            var res = await client.GenerateAsync(parts, cfg, disableThinkingWhenPossible: true, ct).ConfigureAwait(false);

            string? note = res.FinishReason == "MAX_TOKENS" ? "ההקלטה ארוכה מאוד, והתמלול נקטע בסופו בגלל מגבלת אורך של Gemini." : null;
            return new JobResult(JobKind.Transcribe, name, HtmlPages.TranscriptBody(res.Text), res.Text, note);
        }
        finally
        {
            if (up != null) await client.DeleteFileAsync(up).ConfigureAwait(false);
            if (temp != null) try { File.Delete(temp); } catch { }
        }
    }

    // ------------------------------------------------------------------ summarization

    const string SummaryPrompt =
        "אתה מסכם מסמכים. קרא את המסמך וכתוב סיכום בעברית (גם אם המסמך בשפה אחרת). " +
        "oneLine: משפט אחד קצר שמסביר על מה המסמך. " +
        "mainPoints: הנקודות העיקריות במסמך, בין 3 ל-10 נקודות קצרות וברורות. " +
        "actions: פעולות שהמסמך מבקש או מחייב לבצע, ומי אחראי עליהן אם נכתב (מערך ריק אם אין). " +
        "dates: תאריכים, מועדים ומועדי יעד שמופיעים במסמך, כל פריט עם ההקשר שלו (מערך ריק אם אין). " +
        "amounts: סכומי כסף, מחירים וכמויות חשובות, כל פריט עם ההקשר שלו ועם המטבע (מערך ריק אם אין). " +
        "אל תמציא מידע שאינו מופיע במסמך.";

    static JsonObject SummarySchema()
    {
        JsonObject Arr() => new() { ["type"] = "ARRAY", ["items"] = new JsonObject { ["type"] = "STRING" } };
        return new JsonObject
        {
            ["type"] = "OBJECT",
            ["properties"] = new JsonObject
            {
                ["oneLine"] = new JsonObject { ["type"] = "STRING" },
                ["mainPoints"] = Arr(),
                ["actions"] = Arr(),
                ["dates"] = Arr(),
                ["amounts"] = Arr(),
            },
            ["required"] = new JsonArray("oneLine", "mainPoints"),
        };
    }

    static async Task<JobResult> SummarizeAsync(string file, string ext, string name, GeminiClient client, Action<string> status, CancellationToken ct)
    {
        var parts = new JsonArray { new JsonObject { ["text"] = SummaryPrompt } };
        UploadedFile? up = null;
        string? note = null;
        try
        {
            if (ext == ".pdf")
            {
                var len = new FileInfo(file).Length;
                if (len == 0) throw new AppException(ErrKind.FileProblem, "הקובץ ריק");
                if (len <= InlinePdfLimit)
                {
                    status("קורא את המסמך...");
                    parts.Add(new JsonObject { ["inlineData"] = new JsonObject { ["mimeType"] = "application/pdf", ["data"] = Convert.ToBase64String(await File.ReadAllBytesAsync(file, ct).ConfigureAwait(false)) } });
                }
                else
                {
                    status("מעלה את המסמך ל-Gemini...");
                    up = await client.UploadAsync(file, "application/pdf", ct).ConfigureAwait(false);
                    parts.Add(new JsonObject { ["fileData"] = new JsonObject { ["mimeType"] = up.MimeType, ["fileUri"] = up.Uri } });
                }
            }
            else
            {
                status("קורא את המסמך...");
                var text = ext == ".docx" ? ReadDocx(file) : ReadText(file);
                if (string.IsNullOrWhiteSpace(text)) throw new AppException(ErrKind.FileProblem, "לא נמצא טקסט במסמך");
                if (text.Length > MaxChars)
                {
                    text = text[..MaxChars];
                    note = "המסמך ארוך מאוד, ולכן הסיכום מבוסס על החלק הראשון שלו בלבד.";
                }
                parts.Add(new JsonObject { ["text"] = "המסמך:\n\n" + text });
            }

            status("מסכם...");
            var cfg = new JsonObject
            {
                ["temperature"] = 0.2,
                ["maxOutputTokens"] = 8192,
                ["responseMimeType"] = "application/json",
                ["responseSchema"] = SummarySchema(),
            };
            var res = await client.GenerateAsync(parts, cfg, disableThinkingWhenPossible: false, ct).ConfigureAwait(false);

            SummaryData data;
            try { data = SummaryData.Parse(res.Text); }
            catch (Exception ex)
            {
                throw new AppException(ErrKind.Blocked, "Gemini החזיר סיכום במבנה לא צפוי. נסה שוב.", technical: ex.Message + "\n" + res.Text, inner: ex);
            }
            return new JobResult(JobKind.Summarize, name, HtmlPages.SummaryBody(data), data.ToPlainText(name), note);
        }
        finally
        {
            if (up != null) await client.DeleteFileAsync(up).ConfigureAwait(false);
        }
    }

    // ------------------------------------------------------------------ readers

    public static string ReadText(string path)
    {
        var bytes = File.ReadAllBytes(path);
        try
        {
            using var ms = new MemoryStream(bytes);
            using var sr = new StreamReader(ms, new UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: true);
            return sr.ReadToEnd();
        }
        catch (DecoderFallbackException)
        {
            // Older Hebrew text files are usually Windows-1255.
            return Encoding.GetEncoding(1255).GetString(bytes);
        }
    }

    public static string ReadDocx(string path)
    {
        try
        {
            using var zip = ZipFile.OpenRead(path);
            var entry = zip.GetEntry("word/document.xml") ?? throw new AppException(ErrKind.FileProblem, "זה אינו קובץ Word תקין (לא נמצא word/document.xml)");
            XNamespace w = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
            XDocument doc;
            using (var s = entry.Open()) doc = XDocument.Load(s);
            var sb = new StringBuilder();
            foreach (var p in doc.Descendants(w + "p"))
            {
                foreach (var el in p.Descendants())
                {
                    if (el.Name == w + "t") sb.Append(el.Value);
                    else if (el.Name == w + "tab") sb.Append('\t');
                    else if (el.Name == w + "br" || el.Name == w + "cr") sb.Append('\n');
                }
                sb.Append('\n');
            }
            return sb.ToString();
        }
        catch (InvalidDataException ex)
        {
            throw new AppException(ErrKind.FileProblem, "קובץ ה-Word פגום או מוגן בסיסמה ולא ניתן לקרוא אותו", technical: ex.Message, inner: ex);
        }
    }
}

public sealed class SummaryData
{
    public string OneLine { get; init; } = "";
    public List<string> MainPoints { get; init; } = new();
    public List<string> Actions { get; init; } = new();
    public List<string> Dates { get; init; } = new();
    public List<string> Amounts { get; init; } = new();

    static List<string> Strs(JsonNode? n) =>
        n is JsonArray a ? a.Select(x => ((string?)x ?? "").Trim()).Where(x => x.Length > 0).ToList() : new();

    public static SummaryData Parse(string json)
    {
        var s = json.Trim();
        if (s.StartsWith("```")) s = s.Trim('`').Replace("json", "", StringComparison.OrdinalIgnoreCase).Trim();
        var n = JsonNode.Parse(s) ?? throw new FormatException("empty");
        return new SummaryData
        {
            OneLine = ((string?)n["oneLine"] ?? "").Trim(),
            MainPoints = Strs(n["mainPoints"]),
            Actions = Strs(n["actions"]),
            Dates = Strs(n["dates"]),
            Amounts = Strs(n["amounts"]),
        };
    }

    public string ToPlainText(string fileName)
    {
        var sb = new StringBuilder();
        sb.AppendLine("סיכום: " + fileName).AppendLine();
        sb.AppendLine("על מה המסמך:").AppendLine(OneLine).AppendLine();
        void Section(string title, List<string> items)
        {
            if (items.Count == 0) return;
            sb.AppendLine(title);
            foreach (var i in items) sb.AppendLine("• " + i);
            sb.AppendLine();
        }
        Section("הנקודות העיקריות:", MainPoints);
        Section("פעולות שיש לבצע:", Actions);
        Section("תאריכים ומועדים:", Dates);
        Section("סכומים:", Amounts);
        return sb.ToString().TrimEnd();
    }
}

using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace GeminiMenu;

public enum JobKind { Transcribe, Summarize }

public sealed record JobResult(JobKind Kind, string FileName, string BodyHtml, string PlainText, string? Note, TimeSpan? ProcessingTime = null);

public static class Jobs
{
    public static readonly string[] AudioExts = { ".wav", ".mp3", ".m4a", ".ogg", ".amr", ".flac", ".aac", ".wma", ".opus" };
    public static readonly string[] VideoExts = { ".mp4" };
    public static readonly string[] TranscriptionExts = AudioExts.Concat(VideoExts).ToArray();
    public static readonly string[] DocExts = { ".pdf", ".txt", ".docx", ".md" };

    /// <summary>Converts m4a/wma/amr to a temp wav. Set by the Windows app (NAudio). Returns null on failure.</summary>
    public static Func<string, string?>? AudioConverter { get; set; }

    const int MaxChars = 3_000_000;
    const long InlinePdfLimit = 12L * 1024 * 1024;
    /// <summary>If the duration cannot be read, files bigger than this are still split into chunks.</summary>
    const long UnknownDurationChunkBytes = 8L * 1024 * 1024;
    const int MaxTranscriptTokens = 16000;

    static Jobs() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    public static async Task<JobResult> RunAsync(JobKind kind, string file, GeminiClient client, Action<string> status, CancellationToken ct)
    {
        if (!File.Exists(file)) throw new AppException(ErrKind.FileProblem, "הקובץ לא נמצא: " + file);
        var ext = Path.GetExtension(file).ToLowerInvariant();
        var name = Path.GetFileName(file);

        if (kind == JobKind.Transcribe)
        {
            if (!TranscriptionExts.Contains(ext)) throw new AppException(ErrKind.Unsupported, "סוג הקובץ אינו קובץ שמע או וידאו נתמך: " + ext);
            return await TranscribeAsync(file, ext, name, client, status, ct).ConfigureAwait(false);
        }
        if (!DocExts.Contains(ext)) throw new AppException(ErrKind.Unsupported, "סוג הקובץ אינו נתמך לסיכום: " + ext);
        return await SummarizeAsync(file, ext, name, client, status, ct).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------ transcription

    const string TranscribePrompt =
        "תמלל את הדיבור שבהקלטה או בסרטון המצורפים במלואו, מילה במילה, בשפה שבה נאמרו הדברים (דיבור בעברית יתומלל בעברית). " +
        "אל תסכם, אל תתרגם ואל תוסיף הערות משלך. אין מגבלת אורך: תמלל עד סוף ההקלטה ואל תקצר ואל תדלג. " +
        "אם יש כמה דוברים, התחל כל תור דיבור בשורה חדשה עם התווית 'דובר 1:', 'דובר 2:' וכן הלאה. " +
        "קטע שאינו ברור סמן [לא ברור]. אם אין בהקלטה או בסרטון דיבור כלל, כתוב רק: [לא זוהה דיבור]. " +
        "החזר טקסט פשוט בלבד, בלי עיצוב, כותרות או הקדמה.";

    const string ChunkPrompt =
        "הקובץ המצורף הוא קטע מתוך הקלטה ארוכה, וייתכן שחלקים בתחילתו או בסופו חופפים לקטעים סמוכים. " +
        "תמלל במלואו את כל הדיבור שנשמע בקטע המצורף, כולל הדיבור שבחפיפה. אל תשמיט מילים בתחילת הקטע או בסופו. " +
        "אין מגבלת אורך: תמלל כל מילה עד סוף הקטע. אל תקצר, אל תדלג, ואל תכתוב שהקובץ ארוך מדי או שתמשיך בהמשך. " +
        "תמלל מילה במילה ובשפה שבה נאמרו הדברים, והשתמש בתוויות דוברים ('דובר 1:', 'דובר 2:' וכן הלאה). " +
        "קטע שאינו ברור סמן [לא ברור]. אם אין דיבור כלל, כתוב רק: [לא זוהה דיבור]. " +
        "החזר טקסט פשוט בלבד, בלי עיצוב, כותרות או הקדמה.";

    static readonly Regex TranscriptWord = new(@"\S+", RegexOptions.Compiled);
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
        TimeSpan duration;
        try { duration = AudioConvert.GetDuration(file); }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or NotSupportedException or ArgumentException or System.Runtime.InteropServices.COMException)
        {
            duration = TimeSpan.Zero;
        }

        bool unknownButBig = duration == TimeSpan.Zero && new FileInfo(file).Length > UnknownDurationChunkBytes;
        if (duration > TimeSpan.FromSeconds(AudioConvert.ChunkSeconds) || unknownButBig)
            return await TranscribeChunksAsync(file, name, client, status, ct).ConfigureAwait(false);

        string upPath = file;
        string mime = ext == ".mp4" ? "video/mp4" : AudioMime(ext);
        string? temp = null;

        // MP4: send only the audio track (about 5 MB for 3 minutes) instead of the whole video: far faster to upload and process.
        if ((ext is ".m4a" or ".wma" or ".amr" or ".mp4") && AudioConverter != null)
        {
            status(ext == ".mp4" ? "מחלץ את השמע מהסרטון..." : "ממיר את ההקלטה לפורמט נתמך...");
            temp = AudioConverter(file);
            if (temp != null) { upPath = temp; mime = "audio/wav"; }
        }

        UploadedFile? up = null;
        try
        {
            status("מעלה את המדיה ל-Gemini...");
            up = await client.UploadAsync(upPath, mime, ct).ConfigureAwait(false);

            status("מתמלל... (הקלטות ארוכות לוקחות זמן)");
            var parts = new JsonArray
            {
                new JsonObject { ["fileData"] = new JsonObject { ["mimeType"] = up.MimeType, ["fileUri"] = up.Uri } },
                new JsonObject { ["text"] = TranscribePrompt },
            };
            var cfg = new JsonObject { ["maxOutputTokens"] = MaxTranscriptTokens };
            var res = await client.GenerateAsync(parts, cfg, disableThinkingWhenPossible: true, ct).ConfigureAwait(false);
            if (res.FinishReason == "MAX_TOKENS" || Evaluate(res.Text, duration) != ChunkVerdict.Ok)
            {
                if (up != null) { await client.DeleteFileAsync(up).ConfigureAwait(false); up = null; }
                if (temp != null) { try { File.Delete(temp); } catch { } temp = null; }
                status("התמלול נראה חלקי או נקטע. מחלק לקטעים קצרים יותר ומנסה שוב...");
                return await TranscribeChunksAsync(file, name, client, status, ct).ConfigureAwait(false);
            }

            return new JobResult(JobKind.Transcribe, name, HtmlPages.TranscriptBody(res.Text), res.Text, null);
        }
        finally
        {
            if (up != null) await client.DeleteFileAsync(up).ConfigureAwait(false);
            if (temp != null) try { File.Delete(temp); } catch { }
        }
    }

    static async Task<JobResult> TranscribeChunksAsync(string file, string name, GeminiClient client, Action<string> status, CancellationToken ct)
    {
        status("מכין קטעים לתמלול...");
        AudioChunkSet chunks;
        try { chunks = AudioConvert.CreateChunks(file); }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or NotSupportedException or ArgumentException or System.Runtime.InteropServices.COMException)
        {
            throw new AppException(ErrKind.FileProblem,
                "לא ניתן לפצל את קובץ המדיה במחשב הזה. נסה לשמור אותו כ-MP3 או WAV ולתמלל שוב.",
                technical: ex.Message, inner: ex);
        }
        using var chunkSet = chunks;
        if (chunkSet.Chunks.Count == 0)
            throw new AppException(ErrKind.FileProblem, "לא ניתן לחלק את המדיה לקטעי תמלול.");
        var pending = new LinkedList<AudioChunk>(chunkSet.Chunks);
        var transcript = new StringBuilder();
        int total = pending.Count;
        int completed = 0;
        bool split = false;
        var retries = new Dictionary<string, int>();
        var gaps = new List<string>();
        string? abortReason = null;

        void Append(string text) => transcript.Clear().Append(MergeChunkTranscripts(transcript.ToString(), text));
        void Gap(AudioChunk c)
        {
            var range = $"{Fmt(c.AbsStart)}–{Fmt(c.AbsEnd)}";
            gaps.Add(range);
            Append($"[⚠ הקטע {range} לא תומלל במלואו]");
        }
        void SplitAndQueue(AudioChunk c)
        {
            split = true;
            var smaller = chunkSet.Split(c);
            total += smaller.Count - 1;
            for (int i = smaller.Count - 1; i >= 0; i--) pending.AddFirst(smaller[i]);
        }

        while (pending.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var chunk = pending.First!.Value;
            pending.RemoveFirst();
            status($"מתמלל קטע {completed + 1} מתוך {total} ({Fmt(chunk.AbsStart)}–{Fmt(chunk.AbsEnd)})...");

            GenResult result;
            try
            {
                result = await TranscribeChunkAsync(chunk.Path, ChunkPrompt, client, ct).ConfigureAwait(false);
            }
            catch (AppException e) when (transcript.Length > 0 && e.Kind is not (ErrKind.NoKey or ErrKind.BadKey))
            {
                // Keep everything transcribed so far instead of throwing it away.
                abortReason = e.Message;
                Append($"[⚠ התמלול הופסק כאן ({Fmt(chunk.AbsStart)}): {e.Message}]");
                Log.Write("transcription aborted, partial result kept: " + (e.Technical ?? e.Message));
                break;
            }

            if (result.FinishReason == "MAX_TOKENS")
            {
                if (chunkSet.CanSplit(chunk)) SplitAndQueue(chunk);
                else { Gap(chunk); completed++; }
                continue;
            }

            var verdict = Evaluate(result.Text, chunk.CoreDuration);
            if (verdict != ChunkVerdict.Ok)
            {
                Log.Write($"chunk {Fmt(chunk.AbsStart)} looks {verdict}: {TranscriptWord.Matches(result.Text).Count} words in {chunk.CoreDuration.TotalSeconds:0}s");
                int tries = retries.GetValueOrDefault(chunk.Path);
                if (tries < 1)
                {
                    retries[chunk.Path] = tries + 1;
                    status("התמלול של הקטע נראה חלקי. מנסה שוב...");
                    pending.AddFirst(chunk);
                    continue;
                }
                if (verdict == ChunkVerdict.Excuse)
                {
                    if (chunkSet.CanSplit(chunk)) SplitAndQueue(chunk);
                    else { Gap(chunk); completed++; }
                    continue;
                }
                // Short but no excuse text, even after a retry: probably real silence/music. Keep it.
            }

            Append(result.Text);
            completed++;
        }

        var text = transcript.ToString().Trim();
        var notes = new List<string>
        {
            split
                ? "קטעים שנראו חלקיים או נקטעו חולקו אוטומטית לקטעים קצרים יותר."
                : $"המדיה חולקה ל-{chunkSet.Chunks.Count} קטעים עם חפיפה קצרה כדי לשמור על רצף הדיבור.",
        };
        if (gaps.Count > 0) notes.Add("קטעים שלא תומללו במלואם: " + string.Join(", ", gaps) + ".");
        if (abortReason != null) notes.Add("התמלול הופסק באמצע (" + abortReason + "). מה שתומלל עד כה נשמר.");
        return new JobResult(JobKind.Transcribe, name, HtmlPages.TranscriptBody(text), text, string.Join(" ", notes));
    }

    // ------------------------------------------------------------------ output sanity check

    internal enum ChunkVerdict { Ok, Short, Excuse }

    // Typical Hebrew speech is ~100-170 words/minute. Gemini sometimes answers with an excuse
    // ("the file is too long...") or stops early instead of transcribing; detect that.
    const double MinWordsPerMinute = 15;
    const double ExcuseWordsPerMinute = 60;
    static readonly Regex ExcuseRegex = new(
        @"ארוכ[הת]? מדי|אורך (ה)?(קובץ|הקלטה|קטע)|בשל (ה)?אורך|מחמת (ה)?אורך|לא (ניתן|אוכל|אצליח|אספיק) (ל)?תמלל|לא תומלל|אמשיך|המשך (ה)?תמלול|להמשיך (ב)?תמלול|ניתן להמשיך",
        RegexOptions.Compiled);

    internal static ChunkVerdict Evaluate(string text, TimeSpan duration)
    {
        var t = text.Trim();
        if (t.StartsWith("[לא זוהה דיבור]") && t.Length < 40) return ChunkVerdict.Ok;
        if (duration < TimeSpan.FromSeconds(45)) return ChunkVerdict.Ok; // too short to judge
        double wpm = TranscriptWord.Matches(t).Count / duration.TotalMinutes;
        if (wpm < ExcuseWordsPerMinute && ExcuseRegex.IsMatch(t)) return ChunkVerdict.Excuse;
        if (wpm < MinWordsPerMinute) return ChunkVerdict.Short;
        return ChunkVerdict.Ok;
    }

    static string Fmt(TimeSpan t) => $"{(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00}";

    static string MergeChunkTranscripts(string previous, string next)
    {
        if (string.IsNullOrWhiteSpace(previous)) return next.Trim();
        if (string.IsNullOrWhiteSpace(next)) return previous;

        var left = TranscriptWord.Matches(previous).Cast<Match>().ToArray();
        var right = TranscriptWord.Matches(next).Cast<Match>().ToArray();
        int maxOverlap = Math.Min(80, Math.Min(left.Length, right.Length));
        for (int count = maxOverlap; count >= 3; count--)
        {
            bool matches = true;
            for (int i = 0; i < count; i++)
            {
                var leftWord = NormalizeTranscriptWord(left[left.Length - count + i].Value);
                var rightWord = NormalizeTranscriptWord(right[i].Value);
                if (leftWord.Length == 0 || !string.Equals(leftWord, rightWord, StringComparison.OrdinalIgnoreCase))
                {
                    matches = false;
                    break;
                }
            }

            if (matches)
            {
                int nextStart = right[count - 1].Index + right[count - 1].Length;
                while (nextStart < next.Length && char.IsWhiteSpace(next[nextStart])) nextStart++;
                var remainder = next[nextStart..].TrimStart();
                return remainder.Length == 0
                    ? previous.TrimEnd()
                    : previous.TrimEnd() + Environment.NewLine + remainder;
            }
        }

        return previous.TrimEnd() + Environment.NewLine + Environment.NewLine + next.TrimStart();
    }

    static string NormalizeTranscriptWord(string word) =>
        string.Concat(word.Where(char.IsLetterOrDigit));

    static async Task<GenResult> TranscribeChunkAsync(string path, string prompt, GeminiClient client, CancellationToken ct)
    {
        UploadedFile? up = null;
        try
        {
            up = await client.UploadAsync(path, "audio/wav", ct).ConfigureAwait(false);
            var parts = new JsonArray
            {
                new JsonObject { ["fileData"] = new JsonObject { ["mimeType"] = up.MimeType, ["fileUri"] = up.Uri } },
                new JsonObject { ["text"] = prompt },
            };
            var cfg = new JsonObject { ["maxOutputTokens"] = MaxTranscriptTokens };
            return await client.GenerateAsync(parts, cfg, disableThinkingWhenPossible: true, ct).ConfigureAwait(false);
        }
        finally
        {
            if (up != null) await client.DeleteFileAsync(up).ConfigureAwait(false);
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

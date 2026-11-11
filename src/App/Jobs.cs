using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace GeminiMenu;

public enum JobKind { Transcribe, Summarize }

public enum OutputMode
{
    DocumentKeyPoints,
    DocumentBrief,
    DocumentConclusions,
    DocumentTasksDatesAmounts,
    DocumentCustomQuestion,
    DocumentBookStyle,
    TranscriptExact,
    TranscriptClean,
    TranscriptBookStyle,
    TranscriptAndSummary,
    TranscriptSummaryOnly,
    TranscriptMeetingMinutes,
    TranscriptCustomInstruction,
}

public sealed record OutputSelection(OutputMode Mode, string CustomInstruction = "");

/// <summary>Per-run options taken from the settings window.</summary>
public sealed record JobOptions(bool Timestamps = true, int Parallel = 1, int ChunkSeconds = 180, string SummaryDetail = "normal")
{
    public static JobOptions From(AppConfig c) =>
        new(c.Timestamps, Math.Clamp(c.ParallelChunks, 1, 4), Math.Clamp(c.ChunkSeconds, 60, 300), c.SummaryDetail);
}

public sealed record JobResult(JobKind Kind, string FileName, string BodyHtml, string PlainText, string? Note, TimeSpan? ProcessingTime = null, string? Title = null, bool IsSummary = false, string? SupplementaryText = null, string? MarkdownText = null);

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

    /// <summary>Audio chunk to text, for engines other than Gemini (the Gemini prompt is not used by them).</summary>
    public delegate Task<GenResult> AudioTranscriber(string path, CancellationToken ct);

    internal delegate Task<GenResult> ChunkTranscribe(string path, string prompt, CancellationToken ct);

    /// <param name="client">Gemini. May be null when text goes to <paramref name="textClient"/> and audio to <paramref name="audioTranscriber"/>.</param>
    /// <param name="textClient">Edits and summarizes text. Null = Gemini.</param>
    /// <param name="audioTranscriber">Transcribes audio. Null = Gemini.</param>
    public static async Task<JobResult> RunAsync(JobKind kind, string file, GeminiClient? client, Action<string> status, CancellationToken ct, JobOptions? options = null, Func<OutputSelection>? getOutputSelection = null,
        IModelClient? textClient = null, AudioTranscriber? audioTranscriber = null)
    {
        options ??= new JobOptions();
        if (!File.Exists(file)) throw new AppException(ErrKind.FileProblem, "הקובץ לא נמצא: " + file);
        var ext = Path.GetExtension(file).ToLowerInvariant();
        var name = Path.GetFileName(file);

        if (kind == JobKind.Transcribe)
        {
            if (!TranscriptionExts.Contains(ext)) throw new AppException(ErrKind.Unsupported, "סוג הקובץ אינו קובץ שמע או וידאו נתמך: " + ext);
            return await TranscribeAsync(file, ext, name, client, status, ct, options, audioTranscriber).ConfigureAwait(false);
        }
        if (!DocExts.Contains(ext)) throw new AppException(ErrKind.Unsupported, "סוג הקובץ אינו נתמך לסיכום: " + ext);
        var docClient = textClient ?? client ?? throw new AppException(ErrKind.NoKey, "לא הוגדר מפתח API");
        return await SummarizeAsync(file, ext, name, docClient, status, ct, options,
            getOutputSelection ?? (() => new OutputSelection(OutputMode.DocumentKeyPoints))).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------ transcription

    static string TimestampRule(bool ts) => ts
        ? "בתחילת כל משפט או תור דיבור, ולפחות כל 10 עד 15 שניות, התחל שורה חדשה עם חותמת זמן בפורמט [דקות:שניות], יחסית לתחילת הקובץ המצורף, למשל: [03:25] דובר 1: ... "
        : "";

    static string TranscribePrompt(bool ts) =>
        "תמלל את הדיבור שבהקלטה או בסרטון המצורפים במלואו, מילה במילה, בשפה שבה נאמרו הדברים (דיבור בעברית יתומלל בעברית). " +
        "אל תסכם, אל תתרגם ואל תוסיף הערות משלך. אין מגבלת אורך: תמלל עד סוף ההקלטה ואל תקצר ואל תדלג. " +
        "אם יש כמה דוברים, התחל כל תור דיבור בשורה חדשה עם התווית 'דובר 1:', 'דובר 2:' וכן הלאה. " +
        TimestampRule(ts) +
        "קטע שאינו ברור סמן [לא ברור]. אם אין בהקלטה או בסרטון דיבור כלל, כתוב רק: [לא זוהה דיבור]. " +
        "החזר טקסט פשוט בלבד, בלי עיצוב, כותרות או הקדמה.";

    static string ChunkPrompt(bool ts, string context) =>
        "הקובץ המצורף הוא קטע מתוך הקלטה ארוכה, וייתכן שחלקים בתחילתו או בסופו חופפים לקטעים סמוכים. " +
        "תמלל במלואו את כל הדיבור שנשמע בקטע המצורף, כולל הדיבור שבחפיפה. אל תשמיט מילים בתחילת הקטע או בסופו. " +
        "אין מגבלת אורך: תמלל כל מילה עד סוף הקטע. אל תקצר, אל תדלג, ואל תכתוב שהקובץ ארוך מדי או שתמשיך בהמשך. " +
        "תמלל מילה במילה ובשפה שבה נאמרו הדברים, והשתמש בתוויות דוברים ('דובר 1:', 'דובר 2:' וכן הלאה). " +
        TimestampRule(ts) +
        (context.Length > 0
            ? "להמשכיות בלבד, אלו השורות האחרונות מהקטע הקודם. אל תתמלל אותן שוב, והשתמש באותן תוויות דוברים עבור אותם אנשים:\n<<<\n" + context + "\n>>>\n"
            : "") +
        "קטע שאינו ברור סמן [לא ברור]. אם אין דיבור כלל, כתוב רק: [לא זוהה דיבור]. " +
        "החזר טקסט פשוט בלבד, בלי עיצוב, כותרות או הקדמה.";

    static readonly Regex TranscriptWord = new(@"\S+", RegexOptions.Compiled);
    static readonly Regex TsRegex = new(@"\[(\d{1,3}):(\d{2})(?::(\d{2}))?\]", RegexOptions.Compiled);
    static readonly Regex TsToken = new(@"^\[\d{1,3}:\d{2}(?::\d{2})?\]$", RegexOptions.Compiled);

    /// <summary>Rewrites [mm:ss] / [h:mm:ss] stamps (relative to the chunk file) as absolute [HH:MM:SS].</summary>
    internal static string NormalizeTimestamps(string text, TimeSpan offset) =>
        TsRegex.Replace(text, m =>
        {
            int a = int.Parse(m.Groups[1].Value), b = int.Parse(m.Groups[2].Value);
            var t = m.Groups[3].Success
                ? TimeSpan.FromSeconds(a * 3600.0 + b * 60 + int.Parse(m.Groups[3].Value))
                : TimeSpan.FromSeconds(a * 60.0 + b);
            t += offset;
            return $"[{(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00}]";
        });

    /// <summary>Removes [HH:MM:SS] stamps (used when the user chooses to export without them).</summary>
    internal static string StripTimestamps(string text) =>
        Regex.Replace(TsRegex.Replace(text, ""), @"[ \t]{2,}", " ")
             .Replace("\n ", "\n").Trim();

    /// <summary>Last few lines of the transcript without timestamps: given to the next chunk so speaker labels stay consistent.</summary>
    internal static string ContextTail(string transcript)
    {
        var s = TsRegex.Replace(transcript ?? "", "").Trim();
        if (s.Length > 500)
        {
            s = s[^500..];
            int nl = s.IndexOf('\n'); // drop the cut-off first line
            if (nl > 0 && nl < 200) s = s[(nl + 1)..];
        }
        return s.Trim();
    }

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

    static async Task<JobResult> TranscribeAsync(string file, string ext, string name, GeminiClient? client, Action<string> status, CancellationToken ct, JobOptions o, AudioTranscriber? audioTranscriber = null)
    {
        if (audioTranscriber != null)
        {
            // Another engine: always work in short chunks (small uploads, and one stamp per chunk).
            async Task<GenResult> Other(string path, string prompt, CancellationToken c)
            {
                var r = await audioTranscriber(path, c).ConfigureAwait(false);
                return o.Timestamps && r.Text.Length > 0 ? r with { Text = "[00:00] " + r.Text } : r;
            }
            return await TranscribeChunksAsync(file, name, Other, status, ct, o).ConfigureAwait(false);
        }
        if (client == null) throw new AppException(ErrKind.NoKey, "לא הוגדר מפתח API של Gemini");
        TimeSpan duration;
        try { duration = AudioConvert.GetDuration(file); }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or NotSupportedException or ArgumentException or System.Runtime.InteropServices.COMException)
        {
            duration = TimeSpan.Zero;
        }

        bool unknownButBig = duration == TimeSpan.Zero && new FileInfo(file).Length > UnknownDurationChunkBytes;
        if (duration > TimeSpan.FromSeconds(o.ChunkSeconds) || unknownButBig)
            return await TranscribeChunksAsync(file, name, (p, pr, c) => TranscribeChunkAsync(p, pr, client, c), status, ct, o).ConfigureAwait(false);

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
                new JsonObject { ["text"] = TranscribePrompt(o.Timestamps) },
            };
            var cfg = new JsonObject { ["maxOutputTokens"] = MaxTranscriptTokens };
            var res = await client.GenerateAsync(parts, cfg, disableThinkingWhenPossible: true, ct).ConfigureAwait(false);
            if (res.FinishReason == "MAX_TOKENS" || Evaluate(res.Text, duration) != ChunkVerdict.Ok)
            {
                if (up != null) { await client.DeleteFileAsync(up).ConfigureAwait(false); up = null; }
                if (temp != null) { try { File.Delete(temp); } catch { } temp = null; }
                status("התמלול נראה חלקי או נקטע. מחלק לקטעים קצרים יותר ומנסה שוב...");
                return await TranscribeChunksAsync(file, name, (p, pr, c) => TranscribeChunkAsync(p, pr, client, c), status, ct, o).ConfigureAwait(false);
            }

            var text = o.Timestamps ? NormalizeTimestamps(res.Text, TimeSpan.Zero) : res.Text;
            return new JobResult(JobKind.Transcribe, name, HtmlPages.TranscriptBody(text), text, null);
        }
        finally
        {
            if (up != null) await client.DeleteFileAsync(up).ConfigureAwait(false);
            if (temp != null) try { File.Delete(temp); } catch { }
        }
    }

    static async Task<JobResult> TranscribeChunksAsync(string file, string name, ChunkTranscribe transcribe, Action<string> status, CancellationToken ct, JobOptions o)
    {
        status("מכין קטעים לתמלול...");
        AudioChunkSet chunkSet;
        try { chunkSet = AudioConvert.CreateChunks(file, o.ChunkSeconds); }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or NotSupportedException or ArgumentException or System.Runtime.InteropServices.COMException)
        {
            throw new AppException(ErrKind.FileProblem, "לא הצלחתי לקרוא את ההקלטה כדי לחלק אותה לקטעים.", technical: ex.Message, inner: ex);
        }

        using (chunkSet)
        {
            var sync = new object();
            using var gate = new SemaphoreSlim(Math.Clamp(o.Parallel, 1, 4));
            int total = chunkSet.Chunks.Count, completed = 0, successes = 0;
            bool split = false;
            string? fatal = null;
            AppException? firstError = null;
            var gaps = new List<string>();

            string GapText(AudioChunk c)
            {
                var range = $"{Fmt(c.AbsStart)}–{Fmt(c.AbsEnd)}";
                lock (sync) gaps.Add(range);
                return $"[⚠ הקטע {range} לא תומלל במלואו]";
            }

            async Task<string> SplitOrGap(AudioChunk c, string context)
            {
                if (!chunkSet.CanSplit(c)) { lock (sync) completed++; return GapText(c); }
                List<AudioChunk> halves;
                lock (sync) { split = true; total++; halves = chunkSet.Split(c); }
                var first = await Process(halves[0], context, 0).ConfigureAwait(false);
                var second = await Process(halves[1], ContextTail(MergeChunkTranscripts(context, first)), 0).ConfigureAwait(false);
                return MergeChunkTranscripts(first, second);
            }

            async Task<string> Process(AudioChunk c, string context, int attempt)
            {
                ct.ThrowIfCancellationRequested();
                bool stopped;
                lock (sync) stopped = fatal != null;
                if (stopped) { lock (sync) completed++; return GapText(c); }

                GenResult r;
                await gate.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    int shown;
                    lock (sync) shown = Math.Min(completed + 1, total);
                    status($"מתמלל קטע {shown} מתוך {total} ({Fmt(c.AbsStart)}–{Fmt(c.AbsEnd)})...");
                    r = await transcribe(c.Path, ChunkPrompt(o.Timestamps, context), ct).ConfigureAwait(false);
                }
                catch (AppException e) when (e.Kind is not (ErrKind.NoKey or ErrKind.BadKey))
                {
                    Log.Write($"chunk {Fmt(c.AbsStart)} failed: {e.Technical ?? e.Message}");
                    lock (sync)
                    {
                        firstError ??= e;
                        // Quota / overload / network problems will hit every following chunk too: stop starting new ones.
                        if (e.Kind is ErrKind.Quota or ErrKind.Overloaded or ErrKind.Network) fatal ??= e.Message;
                        completed++;
                    }
                    return GapText(c);
                }
                finally { gate.Release(); }

                if (r.FinishReason == "MAX_TOKENS") return await SplitOrGap(c, context).ConfigureAwait(false);

                var verdict = Evaluate(r.Text, c.CoreDuration);
                if (verdict != ChunkVerdict.Ok)
                {
                    Log.Write($"chunk {Fmt(c.AbsStart)} looks {verdict}: {TranscriptWord.Matches(r.Text).Count} words in {c.CoreDuration.TotalSeconds:0}s");
                    if (attempt < 1)
                    {
                        status("התמלול של הקטע נראה חלקי. מנסה שוב...");
                        return await Process(c, context, attempt + 1).ConfigureAwait(false);
                    }
                    if (verdict == ChunkVerdict.Excuse) return await SplitOrGap(c, context).ConfigureAwait(false);
                    // Short but no excuse text, even after a retry: probably real silence/music. Keep it.
                }

                lock (sync) { completed++; successes++; }
                return o.Timestamps ? NormalizeTimestamps(r.Text, c.AbsStart - c.CoreStart) : r.Text;
            }

            string merged = "";
            if (o.Parallel <= 1)
            {
                // In order: each chunk sees the end of the previous one, so speaker labels stay consistent.
                foreach (var c in chunkSet.Chunks.ToList())
                {
                    var text = await Process(c, ContextTail(merged), 0).ConfigureAwait(false);
                    merged = MergeChunkTranscripts(merged, text);
                }
            }
            else
            {
                var texts = await Task.WhenAll(chunkSet.Chunks.ToList().Select(c => Process(c, "", 0))).ConfigureAwait(false);
                foreach (var t in texts) merged = MergeChunkTranscripts(merged, t);
            }

            if (successes == 0)
                throw firstError ?? new AppException(ErrKind.Blocked, "לא הצלחתי לתמלל את ההקלטה.");

            var final = merged.Trim();
            var notes = new List<string>
            {
                split
                    ? "קטעים שנראו חלקיים או נקטעו חולקו אוטומטית לקטעים קצרים יותר."
                    : $"המדיה חולקה ל-{chunkSet.Chunks.Count} קטעים עם חפיפה קצרה כדי לשמור על רצף הדיבור.",
            };
            if (o.Parallel > 1) notes.Add("הקטעים תומללו במקביל, ולכן תוויות הדוברים עלולות להתחלף בין קטעים.");
            if (gaps.Count > 0) notes.Add("קטעים שלא תומללו במלואם: " + string.Join(", ", gaps) + ".");
            if (fatal != null) notes.Add("התמלול הופסק באמצע (" + fatal + "). מה שתומלל עד כה נשמר.");
            return new JobResult(JobKind.Transcribe, name, HtmlPages.TranscriptBody(final), final, string.Join(" ", notes));
        }
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
        var t = TsRegex.Replace(text, "").Trim();
        if (t.StartsWith("[לא זוהה דיבור]") && t.Length < 40) return ChunkVerdict.Ok;
        if (duration < TimeSpan.FromSeconds(45)) return ChunkVerdict.Ok; // too short to judge
        double wpm = TranscriptWord.Matches(t).Count / duration.TotalMinutes;
        if (wpm < ExcuseWordsPerMinute && ExcuseRegex.IsMatch(t)) return ChunkVerdict.Excuse;
        if (wpm < MinWordsPerMinute) return ChunkVerdict.Short;
        return ChunkVerdict.Ok;
    }

    static string Fmt(TimeSpan t) => $"{(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00}";

    internal static string MergeChunkTranscripts(string previous, string next)
    {
        if (string.IsNullOrWhiteSpace(previous)) return next.Trim();
        if (string.IsNullOrWhiteSpace(next)) return previous;

        // Timestamps differ between the two copies of the overlap, so they are ignored when matching words.
        var left = TranscriptWord.Matches(previous).Cast<Match>().Where(m => !TsToken.IsMatch(m.Value)).ToArray();
        var right = TranscriptWord.Matches(next).Cast<Match>().Where(m => !TsToken.IsMatch(m.Value)).ToArray();
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

    /// <summary>Documents longer than this are summarized in stages (piece by piece, then merged).</summary>
    const int StageChars = 200_000;
    const int PieceChars = 150_000;

    static string SummaryPrompt(string detail)
    {
        var points = detail switch
        {
            "short" => "בין 3 ל-5 נקודות קצרות מאוד",
            "detailed" => "בין 8 ל-20 נקודות מפורטות, כל אחת עם ההקשר והנימוקים שלה",
            _ => "בין 3 ל-10 נקודות קצרות וברורות",
        };
        return
            "אתה מסכם מסמכים. קרא את המסמך וכתוב סיכום בעברית (גם אם המסמך בשפה אחרת). " +
            "oneLine: משפט אחד קצר שמסביר על מה המסמך. " +
            $"mainPoints: הנקודות העיקריות במסמך, {points}. " +
            "actions: פעולות שהמסמך מבקש או מחייב לבצע, ומי אחראי עליהן אם נכתב (מערך ריק אם אין). " +
            "dates: תאריכים, מועדים ומועדי יעד שמופיעים במסמך, כל פריט עם ההקשר שלו (מערך ריק אם אין). " +
            "amounts: סכומי כסף, מחירים וכמויות חשובות, כל פריט עם ההקשר שלו ועם המטבע (מערך ריק אם אין). " +
            "אל תמציא מידע שאינו מופיע במסמך.";
    }

    internal static string SummaryPrompt(OutputSelection selection, string detail)
    {
        var transcriptMode = selection.Mode is OutputMode.TranscriptAndSummary or OutputMode.TranscriptSummaryOnly
            or OutputMode.TranscriptMeetingMinutes or OutputMode.TranscriptCustomInstruction;
        var standard = transcriptMode
            ? "עבד בעברית את המקור המצורף והחזר את המידע במבנה המבוקש, ללא פרטים מומצאים."
            : SummaryPrompt(detail);
        if ((selection.Mode is OutputMode.DocumentCustomQuestion or OutputMode.TranscriptCustomInstruction)
            && string.IsNullOrWhiteSpace(selection.CustomInstruction))
            throw new AppException(ErrKind.FileProblem, "נא לכתוב שאלה או הוראה בשדה המתאים.");

        var specific = selection.Mode switch
        {
            OutputMode.DocumentBrief =>
                "סכם את המסמך בעברית בקיצור רב. כתוב משפט אחד קצר ב-oneLine ועד 5 נקודות קצרות ב-mainPoints. אל תוסיף פרטים שאינם נחוצים לסיכום. " +
                "מלא actions, dates ו-amounts רק אם הם חיוניים להבנת הסיכום. אל תוסיף יותר מ-5 נקודות.",
            OutputMode.DocumentConclusions =>
                "נתח את המסמך בעברית והצג מסקנות העולות ממנו בלבד. מלא decisions בהחלטות או מסקנות, problems בבעיות, missing במידע חסר או שאלות פתוחות, ו-recommendations בהמלצות מעשיות. " +
                "ציין במפורש כשאין מידע מתאים באמצעות מערך ריק; אל תציג השערות כעובדות ואל תמציא מידע.",
            OutputMode.DocumentTasksDatesAmounts =>
                "חלץ מהמסמך בעברית רק משימות או פעולות, תאריכים ומועדים, וסכומים או כמויות. מלא רק את actions, dates ו-amounts; השאר oneLine, mainPoints ושאר השדות ריקים. אל תוסיף פרשנות ואל תמציא מידע.",
            OutputMode.DocumentCustomQuestion =>
                "ענה בעברית על השאלה הבאה בהתבסס רק על המסמך. אם אין במסמך תשובה, אמור זאת במפורש. אל תמציא מידע. השב בתמצית ב-oneLine וב-mainPoints, והשתמש בשאר השדות רק כשהם נחוצים לתשובה.\n" +
                "שאלת המשתמש:\n<<<\n" + selection.CustomInstruction.Trim() + "\n>>>",
            OutputMode.TranscriptAndSummary =>
                "סכם בעברית את התמלול שלהלן: משפט תמציתי ב-oneLine ונקודות עיקריות ב-mainPoints. הוסף פעולות, מועדים וסכומים רק אם נאמרו במפורש. אל תמציא מידע.",
            OutputMode.TranscriptSummaryOnly =>
                "כתוב בעברית סיכום תמציתי של התמלול: משפט קצר ב-oneLine ועד 5 נקודות עיקריות ב-mainPoints. ציין פעולות, מועדים וסכומים רק אם נאמרו במפורש. אל תוסיף יותר מ-5 נקודות.",
            OutputMode.TranscriptMeetingMinutes =>
                "הכן פרוטוקול פגישה בעברית על סמך התמלול בלבד: participants לשמות משתתפים שניתן לזהות, topics לנושאים, decisions להחלטות, actions למשימות ו-dates למועדים שנאמרו. אל תנחש שמות או פרטים חסרים; השאר מערכים ריקים כשאין מידע.",
            OutputMode.TranscriptCustomInstruction =>
                "בצע את ההוראה הבאה על סמך התמלול בלבד. התייחס רק למה שנאמר, ואל תמציא עובדות. כתוב תשובה תמציתית ב-oneLine וב-mainPoints והוסף פרטים בשדות המתאימים רק אם הם רלוונטיים.\n" +
                "הוראת המשתמש:\n<<<\n" + selection.CustomInstruction.Trim() + "\n>>>",
            _ => "",
        };
        return standard + (specific.Length == 0 ? "" : "\n" + specific);
    }

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
                ["topics"] = Arr(),
                ["participants"] = Arr(),
                ["decisions"] = Arr(),
                ["problems"] = Arr(),
                ["missing"] = Arr(),
                ["recommendations"] = Arr(),
                ["actions"] = Arr(),
                ["dates"] = Arr(),
                ["amounts"] = Arr(),
            },
            ["required"] = new JsonArray("oneLine", "mainPoints"),
        };
    }

    static async Task<SummaryData> RunSummaryAsync(JsonArray parts, IModelClient client, CancellationToken ct, bool disableThinkingWhenPossible = false)
    {
        var cfg = new JsonObject
        {
            ["maxOutputTokens"] = 8192,
            ["responseMimeType"] = "application/json",
            ["responseSchema"] = SummarySchema(),
        };
        var res = await client.GenerateAsync(parts, cfg, disableThinkingWhenPossible, ct).ConfigureAwait(false);
        try { return SummaryData.Parse(res.Text); }
        catch (Exception ex)
        {
            throw new AppException(ErrKind.Blocked, "המודל החזיר סיכום במבנה לא צפוי. נסה שוב.", technical: ex.Message + "\n" + res.Text, inner: ex);
        }
    }

    internal static List<string> SplitText(string text, int size)
    {
        var pieces = new List<string>();
        int pos = 0;
        while (pos < text.Length)
        {
            int len = Math.Min(size, text.Length - pos);
            if (pos + len < text.Length)
            {
                // Prefer to cut at a line break in the last fifth of the piece.
                int nl = text.LastIndexOf('\n', pos + len - 1, Math.Max(1, len / 5));
                if (nl > pos) len = nl - pos + 1;
            }
            pieces.Add(text.Substring(pos, len));
            pos += len;
        }
        return pieces;
    }

    static async Task<SummaryData> SummarizeStagedAsync(string text, string prompt, IModelClient client, Action<string> status, CancellationToken ct, bool disableThinkingWhenPossible = false)
    {
        var pieces = SplitText(text, PieceChars);
        var partial = new List<SummaryData>();
        for (int i = 0; i < pieces.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            status($"מסכם חלק {i + 1} מתוך {pieces.Count}...");
            var parts = new JsonArray
            {
                new JsonObject { ["text"] = prompt },
                new JsonObject { ["text"] = $"חלק {i + 1} מתוך {pieces.Count} של מסמך ארוך:\n\n" + pieces[i] },
            };
            partial.Add(await RunSummaryAsync(parts, client, ct, disableThinkingWhenPossible).ConfigureAwait(false));
        }

        status("מאחד את הסיכומים לסיכום אחד...");
        var sb = new StringBuilder();
        for (int i = 0; i < partial.Count; i++)
            sb.AppendLine($"--- סיכום חלק {i + 1} מתוך {partial.Count} ---").AppendLine(partial[i].ToPlainText($"חלק {i + 1}")).AppendLine();
        var mergeParts = new JsonArray
        {
            new JsonObject { ["text"] = "להלן סיכומי ביניים של חלקים עוקבים של מקור אחד גדול. אחד אותם לתוצאה אחת של המקור כולו, בלי כפילויות. " + prompt },
            new JsonObject { ["text"] = sb.ToString() },
        };
        return await RunSummaryAsync(mergeParts, client, ct, disableThinkingWhenPossible).ConfigureAwait(false);
    }

    static async Task<JobResult> SummarizeAsync(string file, string ext, string name, IModelClient client, Action<string> status, CancellationToken ct, JobOptions o, Func<OutputSelection> getOutputSelection)
    {
        var parts = new JsonArray();
        UploadedFile? up = null;
        string? note = null;
        OutputSelection? selection = null;
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
                    if (client is not GeminiClient gemini)
                        throw new AppException(ErrKind.Unsupported, "קובץ PDF גדול מ-12MB נתמך רק עם Gemini. בחר ב-Gemini כמנוע העריכה והסיכום, או חלק את הקובץ.");
                    status("מעלה את המסמך ל-Gemini...");
                    up = await gemini.UploadAsync(file, "application/pdf", ct).ConfigureAwait(false);
                    parts.Add(new JsonObject { ["fileData"] = new JsonObject { ["mimeType"] = up.MimeType, ["fileUri"] = up.Uri } });
                }
            }
            else
            {
                status("קורא את המסמך...");
                var text = ext == ".docx" ? ReadDocx(file) : ReadText(file);
                if (string.IsNullOrWhiteSpace(text)) throw new AppException(ErrKind.FileProblem, "לא נמצא טקסט במסמך");
                selection = getOutputSelection();
                var prompt = SummaryPrompt(selection, o.SummaryDetail);
                if (text.Length > MaxChars)
                {
                    text = text[..MaxChars];
                    note = "המסמך ארוך מאוד, ולכן הסיכום מבוסס על החלק הראשון שלו בלבד (כ-3 מיליון תווים).";
                }
                if (selection.Mode == OutputMode.DocumentBookStyle)
                {
                    var (bookText, bookWarnings) = await BookStyleTextAsync(text, GenerateForBook(client), status, ct).ConfigureAwait(false);
                    if (note != null) bookWarnings.Insert(0, note);
                    return BookResult(JobKind.Summarize, name, bookText, bookWarnings);
                }
                if (text.Length > StageChars)
                {
                    var staged = await SummarizeStagedAsync(text, prompt, client, status, ct).ConfigureAwait(false);
                    var stagedNote = "המסמך ארוך, ולכן סוכם בשלבים: כל חלק סוכם בנפרד ואז אוחדו הסיכומים." + (note == null ? "" : " " + note);
                    return new JobResult(JobKind.Summarize, name, HtmlPages.SummaryBody(staged), staged.ToPlainText(name), stagedNote, IsSummary: true);
                }
                parts.Add(new JsonObject { ["text"] = "המסמך:\n\n" + text });
            }

            selection ??= getOutputSelection();
            if (selection.Mode == OutputMode.DocumentBookStyle)
                return await BookStyleFromPdfAsync(name, parts, client, status, ct).ConfigureAwait(false);
            parts.Insert(0, new JsonObject { ["text"] = SummaryPrompt(selection, o.SummaryDetail) });
            status("מסכם...");
            var data = await RunSummaryAsync(parts, client, ct).ConfigureAwait(false);
            return new JobResult(JobKind.Summarize, name, HtmlPages.SummaryBody(data), data.ToPlainText(name), note, IsSummary: true);
        }
        finally
        {
            if (up != null && client is GeminiClient gem) await gem.DeleteFileAsync(up).ConfigureAwait(false);
        }
    }

    public static async Task<JobResult> FormatTranscriptAsync(string fileName, string transcript, OutputSelection selection, IModelClient client, Action<string> status, CancellationToken ct, JobOptions options)
    {
        if (selection.Mode == OutputMode.TranscriptExact)
            return new JobResult(JobKind.Transcribe, fileName, HtmlPages.TranscriptBody(transcript), transcript, null);

        if (selection.Mode == OutputMode.TranscriptClean)
        {
            var clean = await CleanTranscriptAsync(transcript, client, status, ct).ConfigureAwait(false);
            const string note = "התמלול עבר ניקוי של מילות מילוי וחזרות, ולכן אינו תמלול מילה במילה.";
            return new JobResult(JobKind.Transcribe, fileName, HtmlPages.TranscriptBody(clean), clean, note, Title: "תמלול נקי", IsSummary: false);
        }

        if (selection.Mode == OutputMode.TranscriptBookStyle)
        {
            var (bookText, warnings) = await BookStyleTextAsync(transcript, GenerateForBook(client), status, ct).ConfigureAwait(false);
            return BookResult(JobKind.Transcribe, fileName, bookText, warnings);
        }

        var data = await SummarizeTranscriptAsync(transcript, selection, options, client, status, ct).ConfigureAwait(false);
        var summaryText = data.ToPlainText(fileName);
        if (selection.Mode == OutputMode.TranscriptAndSummary)
        {
            var combined = transcript + "\n\n" + summaryText;
            return new JobResult(JobKind.Transcribe, fileName, HtmlPages.TranscriptSummaryBody(transcript, data), combined, null,
                Title: "תמלול וסיכום הקלטה", IsSummary: true, SupplementaryText: summaryText);
        }

        return new JobResult(JobKind.Transcribe, fileName, HtmlPages.SummaryBody(data), summaryText, null,
            Title: selection.Mode switch
            {
                OutputMode.TranscriptMeetingMinutes => "פרוטוקול פגישה",
                OutputMode.TranscriptCustomInstruction => "תשובה על ההקלטה",
                _ => "סיכום הקלטה",
            },
            IsSummary: true);
    }

    static async Task<SummaryData> SummarizeTranscriptAsync(string transcript, OutputSelection selection, JobOptions options, IModelClient client, Action<string> status, CancellationToken ct)
    {
        var prompt = SummaryPrompt(selection, options.SummaryDetail);
        if (transcript.Length > StageChars)
            return await SummarizeStagedAsync(transcript, prompt, client, status, ct, disableThinkingWhenPossible: true).ConfigureAwait(false);

        status("מפיק סיכום מהתמלול...");
        var parts = new JsonArray
        {
            new JsonObject { ["text"] = prompt },
            new JsonObject { ["text"] = "התמלול:\n\n" + transcript },
        };
        return await RunSummaryAsync(parts, client, ct, disableThinkingWhenPossible: true).ConfigureAwait(false);
    }

    static async Task<string> CleanTranscriptAsync(string transcript, IModelClient client, Action<string> status, CancellationToken ct)
    {
        const int cleanPieceChars = 20_000;
        var pieces = transcript.Length > cleanPieceChars ? SplitText(transcript, cleanPieceChars) : new List<string> { transcript };
        var cleaned = new List<string>();
        for (int i = 0; i < pieces.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            status(pieces.Count == 1 ? "מנקה את התמלול..." : $"מנקה חלק {i + 1} מתוך {pieces.Count}...");
            var parts = new JsonArray
            {
                new JsonObject { ["text"] =
                    "ערוך את התמלול המצורף לתמלול נקי: הסר מילות מילוי כגון אה ואממ, גמגומים וחזרות מקריות בלבד. " +
                    "שמור על כל המידע, הניסוח, שפת הדיבור, סדר הדברים, תוויות הדוברים וחותמות הזמן. אל תסכם, אל תשכתב ואל תוסיף דבר. " +
                    "החזר את התמלול בלבד, ללא הקדמה או עיצוב." },
                new JsonObject { ["text"] = pieces[i] },
            };
            var result = await client.GenerateAsync(parts, new JsonObject { ["maxOutputTokens"] = MaxTranscriptTokens }, true, ct).ConfigureAwait(false);
            cleaned.Add(result.Text.Trim());
        }
        return string.Join("\n", cleaned);
    }

    // ------------------------------------------------------------------ book style

    /// <summary>One model call: (parts, generation config) -> result. Injected so the book-style flow can be tested without the network.</summary>
    internal delegate Task<GenResult> Generate(JsonArray parts, JsonObject config, CancellationToken ct);

    static Generate GenerateForBook(IModelClient client) =>
        (parts, cfg, ct) => client.GenerateAsync(parts, cfg, disableThinkingWhenPossible: true, ct);

    static JsonObject BookConfig() => new() { ["maxOutputTokens"] = MaxTranscriptTokens };

    /// <summary>Edits spoken-style text into book style, piece by piece. Returns the text and warnings for the result note.</summary>
    internal static async Task<(string Text, List<string> Warnings)> BookStyleTextAsync(string source, Generate generate, Action<string> status, CancellationToken ct)
    {
        var prepared = BookStyle.Prepare(source);
        if (string.IsNullOrWhiteSpace(prepared)) throw new AppException(ErrKind.FileProblem, "לא נמצא טקסט לעריכה");
        var pieces = SplitText(prepared, BookStyle.PieceChars);
        var warnings = new List<string>();
        var outputs = new List<string>();
        var tail = "";
        for (int i = 0; i < pieces.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            status(pieces.Count == 1 ? "מסגנן את הטקסט לספר..." : $"מסגנן חלק {i + 1} מתוך {pieces.Count} לספר...");
            var edited = await BookPieceAsync(pieces[i], i + 1, pieces.Count, tail, generate, warnings, ct, depth: 0).ConfigureAwait(false);
            outputs.Add(edited);
            tail = BookStyle.Tail(edited);
        }
        return (string.Join("\n\n", outputs.Where(x => x.Length > 0)), warnings);
    }

    static async Task<string> BookPieceAsync(string piece, int part, int totalParts, string tail, Generate generate, List<string> warnings, CancellationToken ct, int depth)
    {
        string last = "";
        for (int attempt = 0; attempt < 2; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            var parts = new JsonArray
            {
                new JsonObject { ["text"] = BookStyle.BuildPrompt(part, totalParts, tail) },
                new JsonObject { ["text"] = "הטקסט לעריכה:\n\n" + piece },
            };
            var res = await generate(parts, BookConfig(), ct).ConfigureAwait(false);

            if (res.FinishReason == "MAX_TOKENS")
            {
                // The edited piece does not fit in one answer: edit it in two halves instead of losing the end.
                if (depth < 2 && piece.Length > 2000)
                {
                    var halves = SplitText(piece, piece.Length / 2 + 1);
                    var results = new List<string>();
                    var ctx = tail;
                    foreach (var h in halves)
                    {
                        var r = await BookPieceAsync(h, part, totalParts, ctx, generate, warnings, ct, depth + 1).ConfigureAwait(false);
                        results.Add(r);
                        ctx = BookStyle.Tail(r);
                    }
                    return string.Join("\n\n", results.Where(x => x.Length > 0));
                }
                warnings.Add($"חלק {part} נקטע באמצע בגלל מגבלת אורך. כדאי לבדוק את סופו מול המקור.");
                return BookStyle.NormalizeOutput(res.Text);
            }

            last = BookStyle.NormalizeOutput(res.Text);
            if (!BookStyle.LooksTruncated(piece, last)) return last;
            Log.Write($"book-style part {part}/{totalParts} looks truncated: {last.Length} chars from {piece.Length}");
        }

        warnings.Add($"חלק {part} קצר בהרבה מהמקור, וייתכן שחלקים ממנו הושמטו. כדאי להשוות מול המקור.");
        return last;
    }

    static async Task<JobResult> BookStyleFromPdfAsync(string name, JsonArray pdfParts, IModelClient client, Action<string> status, CancellationToken ct)
    {
        status("מסגנן את המסמך לספר...");
        var parts = new JsonArray(pdfParts.Select(x => x?.DeepClone()).ToArray());
        parts.Insert(0, new JsonObject
        {
            ["text"] = BookStyle.BuildPrompt(1, 1, "") +
                "\nהמקור הוא המסמך המצורף (PDF). התעלם מכותרות עליונות ותחתונות ומספרי עמודים החוזרים בכל עמוד."
        });
        var res = await client.GenerateAsync(parts, BookConfig(), disableThinkingWhenPossible: true, ct).ConfigureAwait(false);
        var text = BookStyle.NormalizeOutput(res.Text);
        if (text.Length == 0) throw new AppException(ErrKind.Blocked, "המודל לא החזיר טקסט מהמסמך. נסה שוב.");
        var warnings = new List<string>();
        if (res.FinishReason == "MAX_TOKENS")
            warnings.Add("המסמך ארוך מכדי לעבד אותו כקובץ PDF בבת אחת, והטקסט נקטע. להמרה מלאה העתק את הטקסט לקובץ TXT או DOCX והפעל שוב.");
        return BookResult(JobKind.Summarize, name, text, warnings);
    }

    internal static JobResult BookResult(JobKind kind, string fileName, string text, List<string> warnings)
    {
        var flags = BookStyle.ExtractFlags(text);
        var plain = BookStyle.ToPlain(text);
        var review = BookStyle.FlagsSection(flags);
        if (review.Length > 0) plain += "\n\n" + review;

        var notes = new List<string> { "הטקסט נערך לסגנון ספר ואינו תמלול מילה במילה." };
        if (flags.Count > 0) notes.Add($"סומנו {flags.Count} מקומות לבירור, והם מופיעים גם ברשימה בסוף.");
        notes.AddRange(warnings);
        return new JobResult(kind, fileName, HtmlPages.BookBody(text, flags), plain, string.Join(" ", notes), Title: "טקסט בסגנון ספר",
            MarkdownText: review.Length > 0 ? text + "\n\n" + review : text);
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
    public List<string> Topics { get; init; } = new();
    public List<string> Participants { get; init; } = new();
    public List<string> Decisions { get; init; } = new();
    public List<string> Problems { get; init; } = new();
    public List<string> Missing { get; init; } = new();
    public List<string> Recommendations { get; init; } = new();
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
            Topics = Strs(n["topics"]),
            Participants = Strs(n["participants"]),
            Decisions = Strs(n["decisions"]),
            Problems = Strs(n["problems"]),
            Missing = Strs(n["missing"]),
            Recommendations = Strs(n["recommendations"]),
            Actions = Strs(n["actions"]),
            Dates = Strs(n["dates"]),
            Amounts = Strs(n["amounts"]),
        };
    }

    public string ToPlainText(string fileName)
    {
        var sb = new StringBuilder();
        sb.AppendLine("סיכום: " + fileName).AppendLine();
        if (OneLine.Length > 0) sb.AppendLine("על מה המקור:").AppendLine(OneLine).AppendLine();
        void Section(string title, List<string> items)
        {
            if (items.Count == 0) return;
            sb.AppendLine(title);
            foreach (var i in items) sb.AppendLine("• " + i);
            sb.AppendLine();
        }
        Section("הנקודות העיקריות:", MainPoints);
        Section("נושאים:", Topics);
        Section("משתתפים:", Participants);
        Section("החלטות:", Decisions);
        Section("בעיות:", Problems);
        Section("מידע חסר ושאלות פתוחות:", Missing);
        Section("המלצות:", Recommendations);
        Section("פעולות שיש לבצע:", Actions);
        Section("תאריכים ומועדים:", Dates);
        Section("סכומים:", Amounts);
        return sb.ToString().TrimEnd();
    }
}

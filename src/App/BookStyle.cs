using System.Text;
using System.Text.RegularExpressions;

namespace GeminiMenu;

/// <summary>
/// "Book style": turns spoken-language text (a transcript, or any rough document) into written text fit for print.
/// Pure logic (no UI, no network) so it can be unit tested.
/// </summary>
public static class BookStyle
{
    /// <summary>Pieces are edited one at a time; small enough that the edited output fits in one response.</summary>
    public const int PieceChars = 15_000;

    const int ContextChars = 600;

    /// <summary>Editing removes fillers and repetitions, so the output may be shorter, but not by more than this.</summary>
    public const double MinOutputRatio = 0.45;

    /// <summary>Shorter inputs are not checked for truncation (too noisy).</summary>
    public const int MinCharsForRatioCheck = 3000;

    /// <summary>Markers that need a human look: [?] / [? note] from the editor, [לא ברור] and [⚠ ...] from the transcription.</summary>
    public static readonly Regex FlagRegex = new(@"\[\?[^\]\n]*\]|\[לא ברור\]|\[⚠[^\]\n]*\]", RegexOptions.Compiled);

    static readonly Regex HeadingLine = new(@"^\s*(#{1,3})\s+(.+?)\s*$", RegexOptions.Compiled);

    public static bool IsHeading(string line, out int level, out string title)
    {
        var m = HeadingLine.Match(line);
        level = m.Success ? m.Groups[1].Length : 0;
        title = m.Success ? m.Groups[2].Value : "";
        return m.Success;
    }

    const string Rules =
        "אתה עורך לשוני של ספרים. קיבלת טקסט שנאמר בעל פה (בדרך כלל תמלול), והמשימה היא להפוך אותו לטקסט כתוב המתאים להדפסה בספר. " +
        "זו עריכה ולא סיכום: הטקסט צריך להיקרא כאילו נכתב מלכתחילה לקריאה, ועם זאת להישאר נאמן לדברי הדובר, לתוכנם, לרוחם ולסגנונו האישי.\n\n" +
        "נאמנות לתוכן:\n" +
        "- אל תוסיף רעיונות, עובדות, דוגמאות או מסקנות שלא נאמרו.\n" +
        "- אל תשמיט תוכן מהותי. מותר להשמיט רק חזרות מיותרות והססנות. אורך התוצאה צריך להיות קרוב לאורך המקור לאחר הסרת מילות מילוי וחזרות. אל תקצר ואל תסכם.\n" +
        "- שמור על המשמעות המדויקת של כל משפט ועל הסגנון האישי של הדובר ככל האפשר.\n" +
        "- אם משפט אינו ברור או שנראה שחסר בו משהו, אל תנחש: סמן אותו ב-[?] מיד אחרי המקום הבעייתי, או ב-[? הסבר קצר] כשיש מה לציין.\n" +
        "- סימונים שכבר קיימים במקור, כמו [לא ברור] או [⚠ ...], השאר בדיוק כפי שהם.\n\n" +
        "ניקוי סגנון הדיבור:\n" +
        "- הסר מילות מילוי והססנות (אה, כאילו, נו, יעני, בעצם, אז, הנה) כשאין להן משמעות, וכן התחלות שנקטעו, תיקוני דיבור וחזרות מיותרות.\n" +
        "- הסר חותמות זמן.\n" +
        "- הסר או נסח מחדש בצורה ספרותית פניות ישירות לקהל הקשורות לרגע ההרצאה (\"כמו שאתם רואים כאן\", \"תקשיבו רגע\", \"אני רואה שיש שאלה\").\n" +
        "- אל תשאיר ניבים ומבנים שמתאימים רק לדיבור.\n\n" +
        "הפיכה לשפה כתובה:\n" +
        "- חבר משפטים קטועים למשפטים שלמים וזורמים, ופצל משפטים ארוכים מדי.\n" +
        "- כתוב בעברית תקנית וברורה, ברמה לשונית אחידה, עם רצף לוגי ומעברים טבעיים.\n" +
        "- אל תשתמש בביטויי קישור כמו \"כמו שאמרנו קודם\" אלא אם הם מתאימים לספר.\n\n" +
        "מבנה:\n" +
        "- חלק לפסקאות לפי רעיונות, כל פסקה בנושא אחד, ובין פסקאות השאר שורה ריקה.\n" +
        "- בנקודות מעבר בין נושאים הוסף כותרת משנה תמציתית ומכובדת בשורה נפרדת שמתחילה ב-\"## \". כותרת פרק (\"# \") רק כשיש בטקסט כמה נושאים גדולים ונפרדים.\n" +
        "- אם הדובר חזר על אותו רעיון במקומות שונים, אחד אותו במקום המתאים ביותר, ובלבד שלא יאבד שום תוכן.\n" +
        "- תוויות דוברים (\"דובר 1:\"): אם יש דובר אחד בלבד, הסר אותן. אם יש כמה דוברים, השאר את התווית בתחילת כל תור דיבור.\n\n" +
        "ציטוטים, מקורות ושמות:\n" +
        "- ציטוטים ומראי מקומות יועתקו בדיוק כפי שנאמרו. אל תשלים ואל תתקן מקור מזיכרונך. אם נראה שיש טעות, סמן [?] והשאר את הנוסח כפי שהוא.\n" +
        "- הקפד על כתיב נכון ועקבי של שמות ומושגים. אם יש ספק בכתיב, סמן [?].\n\n" +
        "פיסוק ועיצוב:\n" +
        "- השתמש בפיסוק מלא ותקני, ובעקביות בכתיבת מספרים, תאריכים וראשי תיבות.\n" +
        "- אל תוסיף הדגשות או רשימות.\n\n" +
        "פלט: החזר טקסט נקי בלבד, בלי הקדמה, הסברים, הערות ביניים או סיכום, ובלי רשימת סימונים בסוף (היא נוספת אוטומטית).";

    /// <summary>The built-in instruction (shown in Settings so it can be edited).</summary>
    public static string DefaultPrompt => Rules;

    /// <summary>The user's own full instruction (Settings). Empty = the built-in <see cref="Rules"/>.</summary>
    public static string CustomPrompt { get; set; } = "";

    /// <summary>Always appended to a custom instruction: the app depends on these conventions to build headings, the review list and the files.</summary>
    internal const string TechnicalRules =
        "\n\nכללים טכניים שחלים תמיד, גם אם ההנחיה שלמעלה אומרת אחרת:\n" +
        "- כותרת משנה כתובה בשורה נפרדת שמתחילה ב-\"## \", וכותרת פרק ב-\"# \". בין פסקאות השאר שורה ריקה.\n" +
        "- מקום לא ברור או חשוד מסומן [?] או [? הסבר קצר]. סימונים קיימים כמו [לא ברור] או [⚠ ...] נשארים כפי שהם.\n" +
        "- הסר חותמות זמן. תוויות דוברים (\"דובר 1:\"): הסר אם יש דובר אחד, והשאר בתחילת כל תור דיבור אם יש כמה.\n" +
        "- החזר טקסט נקי בלבד, בלי הקדמה, הסברים או הערות, ובלי רשימת סימונים בסוף (היא נוספת אוטומטית).";

    public static string BuildPrompt(int part, int totalParts, string previousTail)
    {
        var sb = new StringBuilder(string.IsNullOrWhiteSpace(CustomPrompt) ? Rules : CustomPrompt.Trim() + TechnicalRules);
        if (totalParts > 1)
        {
            sb.Append($"\n\nהטקסט שלהלן הוא חלק {part} מתוך {totalParts} של טקסט אחד ארוך. ערוך רק את החלק הזה, ");
            sb.Append("בלי פתיחה וסיכום משלו, והמשך ישירות מהמקום שבו החלק הקודם נגמר.");
            if (!string.IsNullOrWhiteSpace(previousTail))
                sb.Append("\nלהמשכיות הסגנון, הכתיב והכותרות בלבד, זה סוף החלק הקודם אחרי העריכה. אל תכלול אותו שוב בתוצאה:\n<<<\n")
                  .Append(previousTail.Trim()).Append("\n>>>");
        }
        return sb.ToString();
    }

    /// <summary>Source text as sent to the editor: no timestamps, normalized line breaks.</summary>
    public static string Prepare(string source) =>
        Jobs.StripTimestamps((source ?? "").Replace("\r\n", "\n").Replace('\r', '\n'));

    /// <summary>Cleans the model output: removes a wrapping code fence and extra blank lines.</summary>
    public static string NormalizeOutput(string text)
    {
        var s = (text ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Trim();
        if (s.StartsWith("```"))
        {
            int nl = s.IndexOf('\n');
            s = nl < 0 ? "" : s[(nl + 1)..];
            if (s.TrimEnd().EndsWith("```")) s = s.TrimEnd()[..^3];
        }
        return Regex.Replace(s.Trim(), @"\n{3,}", "\n\n");
    }

    /// <summary>Last part of an edited piece, handed to the next piece for continuity.</summary>
    public static string Tail(string edited)
    {
        var s = edited ?? "";
        if (s.Length <= ContextChars) return s.Trim();
        s = s[^ContextChars..];
        int nl = s.IndexOf('\n');
        if (nl > 0 && nl < ContextChars / 2) s = s[(nl + 1)..]; // drop the cut-off first line
        return s.Trim();
    }

    /// <summary>True when the output is suspiciously short compared to the input (the model summarized or stopped early).</summary>
    public static bool LooksTruncated(string input, string output)
    {
        var inLen = (input ?? "").Trim().Length;
        var outLen = (output ?? "").Trim().Length;
        if (inLen == 0) return false;
        if (outLen == 0) return true;
        if (inLen < MinCharsForRatioCheck) return false;
        return (double)outLen / inLen < MinOutputRatio;
    }

    /// <summary>Plain-text version: heading markers removed, headings and paragraphs separated by blank lines.</summary>
    public static string ToPlain(string text)
    {
        var sb = new StringBuilder();
        foreach (var raw in (text ?? "").Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.TrimEnd();
            if (line.Length == 0) continue;
            if (sb.Length > 0) sb.Append("\n\n");
            sb.Append(IsHeading(line, out _, out var title) ? title : line.Trim());
        }
        return sb.ToString();
    }

    /// <summary>Every place that needs a human check, with its paragraph number and heading.</summary>
    public static List<string> ExtractFlags(string text)
    {
        var flags = new List<string>();
        string? heading = null;
        int paragraph = 0;
        foreach (var raw in (text ?? "").Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            if (IsHeading(line, out _, out var title)) { heading = title; continue; }
            paragraph++;
            foreach (Match m in FlagRegex.Matches(line))
            {
                int start = Math.Max(0, m.Index - 50);
                var snippet = line[start..(m.Index + m.Length)];
                if (start > 0)
                {
                    int sp = snippet.IndexOf(' ');
                    if (sp > 0 && sp < 20) snippet = snippet[(sp + 1)..]; // do not start mid-word
                    snippet = "…" + snippet;
                }
                flags.Add($"פסקה {paragraph}" + (heading != null ? $" (תחת \"{heading}\")" : "") + ": " + snippet);
            }
        }
        return flags;
    }

    /// <summary>The review list appended to the plain-text output (empty when there is nothing to review).</summary>
    public static string FlagsSection(List<string> flags)
    {
        if (flags.Count == 0) return "";
        var sb = new StringBuilder("מקומות לבירור:\n");
        foreach (var f in flags) sb.Append("• ").Append(f).Append('\n');
        return sb.ToString().TrimEnd();
    }
}

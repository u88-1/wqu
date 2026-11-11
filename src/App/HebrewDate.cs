using System.Globalization;
using System.Text;

namespace GeminiMenu;

/// <summary>
/// Hebrew (לוח עברי) dates for the result pages and for the summary prompts.
/// The Hebrew day really starts at nightfall; this helper uses the calendar date of the computer clock
/// and does not shift the date after sunset.
/// </summary>
public static class HebrewDate
{
    static readonly HebrewCalendar Cal = new();

    // Month numbers of System.Globalization.HebrewCalendar: 1 = Tishrei ... 5 = Shvat, then
    // a common year has 6 = Adar, 7 = Nisan ... 12 = Elul, a leap year has 6 = Adar I, 7 = Adar II, 8 = Nisan ... 13 = Elul.
    static readonly string[] CommonYear = { "", "תשרי", "חשוון", "כסלו", "טבת", "שבט", "אדר", "ניסן", "אייר", "סיוון", "תמוז", "אב", "אלול" };
    static readonly string[] LeapYear = { "", "תשרי", "חשוון", "כסלו", "טבת", "שבט", "אדר א׳", "אדר ב׳", "ניסן", "אייר", "סיוון", "תמוז", "אב", "אלול" };

    static readonly int[] Values = { 400, 300, 200, 100, 90, 80, 70, 60, 50, 40, 30, 20, 10, 9, 8, 7, 6, 5, 4, 3, 2, 1 };
    static readonly string[] Letters = { "ת", "ש", "ר", "ק", "צ", "פ", "ע", "ס", "נ", "מ", "ל", "כ", "י", "ט", "ח", "ז", "ו", "ה", "ד", "ג", "ב", "א" };

    /// <summary>Hebrew numeral: 5 -> ה׳, 15 -> ט״ו, 787 -> תשפ״ז.</summary>
    public static string Gematria(int n)
    {
        if (n <= 0) return n.ToString(CultureInfo.InvariantCulture);
        var sb = new StringBuilder();
        for (int i = 0; i < Values.Length; i++)
        {
            while (n >= Values[i])
            {
                // 15 and 16 are written ט״ו and ט״ז, so that the name of God is not spelled out.
                if (Values[i] == 10 && (n == 15 || n == 16)) break;
                sb.Append(Letters[i]);
                n -= Values[i];
            }
        }
        var s = sb.ToString();
        return s.Length == 1 ? s + "׳" : s[..^1] + "״" + s[^1];
    }

    /// <summary>Hebrew year without the thousands, as written in everyday use: 5787 -> תשפ״ז.</summary>
    public static string YearName(int hebrewYear) => Gematria(hebrewYear % 1000);

    public static string MonthName(int hebrewYear, int month)
    {
        var names = Cal.IsLeapYear(hebrewYear) ? LeapYear : CommonYear;
        return month >= 1 && month < names.Length ? names[month] : "";
    }

    /// <summary>For example "י״א בחשוון תשפ״ז". Dates outside the range of the calendar give an empty string.</summary>
    public static string Format(DateTime date, bool withYear = true)
    {
        try
        {
            int y = Cal.GetYear(date), m = Cal.GetMonth(date), d = Cal.GetDayOfMonth(date);
            var s = Gematria(d) + " ב" + MonthName(y, m);
            return withYear ? s + " " + YearName(y) : s;
        }
        catch (ArgumentOutOfRangeException) { return ""; }
    }

    /// <summary>"dd/MM/yyyy HH:mm" followed by the Hebrew date when <paramref name="withHebrew"/> is on.</summary>
    public static string Stamp(DateTime date, bool withHebrew, bool withTime = true)
    {
        var s = date.ToString(withTime ? "dd/MM/yyyy HH:mm" : "dd/MM/yyyy", CultureInfo.InvariantCulture);
        if (!withHebrew) return s;
        var h = Format(date);
        return h.Length == 0 ? s : s + "  ·  " + h;
    }

    /// <summary>Short note added to the summary prompts: today's date, and how to treat Hebrew dates in the source.</summary>
    public static string PromptNote(DateTime today)
    {
        var heb = Format(today);
        return "התאריך היום: " + today.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture) + (heb.Length > 0 ? " (" + heb + ")" : "") +
               ". תאריכים עבריים שמופיעים במקור (למשל \"י״ג בכסלו תשפ״ז\" או \"כ״ה בכסלו\") יש לשמור בדיוק כפי שנכתבו. " +
               "אל תמיר תאריך עברי ללועזי או להפך אלא אם ההמרה כתובה במקור. " +
               "תאריכים יחסיים (\"מחר\", \"ביום ראשון הקרוב\") אפשר לפרש לפי התאריך של היום.";
    }
}

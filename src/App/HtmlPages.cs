using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace GeminiMenu;

public static class HtmlPages
{
    static string E(string s) => WebUtility.HtmlEncode(s);

    // ------------------------------------------------------------------ bodies

    static readonly Regex Speaker = new(@"^(\s*(?:דובר|Speaker)\s*\d+\s*:)(.*)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static string TranscriptBody(string text)
    {
        var sb = new StringBuilder();
        foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.TrimEnd();
            if (line.Length == 0) continue;
            var m = Speaker.Match(line);
            if (m.Success) sb.Append("<p dir=\"auto\"><strong>").Append(E(m.Groups[1].Value)).Append("</strong>").Append(E(m.Groups[2].Value)).AppendLine("</p>");
            else sb.Append("<p dir=\"auto\">").Append(E(line)).AppendLine("</p>");
        }
        return sb.ToString();
    }

    public static string SummaryBody(SummaryData d)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<section class=\"one\"><h2>על מה המסמך</h2><p dir=\"auto\">" + E(d.OneLine) + "</p></section>");
        void List(string title, List<string> items)
        {
            if (items.Count == 0) return;
            sb.AppendLine("<section><h2>" + E(title) + "</h2><ul>");
            foreach (var i in items) sb.AppendLine("<li dir=\"auto\">" + E(i) + "</li>");
            sb.AppendLine("</ul></section>");
        }
        List("הנקודות העיקריות", d.MainPoints);
        List("פעולות שיש לבצע", d.Actions);
        List("תאריכים ומועדים", d.Dates);
        List("סכומים", d.Amounts);
        return sb.ToString();
    }

    // ------------------------------------------------------------------ page shell

    const string Css = @"
:root{--bg:#f3f5f9;--card:#fff;--text:#1d2433;--muted:#667085;--accent:#2563eb;--accent-d:#1d4ed8;--line:#e4e8f0;--err:#b42318;--errbg:#fef3f2}
@media (prefers-color-scheme:dark){:root{--bg:#10141c;--card:#1a2030;--text:#e8ecf4;--muted:#9aa4b8;--accent:#5b8cff;--accent-d:#7aa2ff;--line:#2a3347;--err:#ff8a7a;--errbg:#2a1a1a}}
*{box-sizing:border-box}
body{margin:0;background:var(--bg);color:var(--text);font-family:'Segoe UI','Noto Sans Hebrew',Arial,sans-serif;line-height:1.75;font-size:18px}
.wrap{max-width:860px;margin:0 auto;padding:24px 16px 60px}
header{display:flex;gap:16px;align-items:flex-start;justify-content:space-between;flex-wrap:wrap;margin-bottom:16px}
h1{font-size:1.45rem;margin:0 0 4px;line-height:1.35}
.meta{color:var(--muted);font-size:.9rem;word-break:break-all}
button#copy{background:var(--accent);color:#fff;border:0;border-radius:10px;padding:10px 22px;font-size:1rem;font-family:inherit;cursor:pointer;font-weight:600;box-shadow:0 1px 3px rgba(0,0,0,.2)}
button#copy:hover{background:var(--accent-d)}
button#copy.done{background:#16a34a}
.card{background:var(--card);border:1px solid var(--line);border-radius:14px;padding:22px 28px;box-shadow:0 1px 4px rgba(0,0,0,.06)}
.card p{margin:.2em 0 .9em}
section{margin-bottom:1.4rem}
section:last-child{margin-bottom:0}
h2{font-size:1.1rem;color:var(--accent);margin:0 0 .35rem}
.one p{font-size:1.15rem;font-weight:600}
ul{margin:.2rem 0;padding-inline-start:1.4rem}
li{margin-bottom:.4rem}
.note{background:#fff7e0;color:#6b4a00;border:1px solid #f0d58a;border-radius:10px;padding:10px 14px;margin-bottom:14px;font-size:.95rem}
footer{color:var(--muted);font-size:.8rem;margin-top:14px;text-align:center}
.err{border-color:var(--err);background:var(--errbg)}
.err h2{color:var(--err)}
details{margin-top:1rem;color:var(--muted);font-size:.85rem}
pre{white-space:pre-wrap;direction:ltr;text-align:left;background:rgba(0,0,0,.06);padding:10px;border-radius:8px;overflow:auto}
code{direction:ltr;unicode-bidi:embed;background:rgba(0,0,0,.07);padding:1px 6px;border-radius:5px;font-size:.9em}
a{color:var(--accent)}
@media print{button#copy,footer{display:none}body{background:#fff}.card{box-shadow:none}}";

    const string CopyScript = @"
const btn=document.getElementById('copy');
function fallback(t){const a=document.createElement('textarea');a.value=t;a.style.position='fixed';a.style.opacity='0';document.body.appendChild(a);a.select();let ok=false;try{ok=document.execCommand('copy')}catch(e){}a.remove();return ok}
btn.addEventListener('click',async()=>{
  let ok=false;
  try{await navigator.clipboard.writeText(PLAIN);ok=true}catch(e){ok=fallback(PLAIN)}
  const old=btn.textContent;btn.textContent=ok?'הועתק ✓':'ההעתקה נכשלה';btn.classList.toggle('done',ok);
  setTimeout(()=>{btn.textContent=old;btn.classList.remove('done')},2000);
});";

    static string Shell(string title, string meta, string bodyHtml, string? plain, string? noteHtml, string cardClass = "")
    {
        var sb = new StringBuilder();
        sb.AppendLine("<!DOCTYPE html>");
        sb.AppendLine("<html lang=\"he\" dir=\"rtl\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">");
        sb.AppendLine("<title>" + E(title) + "</title><style>" + Css + "</style></head><body><div class=\"wrap\">");
        sb.AppendLine("<header><div><h1>" + E(title) + "</h1><div class=\"meta\">" + E(meta) + "</div></div>");
        if (plain != null) sb.AppendLine("<button id=\"copy\" type=\"button\">העתק הכל</button>");
        sb.AppendLine("</header>");
        if (noteHtml != null) sb.AppendLine("<div class=\"note\">" + noteHtml + "</div>");
        sb.AppendLine("<div class=\"card " + cardClass + "\" id=\"content\">" + bodyHtml + "</div>");
        sb.AppendLine("<footer>הופק באמצעות Gemini. ייתכנו אי-דיוקים, ולכן כדאי לבדוק פרטים חשובים מול המקור.</footer></div>");
        if (plain != null)
        {
            // JsonSerializer escapes < > & so the text cannot break out of the script tag.
            sb.AppendLine("<script>const PLAIN=" + JsonSerializer.Serialize(plain) + ";" + CopyScript + "</script>");
        }
        sb.AppendLine("</body></html>");
        return sb.ToString();
    }

    public static string ResultPage(JobResult r)
    {
        var title = r.Kind == JobKind.Transcribe ? "תמלול הקלטה" : "סיכום מסמך";
        var meta = r.FileName + "  ·  " + DateTime.Now.ToString("dd/MM/yyyy HH:mm");
        if (r.ProcessingTime is { } elapsed)
            meta += "  ·  זמן עיבוד: " + $"{(int)elapsed.TotalHours:00}:{elapsed.Minutes:00}:{elapsed.Seconds:00}";
        return Shell(title, meta, r.BodyHtml, r.PlainText, r.Note == null ? null : E(r.Note));
    }

    // ------------------------------------------------------------------ errors

    public static string ErrorPage(Exception ex, string fileName, JobKind kind)
    {
        string what, happened;
        var todo = new List<string>();
        string? tech = null;

        if (ex is AppException ae)
        {
            tech = ae.Technical ?? ae.Message;
            switch (ae.Kind)
            {
                case ErrKind.NoKey:
                    what = "חסר מפתח API של Gemini";
                    happened = "כדי להשתמש בתמלול ובסיכום צריך מפתח API חינמי מ-Google, והוא עדיין לא הוגדר.";
                    todo.Add("היכנס ל-<a href=\"https://aistudio.google.com/apikey\">aistudio.google.com/apikey</a> עם חשבון Google ולחץ <b>Create API key</b>.");
                    todo.Add("הפעל שוב את קובץ ההתקנה <code>GeminiMenu-Setup.exe</code>, הדבק את המפתח ולחץ <b>התקן / עדכן</b>.");
                    todo.Add("או ערוך ידנית את הקובץ <code>" + E(AppConfig.FilePath) + "</code> ושים את המפתח בשדה <code>apiKey</code>.");
                    break;
                case ErrKind.BadKey:
                    what = "Gemini דחה את מפתח ה-API";
                    happened = "המפתח השמור אינו תקין, פג תוקפו או שאין לו הרשאה.";
                    todo.Add("צור מפתח חדש ב-<a href=\"https://aistudio.google.com/apikey\">aistudio.google.com/apikey</a>.");
                    todo.Add("הפעל שוב את <code>GeminiMenu-Setup.exe</code> והדבק את המפתח החדש, או עדכן את <code>apiKey</code> בקובץ <code>" + E(AppConfig.FilePath) + "</code>.");
                    todo.Add("ודא שהמפתח הועתק במלואו, בלי רווחים בתחילתו או בסופו.");
                    break;
                case ErrKind.Quota:
                    what = "הגעת למגבלת השימוש של Gemini";
                    happened = "במסלול החינמי יש מגבלה על מספר הבקשות בדקה ובמשך היום. ניסינו כמה פעמים ולא הצלחנו.";
                    todo.Add("המתן כמה דקות ונסה שוב. אם המגבלה היומית נוצלה, נסה מחר.");
                    todo.Add("בבחירה של כמה קבצים יחד, כל קובץ נשלח בנפרד ומנצל את המגבלה, ולכן כדאי לבחור פחות קבצים בכל פעם.");
                    todo.Add("אפשר לבדוק את השימוש ב-<a href=\"https://aistudio.google.com/usage\">AI Studio</a> או לעבור למסלול בתשלום.");
                    break;
                case ErrKind.Overloaded:
                    what = "Gemini עמוס כרגע";
                    happened = "ניסינו כמה פעמים, גם במודל חלופי, אבל השרת של Google לא הצליח לענות.";
                    todo.Add("נסה שוב בעוד כמה דקות. עומסים כאלה בדרך כלל עוברים מהר.");
                    todo.Add("אם הבעיה נמשכת, ייתכן שמדובר בתקלה אצל Google: <a href=\"https://aistudio.google.com/status\">דף הסטטוס</a>.");
                    break;
                case ErrKind.Network:
                    what = "אין תקשורת עם Gemini";
                    happened = "לא הצלחנו להתחבר לשרת של Google, או שהחיבור נותק.";
                    todo.Add("בדוק שיש חיבור לאינטרנט ונסה שוב.");
                    todo.Add("אם יש חומת אש או סינון אינטרנט, ודא שהכתובת <code>generativelanguage.googleapis.com</code> מותרת.");
                    todo.Add("בקבצים גדולים החיבור עלול להיות איטי מדי. נסה קובץ קטן יותר.");
                    break;
                case ErrKind.NotFound:
                    what = "המודל של Gemini לא נמצא";
                    happened = "שם המודל שבהגדרות כבר אינו זמין.";
                    todo.Add("פתח את <code>" + E(AppConfig.FilePath) + "</code> ושנה את השדה <code>model</code> לשם מודל עדכני, למשל <code>gemini-2.5-flash</code>.");
                    todo.Add("רשימת המודלים: <a href=\"https://ai.google.dev/gemini-api/docs/models\">ai.google.dev/gemini-api/docs/models</a>.");
                    break;
                case ErrKind.Blocked:
                    what = "Gemini לא החזיר תוצאה";
                    happened = "התשובה נחסמה או שהתקבלה ריקה. זה קורה לעיתים בגלל מסנני תוכן, או כשאין בקובץ תוכן שאפשר לעבד.";
                    todo.Add("נסה שוב. לפעמים הניסיון השני מצליח.");
                    todo.Add("ודא שיש בקובץ דיבור או טקסט ברור.");
                    break;
                case ErrKind.BadRequest:
                    what = "Gemini לא קיבל את הבקשה";
                    happened = "הקובץ או הבקשה לא התקבלו, למשל בגלל פורמט שאינו נתמך או קובץ גדול או פגום.";
                    todo.Add("נסה להמיר את הקובץ לפורמט נפוץ (לשמע: MP3 או WAV, למסמך: PDF).");
                    todo.Add("ודא שהקובץ נפתח כרגיל במחשב ושאינו מוגן בסיסמה.");
                    break;
                case ErrKind.Unsupported:
                    what = "סוג הקובץ אינו נתמך";
                    happened = ae.Message;
                    todo.Add("תמלול עובד על קובצי שמע (wav, mp3, m4a, ogg, amr, flac, aac, wma, opus) ועל סרטוני mp4.");
                    todo.Add("סיכום עובד על pdf, txt, docx ו-md.");
                    break;
                case ErrKind.FileProblem:
                    what = "הייתה בעיה בקובץ";
                    happened = ae.Message;
                    todo.Add("ודא שהקובץ קיים, נפתח כרגיל ואינו פתוח או נעול על ידי תוכנה אחרת.");
                    break;
                default:
                    what = "משהו השתבש";
                    happened = ae.Message;
                    todo.Add("נסה שוב. אם הבעיה חוזרת, העתק את הפרטים הטכניים שבתחתית הדף ושלח למי שמתחזק את הכלי.");
                    break;
            }
        }
        else
        {
            what = "משהו השתבש";
            happened = "אירעה שגיאה לא צפויה.";
            todo.Add("נסה שוב. אם הבעיה חוזרת, העתק את הפרטים הטכניים שבתחתית הדף ושלח למי שמתחזק את הכלי.");
            tech = ex.GetType().Name + ": " + ex.Message;
        }

        var body = new StringBuilder();
        body.AppendLine("<section><h2>" + E(what) + "</h2><p>" + E(happened) + "</p></section>");
        body.AppendLine("<section><h2>מה אפשר לעשות</h2><ol>" + string.Concat(todo.Select(t => "<li>" + t + "</li>")) + "</ol></section>");
        if (!string.IsNullOrWhiteSpace(tech))
            body.AppendLine("<details><summary>פרטים טכניים</summary><pre>" + E(tech!) + "</pre></details>");

        var title = kind == JobKind.Transcribe ? "התמלול לא הושלם" : "הסיכום לא הושלם";
        return Shell(title, fileName + "  ·  " + DateTime.Now.ToString("dd/MM/yyyy HH:mm"), body.ToString(), null, null, "err");
    }

    // ------------------------------------------------------------------ saving

    static string Unique(string dir, string baseName, string suffix, string extension)
    {
        var p = Path.Combine(dir, $"{baseName} - {suffix}{extension}");
        for (int i = 2; File.Exists(p); i++) p = Path.Combine(dir, $"{baseName} - {suffix} ({i}){extension}");
        return p;
    }

    /// <summary>Saves next to the source file; falls back to the temp folder if that folder is not writable.</summary>
    static string SaveContent(string sourceFile, string suffix, string content, string extension)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(sourceFile)) ?? ".";
        var baseName = Path.GetFileNameWithoutExtension(sourceFile);
        try
        {
            var p = Unique(dir, baseName, suffix, extension);
            File.WriteAllText(p, content, new UTF8Encoding(true));
            return p;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            var tmp = Path.Combine(Path.GetTempPath(), "GeminiMenu");
            Directory.CreateDirectory(tmp);
            var p = Unique(tmp, baseName, suffix, extension);
            File.WriteAllText(p, content, new UTF8Encoding(true));
            return p;
        }
    }

    public static string Save(string sourceFile, string suffix, string html) => SaveContent(sourceFile, suffix, html, ".html");

    public static string SaveText(string sourceFile, string suffix, string text) => SaveContent(sourceFile, suffix, text, ".txt");
}

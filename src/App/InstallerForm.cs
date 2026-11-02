using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;

namespace GeminiMenu;

public sealed class InstallerForm : Form
{
    static readonly Color Accent = Color.FromArgb(37, 99, 235);
    static readonly Color AccentDark = Color.FromArgb(29, 78, 216);
    static readonly Color PageBg = Color.FromArgb(244, 246, 251);
    static readonly Color Muted = Color.FromArgb(102, 112, 133);
    static readonly Color Ink = Color.FromArgb(29, 36, 51);

    readonly TextBox _key = new();
    readonly CheckBox _show = new();
    readonly Button _install = new();
    readonly Button _uninstall = new();
    readonly TextBox _log = new();
    readonly ProgressBar _bar = new();

    public InstallerForm()
    {
        Text = "תפריט Gemini";
        RightToLeft = RightToLeft.Yes;
        RightToLeftLayout = false; // coordinates below are plain left-to-right pixels
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = new Font("Segoe UI", 10f);
        BackColor = PageBg;
        ForeColor = Ink;
        ClientSize = new Size(560, 600);
        try { Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!); } catch { }

        // ---- header band
        var header = new Panel { Bounds = new Rectangle(0, 0, 560, 96), BackColor = Accent };
        var title = new Label
        {
            Text = "תפריט Gemini",
            Font = new Font("Segoe UI Semibold", 20f),
            ForeColor = Color.White,
            BackColor = Color.Transparent,
            Bounds = new Rectangle(24, 14, 512, 44),
        };
        var sub = new Label
        {
            Text = "תמלול הקלטות וסיכום מסמכים בלחיצה ימנית על קובץ",
            Font = new Font("Segoe UI", 10.5f),
            ForeColor = Color.FromArgb(219, 230, 255),
            BackColor = Color.Transparent,
            Bounds = new Rectangle(24, 58, 512, 26),
        };
        header.Controls.AddRange(new Control[] { title, sub });

        // ---- card
        var card = new Panel { Bounds = new Rectangle(24, 116, 512, 266), BackColor = Color.White };
        card.Paint += (_, e) => e.Graphics.DrawRectangle(new Pen(Color.FromArgb(228, 232, 240)), 0, 0, card.Width - 1, card.Height - 1);

        var what = new Label
        {
            Text = "מוסיף לתפריט הראשי של Windows 11:\r\n•  \"תמלל הקלטה\" לקובצי שמע ווידאו MP4\r\n•  \"סכם לי\" לקובצי pdf, txt, docx ו-md",
            Bounds = new Rectangle(20, 16, 472, 76),
        };

        var keyLabel = new Label { Text = "מפתח Gemini API", Font = new Font("Segoe UI Semibold", 10f), Bounds = new Rectangle(20, 100, 472, 22) };
        var existing = SafeLoad();
        var hint = new Label
        {
            Text = string.IsNullOrEmpty(existing.ApiKey) ? "הדבק כאן את המפתח שקיבלת מ-Google." : "כבר שמור מפתח. השאר ריק כדי לא לשנות אותו.",
            ForeColor = Muted,
            Font = new Font("Segoe UI", 9f),
            Bounds = new Rectangle(20, 122, 472, 20),
        };
        _key.Bounds = new Rectangle(20, 148, 472, 30);
        _key.Font = new Font("Segoe UI", 11f);
        _key.UseSystemPasswordChar = true;
        _key.BorderStyle = BorderStyle.FixedSingle;
        _key.RightToLeft = RightToLeft.No; // keys are Latin text
        _key.TextAlign = HorizontalAlignment.Left;

        _show.Text = "הצג את המפתח";
        _show.Bounds = new Rectangle(20, 188, 160, 24);
        _show.CheckedChanged += (_, _) => _key.UseSystemPasswordChar = !_show.Checked;

        var link = new LinkLabel
        {
            Text = "אין לך מפתח? קבל אחד בחינם",
            Bounds = new Rectangle(190, 188, 302, 24),
            LinkColor = Accent,
            ActiveLinkColor = AccentDark,
            TextAlign = ContentAlignment.TopRight,
        };
        link.LinkClicked += (_, _) => Process.Start(new ProcessStartInfo("https://aistudio.google.com/apikey") { UseShellExecute = true });

        var note = new Label
        {
            Text = "המפתח נשמר רק במחשב הזה, בתיקיית AppData שלך.",
            ForeColor = Muted,
            Font = new Font("Segoe UI", 9f),
            Bounds = new Rectangle(20, 224, 472, 22),
        };
        card.Controls.AddRange(new Control[] { what, keyLabel, hint, _key, _show, link, note });

        // ---- buttons
        StyleButton(_install, "התקן / עדכן", primary: true);
        _install.Bounds = new Rectangle(24 + 512 - 220, 398, 220, 46);
        _install.Click += async (_, _) => await Run(() => Installer.Install(_key.Text, Log), validateKey: true);

        StyleButton(_uninstall, "הסר התקנה", primary: false);
        _uninstall.Bounds = new Rectangle(24, 398, 150, 46);
        _uninstall.Click += async (_, _) => await Run(() => Installer.Uninstall(Log), validateKey: false);

        _bar.Style = ProgressBarStyle.Marquee;
        _bar.MarqueeAnimationSpeed = 30;
        _bar.Visible = false;
        _bar.Bounds = new Rectangle(24, 456, 512, 6);

        // ---- log
        _log.Multiline = true;
        _log.ReadOnly = true;
        _log.ScrollBars = ScrollBars.Vertical;
        _log.BorderStyle = BorderStyle.FixedSingle;
        _log.BackColor = Color.White;
        _log.Font = new Font("Segoe UI", 9.5f);
        _log.Bounds = new Rectangle(24, 472, 512, 112);

        Controls.AddRange(new Control[] { header, card, _install, _uninstall, _bar, _log });
        Log("מוכן. הדבק מפתח ולחץ \"התקן / עדכן\".");
    }

    static void StyleButton(Button b, string text, bool primary)
    {
        b.Text = text;
        b.FlatStyle = FlatStyle.Flat;
        b.Cursor = Cursors.Hand;
        b.Font = new Font("Segoe UI Semibold", 11f);
        b.UseVisualStyleBackColor = false;
        if (primary)
        {
            b.BackColor = Accent;
            b.ForeColor = Color.White;
            b.FlatAppearance.BorderSize = 0;
            b.FlatAppearance.MouseOverBackColor = AccentDark;
        }
        else
        {
            b.BackColor = Color.White;
            b.ForeColor = Ink;
            b.FlatAppearance.BorderColor = Color.FromArgb(208, 213, 221);
            b.FlatAppearance.MouseOverBackColor = Color.FromArgb(240, 242, 247);
        }
    }

    static AppConfig SafeLoad() { try { return AppConfig.Load(); } catch { return new AppConfig(); } }

    void Log(string s)
    {
        if (InvokeRequired) { BeginInvoke(() => Log(s)); return; }
        _log.AppendText(s + Environment.NewLine);
    }

    async Task Run(Action action, bool validateKey)
    {
        _install.Enabled = _uninstall.Enabled = false;
        _bar.Visible = true;
        try
        {
            if (validateKey)
            {
                var key = _key.Text.Trim();
                if (key.Length == 0 && string.IsNullOrEmpty(SafeLoad().ApiKey))
                    Log("שים לב: לא הוזן מפתח. אפשר להוסיף אותו אחר כך בהפעלה חוזרת של ההתקנה.");
                else if (key.Length > 0)
                {
                    Log("בודק את המפתח מול Gemini...");
                    try { Log(await GeminiClient.ValidateKeyAsync(key) ? "המפתח תקין." : "אזהרה: Gemini דחה את המפתח. ההתקנה תימשך, אבל כדאי לבדוק אותו."); }
                    catch { Log("לא הצלחתי לבדוק את המפתח (אין אינטרנט?). ההתקנה ממשיכה."); }
                }
            }
            await Task.Run(action);
        }
        catch (Exception ex)
        {
            Log("שגיאה: " + ex.Message);
        }
        finally
        {
            _bar.Visible = false;
            _install.Enabled = _uninstall.Enabled = true;
        }
    }
}

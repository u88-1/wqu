using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;

namespace GeminiMenu;

public sealed class InstallerForm : Form
{
    readonly TextBox _key = new();
    readonly CheckBox _show = new();
    readonly Button _install = new();
    readonly Button _uninstall = new();
    readonly TextBox _log = new();

    public InstallerForm()
    {
        Text = "התקנת תפריט Gemini";
        RightToLeft = RightToLeft.Yes;
        RightToLeftLayout = true;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(540, 430);
        Font = new Font("Segoe UI", 10f);

        var intro = new Label
        {
            Text = "מוסיף לתפריט הלחיצה הימנית של Windows 11 את הפקודות \"תמלל הקלטה\" (קובצי שמע) ו\"סכם לי\" (pdf, txt, docx, md).",
            Bounds = new Rectangle(14, 12, 512, 44),
        };
        var keyLabel = new Label { Text = "מפתח Gemini API:", Bounds = new Rectangle(14, 64, 512, 22) };
        _key.Bounds = new Rectangle(14, 88, 512, 28);
        _key.UseSystemPasswordChar = true;
        var existing = SafeLoad();
        if (!string.IsNullOrEmpty(existing.ApiKey))
            keyLabel.Text = "מפתח Gemini API (כבר שמור מפתח. השאר ריק כדי לא לשנות):";

        _show.Text = "הצג את המפתח";
        _show.Bounds = new Rectangle(14, 120, 200, 24);
        _show.CheckedChanged += (_, _) => _key.UseSystemPasswordChar = !_show.Checked;

        var link = new LinkLabel { Text = "אין לך מפתח? קבל אחד בחינם (נפתח בדפדפן)", Bounds = new Rectangle(220, 120, 306, 24), TextAlign = ContentAlignment.MiddleLeft };
        link.LinkClicked += (_, _) => Process.Start(new ProcessStartInfo("https://aistudio.google.com/apikey") { UseShellExecute = true });

        _install.Text = "התקן / עדכן";
        _install.Bounds = new Rectangle(14 + 512 - 150, 156, 150, 34);
        _install.Click += async (_, _) => await Run(() => Installer.Install(_key.Text, Log), validateKey: true);

        _uninstall.Text = "הסר התקנה";
        _uninstall.Bounds = new Rectangle(14, 156, 150, 34);
        _uninstall.Click += async (_, _) => await Run(() => Installer.Uninstall(Log), validateKey: false);

        _log.Multiline = true;
        _log.ReadOnly = true;
        _log.ScrollBars = ScrollBars.Vertical;
        _log.Bounds = new Rectangle(14, 202, 512, 214);

        Controls.AddRange(new Control[] { intro, keyLabel, _key, _show, link, _install, _uninstall, _log });
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
            _install.Enabled = _uninstall.Enabled = true;
        }
    }
}

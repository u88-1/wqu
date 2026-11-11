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
    static readonly Color Good = Color.FromArgb(22, 128, 60);
    static readonly Color Bad = Color.FromArgb(180, 35, 24);

    /// <summary>One provider: its key box, a "check" button and a small status text.</summary>
    sealed class KeyRow
    {
        public string Name = "";
        public TextBox Box = new();
        public Label Status = new();
        public Button Check = new();
        public Func<string, CancellationToken, Task<bool>> Validate = (_, _) => Task.FromResult(true);
    }

    readonly List<KeyRow> _rows = new();
    readonly CheckBox _show = new();
    readonly Button _install = new();
    readonly Button _uninstall = new();
    readonly Button _settings = new();
    readonly Button _cancel = new();
    readonly TextBox _log = new();
    readonly ProgressBar _bar = new();
    CancellationTokenSource? _cts;

    public InstallerForm()
    {
        Text = "תפריט Gemini";
        RightToLeft = RightToLeft.Yes;
        RightToLeftLayout = false; // coordinates below are plain left-to-right pixels
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScroll = true; // small screens: scroll instead of cutting the buttons off
        Font = new Font("Segoe UI", 10f);
        BackColor = PageBg;
        ForeColor = Ink;
        ClientSize = new Size(560, 606);
        try { Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!); } catch { }
        Load += (_, _) =>
        {
            var wa = Screen.FromControl(this).WorkingArea;
            if (Height > wa.Height) { Height = wa.Height; Top = wa.Top; }
        };

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
        var card = new Panel { Bounds = new Rectangle(24, 112, 512, 312), BackColor = Color.White };
        card.Paint += (_, e) => e.Graphics.DrawRectangle(new Pen(Color.FromArgb(228, 232, 240)), 0, 0, card.Width - 1, card.Height - 1);

        var what = new Label
        {
            Text = "מוסיף לתפריט הראשי של Windows 11 את \"תמלל הקלטה\" (שמע ווידאו MP4) ואת \"סכם לי\" (pdf, txt, docx, md). " +
                   "מספיק מפתח אחד. אפשר להזין כמה ספקים ולבחור ביניהם אחר כך בהגדרות.",
            Bounds = new Rectangle(20, 12, 472, 50),
        };
        card.Controls.Add(what);

        var existing = SafeLoad();
        int y = 66;
        AddKeyRow(card, ref y, "Gemini (Google)", "https://aistudio.google.com/apikey", existing.ApiKey,
            (k, ct) => GeminiClient.ValidateKeyAsync(k, null, ct));
        AddKeyRow(card, ref y, "Claude (Anthropic)", "https://console.anthropic.com/settings/keys", existing.ClaudeKey,
            (k, ct) => ClaudeClient.ValidateKeyAsync(k, null, ct));
        AddKeyRow(card, ref y, "OpenAI (GPT)", "https://platform.openai.com/api-keys", existing.OpenAiKey,
            (k, ct) => OpenAiClient.ValidateKeyAsync(k, null, ct));

        _show.Text = "הצג את המפתחות";
        _show.Bounds = new Rectangle(20, y, 200, 24);
        _show.CheckedChanged += (_, _) => { foreach (var r in _rows) r.Box.UseSystemPasswordChar = !_show.Checked; };

        var note = new Label
        {
            Text = "המפתחות נשמרים מוצפנים ב-AppData ונשארים גם אחרי הסרה והתקנה מחדש.",
            ForeColor = Muted,
            Font = new Font("Segoe UI", 9f),
            Bounds = new Rectangle(20, y + 28, 472, 22),
        };
        card.Controls.AddRange(new Control[] { _show, note });

        // ---- buttons
        StyleButton(_install, "התקן / עדכן", primary: true);
        _install.Bounds = new Rectangle(24 + 512 - 220, 440, 220, 46);
        _install.Click += async (_, _) => await Run(ct => Installer.Install(
            new Installer.InstallKeys(KeyOf(0), KeyOf(1), KeyOf(2)), Log, ct), validateKeys: true);

        StyleButton(_uninstall, "הסר התקנה", primary: false);
        _uninstall.Bounds = new Rectangle(24, 440, 150, 46);
        _uninstall.Click += async (_, _) => await Run(ct => Installer.Uninstall(Log, ct), validateKeys: false);

        StyleButton(_settings, "הגדרות...", primary: false);
        _settings.Bounds = new Rectangle(190, 440, 110, 46);
        _settings.Click += (_, _) => { using var f = new SettingsForm(); f.ShowDialog(this); };

        // Shown instead of "settings" while something is running.
        StyleButton(_cancel, "בטל", primary: false);
        _cancel.Bounds = _settings.Bounds;
        _cancel.Visible = false;
        _cancel.Click += (_, _) => { Log("מבטל..."); try { _cts?.Cancel(); } catch { } };

        _bar.Style = ProgressBarStyle.Marquee;
        _bar.MarqueeAnimationSpeed = 30;
        _bar.Visible = false;
        _bar.Bounds = new Rectangle(24, 496, 512, 6);

        // ---- log
        _log.Multiline = true;
        _log.ReadOnly = true;
        _log.ScrollBars = ScrollBars.Vertical;
        _log.BorderStyle = BorderStyle.FixedSingle;
        _log.BackColor = Color.White;
        _log.Font = new Font("Segoe UI", 9.5f);
        _log.Bounds = new Rectangle(24, 508, 512, 90);

        Controls.AddRange(new Control[] { header, card, _install, _uninstall, _settings, _cancel, _bar, _log });
        Log("מוכן. הדבק מפתח אחד או יותר ולחץ \"התקן / עדכן\".");
    }

    string KeyOf(int i) => _rows[i].Box.Text.Trim();

    /// <summary>Adds "title + link", a key box with a check button, and a status text. Advances <paramref name="y"/>.</summary>
    void AddKeyRow(Panel card, ref int y, string name, string url, string savedKey, Func<string, CancellationToken, Task<bool>> validate)
    {
        var row = new KeyRow { Name = name, Validate = validate };

        var label = new Label
        {
            Text = "מפתח " + name,
            Font = new Font("Segoe UI Semibold", 10f),
            Bounds = new Rectangle(20, y, 472, 22),
        };
        var link = new LinkLabel
        {
            Text = "קבל מפתח",
            Bounds = new Rectangle(20, y, 110, 22),
            LinkColor = Accent,
            ActiveLinkColor = AccentDark,
            TextAlign = ContentAlignment.MiddleLeft,
            RightToLeft = RightToLeft.No,
        };
        link.LinkClicked += (_, _) =>
        {
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
        };
        row.Status.Bounds = new Rectangle(136, y, 160, 22);
        row.Status.Font = new Font("Segoe UI", 9f);
        row.Status.ForeColor = Muted;
        row.Status.TextAlign = ContentAlignment.MiddleLeft;
        row.Status.Text = string.IsNullOrEmpty(savedKey) ? "" : "שמור";

        row.Box.Bounds = new Rectangle(110, y + 24, 382, 28);
        row.Box.Font = new Font("Segoe UI", 10.5f);
        row.Box.UseSystemPasswordChar = true;
        row.Box.BorderStyle = BorderStyle.FixedSingle;
        row.Box.RightToLeft = RightToLeft.No; // keys are Latin text
        row.Box.TextAlign = HorizontalAlignment.Left;
        row.Box.Text = savedKey;

        row.Check.Text = "בדוק";
        row.Check.Bounds = new Rectangle(20, y + 23, 80, 30);
        row.Check.Cursor = Cursors.Hand;
        row.Check.Click += async (_, _) =>
        {
            try { await CheckRowAsync(row, CancellationToken.None); } catch (OperationCanceledException) { }
        };

        card.Controls.AddRange(new Control[] { label, link, row.Status, row.Box, row.Check });
        _rows.Add(row);
        y += 62;
    }

    void SetStatus(KeyRow row, string text, Color color)
    {
        row.Status.Text = text;
        row.Status.ForeColor = color;
    }

    /// <summary>Checks one key against its provider. Never throws for network problems; gives up after 12 seconds.</summary>
    async Task CheckRowAsync(KeyRow row, CancellationToken outer)
    {
        var key = row.Box.Text.Trim();
        if (key.Length == 0) { SetStatus(row, "לא הוזן מפתח", Muted); return; }

        SetStatus(row, "בודק...", Muted);
        row.Check.Enabled = false;
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(outer);
            cts.CancelAfter(TimeSpan.FromSeconds(12));
            bool ok = await row.Validate(key, cts.Token);
            if (ok)
            {
                SetStatus(row, "המפתח תקין ✓", Good);
                Log(row.Name + ": המפתח תקין.");
            }
            else
            {
                SetStatus(row, "הספק דחה את המפתח", Bad);
                Log(row.Name + ": הספק דחה את המפתח (או שאין חיבור לאינטרנט). ההתקנה תימשך, אבל כדאי לבדוק אותו.");
            }
        }
        catch (OperationCanceledException) when (outer.IsCancellationRequested) { throw; }
        catch (OperationCanceledException)
        {
            SetStatus(row, "אין תשובה מהשרת", Muted);
            Log(row.Name + ": לא התקבלה תשובה תוך 12 שניות. ההתקנה ממשיכה בלי הבדיקה.");
        }
        catch (Exception)
        {
            SetStatus(row, "לא הצלחתי לבדוק", Muted);
            Log(row.Name + ": לא הצלחתי לבדוק את המפתח (אין אינטרנט?). ההתקנה ממשיכה.");
        }
        finally
        {
            row.Check.Enabled = true;
        }
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

    void SetBusy(bool busy)
    {
        _install.Enabled = _uninstall.Enabled = !busy;
        _settings.Visible = !busy;
        _cancel.Visible = busy;
        _bar.Visible = busy;
        foreach (var r in _rows) r.Box.ReadOnly = busy;
    }

    /// <summary>Runs one action off the UI thread. The key check never blocks the work, and "cancel" stops the current step.</summary>
    async Task Run(Action<CancellationToken> action, bool validateKeys)
    {
        SetBusy(true);
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        try
        {
            if (validateKeys)
            {
                var checks = _rows.Where(r => r.Box.Text.Trim().Length > 0).Select(r => CheckRowAsync(r, ct)).ToList();
                if (checks.Count == 0) Log("לא הוזן מפתח. אפשר להוסיף אותו אחר כך, בהפעלה חוזרת של ההתקנה או בהגדרות.");
                else { Log("בודק את המפתחות מול הספקים (עד 12 שניות)..."); await Task.WhenAll(checks); }
            }
            await Task.Run(() => action(ct), ct);
        }
        catch (OperationCanceledException)
        {
            Log("הפעולה בוטלה. אם התפריט נעלם מהלחיצה הימנית, הפעל שוב את \"התקן / עדכן\".");
        }
        catch (Exception ex)
        {
            Log("שגיאה: " + ex.Message);
        }
        finally
        {
            _cts?.Dispose();
            _cts = null;
            SetBusy(false);
        }
    }
}

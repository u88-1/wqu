using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;

namespace GeminiMenu;

public enum ResultFormat { Html, Text, Both, Srt }

/// <summary>Small always-on-top progress window shown while Gemini works.</summary>
public sealed class WaitForm : Form
{
    readonly Label _title = new();
    readonly Label _status = new();
    readonly Label _elapsedLabel = new();
    readonly ComboBox _format = new();
    readonly ProgressBar _bar = new();
    readonly Button _cancel = new();
    readonly LinkLabel _update = new();
    readonly System.Windows.Forms.Timer _clock = new() { Interval = 1000 };
    readonly Stopwatch _elapsed = Stopwatch.StartNew();
    public event Action? CancelRequested;
    public bool Finished { get; set; }
    public ResultFormat SelectedFormat => _format.SelectedIndex switch
    {
        1 => ResultFormat.Text,
        2 => ResultFormat.Both,
        3 => ResultFormat.Srt,
        _ => ResultFormat.Html,
    };

    static readonly Color Accent = Color.FromArgb(46, 110, 235);
    static readonly Color Muted = Color.FromArgb(102, 112, 133);

    public WaitForm(string title, string fileName, bool allowSrt = false, string defaultFormat = "html")
    {
        Text = "Gemini – " + title;
        RightToLeft = RightToLeft.Yes;
        RightToLeftLayout = true;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = true;
        TopMost = true;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(480, 300);
        Font = new Font("Segoe UI", 10f);
        BackColor = Color.White;
        ShowInTaskbar = true;
        ShowIcon = true;
        try
        {
            using var res = System.Reflection.Assembly.GetExecutingAssembly().GetManifestResourceStream("mic.ico");
            if (res != null) Icon = new Icon(res);
            else Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!);
        }
        catch { try { Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!); } catch { } }

        // Header band with the app icon, the action and the file name.
        var header = new Panel { Dock = DockStyle.Top, Height = 72, BackColor = Accent };
        var logo = new PictureBox { SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.White };
        logo.SetBounds(16, 16, 40, 40);
        try { if (Icon != null) logo.Image = new Icon(Icon, 48, 48).ToBitmap(); } catch { }
        var heading = new Label
        {
            Text = title + "...",
            ForeColor = Color.White,
            BackColor = Accent,
            Font = new Font("Segoe UI Semibold", 13f),
            AutoEllipsis = true,
        };
        heading.SetBounds(68, 10, 396, 28);
        _title.Text = fileName;
        _title.ForeColor = Color.FromArgb(225, 235, 255);
        _title.BackColor = Accent;
        _title.AutoEllipsis = true;
        _title.Font = new Font("Segoe UI", 9.5f);
        _title.SetBounds(68, 40, 396, 22);
        header.Controls.AddRange(new Control[] { logo, heading, _title });

        _status.Text = "מתחיל...";
        _status.AutoEllipsis = true;
        _status.Font = new Font("Segoe UI", 10.5f);
        _status.SetBounds(16, 86, 448, 44);

        _bar.Style = ProgressBarStyle.Marquee;
        _bar.MarqueeAnimationSpeed = 30;
        _bar.SetBounds(16, 134, 448, 10);

        _elapsedLabel.Text = "זמן ריצה: 00:00:00";
        _elapsedLabel.ForeColor = Muted;
        _elapsedLabel.SetBounds(16, 150, 448, 22);

        var formatLabel = new Label { Text = "שמור כ:", ForeColor = Muted };
        formatLabel.SetBounds(16, 186, 70, 26);

        _format.DropDownStyle = ComboBoxStyle.DropDownList;
        _format.FlatStyle = FlatStyle.Flat;
        _format.Items.AddRange(new object[] { "דף HTML (נפתח בדפדפן)", "קובץ טקסט (TXT)", "גם HTML וגם TXT" });
        if (allowSrt) _format.Items.Add("כתוביות (SRT)");
        _format.SelectedIndex = defaultFormat switch { "text" => 1, "both" => 2, "srt" when allowSrt => 3, _ => 0 };
        _format.SetBounds(92, 183, 372, 28);

        _cancel.Text = "ביטול";
        _cancel.FlatStyle = FlatStyle.Flat;
        _cancel.FlatAppearance.BorderColor = Color.FromArgb(200, 205, 215);
        _cancel.BackColor = Color.White;
        _cancel.Cursor = Cursors.Hand;
        _cancel.SetBounds(16, 226, 96, 30);
        _cancel.Click += (_, _) => { CancelRequested?.Invoke(); Close(); };

        var minimizeHint = new Label
        {
            Text = "אפשר למזער את החלון, העבודה ממשיכה ברקע.",
            ForeColor = Muted,
            Font = new Font("Segoe UI", 9f),
            TextAlign = ContentAlignment.MiddleRight,
        };
        minimizeHint.SetBounds(124, 226, 340, 30);

        _update.Visible = false;
        _update.LinkColor = Accent;
        _update.TextAlign = ContentAlignment.MiddleRight;
        _update.SetBounds(16, 266, 448, 24);
        _update.LinkClicked += (_, _) =>
        {
            try { if (_update.Tag is string url) Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
        };

        Controls.AddRange(new Control[] { header, _status, _bar, _elapsedLabel, formatLabel, _format, _cancel, minimizeHint, _update });
        _clock.Tick += (_, _) => UpdateElapsed();
        _clock.Start();
        FormClosed += (_, _) => { _clock.Stop(); _clock.Dispose(); };
        FormClosing += (_, _) => { if (!Finished) CancelRequested?.Invoke(); };
    }

    static readonly System.Text.RegularExpressions.Regex ChunkProgress =
        new(@"קטע (\d+) מתוך (\d+)", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>Shows a small "new version available" link at the bottom of the window.</summary>
    public void ShowUpdate(string text, string url)
    {
        if (IsDisposed) return;
        if (InvokeRequired) { try { BeginInvoke(() => ShowUpdate(text, url)); } catch { } return; }
        _update.Text = text;
        _update.Tag = url;
        _update.Visible = true;
    }

    public void SetStatus(string text)
    {
        if (IsDisposed) return;
        if (InvokeRequired) { try { BeginInvoke(() => SetStatus(text)); } catch { } return; }
        _status.Text = text;

        // Real progress for long recordings ("מתמלל קטע 3 מתוך 20...").
        var m = ChunkProgress.Match(text);
        if (m.Success && int.TryParse(m.Groups[1].Value, out var cur) && int.TryParse(m.Groups[2].Value, out var tot) && tot > 0)
        {
            if (_bar.Style != ProgressBarStyle.Continuous) _bar.Style = ProgressBarStyle.Continuous;
            _bar.Maximum = tot;
            _bar.Value = Math.Clamp(cur - 1, 0, tot);
        }
    }

    void UpdateElapsed()
    {
        var elapsed = _elapsed.Elapsed;
        _elapsedLabel.Text = $"זמן ריצה: {(int)elapsed.TotalHours:00}:{elapsed.Minutes:00}:{elapsed.Seconds:00}";
    }
}

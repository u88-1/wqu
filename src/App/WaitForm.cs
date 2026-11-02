using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;

namespace GeminiMenu;

public enum ResultFormat { Html, Text, Both }

/// <summary>Small always-on-top progress window shown while Gemini works.</summary>
public sealed class WaitForm : Form
{
    readonly Label _title = new();
    readonly Label _status = new();
    readonly Label _elapsedLabel = new();
    readonly ComboBox _format = new();
    readonly ProgressBar _bar = new();
    readonly Button _cancel = new();
    readonly System.Windows.Forms.Timer _clock = new() { Interval = 1000 };
    readonly Stopwatch _elapsed = Stopwatch.StartNew();
    public event Action? CancelRequested;
    public bool Finished { get; set; }
    public ResultFormat SelectedFormat => _format.SelectedIndex switch
    {
        1 => ResultFormat.Text,
        2 => ResultFormat.Both,
        _ => ResultFormat.Html,
    };

    public WaitForm(string title, string fileName)
    {
        Text = "Gemini";
        RightToLeft = RightToLeft.Yes;
        RightToLeftLayout = true;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        TopMost = true;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(440, 220);
        Font = new Font("Segoe UI", 10f);
        ShowInTaskbar = true;
        ShowIcon = true;
        Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!);

        _title.Text = title + ": " + fileName;
        _title.AutoEllipsis = true;
        _title.SetBounds(14, 12, 412, 24);
        _title.Font = new Font("Segoe UI", 10.5f, FontStyle.Bold);

        _status.Text = "מתחיל...";
        _status.AutoEllipsis = true;
        _status.SetBounds(14, 42, 412, 38);

        _elapsedLabel.Text = "זמן ריצה: 00:00:00";
        _elapsedLabel.SetBounds(14, 82, 412, 22);

        var formatLabel = new Label { Text = "פורמט התוצאה:" };
        formatLabel.SetBounds(14, 110, 110, 26);

        _format.DropDownStyle = ComboBoxStyle.DropDownList;
        _format.Items.AddRange(new object[] { "דף HTML בלבד", "קובץ טקסט (TXT) בלבד", "גם HTML וגם TXT" });
        _format.SelectedIndex = 0;
        _format.SetBounds(130, 106, 296, 28);

        _bar.Style = ProgressBarStyle.Marquee;
        _bar.MarqueeAnimationSpeed = 30;
        _bar.SetBounds(14, 148, 412, 12);

        _cancel.Text = "ביטול";
        _cancel.SetBounds(14, 174, 90, 30);
        _cancel.Click += (_, _) => { CancelRequested?.Invoke(); Close(); };

        Controls.AddRange(new Control[] { _title, _status, _elapsedLabel, formatLabel, _format, _bar, _cancel });
        _clock.Tick += (_, _) => UpdateElapsed();
        _clock.Start();
        FormClosed += (_, _) => { _clock.Stop(); _clock.Dispose(); };
        FormClosing += (_, _) => { if (!Finished) CancelRequested?.Invoke(); };
    }

    public void SetStatus(string text)
    {
        if (IsDisposed) return;
        if (InvokeRequired) { try { BeginInvoke(() => SetStatus(text)); } catch { } return; }
        _status.Text = text;
    }

    void UpdateElapsed()
    {
        var elapsed = _elapsed.Elapsed;
        _elapsedLabel.Text = $"זמן ריצה: {(int)elapsed.TotalHours:00}:{elapsed.Minutes:00}:{elapsed.Seconds:00}";
    }
}

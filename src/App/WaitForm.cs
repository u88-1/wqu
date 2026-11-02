using System.Drawing;
using System.Windows.Forms;

namespace GeminiMenu;

/// <summary>Small always-on-top progress window shown while Gemini works.</summary>
public sealed class WaitForm : Form
{
    readonly Label _title = new();
    readonly Label _status = new();
    readonly ProgressBar _bar = new();
    readonly Button _cancel = new();
    public event Action? CancelRequested;
    public bool Finished { get; set; }

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
        ClientSize = new Size(420, 150);
        Font = new Font("Segoe UI", 10f);
        ShowInTaskbar = true;

        _title.Text = title + ": " + fileName;
        _title.AutoEllipsis = true;
        _title.SetBounds(14, 12, 392, 24);
        _title.Font = new Font("Segoe UI", 10.5f, FontStyle.Bold);

        _status.Text = "מתחיל...";
        _status.AutoEllipsis = true;
        _status.SetBounds(14, 42, 392, 40);

        _bar.Style = ProgressBarStyle.Marquee;
        _bar.MarqueeAnimationSpeed = 30;
        _bar.SetBounds(14, 88, 392, 12);

        _cancel.Text = "ביטול";
        _cancel.SetBounds(14, 110, 90, 30);
        _cancel.Click += (_, _) => { CancelRequested?.Invoke(); Close(); };

        Controls.AddRange(new Control[] { _title, _status, _bar, _cancel });
        FormClosing += (_, _) => { if (!Finished) CancelRequested?.Invoke(); };
    }

    public void SetStatus(string text)
    {
        if (IsDisposed) return;
        if (InvokeRequired) { try { BeginInvoke(() => SetStatus(text)); } catch { } return; }
        _status.Text = text;
    }
}

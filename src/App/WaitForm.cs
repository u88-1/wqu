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
    readonly ComboBox _output = new();
    readonly Label _outputLabel = new();
    readonly TextBox _customInstruction = new();
    readonly ProgressBar _bar = new();
    readonly Button _cancel = new();
    readonly LinkLabel _update = new();
    readonly CheckBox _withTimestamps = new();
    readonly System.Windows.Forms.Timer _clock = new() { Interval = 1000 };
    readonly Stopwatch _elapsed = Stopwatch.StartNew();
    readonly bool _allowSrt;
    readonly bool _isTranscription;
    public event Action? CancelRequested;
    public bool Finished { get; set; }
    /// <summary>Whether the saved file should keep the [HH:MM:SS] stamps (transcripts only).</summary>
    public bool IncludeTimestamps => _withTimestamps.Checked;
    public OutputSelection SelectedOutput => _isTranscription
        ? _output.SelectedIndex switch
        {
            1 => new OutputSelection(OutputMode.TranscriptClean),
            2 => new OutputSelection(OutputMode.TranscriptBookStyle),
            3 => new OutputSelection(OutputMode.TranscriptAndSummary),
            4 => new OutputSelection(OutputMode.TranscriptSummaryOnly),
            5 => new OutputSelection(OutputMode.TranscriptMeetingMinutes),
            6 => new OutputSelection(OutputMode.TranscriptCustomInstruction, _customInstruction.Text),
            _ => new OutputSelection(OutputMode.TranscriptExact),
        }
        : _output.SelectedIndex switch
        {
            1 => new OutputSelection(OutputMode.DocumentBrief),
            2 => new OutputSelection(OutputMode.DocumentConclusions),
            3 => new OutputSelection(OutputMode.DocumentTasksDatesAmounts),
            4 => new OutputSelection(OutputMode.DocumentBookStyle),
            5 => new OutputSelection(OutputMode.DocumentCustomQuestion, _customInstruction.Text),
            _ => new OutputSelection(OutputMode.DocumentKeyPoints),
        };

    public ResultFormat SelectedFormat => _format.SelectedIndex switch
    {
        1 => ResultFormat.Text,
        2 => ResultFormat.Both,
        3 => ResultFormat.Srt,
        _ => ResultFormat.Html,
    };

    static readonly Color Accent = Color.FromArgb(46, 110, 235);
    static readonly Color Muted = Color.FromArgb(102, 112, 133);

    public WaitForm(string title, string fileName, bool allowSrt = false, string defaultFormat = "html", bool isTranscription = false)
    {
        _allowSrt = allowSrt;
        _isTranscription = isTranscription;
        Text = "Gemini – " + title;
        RightToLeft = RightToLeft.Yes;
        RightToLeftLayout = true;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = true;
        TopMost = true;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(480, 382);
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

        _outputLabel.Text = "מה להפיק:";
        _outputLabel.ForeColor = Muted;
        _outputLabel.SetBounds(16, 177, 70, 26);

        _output.DropDownStyle = ComboBoxStyle.DropDownList;
        _output.FlatStyle = FlatStyle.Flat;
        _output.Items.AddRange(isTranscription
            ? new object[] { "תמלול מדויק", "תמלול נקי", "תמלול בסגנון ספר", "תמלול ואחריו סיכום נקודות", "סיכום בלבד (בלי התמלול)", "פרוטוקול פגישה", "הוראה חופשית..." }
            : new object[] { "נקודות עיקריות (המבנה הנוכחי)", "סיכום מקוצר", "מסקנות: החלטות, בעיות, חסרים והמלצות", "משימות, תאריכים וסכומים בלבד", "עריכה בסגנון ספר", "שאלה חופשית..." });
        _output.SelectedIndex = 0;
        _output.SetBounds(92, 174, 372, 28);
        _output.SelectedIndexChanged += (_, _) =>
        {
            bool custom = SelectedOutput.Mode is OutputMode.DocumentCustomQuestion or OutputMode.TranscriptCustomInstruction;
            _customInstruction.Visible = custom;
            UpdateFormatOptions();
        };

        _customInstruction.Multiline = true;
        _customInstruction.ScrollBars = ScrollBars.Vertical;
        _customInstruction.PlaceholderText = isTranscription ? "כתוב הוראה, למשל: הוצא רק מה שנאמר על התקציב" : "כתוב שאלה, למשל: מה המסמך אומר על תנאי התשלום?";
        _customInstruction.SetBounds(16, 207, 448, 42);
        _customInstruction.Visible = false;

        var formatLabel = new Label { Text = "שמור כ:", ForeColor = Muted };
        formatLabel.SetBounds(16, 256, 70, 26);

        _format.DropDownStyle = ComboBoxStyle.DropDownList;
        _format.FlatStyle = FlatStyle.Flat;
        _format.SetBounds(92, 253, 372, 28);
        UpdateFormatOptions();
        _format.SelectedIndex = defaultFormat switch
        {
            "text" => 1,
            "both" => 2,
            "srt" when _format.Items.Count > 3 => 3,
            _ => 0,
        };
        if (SelectedFormat == ResultFormat.Srt) _withTimestamps.Checked = true;
        RefreshTimestampBox();
        _format.SelectedIndexChanged += (_, _) => RefreshTimestampBox();

        _withTimestamps.Text = "כלול חותמות זמן בקובץ";
        _withTimestamps.Checked = true;
        _withTimestamps.Visible = allowSrt;
        _withTimestamps.SetBounds(16, 285, 448, 26);
        // Subtitles (SRT) cannot exist without timestamps.
        _cancel.Text = "ביטול";
        _cancel.FlatStyle = FlatStyle.Flat;
        _cancel.FlatAppearance.BorderColor = Color.FromArgb(200, 205, 215);
        _cancel.BackColor = Color.White;
        _cancel.Cursor = Cursors.Hand;
        _cancel.SetBounds(16, 318, 96, 30);
        _cancel.Click += (_, _) => { CancelRequested?.Invoke(); Close(); };

        var minimizeHint = new Label
        {
            Text = "אפשר למזער את החלון, העבודה ממשיכה ברקע.",
            ForeColor = Muted,
            Font = new Font("Segoe UI", 9f),
            TextAlign = ContentAlignment.MiddleRight,
        };
        minimizeHint.SetBounds(124, 318, 340, 30);

        _update.Visible = false;
        _update.LinkColor = Accent;
        _update.TextAlign = ContentAlignment.MiddleRight;
        _update.SetBounds(16, 350, 448, 24);
        _update.LinkClicked += (_, _) =>
        {
            try { if (_update.Tag is string url) Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
        };

        Controls.AddRange(new Control[] { header, _status, _bar, _elapsedLabel, _outputLabel, _output, _customInstruction, formatLabel, _format, _withTimestamps, _cancel, minimizeHint, _update });
        _clock.Tick += (_, _) => UpdateElapsed();
        _clock.Start();
        FormClosed += (_, _) => { _clock.Stop(); _clock.Dispose(); };
        FormClosing += (_, _) => { if (!Finished) CancelRequested?.Invoke(); };
    }

    void UpdateFormatOptions()
    {
        var selected = SelectedFormat;
        bool supportsSrt = _allowSrt && SelectedOutput.Mode is
            OutputMode.TranscriptExact or OutputMode.TranscriptClean or OutputMode.TranscriptAndSummary;
        _format.BeginUpdate();
        _format.Items.Clear();
        _format.Items.AddRange(new object[] { "דף HTML (נפתח בדפדפן)", "קובץ טקסט (TXT)", "גם HTML וגם TXT" });
        if (supportsSrt) _format.Items.Add("כתוביות (SRT)");
        _format.SelectedIndex = selected switch
        {
            ResultFormat.Text => 1,
            ResultFormat.Both => 2,
            ResultFormat.Srt when supportsSrt => 3,
            _ => 0,
        };
        _format.EndUpdate();
        RefreshTimestampBox();
    }

    /// <summary>Subtitles (SRT) always have timestamps; book style never keeps them. Otherwise the user decides.</summary>
    void RefreshTimestampBox()
    {
        bool srt = SelectedFormat == ResultFormat.Srt;
        if (srt) _withTimestamps.Checked = true;
        _withTimestamps.Enabled = !srt && SelectedOutput.Mode != OutputMode.TranscriptBookStyle;
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

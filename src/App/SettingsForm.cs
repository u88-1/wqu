using System.Drawing;
using System.Windows.Forms;

namespace GeminiMenu;

/// <summary>Advanced settings: everything that used to require editing config.json by hand.</summary>
public sealed class SettingsForm : Form
{
    static readonly Color Accent = Color.FromArgb(37, 99, 235);
    static readonly Color Muted = Color.FromArgb(102, 112, 133);

    readonly AppConfig _cfg;
    readonly ComboBox _model = new();
    readonly CheckBox _timestamps = new();
    readonly NumericUpDown _parallel = new();
    readonly NumericUpDown _chunk = new();
    readonly ComboBox _format = new();
    readonly TextBox _outDir = new();
    readonly ComboBox _detail = new();
    readonly NumericUpDown _jobs = new();
    readonly CheckBox _updates = new();

    static readonly string[] FormatKeys = { "html", "text", "both", "docx", "srt" };
    const int FormHeight = 700;
    static readonly string[] DetailKeys = { "short", "normal", "detailed" };

    public SettingsForm()
    {
        try { _cfg = AppConfig.Load(); } catch { _cfg = new AppConfig(); }

        Text = "הגדרות מתקדמות";
        RightToLeft = RightToLeft.Yes;
        RightToLeftLayout = false; // plain left-to-right pixel coordinates, like the installer window
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        Font = new Font("Segoe UI", 10f);
        BackColor = Color.White;
        ClientSize = new Size(580, FormHeight);
        try
        {
            using var res = System.Reflection.Assembly.GetExecutingAssembly().GetManifestResourceStream("mic.ico");
            if (res != null) Icon = new Icon(res);
        }
        catch { }

        int y = 16;
        void Label(string text, bool muted = false, int height = 24)
        {
            var l = new Label { Text = text, Bounds = new Rectangle(20, y, 540, height), AutoEllipsis = true };
            if (muted) { l.ForeColor = Muted; l.Font = new Font("Segoe UI", 9f); }
            Controls.Add(l);
            y += height + 2;
        }
        void Field(Control c, int width = 260, int height = 28)
        {
            c.Bounds = new Rectangle(300, y, width, height);
            Controls.Add(c);
            y += height + 12;
        }

        Label("דגם Gemini (auto = הדגם החדש ביותר שזמין)");
        _model.DropDownStyle = ComboBoxStyle.DropDown;
        _model.Items.AddRange(new object[] { "auto", "gemini-3.5-flash", "gemini-3-flash-preview", "gemini-2.5-flash" });
        _model.Text = _cfg.Model;
        _model.RightToLeft = RightToLeft.No;
        Field(_model);

        _timestamps.Text = "בקש חותמות זמן [HH:MM:SS] בתמלול (בחלון התמלול בוחרים אם לכלול אותן בקובץ)";
        _timestamps.Checked = _cfg.Timestamps;
        _timestamps.Bounds = new Rectangle(20, y, 540, 26);
        Controls.Add(_timestamps);
        y += 38;

        Label("קטעים במקביל");
        Label("1 = לפי הסדר (תוויות דוברים עקביות בין קטעים). 2 עד 4 מהיר יותר, אבל תוויות הדוברים עלולות להתחלף.", muted: true, height: 36);
        _parallel.Minimum = 1; _parallel.Maximum = 4; _parallel.Value = Math.Clamp(_cfg.ParallelChunks, 1, 4);
        Field(_parallel, 90);

        Label("אורך קטע בהקלטות ארוכות (שניות)");
        _chunk.Minimum = 60; _chunk.Maximum = 300; _chunk.Increment = 30; _chunk.Value = Math.Clamp(_cfg.ChunkSeconds, 60, 300);
        Field(_chunk, 90);

        Label("פורמט שמירה כברירת מחדל");
        _format.DropDownStyle = ComboBoxStyle.DropDownList;
        _format.Items.AddRange(new object[] { "דף HTML", "קובץ טקסט (TXT)", "HTML וגם TXT", "קובץ Word (DOCX)", "כתוביות (SRT)" });
        _format.SelectedIndex = Math.Max(0, Array.IndexOf(FormatKeys, _cfg.DefaultFormat));
        Field(_format);

        Label("רמת פירוט בסיכום מסמכים");
        _detail.DropDownStyle = ComboBoxStyle.DropDownList;
        _detail.Items.AddRange(new object[] { "קצר", "רגיל", "מפורט" });
        _detail.SelectedIndex = Math.Max(0, Array.IndexOf(DetailKeys, _cfg.SummaryDetail));
        if (_detail.SelectedIndex < 0) _detail.SelectedIndex = 1;
        Field(_detail);

        Label("תיקיית שמירה (ריק = ליד הקובץ המקורי)");
        _outDir.Text = _cfg.OutputDir;
        _outDir.RightToLeft = RightToLeft.No;
        _outDir.Bounds = new Rectangle(110, y, 450, 28);
        var browse = new Button { Text = "בחר...", Bounds = new Rectangle(20, y - 1, 80, 30) };
        browse.Click += (_, _) =>
        {
            using var d = new FolderBrowserDialog { SelectedPath = _outDir.Text };
            if (d.ShowDialog(this) == DialogResult.OK) _outDir.Text = d.SelectedPath;
        };
        Controls.Add(_outDir);
        Controls.Add(browse);
        y += 40;

        Label("כמה קבצים לעבד בו-זמנית (בין כל החלונות הפתוחים)");
        _jobs.Minimum = 1; _jobs.Maximum = 4; _jobs.Value = Math.Clamp(_cfg.MaxConcurrentJobs, 1, 4);
        Field(_jobs, 90);

        _updates.Text = "בדוק אם יצאה גרסה חדשה";
        _updates.Checked = _cfg.CheckUpdates;
        _updates.Bounds = new Rectangle(20, y, 540, 26);
        Controls.Add(_updates);
        y += 36;

        var privacy = new Label
        {
            Text = "פרטיות: הקבצים והטקסטים נשלחים לספק שנבחר (Google, Anthropic או OpenAI) לצורך העיבוד. במפתח חינמי של Google היא רשאית להשתמש בתוכן לשיפור המוצרים שלה, " +
                   "ולכן להקלטות ומסמכים רגישים מומלץ להשתמש במפתח של חשבון בתשלום.",
            ForeColor = Muted,
            Font = new Font("Segoe UI", 9f),
            Bounds = new Rectangle(20, y, 540, 52),
        };
        Controls.Add(privacy);

        var save = new Button
        {
            Text = "שמור",
            Bounds = new Rectangle(580 - 20 - 120, FormHeight - 56, 120, 38),
            FlatStyle = FlatStyle.Flat,
            BackColor = Accent,
            ForeColor = Color.White,
            UseVisualStyleBackColor = false,
        };
        save.FlatAppearance.BorderSize = 0;
        save.Click += (_, _) => SaveAndClose();
        var cancel = new Button { Text = "ביטול", Bounds = new Rectangle(20, FormHeight - 56, 100, 38), DialogResult = DialogResult.Cancel };
        var providers = new Button { Text = "מנועי AI ומפתחות...", Bounds = new Rectangle(140, FormHeight - 56, 300, 38) };
        providers.Click += (_, _) => { using var f = new ProvidersForm(_cfg); f.ShowDialog(this); };
        Controls.Add(save);
        Controls.Add(cancel);
        Controls.Add(providers);
        AcceptButton = save;
        CancelButton = cancel;
    }

    void SaveAndClose()
    {
        try
        {
            _cfg.Model = string.IsNullOrWhiteSpace(_model.Text) ? "auto" : _model.Text.Trim();
            _cfg.Timestamps = _timestamps.Checked;
            _cfg.ParallelChunks = (int)_parallel.Value;
            _cfg.ChunkSeconds = (int)_chunk.Value;
            _cfg.DefaultFormat = FormatKeys[Math.Clamp(_format.SelectedIndex, 0, FormatKeys.Length - 1)];
            _cfg.SummaryDetail = DetailKeys[Math.Clamp(_detail.SelectedIndex, 0, DetailKeys.Length - 1)];
            _cfg.OutputDir = _outDir.Text.Trim();
            _cfg.MaxConcurrentJobs = (int)_jobs.Value;
            _cfg.CheckUpdates = _updates.Checked;
            _cfg.Save();
            DialogResult = DialogResult.OK;
            Close();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "לא הצלחתי לשמור את ההגדרות: " + ex.Message, "הגדרות", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}

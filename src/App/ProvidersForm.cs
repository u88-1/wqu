using System.Drawing;
using System.Windows.Forms;

namespace GeminiMenu;

/// <summary>Which AI does what, the API keys of Gemini, Claude and OpenAI, and the book-style instruction.</summary>
public sealed class ProvidersForm : Form
{
    static readonly Color Accent = Color.FromArgb(37, 99, 235);
    static readonly Color Muted = Color.FromArgb(102, 112, 133);

    readonly AppConfig _cfg;
    readonly ComboBox _audio = new();
    readonly ComboBox _text = new();
    readonly TextBox _geminiKey = new();
    readonly TextBox _claudeKey = new();
    readonly TextBox _openAiKey = new();
    readonly TextBox _claudeModel = new();
    readonly TextBox _openAiModel = new();
    readonly TextBox _openAiTranscribeModel = new();
    readonly TextBox _language = new();
    readonly TextBox _bookPrompt = new();

    static readonly string[] AudioKeys = { "gemini", "openai" };
    static readonly string[] TextKeys = { "gemini", "claude", "openai" };

    public ProvidersForm(AppConfig cfg)
    {
        _cfg = cfg;

        Text = "מנועי AI ומפתחות";
        RightToLeft = RightToLeft.Yes;
        RightToLeftLayout = false; // plain left-to-right pixel coordinates, like the settings window
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        Font = new Font("Segoe UI", 10f);
        BackColor = Color.White;
        ClientSize = new Size(600, 640);
        try
        {
            using var res = System.Reflection.Assembly.GetExecutingAssembly().GetManifestResourceStream("mic.ico");
            if (res != null) Icon = new Icon(res);
        }
        catch { }

        var body = new Panel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = Color.White };
        var bottom = new Panel { Dock = DockStyle.Bottom, Height = 62, BackColor = Color.White };
        Controls.Add(body);
        Controls.Add(bottom);

        int y = 14;
        void Label(string text, bool muted = false, int height = 24)
        {
            var l = new Label { Text = text, Bounds = new Rectangle(20, y, 540, height) };
            if (muted) { l.ForeColor = Muted; l.Font = new Font("Segoe UI", 9f); }
            body.Controls.Add(l);
            y += height + 2;
        }
        void Field(Control c, int width = 260, int height = 28)
        {
            c.Bounds = new Rectangle(300, y, width, height);
            body.Controls.Add(c);
            y += height + 12;
        }
        void KeyRow(string title, TextBox box, Func<string, Task<bool>> validate)
        {
            Label(title);
            box.UseSystemPasswordChar = true;
            box.RightToLeft = RightToLeft.No;
            box.Bounds = new Rectangle(110, y, 450, 28);
            var check = new Button { Text = "בדוק", Bounds = new Rectangle(20, y - 1, 80, 30) };
            check.Click += async (_, _) => await CheckKeyAsync(check, box, validate);
            body.Controls.Add(box);
            body.Controls.Add(check);
            y += 42;
        }
        void ModelRow(string title, TextBox box, string value)
        {
            Label(title);
            box.Text = value;
            box.RightToLeft = RightToLeft.No;
            Field(box);
        }

        // ---- who does what
        Label("מנוע תמלול הקלטות (שמע לטקסט)");
        _audio.DropDownStyle = ComboBoxStyle.DropDownList;
        _audio.Items.AddRange(new object[] { "Gemini", "OpenAI (GPT)" });
        _audio.SelectedIndex = Math.Max(0, Array.IndexOf(AudioKeys, _cfg.TranscriptionProvider));
        Field(_audio);
        Label("Claude אינו מקבל שמע, ולכן אינו זמין לתמלול. עם OpenAI נשמרת חותמת זמן אחת לכל קטע ואין תוויות דוברים.", muted: true, height: 36);

        Label("מנוע עריכה וסיכום (סגנון ספר, ניקוי תמלול, סיכום מסמכים)");
        _text.DropDownStyle = ComboBoxStyle.DropDownList;
        _text.Items.AddRange(new object[] { "Gemini", "Claude", "OpenAI (GPT)" });
        _text.SelectedIndex = Math.Max(0, Array.IndexOf(TextKeys, _cfg.TextProvider));
        Field(_text);

        // ---- keys
        KeyRow("מפתח Gemini (aistudio.google.com/apikey)", _geminiKey, k => GeminiClient.ValidateKeyAsync(k));
        _geminiKey.Text = _cfg.ApiKey;
        KeyRow("מפתח Claude (console.anthropic.com)", _claudeKey, k => ClaudeClient.ValidateKeyAsync(k));
        _claudeKey.Text = _cfg.ClaudeKey;
        KeyRow("מפתח OpenAI (platform.openai.com/api-keys)", _openAiKey, k => OpenAiClient.ValidateKeyAsync(k));
        _openAiKey.Text = _cfg.OpenAiKey;
        Label("המפתחות נשמרים מוצפנים במחשב הזה בלבד. השימוש ב-Claude וב-OpenAI מחויב בחשבון שלך אצלם.", muted: true, height: 36);

        // ---- models
        ModelRow("דגם Claude", _claudeModel, _cfg.ClaudeModel);
        ModelRow("דגם OpenAI לעריכה וסיכום", _openAiModel, _cfg.OpenAiModel);
        ModelRow("דגם OpenAI לתמלול", _openAiTranscribeModel, _cfg.OpenAiTranscribeModel);
        ModelRow("שפת ההקלטות ל-OpenAI (קוד, למשל he; ריק = זיהוי אוטומטי)", _language, _cfg.TranscriptionLanguage);
        Label("שמות הדגמים משתנים עם הזמן. אם מופיעה שגיאה שהדגם לא נמצא, עדכן אותו לפי הרשימה באתר של הספק.", muted: true, height: 36);

        // ---- book style instruction
        Label("הנחיית עריכה בסגנון ספר (אופציונלי)");
        Label("אפשר להדביק כאן הנחיה מלאה משלך. ריק = ההנחיה המובנית. הכללים הטכניים (כותרות, סימון [?]) נוספים אוטומטית.", muted: true, height: 36);
        _bookPrompt.Multiline = true;
        _bookPrompt.ScrollBars = ScrollBars.Vertical;
        _bookPrompt.AcceptsReturn = true;
        _bookPrompt.Text = (_cfg.BookPrompt ?? "").Replace("\r\n", "\n").Replace("\n", "\r\n");
        _bookPrompt.Bounds = new Rectangle(20, y, 540, 170);
        body.Controls.Add(_bookPrompt);
        y += 178;
        var loadDefault = new Button { Text = "טען את ההנחיה המובנית לעריכה", Bounds = new Rectangle(20, y, 260, 32) };
        loadDefault.Click += (_, _) => _bookPrompt.Text = BookStyle.DefaultPrompt.Replace("\n", "\r\n");
        var clear = new Button { Text = "נקה (חזרה למובנית)", Bounds = new Rectangle(290, y, 180, 32) };
        clear.Click += (_, _) => _bookPrompt.Text = "";
        body.Controls.Add(loadDefault);
        body.Controls.Add(clear);
        y += 48;

        var save = new Button
        {
            Text = "שמור",
            Bounds = new Rectangle(600 - 20 - 120, 12, 120, 38),
            FlatStyle = FlatStyle.Flat,
            BackColor = Accent,
            ForeColor = Color.White,
            UseVisualStyleBackColor = false,
        };
        save.FlatAppearance.BorderSize = 0;
        save.Click += (_, _) => SaveAndClose();
        var cancel = new Button { Text = "ביטול", Bounds = new Rectangle(20, 12, 100, 38), DialogResult = DialogResult.Cancel };
        bottom.Controls.Add(save);
        bottom.Controls.Add(cancel);
        AcceptButton = save;
        CancelButton = cancel;
    }

    async Task CheckKeyAsync(Button button, TextBox box, Func<string, Task<bool>> validate)
    {
        var key = box.Text.Trim();
        if (key.Length == 0)
        {
            MessageBox.Show(this, "הדבק קודם מפתח.", "בדיקת מפתח", MessageBoxButtons.OK, MessageBoxIcon.Information,
                MessageBoxDefaultButton.Button1, MessageBoxOptions.RtlReading | MessageBoxOptions.RightAlign);
            return;
        }
        button.Enabled = false;
        try
        {
            bool ok = await validate(key);
            MessageBox.Show(this, ok ? "המפתח תקין." : "המפתח נדחה. בדוק שהועתק במלואו ושיש לו הרשאה.", "בדיקת מפתח",
                MessageBoxButtons.OK, ok ? MessageBoxIcon.Information : MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button1, MessageBoxOptions.RtlReading | MessageBoxOptions.RightAlign);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "לא הצלחתי לבדוק את המפתח: " + ex.Message, "בדיקת מפתח", MessageBoxButtons.OK, MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button1, MessageBoxOptions.RtlReading | MessageBoxOptions.RightAlign);
        }
        finally { button.Enabled = true; }
    }

    void SaveAndClose()
    {
        try
        {
            var audio = AudioKeys[Math.Clamp(_audio.SelectedIndex, 0, AudioKeys.Length - 1)];
            var text = TextKeys[Math.Clamp(_text.SelectedIndex, 0, TextKeys.Length - 1)];
            var claudeKey = _claudeKey.Text.Trim();
            var openAiKey = _openAiKey.Text.Trim();
            var geminiKey = _geminiKey.Text.Trim();

            // Warn (but still save) when a chosen engine has no key: the work would fail later.
            var missing = new List<string>();
            if ((audio == "gemini" || text == "gemini") && geminiKey.Length == 0) missing.Add("Gemini");
            if ((audio == "openai" || text == "openai") && openAiKey.Length == 0) missing.Add("OpenAI");
            if (text == "claude" && claudeKey.Length == 0) missing.Add("Claude");
            if (missing.Count > 0 && MessageBox.Show(this,
                    "לא הוגדר מפתח עבור: " + string.Join(", ", missing) + ".\nבלי מפתח העיבוד ייכשל. לשמור בכל זאת?", "חסר מפתח",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2,
                    MessageBoxOptions.RtlReading | MessageBoxOptions.RightAlign) != DialogResult.Yes)
                return;

            var prompt = _bookPrompt.Text.Replace("\r\n", "\n").Trim();
            if (prompt == BookStyle.DefaultPrompt.Trim()) prompt = ""; // unchanged built-in text: keep using the built-in one

            _cfg.TranscriptionProvider = audio;
            _cfg.TextProvider = text;
            _cfg.ApiKey = geminiKey;
            _cfg.ClaudeKey = claudeKey;
            _cfg.OpenAiKey = openAiKey;
            _cfg.ClaudeModel = string.IsNullOrWhiteSpace(_claudeModel.Text) ? ClaudeClient.DefaultModel : _claudeModel.Text.Trim();
            _cfg.OpenAiModel = string.IsNullOrWhiteSpace(_openAiModel.Text) ? OpenAiClient.DefaultModel : _openAiModel.Text.Trim();
            _cfg.OpenAiTranscribeModel = string.IsNullOrWhiteSpace(_openAiTranscribeModel.Text) ? OpenAiClient.DefaultTranscribeModel : _openAiTranscribeModel.Text.Trim();
            _cfg.TranscriptionLanguage = _language.Text.Trim();
            _cfg.BookPrompt = prompt;
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

using System.Text;
using System.Windows.Forms;

namespace GeminiMenu;

static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        Application.SetHighDpiMode(HighDpiMode.SystemAware);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        string? mode = null, file = null;
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == "--mode") mode = args[i + 1];
            else if (args[i] == "--file") file = args[i + 1];
        }

        // Launched from the Explorer menu: one process per file.
        if ((mode is "transcribe" or "summarize") && !string.IsNullOrEmpty(file))
        {
            Worker.Run(mode == "transcribe" ? JobKind.Transcribe : JobKind.Summarize, file);
            return 0;
        }

        // Launched by the user: the installer window (elevates itself).
        if (!Installer.IsAdmin())
        {
            if (Installer.RelaunchElevated()) return 0;
            MessageBox.Show("ההתקנה דורשת הרשאות מנהל.", "Gemini Menu", MessageBoxButtons.OK, MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button1, MessageBoxOptions.RtlReading | MessageBoxOptions.RightAlign);
            return 1;
        }
        Application.Run(new InstallerForm());
        return 0;
    }
}

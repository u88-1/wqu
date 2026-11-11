using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;

namespace GeminiMenu;

public static class Installer
{
    const string PackageName = "GeminiMenu";

    public static string Root => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GeminiMenu");
    static string AppRoot => Path.Combine(Root, "app");

    public static bool IsAdmin()
    {
        using var id = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
    }

    public static bool RelaunchElevated()
    {
        try
        {
            Process.Start(new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true, Verb = "runas" });
            return true;
        }
        catch { return false; } // user declined the UAC prompt
    }

    /// <summary>Exit code returned by <see cref="RunPs"/> when the command did not finish in time.</summary>
    public const int TimedOut = -999;

    static (int Code, string Output) RunPs(string script, TimeSpan timeout, CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-NonInteractive");
        psi.ArgumentList.Add("-ExecutionPolicy");
        psi.ArgumentList.Add("Bypass");
        psi.ArgumentList.Add("-Command");
        psi.ArgumentList.Add("$ErrorActionPreference='Stop'; " + script);
        using var p = Process.Start(psi)!;
        var err = p.StandardError.ReadToEndAsync();
        var outp = p.StandardOutput.ReadToEndAsync();

        // Poll instead of waiting forever: a deployment that hangs must neither freeze the installer nor ignore "cancel".
        var deadline = Environment.TickCount64 + (long)timeout.TotalMilliseconds;
        while (!p.WaitForExit(500))
        {
            if (ct.IsCancellationRequested)
            {
                KillTree(p);
                ct.ThrowIfCancellationRequested();
            }
            if (Environment.TickCount64 >= deadline)
            {
                KillTree(p);
                return (TimedOut, "");
            }
        }
        p.WaitForExit(); // flush the redirected streams
        string Read(Task<string> t) => t.Wait(3000) ? t.Result : "";
        return (p.ExitCode, (Read(outp) + "\n" + Read(err)).Trim());
    }

    static void KillTree(Process p)
    {
        try { p.Kill(entireProcessTree: true); } catch { }
    }

    static void RestartExplorer(Action<string> log)
    {
        log("מרענן את סייר הקבצים (המסך יהבהב לרגע)...");
        foreach (var p in Process.GetProcessesByName("explorer"))
        {
            try { p.Kill(); } catch { }
        }
        // Windows restarts the shell by itself; wait for it, and start it by hand if it does not come back.
        for (int i = 0; i < 20; i++)
        {
            Thread.Sleep(500);
            if (Process.GetProcessesByName("explorer").Length > 0) return;
        }
        try { Process.Start(new ProcessStartInfo("explorer.exe") { UseShellExecute = true }); } catch { }
    }

    /// <summary>The API keys typed in the installer. Empty = keep what is already saved.</summary>
    public sealed record InstallKeys(string Gemini = "", string Claude = "", string OpenAi = "");

    static readonly TimeSpan RemoveTimeout = TimeSpan.FromSeconds(90);
    static readonly TimeSpan RegisterTimeout = TimeSpan.FromMinutes(3);
    static readonly TimeSpan QuickTimeout = TimeSpan.FromSeconds(20);

    public static void Install(InstallKeys keys, Action<string> log, CancellationToken ct = default)
    {
        const int steps = 6;
        var dir = Path.Combine(AppRoot, DateTime.Now.ToString("yyyyMMddHHmmss"));

        log($"שלב 1 מתוך {steps}: מסיר גרסה קודמת (אם יש)...");
        var (rc, _) = RunPs($"Get-AppxPackage -Name {PackageName} | Remove-AppxPackage", RemoveTimeout, ct);
        if (rc == TimedOut) log("הסרת הגרסה הקודמת לוקחת יותר מדי זמן. ממשיך בלעדיה.");
        ct.ThrowIfCancellationRequested();

        log($"שלב 2 מתוך {steps}: מחלץ קבצים...");
        Directory.CreateDirectory(dir);
        using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("payload.zip")
                       ?? throw new InvalidOperationException("payload.zip לא נמצא בתוך הקובץ"))
            ZipFile.ExtractToDirectory(s, dir, overwriteFiles: true);
        File.Copy(Environment.ProcessPath!, Path.Combine(dir, "GeminiMenu.Worker.exe"), overwrite: true);
        ct.ThrowIfCancellationRequested();

        log($"שלב 3 מתוך {steps}: מתקין תעודה מהימנה לחבילה...");
        using (var cert = new X509Certificate2(Path.Combine(dir, "GeminiMenu.cer")))
        using (var store = new X509Store("TrustedPeople", StoreLocation.LocalMachine))
        {
            store.Open(OpenFlags.ReadWrite);
            // Every build used to add another certificate: remove the old ones first.
            foreach (var old in store.Certificates.Find(X509FindType.FindBySubjectDistinguishedName, "CN=GeminiMenu", false))
                store.Remove(old);
            store.Add(cert);
        }
        ct.ThrowIfCancellationRequested();

        log($"שלב 4 מתוך {steps}: רושם את הפריטים בתפריט הלחיצה הימנית (עד 3 דקות)...");
        var msix = Path.Combine(dir, "GeminiMenu.msix");
        var (code, output) = RunPs($"Add-AppxPackage -Path '{msix}' -ExternalLocation '{dir}'", RegisterTimeout, ct);
        if (code == TimedOut)
            throw new Exception("רישום החבילה לא הסתיים תוך 3 דקות, ולכן הופסק.\nסגור חלונות פתוחים של סייר הקבצים, הפעל מחדש את המחשב והרץ שוב את ההתקנה.");
        if (code != 0) throw new Exception("רישום החבילה נכשל:\n" + output);
        ct.ThrowIfCancellationRequested();

        log($"שלב 5 מתוך {steps}: שומר את מפתחות ה-API...");
        SaveKeys(keys, log);

        log($"שלב 6 מתוך {steps}: מרענן את סייר הקבצים...");
        try { RunPs("ie4uinit.exe -show", QuickTimeout, ct); } catch (OperationCanceledException) { throw; } catch { }
        RestartExplorer(log);

        // Old versions: best effort (files may still be locked until sign-out).
        try
        {
            foreach (var d in Directory.GetDirectories(AppRoot))
                if (!string.Equals(d, dir, StringComparison.OrdinalIgnoreCase)) try { Directory.Delete(d, true); } catch { }
        }
        catch { }
        log("ההתקנה הסתיימה. לחץ לחיצה ימנית על קובץ שמע או מסמך.");
    }

    /// <summary>Saves the typed keys (only the non-empty ones) and picks sensible engines when Gemini has no key.</summary>
    static void SaveKeys(InstallKeys keys, Action<string> log)
    {
        bool any = !string.IsNullOrWhiteSpace(keys.Gemini) || !string.IsNullOrWhiteSpace(keys.Claude) || !string.IsNullOrWhiteSpace(keys.OpenAi);
        if (!any) { log("לא הוזן מפתח. אפשר להוסיף אותו אחר כך, בהפעלה חוזרת של ההתקנה או בהגדרות."); return; }

        AppConfig cfg;
        try { cfg = AppConfig.Load(); }
        catch
        {
            // A damaged settings file must not fail the whole installation: keep it aside and start fresh.
            try { File.Move(AppConfig.FilePath, AppConfig.FilePath + ".bad", overwrite: true); } catch { }
            log("קובץ ההגדרות היה פגום ונשמר בשם config.json.bad. נוצר קובץ חדש.");
            cfg = new AppConfig();
        }

        if (!string.IsNullOrWhiteSpace(keys.Gemini)) cfg.ApiKey = keys.Gemini.Trim();
        if (!string.IsNullOrWhiteSpace(keys.Claude)) cfg.ClaudeKey = keys.Claude.Trim();
        if (!string.IsNullOrWhiteSpace(keys.OpenAi)) cfg.OpenAiKey = keys.OpenAi.Trim();

        // Without a Gemini key the default engine (Gemini) could not work: switch to a provider that has a key.
        if (string.IsNullOrWhiteSpace(cfg.ApiKey))
        {
            if (!string.IsNullOrWhiteSpace(cfg.OpenAiKey))
            {
                if (cfg.TranscriptionProvider == "gemini") cfg.TranscriptionProvider = "openai";
                if (cfg.TextProvider == "gemini") cfg.TextProvider = "openai";
                log("אין מפתח Gemini, ולכן נבחר OpenAI לתמלול ולסיכום. אפשר לשנות בהגדרות, תחת \"מנועי AI ומפתחות\".");
            }
            else if (!string.IsNullOrWhiteSpace(cfg.ClaudeKey))
            {
                if (cfg.TextProvider == "gemini") cfg.TextProvider = "claude";
                log("נבחר Claude לסיכום ולעריכה. Claude אינו מתמלל שמע, ולתמלול הקלטות צריך גם מפתח Gemini או OpenAI.");
            }
        }
        cfg.Save();
        log("המפתחות נשמרו מוצפנים.");
    }

    public static void Uninstall(Action<string> log, CancellationToken ct = default)
    {
        log("מסיר את החבילה...");
        var (rc, _) = RunPs($"Get-AppxPackage -Name {PackageName} | Remove-AppxPackage", RemoveTimeout, ct);
        if (rc == TimedOut) log("הסרת החבילה לוקחת יותר מדי זמן. אם הפריטים עדיין בתפריט, הפעל מחדש את המחשב והרץ שוב את ההסרה.");
        ct.ThrowIfCancellationRequested();

        log("מסיר את התעודה...");
        try
        {
            using var store = new X509Store("TrustedPeople", StoreLocation.LocalMachine);
            store.Open(OpenFlags.ReadWrite);
            foreach (var c in store.Certificates.Find(X509FindType.FindBySubjectDistinguishedName, "CN=GeminiMenu", false))
                store.Remove(c);
        }
        catch { }

        RestartExplorer(log);

        try { Directory.Delete(AppRoot, true); }
        catch { log("חלק מהקבצים עדיין בשימוש ויימחקו אחרי הפעלה מחדש."); }
        log("ההסרה הסתיימה. מפתחות ה-API נשארו ב-" + AppConfig.FilePath);
    }
}

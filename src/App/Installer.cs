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

    static (int Code, string Output) RunPs(string script)
    {
        var psi = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-ExecutionPolicy");
        psi.ArgumentList.Add("Bypass");
        psi.ArgumentList.Add("-Command");
        psi.ArgumentList.Add("$ErrorActionPreference='Stop'; " + script);
        using var p = Process.Start(psi)!;
        var err = p.StandardError.ReadToEndAsync();
        var outp = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return (p.ExitCode, (outp + "\n" + err.Result).Trim());
    }

    static void RestartExplorer(Action<string> log)
    {
        log("מרענן את סייר הקבצים (המסך יהבהב לרגע)...");
        foreach (var p in Process.GetProcessesByName("explorer"))
        {
            try { p.Kill(); } catch { }
        }
        // Windows restarts the shell by itself; wait for it.
        for (int i = 0; i < 20; i++)
        {
            Thread.Sleep(500);
            if (Process.GetProcessesByName("explorer").Length > 0) return;
        }
    }

    public static void Install(string apiKey, Action<string> log)
    {
        var dir = Path.Combine(AppRoot, DateTime.Now.ToString("yyyyMMddHHmmss"));

        log("מסיר גרסה קודמת (אם יש)...");
        RunPs($"Get-AppxPackage -Name {PackageName} | Remove-AppxPackage");

        log("מחלץ קבצים...");
        Directory.CreateDirectory(dir);
        using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("payload.zip")
                       ?? throw new InvalidOperationException("payload.zip לא נמצא בתוך הקובץ"))
            ZipFile.ExtractToDirectory(s, dir, overwriteFiles: true);
        File.Copy(Environment.ProcessPath!, Path.Combine(dir, "GeminiMenu.Worker.exe"), overwrite: true);

        log("מתקין תעודה מהימנה לחבילה...");
        using (var cert = new X509Certificate2(Path.Combine(dir, "GeminiMenu.cer")))
        using (var store = new X509Store("TrustedPeople", StoreLocation.LocalMachine))
        {
            store.Open(OpenFlags.ReadWrite);
            store.Add(cert);
        }

        log("רושם את הפריטים בתפריט הלחיצה הימנית...");
        var msix = Path.Combine(dir, "GeminiMenu.msix");
        var (code, output) = RunPs($"Add-AppxPackage -Path '{msix}' -ExternalLocation '{dir}'");
        if (code != 0) throw new Exception("רישום החבילה נכשל:\n" + output);

        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            log("שומר את מפתח ה-API...");
            var cfg = AppConfig.Load();
            cfg.ApiKey = apiKey.Trim();
            cfg.Save();
        }

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

    public static void Uninstall(Action<string> log)
    {
        log("מסיר את החבילה...");
        RunPs($"Get-AppxPackage -Name {PackageName} | Remove-AppxPackage");

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
        log("ההסרה הסתיימה. מפתח ה-API נשאר ב-" + AppConfig.FilePath);
    }
}

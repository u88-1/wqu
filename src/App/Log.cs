using System.Text;

namespace GeminiMenu;

/// <summary>Tiny file logger: %APPDATA%\GeminiMenu\logs\log.txt (trimmed at ~1 MB). Never throws.</summary>
public static class Log
{
    static readonly object Gate = new();
    static string Path_ => System.IO.Path.Combine(AppConfig.Dir, "logs", "log.txt");

    public static void Write(string message)
    {
        try
        {
            lock (Gate)
            {
                var path = Path_;
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
                var info = new FileInfo(path);
                if (info.Exists && info.Length > 1_000_000)
                    File.Move(path, path + ".old", overwrite: true);
                File.AppendAllText(path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{Environment.ProcessId}] {message}{Environment.NewLine}", Encoding.UTF8);
            }
        }
        catch { /* logging must never break the app */ }
    }
}

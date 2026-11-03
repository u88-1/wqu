using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace GeminiMenu;

public sealed class AppConfig
{
    /// <summary>Plain key in memory only. On disk it is stored encrypted (ApiKeyProtected, Windows DPAPI, current user).</summary>
    public string ApiKey { get; set; } = "";
    public string ApiKeyProtected { get; set; } = "";
    /// <summary>"auto" = pick the newest available Flash model from the API.</summary>
    public string Model { get; set; } = "auto";
    public string FallbackModel { get; set; } = "";
    /// <summary>Last model that worked in auto mode (tried first next time).</summary>
    public string ResolvedModel { get; set; } = "";

    // ---- user settings (Settings window)
    /// <summary>Add [HH:MM:SS] to transcripts.</summary>
    public bool Timestamps { get; set; } = true;
    /// <summary>Chunks transcribed at the same time. 1 = in order (consistent speaker labels).</summary>
    public int ParallelChunks { get; set; } = 1;
    public int ChunkSeconds { get; set; } = 180;
    /// <summary>html | text | both | srt</summary>
    public string DefaultFormat { get; set; } = "html";
    /// <summary>Empty = next to the source file.</summary>
    public string OutputDir { get; set; } = "";
    /// <summary>short | normal | detailed</summary>
    public string SummaryDetail { get; set; } = "normal";
    /// <summary>How many files may be processed at the same time across all open windows.</summary>
    public int MaxConcurrentJobs { get; set; } = 2;
    public bool CheckUpdates { get; set; } = true;

    public static string Dir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GeminiMenu");
    public static string FilePath => Path.Combine(Dir, "config.json");

    static readonly JsonSerializerOptions Opts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static AppConfig Load(string? path = null)
    {
        path ??= FilePath;
        try
        {
            if (!File.Exists(path)) return new AppConfig();
            var cfg = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(path), Opts) ?? new AppConfig();
            cfg.ApiKey = (cfg.ApiKey ?? "").Trim();
            if (!string.IsNullOrEmpty(cfg.ApiKeyProtected))
            {
                try { cfg.ApiKey = Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(cfg.ApiKeyProtected), null, DataProtectionScope.CurrentUser)).Trim(); }
                catch { /* saved by another Windows user: ask for the key again */ }
            }
            else if (cfg.ApiKey.Length > 0)
            {
                // Older versions stored the key as plain text: encrypt it now.
                try { cfg.Save(path); } catch { }
            }
            if (string.IsNullOrWhiteSpace(cfg.Model)) cfg.Model = "auto";
            cfg.ParallelChunks = Math.Clamp(cfg.ParallelChunks, 1, 4);
            cfg.ChunkSeconds = Math.Clamp(cfg.ChunkSeconds, 60, 300);
            cfg.MaxConcurrentJobs = Math.Clamp(cfg.MaxConcurrentJobs, 1, 4);
            // Older versions saved fixed defaults that Google has since retired for new keys.
            if (cfg.Model == "gemini-2.5-flash" && (cfg.FallbackModel is "" or "gemini-2.5-flash-lite"))
            {
                cfg.Model = "auto";
                cfg.FallbackModel = "";
            }
            return cfg;
        }
        catch (Exception ex)
        {
            throw new AppException(ErrKind.Other, "קובץ ההגדרות אינו תקין: " + path, technical: ex.Message, inner: ex);
        }
    }

    public void Save(string? path = null)
    {
        path ??= FilePath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var copy = (AppConfig)MemberwiseClone();
        copy.ApiKeyProtected = "";
        if (!string.IsNullOrEmpty(ApiKey))
        {
            try
            {
                copy.ApiKeyProtected = Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(ApiKey), null, DataProtectionScope.CurrentUser));
                copy.ApiKey = "";
            }
            catch { /* DPAPI unavailable: keep the plain key rather than lose it */ }
        }
        File.WriteAllText(path, JsonSerializer.Serialize(copy, Opts));
    }
}

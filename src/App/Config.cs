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

    // ---- other providers (keys are stored encrypted, like the Gemini key)
    public string ClaudeKey { get; set; } = "";
    public string ClaudeKeyProtected { get; set; } = "";
    public string OpenAiKey { get; set; } = "";
    public string OpenAiKeyProtected { get; set; } = "";
    /// <summary>Who edits and summarizes text (book style, clean transcript, summaries): gemini | claude | openai.</summary>
    public string TextProvider { get; set; } = "gemini";
    /// <summary>Who turns audio into text: gemini | openai (Claude has no audio input).</summary>
    public string TranscriptionProvider { get; set; } = "gemini";
    public string ClaudeModel { get; set; } = ClaudeClient.DefaultModel;
    public string OpenAiModel { get; set; } = OpenAiClient.DefaultModel;
    public string OpenAiTranscribeModel { get; set; } = OpenAiClient.DefaultTranscribeModel;
    /// <summary>Language hint for OpenAI transcription (ISO code, empty = detect automatically).</summary>
    public string TranscriptionLanguage { get; set; } = "he";
    /// <summary>Optional full book-style instruction written by the user. Empty = the built-in one.</summary>
    public string BookPrompt { get; set; } = "";
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

    static string Pick(string? value, params string[] allowed)
    {
        var v = (value ?? "").Trim().ToLowerInvariant();
        return Array.IndexOf(allowed, v) >= 0 ? v : allowed[0];
    }

    /// <summary>Returns the plain key: decrypted from the protected value, or the plain one (older/hand-edited files), which is then encrypted on the next save.</summary>
    static string Unprotect(string? plain, string? protectedValue, ref bool migrate)
    {
        plain = (plain ?? "").Trim();
        if (!string.IsNullOrEmpty(protectedValue))
        {
            try { return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(protectedValue), null, DataProtectionScope.CurrentUser)).Trim(); }
            catch { return plain; /* saved by another Windows user: ask for the key again */ }
        }
        if (plain.Length > 0) migrate = true;
        return plain;
    }

    /// <summary>Encrypted form of a key, or "" when there is no key or DPAPI is unavailable (the caller then keeps the plain key rather than lose it).</summary>
    static string Protect(string key)
    {
        if (string.IsNullOrEmpty(key)) return "";
        try { return Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(key), null, DataProtectionScope.CurrentUser)); }
        catch { return ""; }
    }

    public static AppConfig Load(string? path = null)
    {
        path ??= FilePath;
        try
        {
            if (!File.Exists(path)) return new AppConfig();
            var cfg = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(path), Opts) ?? new AppConfig();
            bool migrate = false;
            cfg.ApiKey = (cfg.ApiKey ?? "").Trim();
            if (!string.IsNullOrEmpty(cfg.ApiKeyProtected))
            {
                try { cfg.ApiKey = Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(cfg.ApiKeyProtected), null, DataProtectionScope.CurrentUser)).Trim(); }
                catch { /* saved by another Windows user: ask for the key again */ }
            }
            else if (cfg.ApiKey.Length > 0)
            {
                // Older versions stored the key as plain text: encrypt it once everything is loaded (below).
                migrate = true;
            }
            cfg.ClaudeKey = Unprotect(cfg.ClaudeKey, cfg.ClaudeKeyProtected, ref migrate);
            cfg.OpenAiKey = Unprotect(cfg.OpenAiKey, cfg.OpenAiKeyProtected, ref migrate);
            cfg.TextProvider = Pick(cfg.TextProvider, "gemini", "claude", "openai");
            cfg.TranscriptionProvider = Pick(cfg.TranscriptionProvider, "gemini", "openai");
            if (string.IsNullOrWhiteSpace(cfg.ClaudeModel)) cfg.ClaudeModel = ClaudeClient.DefaultModel;
            if (string.IsNullOrWhiteSpace(cfg.OpenAiModel)) cfg.OpenAiModel = OpenAiClient.DefaultModel;
            if (string.IsNullOrWhiteSpace(cfg.OpenAiTranscribeModel)) cfg.OpenAiTranscribeModel = OpenAiClient.DefaultTranscribeModel;
            cfg.BookPrompt ??= "";
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
            if (migrate) { try { cfg.Save(path); } catch { } }
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
        copy.ClaudeKeyProtected = Protect(ClaudeKey);
        if (copy.ClaudeKeyProtected.Length > 0) copy.ClaudeKey = "";
        copy.OpenAiKeyProtected = Protect(OpenAiKey);
        if (copy.OpenAiKeyProtected.Length > 0) copy.OpenAiKey = "";
        File.WriteAllText(path, JsonSerializer.Serialize(copy, Opts));
    }
}

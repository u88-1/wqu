using System.Text.Json;

namespace GeminiMenu;

public sealed class AppConfig
{
    public string ApiKey { get; set; } = "";
    /// <summary>"auto" = pick the newest available Flash model from the API.</summary>
    public string Model { get; set; } = "auto";
    public string FallbackModel { get; set; } = "";
    /// <summary>Last model that worked in auto mode (tried first next time).</summary>
    public string ResolvedModel { get; set; } = "";

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
            if (string.IsNullOrWhiteSpace(cfg.Model)) cfg.Model = "auto";
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
        File.WriteAllText(path, JsonSerializer.Serialize(this, Opts));
    }
}

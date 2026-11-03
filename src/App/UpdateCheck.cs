using System.Reflection;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace GeminiMenu;

public sealed record UpdateInfo(int Build, string Tag, string Url);

/// <summary>Looks for a newer GitHub release (one small unauthenticated request; never blocks the job).</summary>
public static class UpdateCheck
{
    const string Repo = "u88-1/wqu";

    public static int CurrentBuild => Assembly.GetExecutingAssembly().GetName().Version?.Build ?? 0;

    internal static int ParseBuild(string tag)
    {
        var m = Regex.Match(tag ?? "", @"^v?\d+\.\d+\.(\d+)");
        return m.Success ? int.Parse(m.Groups[1].Value) : 0;
    }

    public static async Task<UpdateInfo?> CheckAsync(AppConfig cfg, CancellationToken ct)
    {
        if (!cfg.CheckUpdates) return null;
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("GeminiMenu");
            var body = await http.GetStringAsync($"https://api.github.com/repos/{Repo}/releases/latest", ct).ConfigureAwait(false);
            var node = JsonNode.Parse(body);
            var tag = (string?)node?["tag_name"] ?? "";
            var url = (string?)node?["html_url"] ?? $"https://github.com/{Repo}/releases";
            int build = ParseBuild(tag);
            return build > CurrentBuild ? new UpdateInfo(build, tag, url) : null;
        }
        catch { return null; }
    }
}

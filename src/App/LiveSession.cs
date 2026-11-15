using System.Diagnostics;

namespace GeminiMenu;

/// <summary>The live page of one transcription: the state, the local server, and the browser tab that shows it.</summary>
public sealed class LiveSession : IDisposable
{
    readonly LiveServer _server;

    public LiveTranscript View { get; }
    public string Url => _server.Url;

    LiveSession(LiveTranscript view, LiveServer server) { View = view; _server = server; }

    /// <summary>Starts the server and opens the page. Null (and nothing left running) when either fails: the job goes on without it.</summary>
    /// <param name="open">Opens a URL in the browser; replaced in tests.</param>
    public static LiveSession? TryStart(string file, Func<string, bool>? open = null)
    {
        LiveServer? server = null;
        try
        {
            var view = new LiveTranscript();
            var meta = Path.GetFileName(file) + "  ·  " + HebrewDate.Stamp(DateTime.Now, HtmlPages.ShowHebrewDate);
            server = LiveServer.TryStart(view, HtmlPages.LivePage("תמלול הקלטה", meta));
            if (server == null) return null;
            if (!(open ?? OpenInBrowser)(server.Url)) { server.Dispose(); return null; }
            return new LiveSession(view, server);
        }
        catch (Exception e)
        {
            Log.Write("live view not opened: " + e.Message);
            server?.Dispose();
            return null;
        }
    }

    static bool OpenInBrowser(string url)
    {
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        return true;
    }

    /// <summary>After the final state: did a page open in the browser pick it up? If not, the caller opens the saved file itself.</summary>
    public bool ResultSeenInBrowser(TimeSpan wait) => _server.WaitForAck(wait);

    public void Dispose() => _server.Dispose();
}

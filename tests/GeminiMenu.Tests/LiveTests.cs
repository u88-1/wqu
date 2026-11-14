using System.Net;
using System.Text.Json;
using Xunit;

namespace GeminiMenu;

public class LiveTranscriptTests
{
    static LiveChunk C(int i, int total, string text, bool gap = false) =>
        new(i, total, TimeSpan.FromSeconds(i * 180), TimeSpan.FromSeconds((i + 1) * 180), text, "m", gap);

    [Fact]
    public void Parallel_OnlyTheFinishedPrefixIsShown_AndAGapIsMarkedAsWorking()
    {
        var live = new LiveTranscript();
        live.Begin(3);
        live.ChunkDone(C(2, 3, "[00:06:00] שלישי"));
        var s = live.Snapshot();
        Assert.Equal("", s.Plain);                       // chunk 2 is ready, but 0 and 1 are not
        Assert.Contains("הושלמו 1 מתוך 3", s.Pending);

        live.ChunkDone(C(0, 3, "[00:00:00] ראשון"));
        s = live.Snapshot();
        Assert.Equal("[00:00:00] ראשון", s.Plain);       // the prefix grows, chunk 2 still waits for chunk 1
        Assert.DoesNotContain("שלישי", s.Html);

        live.ChunkDone(C(1, 3, "[00:03:00] שני"));
        s = live.Snapshot();
        Assert.Contains("ראשון", s.Plain);
        Assert.Contains("שני", s.Plain);
        Assert.Contains("שלישי", s.Plain);
        Assert.True(s.Plain.IndexOf("ראשון") < s.Plain.IndexOf("שני") && s.Plain.IndexOf("שני") < s.Plain.IndexOf("שלישי"));
    }

    [Fact]
    public void TextIsTheSameAsTheFinalFold_EvenWhenChunksOverlap()
    {
        var texts = new[]
        {
            "[00:00:00] דובר 1: אנחנו מתחילים היום בנושא החשוב של השבוע ואחר כך נעבור לשאלות",
            "[00:03:00] נעבור לשאלות מהקהל ונסיים בסיכום קצר של כל מה שנאמר",
            "[00:06:00] סיכום קצר של כל מה שנאמר ובזה סיימנו את הערב תודה רבה",
        };
        string expected = "";
        foreach (var t in texts) expected = Jobs.MergeChunkTranscripts(expected, t);

        var live = new LiveTranscript();
        live.Begin(3);
        foreach (var i in new[] { 1, 2, 0 }) live.ChunkDone(C(i, 3, texts[i]));   // arrives out of order
        Assert.Equal(expected, live.Snapshot().Plain);
    }

    [Fact]
    public void TheSameChunkTwiceOrAnUnknownIndex_ChangesNothing()
    {
        var live = new LiveTranscript();
        live.Begin(2);
        live.ChunkDone(C(0, 2, "אחד"));
        var seq = live.Seq;
        live.ChunkDone(C(0, 2, "אחר לגמרי"));
        live.ChunkDone(C(7, 2, "מחוץ לתחום"));
        live.ChunkDone(C(-1, 2, "שלילי"));
        Assert.Equal(seq, live.Seq);
        Assert.Equal("אחד", live.Snapshot().Plain);
    }

    [Fact]
    public void EveryRealChangeBumpsTheSequence()
    {
        var live = new LiveTranscript();
        long last = live.Seq;
        void Changed() { Assert.True(live.Seq > last); last = live.Seq; }
        live.Begin(1); Changed();
        live.ChunkDone(C(0, 1, "x")); Changed();
        live.Processing("עורך"); Changed();
        live.Complete("כותרת", "<p>x</p>", "x", null, null); Changed();
    }

    [Fact]
    public void Complete_ShowsTheFinalResult_WithNoteAndSavedPath()
    {
        var live = new LiveTranscript();
        live.Begin(1);
        live.ChunkDone(C(0, 1, "גולמי"));
        live.Complete("תמלול בסגנון ספר", "<p>סופי</p>", "סופי", "הערה", @"C:\a\b.html");
        var s = live.Snapshot();
        Assert.Equal(LivePhase.Done, s.Phase);
        Assert.Equal("<p>סופי</p>", s.Html);
        Assert.Equal("סופי", s.Plain);
        Assert.Equal("תמלול בסגנון ספר", s.Title);
        Assert.Equal("הערה", s.Note);
        Assert.Equal(@"C:\a\b.html", s.Saved);
        Assert.Equal("", s.Pending);
        Assert.True(live.IsFinished);
    }

    [Fact]
    public void FailAndCancel_KeepWhatWasTranscribed()
    {
        var live = new LiveTranscript();
        live.Begin(2);
        live.ChunkDone(C(0, 2, "נשמר"));
        live.Fail("אין חיבור.");
        var s = live.Snapshot();
        Assert.Equal(LivePhase.Error, s.Phase);
        Assert.Equal("נשמר", s.Plain);
        Assert.Contains("אין חיבור.", s.Pending);

        var live2 = new LiveTranscript();
        live2.Begin(2);
        live2.ChunkDone(C(0, 2, "נשמר"));
        live2.Cancel();
        Assert.Equal(LivePhase.Cancelled, live2.Snapshot().Phase);
        Assert.Contains("בוטל", live2.Snapshot().Pending);
        Assert.Equal("נשמר", live2.Snapshot().Plain);
    }

    [Fact]
    public void Seed_ShowsEarlierWork_AndNewChunksContinueAfterIt()
    {
        var live = new LiveTranscript();
        live.Seed("[00:00:00] מהריצה הקודמת");
        live.Begin(1);
        live.ChunkDone(new LiveChunk(0, 1, TimeSpan.FromSeconds(180), TimeSpan.FromSeconds(360), "[00:03:00] המשך", "m", false));
        Assert.Equal(Jobs.MergeChunkTranscripts("[00:00:00] מהריצה הקודמת", "[00:03:00] המשך"), live.Snapshot().Plain);
    }

    [Fact]
    public void TextIsEscapedInTheHtml()
    {
        var live = new LiveTranscript();
        live.Begin(1);
        live.ChunkDone(C(0, 1, "<script>alert(1)</script> & עוד"));
        var html = live.Snapshot().Html;
        Assert.DoesNotContain("<script>", html);
        Assert.Contains("&lt;script&gt;", html);
    }

    [Fact]
    public void ASinkThatThrows_DoesNotStopTheOthers()
    {
        var live = new LiveTranscript();
        var composite = new CompositeSink(new Thrower(), null, live);
        composite.Begin(1);
        composite.ChunkDone(C(0, 1, "עבר"));
        Assert.Equal("עבר", live.Snapshot().Plain);
    }

    sealed class Thrower : ILiveSink
    {
        public void Begin(int totalChunks) => throw new InvalidOperationException("boom");
        public void ChunkDone(LiveChunk chunk) => throw new InvalidOperationException("boom");
    }
}

public class PartialFileTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "gm-partial-" + Guid.NewGuid().ToString("N"));
    readonly string _src;

    public PartialFileTests()
    {
        Directory.CreateDirectory(_dir);
        _src = Path.Combine(_dir, "rec.mp3");
        File.WriteAllBytes(_src, new byte[1000]);
    }

    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    string Store => Path.Combine(_dir, "partial");

    static LiveChunk C(int i, double from, double to, string text, bool gap = false) =>
        new(i, 10, TimeSpan.FromSeconds(from), TimeSpan.FromSeconds(to), text, "gemini-x", gap);

    [Fact]
    public void EveryChunkIsOnDiskAtOnce_AndNoTempFileIsLeft()
    {
        var p = new PartialFile(Store, _src, 180, true);
        p.ChunkDone(C(0, 0, 180, "[00:00:00] אחד"));
        Assert.True(File.Exists(p.FilePath));
        Assert.False(File.Exists(p.FilePath + ".tmp"));
        var data = PartialFile.TryLoad(Store, _src);
        Assert.NotNull(data);
        Assert.Equal(1, data!.Chunks.Count);
        Assert.Equal("gemini-x", data.Chunks[0].Model);
        p.ChunkDone(C(1, 180, 360, "[00:03:00] שניים"));
        Assert.Equal(2, PartialFile.TryLoad(Store, _src)!.Chunks.Count);
    }

    [Fact]
    public void ARedoneChunkReplacesTheOldOne()
    {
        var p = new PartialFile(Store, _src, 180, true);
        p.ChunkDone(C(0, 0, 180, "[⚠ הקטע 00:00:00–00:03:00 לא תומלל במלואו]", gap: true));
        p.ChunkDone(C(0, 0, 180, "תוקן"));
        var data = PartialFile.TryLoad(Store, _src)!;
        Assert.Equal(1, data.Chunks.Count);
        Assert.Equal("תוקן", data.Chunks[0].Text);
        Assert.False(data.Chunks[0].Gap);
    }

    [Fact]
    public void ChangedOrOldOrBrokenFiles_AreNotOffered()
    {
        var p = new PartialFile(Store, _src, 180, true);
        p.ChunkDone(C(0, 0, 180, "טקסט"));
        Assert.NotNull(PartialFile.TryLoad(Store, _src));

        // too old
        Assert.Null(PartialFile.TryLoad(Store, _src, DateTime.UtcNow.AddDays(8)));

        // corrupt
        File.WriteAllText(p.FilePath, "{ not json");
        Assert.Null(PartialFile.TryLoad(Store, _src));

        // a different recording at the same path (size changed) is not the same work
        var p2 = new PartialFile(Store, _src, 180, true);
        p2.ChunkDone(C(0, 0, 180, "טקסט"));
        var key = p2.FilePath;
        File.WriteAllBytes(_src, new byte[2000]);
        Assert.Null(PartialFile.TryLoad(Store, _src));
        Assert.True(File.Exists(key)); // the stale file is simply ignored (and purged later)
    }

    [Fact]
    public void ResumePoint_IsTheEndOfTheUnbrokenFinishedPrefix()
    {
        var data = new PartialData();
        data.Chunks.Add(new PartialChunk { StartSec = 0, EndSec = 180, Text = "[00:00:00] אחד שני שלישי רביעי חמישי" });
        data.Chunks.Add(new PartialChunk { StartSec = 180, EndSec = 360, Text = "[00:03:00] ששי שביעי שמיני תשיעי עשירי" });
        data.Chunks.Add(new PartialChunk { StartSec = 360, EndSec = 540, Text = "[00:06:00] עוד טקסט כאן ועוד" });
        var r = PartialFile.GetResumePoint(data)!;
        Assert.Equal(TimeSpan.FromSeconds(540), r.StartAt);
        Assert.Equal(3, r.Chunks);
        Assert.Contains("ששי", r.Text);
    }

    [Fact]
    public void ResumePoint_StopsAtAGapAndAtAHole_SoThoseChunksAreDoneAgain()
    {
        var withGap = new PartialData();
        withGap.Chunks.Add(new PartialChunk { StartSec = 0, EndSec = 180, Text = "אחד" });
        withGap.Chunks.Add(new PartialChunk { StartSec = 180, EndSec = 360, Text = "[⚠ הקטע]", Gap = true });
        withGap.Chunks.Add(new PartialChunk { StartSec = 360, EndSec = 540, Text = "שלישי" });
        var r = PartialFile.GetResumePoint(withGap)!;
        Assert.Equal(TimeSpan.FromSeconds(180), r.StartAt);
        Assert.Equal(1, r.Chunks);
        Assert.Equal(1, PartialFile.PrefixChunks(withGap).Count);

        var withHole = new PartialData();
        withHole.Chunks.Add(new PartialChunk { StartSec = 0, EndSec = 180, Text = "אחד" });
        withHole.Chunks.Add(new PartialChunk { StartSec = 360, EndSec = 540, Text = "שלישי" }); // chunk 1 never finished (parallel)
        Assert.Equal(TimeSpan.FromSeconds(180), PartialFile.GetResumePoint(withHole)!.StartAt);

        var onlyGap = new PartialData();
        onlyGap.Chunks.Add(new PartialChunk { StartSec = 0, EndSec = 180, Text = "[⚠]", Gap = true });
        Assert.Null(PartialFile.GetResumePoint(onlyGap));
    }

    [Fact]
    public void ResumedRun_KeepsTheEarlierChunks()
    {
        var first = new PartialFile(Store, _src, 180, true);
        first.ChunkDone(C(0, 0, 180, "אחד"));
        var loaded = PartialFile.TryLoad(Store, _src)!;

        var second = new PartialFile(Store, _src, 180, true, PartialFile.PrefixChunks(loaded));
        second.ChunkDone(C(0, 180, 360, "שניים"));
        var after = PartialFile.TryLoad(Store, _src)!;
        Assert.Equal(2, after.Chunks.Count);
        Assert.Equal(TimeSpan.FromSeconds(360), PartialFile.GetResumePoint(after)!.StartAt);
    }

    [Fact]
    public void Delete_RemovesTheFile_AndPurgeRemovesOldOnes()
    {
        var p = new PartialFile(Store, _src, 180, true);
        p.ChunkDone(C(0, 0, 180, "אחד"));
        p.Delete();
        Assert.False(File.Exists(p.FilePath));

        Directory.CreateDirectory(Store);
        var old = Path.Combine(Store, "old.json");
        File.WriteAllText(old, "{}");
        File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddDays(-9));
        var fresh = Path.Combine(Store, "fresh.json");
        File.WriteAllText(fresh, "{}");
        PartialFile.Purge(Store);
        Assert.False(File.Exists(old));
        Assert.True(File.Exists(fresh));
    }
}

public class LiveServerTests
{
    static HttpClient Client() => new() { Timeout = TimeSpan.FromSeconds(10) };

    static (LiveTranscript live, LiveServer server) Start()
    {
        var live = new LiveTranscript();
        var server = LiveServer.TryStart(live, HtmlPages.LivePage("תמלול הקלטה", "rec.mp3"))!;
        Assert.NotNull(server);
        return (live, server);
    }

    [Fact]
    public async Task ServesThePage_OnlyUnderTheTokenAndOnlyOnLoopback()
    {
        var (_, server) = Start();
        using var _s = server;
        using var http = Client();

        var page = await http.GetAsync(server.Url);
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        var html = await page.Content.ReadAsStringAsync();
        Assert.Contains("<html", html);
        Assert.Contains("fetch('state?since='", html);

        Assert.StartsWith("http://127.0.0.1:", server.Url);
        var noToken = await http.GetAsync($"http://127.0.0.1:{server.Port}/");
        Assert.Equal(HttpStatusCode.NotFound, noToken.StatusCode);
        var wrong = await http.GetAsync($"http://127.0.0.1:{server.Port}/00000000000000000000000000000000/state?since=-1");
        Assert.Equal(HttpStatusCode.NotFound, wrong.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync(server.Url + "nothing")).StatusCode);
    }

    [Fact]
    public async Task ForeignHostHeader_IsRefused_AgainstDnsRebinding()
    {
        var (_, server) = Start();
        using var _s = server;
        using var http = Client();
        var req = new HttpRequestMessage(HttpMethod.Get, server.Url + "state?since=-1");
        req.Headers.Host = "evil.example:" + server.Port;
        Assert.Equal(HttpStatusCode.Forbidden, (await http.SendAsync(req)).StatusCode);

        var ok = new HttpRequestMessage(HttpMethod.Get, server.Url + "state?since=-1");
        ok.Headers.Host = "localhost:" + server.Port;
        Assert.Equal(HttpStatusCode.OK, (await http.SendAsync(ok)).StatusCode);
    }

    [Fact]
    public async Task OnlyGet_IsAllowed()
    {
        var (_, server) = Start();
        using var _s = server;
        using var http = Client();
        var r = await http.PostAsync(server.Url + "state", new StringContent("x"));
        Assert.Equal(HttpStatusCode.MethodNotAllowed, r.StatusCode);
    }

    [Fact]
    public async Task State_IsReturnedOnlyWhenSomethingChanged()
    {
        var (live, server) = Start();
        using var _s = server;
        using var http = Client();
        live.Begin(2);

        var first = await http.GetAsync(server.Url + "state?since=-1");
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var doc = JsonDocument.Parse(await first.Content.ReadAsStringAsync()).RootElement;
        long seq = doc.GetProperty("seq").GetInt64();
        Assert.Equal("transcribing", doc.GetProperty("phase").GetString());

        var same = await http.GetAsync(server.Url + $"state?since={seq}");
        Assert.Equal(HttpStatusCode.NoContent, same.StatusCode);

        live.ChunkDone(new LiveChunk(0, 2, TimeSpan.Zero, TimeSpan.FromSeconds(180), "[00:00:00] שלום עולם", "m", false));
        var next = await http.GetAsync(server.Url + $"state?since={seq}");
        Assert.Equal(HttpStatusCode.OK, next.StatusCode);
        var doc2 = JsonDocument.Parse(await next.Content.ReadAsStringAsync()).RootElement;
        Assert.True(doc2.GetProperty("seq").GetInt64() > seq);
        Assert.Contains("שלום עולם", doc2.GetProperty("html").GetString());
        Assert.Contains("שלום עולם", doc2.GetProperty("plain").GetString());
        Assert.Contains("הושלמו 1 מתוך 2", doc2.GetProperty("pending").GetString());
    }

    [Fact]
    public async Task Ack_OfTheFinalState_ReleasesTheWaiter()
    {
        var (live, server) = Start();
        using var _s = server;
        using var http = Client();
        Assert.False(server.WaitForAck(TimeSpan.FromMilliseconds(200))); // no page ever connected

        live.Begin(1);
        await http.GetAsync(server.Url + "state?since=-1");
        Assert.True(server.TabAlive);

        live.Complete("סופי", "<p>x</p>", "x", null, "C:\\a.html");
        var st = await http.GetAsync(server.Url + "state?since=-1");
        long seq = JsonDocument.Parse(await st.Content.ReadAsStringAsync()).RootElement.GetProperty("seq").GetInt64();

        var early = await http.GetAsync(server.Url + $"ack?seq={seq - 1}"); // an old state does not count
        Assert.Equal(HttpStatusCode.NoContent, early.StatusCode);
        Assert.False(server.WaitForAck(TimeSpan.FromMilliseconds(300)));

        await http.GetAsync(server.Url + $"ack?seq={seq}");
        Assert.True(server.WaitForAck(TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public async Task AckBeforeTheEnd_IsIgnored()
    {
        var (live, server) = Start();
        using var _s = server;
        using var http = Client();
        live.Begin(1);
        await http.GetAsync(server.Url + $"ack?seq={live.Seq}");
        Assert.False(server.WaitForAck(TimeSpan.FromMilliseconds(300)));
    }

    [Fact]
    public async Task Dispose_ClosesThePort()
    {
        var (_, server) = Start();
        var url = server.Url;
        server.Dispose();
        using var http = Client();
        await Task.Delay(100);
        await Assert.ThrowsAsync<HttpRequestException>(() => http.GetAsync(url));
    }

    [Fact]
    public void EachServer_HasItsOwnPortAndToken()
    {
        var (_, a) = Start();
        var (_, b) = Start();
        using var _a = a; using var _b = b;
        Assert.NotEqual(a.Url, b.Url);
        Assert.NotEqual(a.Port, b.Port);
    }

    [Fact]
    public async Task ManyPollsAtOnce_AreAllAnswered()
    {
        var (live, server) = Start();
        using var _s = server;
        live.Begin(1);
        using var http = Client();
        var all = await Task.WhenAll(Enumerable.Range(0, 40).Select(_ => http.GetAsync(server.Url + "state?since=-1")));
        Assert.All(all, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
    }

    [Fact]
    public void LivePage_HasTheCopyButton_FollowsTheEndOnlyWhenTheReaderIsThere_AndIsEscaped()
    {
        var html = HtmlPages.LivePage("<b>כותרת</b>", "a&b.mp3");
        Assert.Contains("id=\"copy\"", html);
        Assert.Contains("id=\"content\"", html);
        Assert.Contains("atBottom()", html);
        Assert.Contains("insertAdjacentHTML", html);
        Assert.DoesNotContain("<b>כותרת</b>", html);
        Assert.Contains("&lt;b&gt;", html);
        Assert.DoesNotContain("localStorage", html);
    }
}

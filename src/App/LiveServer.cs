using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace GeminiMenu;

/// <summary>
/// A tiny HTTP server for the live page, on 127.0.0.1 only. It is a plain TcpListener on purpose: no URL reservation,
/// no administrator rights, nothing for the firewall to ask about. Random port, random token in the address, a Host
/// check against DNS rebinding, GET only. It serves the page, a polled state, and an acknowledgement of the final state.
/// </summary>
public sealed class LiveServer : IDisposable
{
    readonly TcpListener _listener;
    readonly string _token;
    readonly LiveTranscript _live;
    readonly string _page;
    readonly CancellationTokenSource _cts = new();
    readonly SemaphoreSlim _slots = new(16);
    readonly ManualResetEventSlim _acked = new(false);
    long _lastPollTicks;
    volatile bool _everPolled;

    public int Port { get; }
    public string Url => $"http://127.0.0.1:{Port}/{_token}/";

    /// <summary>The page polled in the last few seconds: someone is looking at it.</summary>
    public bool TabAlive => _everPolled && DateTime.UtcNow.Ticks - Interlocked.Read(ref _lastPollTicks) < TimeSpan.FromSeconds(6).Ticks;

    public static LiveServer? TryStart(LiveTranscript live, string pageHtml)
    {
        try { return new LiveServer(live, pageHtml); }
        catch (Exception e) { Log.Write("live view not started: " + e.Message); return null; }
    }

    LiveServer(LiveTranscript live, string pageHtml)
    {
        _live = live;
        _page = pageHtml;
        _token = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start(16);
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _ = Task.Run(AcceptLoopAsync);
    }

    /// <summary>
    /// After the final state was published: wait until the page fetched it. False when no page is open
    /// (never connected, or closed), so the caller can open the saved file itself.
    /// </summary>
    public bool WaitForAck(TimeSpan timeout)
    {
        if (!_everPolled) return false;
        if (_acked.IsSet) return true;
        var end = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < end)
        {
            if (_acked.Wait(100)) return true;
            if (!TabAlive && !_acked.IsSet) return false;
        }
        return _acked.IsSet;
    }

    async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(_cts.Token).ConfigureAwait(false); }
            catch { break; }

            if (!_slots.Wait(0)) { client.Dispose(); continue; }
            _ = Task.Run(async () =>
            {
                try { await HandleAsync(client).ConfigureAwait(false); }
                catch { /* a broken connection is not a problem */ }
                finally { client.Dispose(); _slots.Release(); }
            });
        }
    }

    async Task HandleAsync(TcpClient client)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        var stream = client.GetStream();

        // Read the request head (no body is ever expected).
        var buffer = new byte[8192];
        int len = 0;
        while (true)
        {
            int n = await stream.ReadAsync(buffer.AsMemory(len, buffer.Length - len), timeout.Token).ConfigureAwait(false);
            if (n == 0) return; // browsers open speculative connections and close them
            len += n;
            if (Encoding.ASCII.GetString(buffer, 0, len).Contains("\r\n\r\n")) break;
            if (len == buffer.Length) { await RespondAsync(stream, 431, "text/plain", "too large", timeout.Token).ConfigureAwait(false); return; }
        }

        var head = Encoding.ASCII.GetString(buffer, 0, len).Split("\r\n");
        var parts = head[0].Split(' ');
        if (parts.Length < 2) { await RespondAsync(stream, 400, "text/plain", "bad request", timeout.Token).ConfigureAwait(false); return; }
        if (parts[0] != "GET") { await RespondAsync(stream, 405, "text/plain", "GET only", timeout.Token).ConfigureAwait(false); return; }

        string? host = head.Skip(1).Select(h => h.Split(':', 2)).Where(h => h.Length == 2 && h[0].Trim().Equals("host", StringComparison.OrdinalIgnoreCase))
            .Select(h => h[1].Trim()).FirstOrDefault();
        if (host != $"127.0.0.1:{Port}" && host != $"localhost:{Port}")
        {
            await RespondAsync(stream, 403, "text/plain", "forbidden", timeout.Token).ConfigureAwait(false);
            return;
        }

        var target = parts[1];
        var q = target.IndexOf('?');
        var path = q < 0 ? target : target[..q];
        var query = q < 0 ? "" : target[(q + 1)..];

        var prefix = "/" + _token + "/";
        if (!path.StartsWith(prefix, StringComparison.Ordinal)) { await RespondAsync(stream, 404, "text/plain", "not found", timeout.Token).ConfigureAwait(false); return; }
        var route = path[prefix.Length..];

        switch (route)
        {
            case "":
                await RespondAsync(stream, 200, "text/html; charset=utf-8", _page, timeout.Token).ConfigureAwait(false);
                break;

            case "state":
            {
                MarkPoll();
                long since = ParseLong(query, "since", -1);
                if (since == _live.Seq) { await RespondAsync(stream, 204, "text/plain", "", timeout.Token).ConfigureAwait(false); break; }
                var s = _live.Snapshot();
                var json = JsonSerializer.Serialize(new
                {
                    seq = s.Seq,
                    phase = s.Phase.ToString().ToLowerInvariant(),
                    title = s.Title,
                    html = s.Html,
                    plain = s.Plain,
                    pending = s.Pending,
                    note = s.Note,
                    saved = s.Saved,
                });
                await RespondAsync(stream, 200, "application/json; charset=utf-8", json, timeout.Token).ConfigureAwait(false);
                break;
            }

            case "ack":
                MarkPoll();
                if (_live.IsFinished && ParseLong(query, "seq", -1) >= _live.Seq) _acked.Set();
                await RespondAsync(stream, 204, "text/plain", "", timeout.Token).ConfigureAwait(false);
                break;

            default:
                await RespondAsync(stream, 404, "text/plain", "not found", timeout.Token).ConfigureAwait(false);
                break;
        }
    }

    void MarkPoll()
    {
        Interlocked.Exchange(ref _lastPollTicks, DateTime.UtcNow.Ticks);
        _everPolled = true;
    }

    static long ParseLong(string query, string name, long fallback)
    {
        foreach (var pair in query.Split('&'))
        {
            var kv = pair.Split('=', 2);
            if (kv.Length == 2 && kv[0] == name && long.TryParse(kv[1], out var v)) return v;
        }
        return fallback;
    }

    static async Task RespondAsync(NetworkStream stream, int status, string contentType, string body, CancellationToken ct)
    {
        var reason = status switch { 200 => "OK", 204 => "No Content", 400 => "Bad Request", 403 => "Forbidden", 404 => "Not Found", 405 => "Method Not Allowed", _ => "Error" };
        var payload = status == 204 ? Array.Empty<byte>() : Encoding.UTF8.GetBytes(body);
        var headers = new StringBuilder()
            .Append($"HTTP/1.1 {status} {reason}\r\n")
            .Append($"Content-Type: {contentType}\r\n")
            .Append($"Content-Length: {payload.Length}\r\n")
            .Append("Cache-Control: no-store\r\n")
            .Append("X-Content-Type-Options: nosniff\r\n")
            .Append("Referrer-Policy: no-referrer\r\n")
            .Append("Content-Security-Policy: default-src 'none'; style-src 'unsafe-inline'; script-src 'unsafe-inline'; connect-src 'self'\r\n")
            .Append("Connection: close\r\n\r\n");
        var head = Encoding.ASCII.GetBytes(headers.ToString());
        await stream.WriteAsync(head, ct).ConfigureAwait(false);
        if (payload.Length > 0) await stream.WriteAsync(payload, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    public void Dispose()
    {
        try { _cts.Cancel(); } catch { }
        try { _listener.Stop(); } catch { }
    }
}

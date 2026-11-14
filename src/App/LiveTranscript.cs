namespace GeminiMenu;

/// <summary>One top-level chunk that finished (its text already has absolute timestamps).</summary>
public sealed record LiveChunk(int Index, int Total, TimeSpan Start, TimeSpan End, string Text, string? Model, bool IsGap);

/// <summary>Receives progress from <see cref="Jobs"/>. A sink must never throw into the job.</summary>
public interface ILiveSink
{
    /// <summary>The chunk plan is known: this many top-level chunks will be reported (on resume: only the remaining ones).</summary>
    void Begin(int totalChunks);
    void ChunkDone(LiveChunk chunk);
}

/// <summary>Fans out to several sinks; one failing sink never affects the others or the job.</summary>
public sealed class CompositeSink : ILiveSink
{
    readonly ILiveSink[] _sinks;
    public CompositeSink(params ILiveSink?[] sinks) => _sinks = sinks.Where(s => s != null).Select(s => s!).ToArray();

    public void Begin(int totalChunks) { foreach (var s in _sinks) Safe(() => s.Begin(totalChunks)); }
    public void ChunkDone(LiveChunk chunk) { foreach (var s in _sinks) Safe(() => s.ChunkDone(chunk)); }

    static void Safe(Action a) { try { a(); } catch (Exception e) { Log.Write("sink failed: " + e.Message); } }
}

public enum LivePhase { Transcribing, Processing, Done, Error, Cancelled }

public sealed record LiveSnapshot(long Seq, LivePhase Phase, string Title, string Html, string Plain, string Pending, string? Note, string? Saved);

/// <summary>
/// What the live page shows. Chunks may finish in any order (parallel mode) but the text only grows by the
/// contiguous finished prefix, merged with exactly the function the final result uses, so nothing jumps around.
/// </summary>
public sealed class LiveTranscript : ILiveSink
{
    readonly object _gate = new();
    LiveChunk?[] _slots = Array.Empty<LiveChunk?>();
    int _prefix, _done;
    string _merged = "";
    long _seq;
    LivePhase _phase = LivePhase.Transcribing;
    string _title;
    string _status = "";
    string? _note, _saved, _finalHtml, _finalPlain;
    string? _htmlCache; long _htmlCacheSeq = -1;

    public LiveTranscript(string title = "תמלול הקלטה") => _title = title;

    public long Seq { get { lock (_gate) return _seq; } }
    public bool IsFinished { get { lock (_gate) return _phase is LivePhase.Done or LivePhase.Error or LivePhase.Cancelled; } }

    /// <summary>Text that was transcribed in an earlier run (resume): shown first, new chunks continue after it.</summary>
    public void Seed(string text)
    {
        lock (_gate) { _merged = text ?? ""; _seq++; }
    }

    public void Begin(int totalChunks)
    {
        lock (_gate)
        {
            _slots = new LiveChunk?[Math.Max(0, totalChunks)];
            _prefix = 0; _done = 0;
            _seq++;
        }
    }

    public void ChunkDone(LiveChunk c)
    {
        lock (_gate)
        {
            if (c.Index < 0 || c.Index >= _slots.Length || _slots[c.Index] != null) return;
            _slots[c.Index] = c;
            _done++;
            while (_prefix < _slots.Length && _slots[_prefix] is { } next)
            {
                _merged = Jobs.MergeChunkTranscripts(_merged, next.Text);
                _prefix++;
            }
            _seq++;
        }
    }

    /// <summary>The raw transcript is complete and is now being edited / summarized / saved.</summary>
    public void Processing(string status)
    {
        lock (_gate) { _phase = LivePhase.Processing; _status = status; _seq++; }
    }

    public void Complete(string title, string bodyHtml, string plain, string? note, string? savedPath)
    {
        lock (_gate)
        {
            _phase = LivePhase.Done; _title = title; _finalHtml = bodyHtml; _finalPlain = plain; _note = note; _saved = savedPath; _seq++;
        }
    }

    public void Fail(string message)
    {
        lock (_gate) { _phase = LivePhase.Error; _status = message; _seq++; }
    }

    public void Cancel()
    {
        lock (_gate) { _phase = LivePhase.Cancelled; _seq++; }
    }

    public LiveSnapshot Snapshot()
    {
        lock (_gate)
        {
            string html, plain;
            if (_phase == LivePhase.Done && _finalHtml != null) { html = _finalHtml; plain = _finalPlain ?? ""; }
            else
            {
                if (_htmlCacheSeq != _seq) { _htmlCache = HtmlPages.TranscriptBody(_merged); _htmlCacheSeq = _seq; }
                html = _htmlCache ?? "";
                plain = _merged;
            }

            string pending = _phase switch
            {
                LivePhase.Transcribing => _slots.Length == 0
                    ? "מתמלל..."
                    : $"מתמלל... (הושלמו {_done} מתוך {_slots.Length} קטעים)",
                LivePhase.Processing => string.IsNullOrWhiteSpace(_status) ? "מעבד את התמלול..." : _status,
                LivePhase.Error => "התמלול נכשל: " + _status + " מה שתומלל עד כה נשמר.",
                LivePhase.Cancelled => "התמלול בוטל. מה שתומלל עד כה נשמר.",
                _ => "",
            };
            return new LiveSnapshot(_seq, _phase, _title, html, plain, pending, _note, _saved);
        }
    }
}

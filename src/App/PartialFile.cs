using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace GeminiMenu;

public sealed class PartialChunk
{
    public int Index { get; set; }
    public double StartSec { get; set; }
    public double EndSec { get; set; }
    public string Text { get; set; } = "";
    public string? Model { get; set; }
    public bool Gap { get; set; }
}

public sealed class PartialData
{
    public int Version { get; set; } = 1;
    public string Source { get; set; } = "";
    public long Size { get; set; }
    public long MtimeTicks { get; set; }
    public int ChunkSeconds { get; set; }
    public bool Timestamps { get; set; }
    /// <summary>Which engine transcribed ("gemini" or "openai"): a resumed run keeps to the same one.</summary>
    public string Engine { get; set; } = "";
    public DateTime UpdatedUtc { get; set; }
    public List<PartialChunk> Chunks { get; set; } = new();
}

/// <summary>Where a resumed run starts and what was already transcribed.</summary>
public sealed record ResumePoint(TimeSpan StartAt, string Text, int Chunks);

/// <summary>
/// Every finished chunk is written to disk at once (temp file + replace), so a crash or a cancel loses nothing.
/// A later run on the same, unchanged file can continue after the last chunk that finished without a gap.
/// </summary>
public sealed class PartialFile : ILiveSink
{
    public static string DefaultDir => System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GeminiMenu", "partial");
    public static readonly TimeSpan MaxAge = TimeSpan.FromDays(7);

    readonly object _gate = new();
    readonly string _path;
    readonly PartialData _data;

    public string FilePath => _path;

    /// <param name="existing">Chunks of an earlier run that are kept (resume).</param>
    public PartialFile(string dir, string sourceFile, int chunkSeconds, bool timestamps, string engine, IEnumerable<PartialChunk>? existing = null)
    {
        var info = new FileInfo(sourceFile);
        _path = PathFor(dir, info);
        _data = new PartialData
        {
            Source = info.FullName, Size = SafeLength(info), MtimeTicks = SafeMtime(info),
            ChunkSeconds = chunkSeconds, Timestamps = timestamps, Engine = engine,
        };
        if (existing != null) _data.Chunks.AddRange(existing);
    }

    static long SafeLength(FileInfo f) { try { return f.Length; } catch { return 0; } }
    static long SafeMtime(FileInfo f) { try { return f.LastWriteTimeUtc.Ticks; } catch { return 0; } }

    /// <summary>Same file path, size and modification time = the same recording.</summary>
    public static string PathFor(string dir, FileInfo f)
    {
        var key = $"{f.FullName.ToLowerInvariant()}|{SafeLength(f)}|{SafeMtime(f)}";
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..20].ToLowerInvariant();
        return System.IO.Path.Combine(dir, hash + ".json");
    }

    public void Begin(int totalChunks) { }

    public void ChunkDone(LiveChunk c)
    {
        lock (_gate)
        {
            // A re-done chunk (same start) replaces the older one.
            _data.Chunks.RemoveAll(x => Math.Abs(x.StartSec - c.Start.TotalSeconds) < 0.5);
            _data.Chunks.Add(new PartialChunk
            {
                Index = c.Index, StartSec = c.Start.TotalSeconds, EndSec = c.End.TotalSeconds,
                Text = c.Text, Model = c.Model, Gap = c.IsGap,
            });
            _data.UpdatedUtc = DateTime.UtcNow;
            Write();
        }
    }

    void Write()
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_path)!);
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_data), new UTF8Encoding(false));
            File.Move(tmp, _path, overwrite: true);
        }
        catch (Exception e) { Log.Write("partial file not saved: " + e.Message); }
    }

    /// <summary>The job finished: nothing to resume any more.</summary>
    public void Delete()
    {
        lock (_gate) { try { File.Delete(_path); File.Delete(_path + ".tmp"); } catch { } }
    }

    /// <summary>Loads the partial file of this exact recording, or null (missing, unreadable, other version, too old).</summary>
    public static PartialData? TryLoad(string dir, string sourceFile, DateTime? nowUtc = null)
    {
        try
        {
            var info = new FileInfo(sourceFile);
            var path = PathFor(dir, info);
            if (!File.Exists(path)) return null;
            var data = JsonSerializer.Deserialize<PartialData>(File.ReadAllText(path, Encoding.UTF8));
            if (data == null || data.Version != 1 || data.Chunks.Count == 0) return null;
            if (data.Size != info.Length || data.MtimeTicks != info.LastWriteTimeUtc.Ticks) return null;
            if ((nowUtc ?? DateTime.UtcNow) - data.UpdatedUtc > MaxAge) return null;
            return data;
        }
        catch { return null; }
    }

    /// <summary>
    /// Continues after the last chunk of the unbroken run of finished chunks from the start. Chunks that ended as a gap are
    /// not counted, so they are tried again. Returns null when nothing usable was saved.
    /// </summary>
    public static ResumePoint? GetResumePoint(PartialData data)
    {
        var ordered = data.Chunks.OrderBy(c => c.StartSec).ToList();
        string text = "";
        double end = 0;
        int n = 0;
        foreach (var c in ordered)
        {
            if (c.Gap || c.StartSec > end + 1.0) break;
            text = Jobs.MergeChunkTranscripts(text, c.Text);
            end = Math.Max(end, c.EndSec);
            n++;
        }
        return n == 0 ? null : new ResumePoint(TimeSpan.FromSeconds(end), text, n);
    }

    /// <summary>The chunks of the unbroken prefix, to carry into the next partial file.</summary>
    public static List<PartialChunk> PrefixChunks(PartialData data)
    {
        var res = new List<PartialChunk>();
        double end = 0;
        foreach (var c in data.Chunks.OrderBy(c => c.StartSec))
        {
            if (c.Gap || c.StartSec > end + 1.0) break;
            res.Add(c);
            end = Math.Max(end, c.EndSec);
        }
        return res;
    }

    /// <summary>Removes partial files nobody will resume.</summary>
    public static void Purge(string dir, DateTime? nowUtc = null)
    {
        try
        {
            if (!Directory.Exists(dir)) return;
            var now = nowUtc ?? DateTime.UtcNow;
            foreach (var f in Directory.GetFiles(dir))
                if (now - File.GetLastWriteTimeUtc(f) > MaxAge) { try { File.Delete(f); } catch { } }
        }
        catch { }
    }
}

using NAudio.Wave;

namespace GeminiMenu;

/// <param name="AbsStart">Start of the core part inside the ORIGINAL recording (CoreStart/CoreEnd are relative to this chunk's own wav file).</param>
public sealed record AudioChunk(string Path, TimeSpan CoreStart, TimeSpan CoreEnd, int Depth, TimeSpan AbsStart = default)
{
    public TimeSpan CoreDuration => CoreEnd - CoreStart;
    public TimeSpan AbsEnd => AbsStart + CoreDuration;
}

public sealed class AudioChunkSet : IDisposable
{
    const int OverlapSeconds = 2;
    readonly string _directory;

    internal AudioChunkSet(string directory, List<AudioChunk> chunks)
    {
        _directory = directory;
        Chunks = chunks;
    }

    public List<AudioChunk> Chunks { get; }

    public bool CanSplit(AudioChunk chunk) =>
        chunk.CoreDuration > TimeSpan.FromSeconds(20) && chunk.Depth < 4;

    public List<AudioChunk> Split(AudioChunk chunk)
    {
        var coreDuration = chunk.CoreEnd - chunk.CoreStart;
        if (!CanSplit(chunk))
            throw new AppException(ErrKind.Blocked, "Gemini חתך תמלול של קטע קצר, ולא ניתן לחלק אותו עוד באופן בטוח.");

        using var reader = new WaveFileReader(chunk.Path);
        var middle = chunk.CoreStart + TimeSpan.FromTicks(coreDuration.Ticks / 2);
        var overlap = TimeSpan.FromSeconds(OverlapSeconds);
        var left = CreateChunk(reader, chunk.CoreStart, middle, overlap, _directory, chunk.Depth + 1, chunk.AbsStart);
        var right = CreateChunk(reader, middle, chunk.CoreEnd, overlap, _directory, chunk.Depth + 1, chunk.AbsStart + (middle - chunk.CoreStart));
        return new List<AudioChunk> { left, right };
    }

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    internal static AudioChunk CreateChunk(WaveFileReader reader, TimeSpan coreStart, TimeSpan coreEnd,
        TimeSpan overlap, string directory, int depth, TimeSpan absStart)
    {
        var audioStart = coreStart > overlap ? coreStart - overlap : TimeSpan.Zero;
        var audioEnd = coreEnd + overlap;
        if (audioEnd > reader.TotalTime) audioEnd = reader.TotalTime;

        var from = Align(reader.WaveFormat, audioStart);
        var to = Align(reader.WaveFormat, audioEnd);
        var path = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".wav");
        reader.Position = from;
        using var writer = new WaveFileWriter(path, reader.WaveFormat);
        var buffer = new byte[64 * 1024];
        var remaining = to - from;
        while (remaining > 0)
        {
            var requested = (int)Math.Min(buffer.Length, remaining);
            requested -= requested % reader.WaveFormat.BlockAlign;
            if (requested == 0) break;
            var read = reader.Read(buffer, 0, requested);
            if (read == 0) break;
            writer.Write(buffer, 0, read);
            remaining -= read;
        }

        return new AudioChunk(path, coreStart - audioStart, coreEnd - audioStart, depth, absStart);
    }

    static long Align(WaveFormat format, TimeSpan time)
    {
        var bytes = (long)(time.TotalSeconds * format.AverageBytesPerSecond);
        return bytes - bytes % format.BlockAlign;
    }
}

/// <summary>Uses Windows Media Foundation (via NAudio) to convert media into 16 kHz mono wav chunks.</summary>
public static class AudioConvert
{
    /// <summary>Shorter chunks are more reliable: the model rarely "gives up" on 3 minutes of audio.</summary>
    public const int ChunkSeconds = 180;
    const int ChunkOverlapSeconds = 2;

    public static TimeSpan GetDuration(string input)
    {
        using var reader = new MediaFoundationReader(input);
        return reader.TotalTime;
    }

    public static AudioChunkSet CreateChunks(string input)
    {
        var directory = Path.Combine(Path.GetTempPath(), "GeminiMenu_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var wavPath = Path.Combine(directory, "source.wav");
            using (var reader = new MediaFoundationReader(input))
            using (var resampler = new MediaFoundationResampler(reader, new WaveFormat(16000, 16, 1)) { ResamplerQuality = 60 })
                WaveFileWriter.CreateWaveFile(wavPath, resampler);

            var chunks = new List<AudioChunk>();
            using (var wav = new WaveFileReader(wavPath))
            {
                var duration = wav.TotalTime;
                var coreLength = TimeSpan.FromSeconds(ChunkSeconds);
                var overlap = TimeSpan.FromSeconds(ChunkOverlapSeconds);
                for (var start = TimeSpan.Zero; start < duration; start += coreLength)
                {
                    var end = start + coreLength;
                    if (end > duration) end = duration;
                    chunks.Add(AudioChunkSet.CreateChunk(wav, start, end, overlap, directory, 0, start));
                }
            }
            File.Delete(wavPath);
            return new AudioChunkSet(directory, chunks);
        }
        catch
        {
            try { Directory.Delete(directory, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            throw;
        }
    }

    public static string? ToWav(string input)
    {
        try
        {
            var tmp = Path.Combine(Path.GetTempPath(), "GeminiMenu_" + Guid.NewGuid().ToString("N") + ".wav");
            using var reader = new MediaFoundationReader(input);
            using var res = new MediaFoundationResampler(reader, new WaveFormat(16000, 16, 1)) { ResamplerQuality = 60 };
            WaveFileWriter.CreateWaveFile(tmp, res);
            return new FileInfo(tmp).Length > 1000 ? tmp : null;
        }
        catch
        {
            return null; // caller falls back to sending the original file
        }
    }
}

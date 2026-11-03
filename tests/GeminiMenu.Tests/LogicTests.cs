using Xunit;

namespace GeminiMenu;

public class TranscriptTests
{
    static readonly TimeSpan Chunk = TimeSpan.FromSeconds(180);

    [Fact]
    public void Evaluate_AcceptsNormalSpeech()
    {
        var text = string.Join(" ", Enumerable.Repeat("שלום זה משפט רגיל בתמלול", 100));
        Assert.Equal(Jobs.ChunkVerdict.Ok, Jobs.Evaluate(text, Chunk));
    }

    [Fact]
    public void Evaluate_DetectsExcuse()
    {
        Assert.Equal(Jobs.ChunkVerdict.Excuse, Jobs.Evaluate("הקובץ ארוך מדי ולכן לא תמלל את הכל, אמשיך בהמשך", Chunk));
    }

    [Fact]
    public void Evaluate_DetectsSuspiciouslyShort()
    {
        Assert.Equal(Jobs.ChunkVerdict.Short, Jobs.Evaluate("מילה אחת שתיים", Chunk));
    }

    [Fact]
    public void Evaluate_AcceptsNoSpeechMarkerAndShortChunks()
    {
        Assert.Equal(Jobs.ChunkVerdict.Ok, Jobs.Evaluate("[לא זוהה דיבור]", Chunk));
        Assert.Equal(Jobs.ChunkVerdict.Ok, Jobs.Evaluate("קצר", TimeSpan.FromSeconds(30)));
    }

    [Fact]
    public void NormalizeTimestamps_ShiftsToAbsoluteTime()
    {
        var r = Jobs.NormalizeTimestamps("[03:25] דובר 1: שלום\n[1:02:03] עוד", TimeSpan.FromMinutes(10));
        Assert.Contains("[00:13:25] דובר 1: שלום", r);
        Assert.Contains("[01:12:03] עוד", r);
    }

    [Fact]
    public void Merge_RemovesOverlapIgnoringTimestamps()
    {
        var a = "[00:09:50] דובר 1: אנחנו הולכים היום לדבר על הפרויקט החדש\n[00:09:58] דובר 2: כן בטח בוא נתחיל";
        var b = "[00:09:59] דובר 2: כן בטח בוא נתחיל\n[00:10:03] דובר 1: אז קודם כל התקציב";
        var merged = Jobs.MergeChunkTranscripts(a, b);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(merged, "בוא נתחיל"));
        Assert.Contains("אז קודם כל התקציב", merged);
    }

    [Fact]
    public void Merge_KeepsEverythingWhenNoOverlap()
    {
        var merged = Jobs.MergeChunkTranscripts("ראשון שני שלישי", "רביעי חמישי שישי");
        Assert.Contains("ראשון", merged);
        Assert.Contains("שישי", merged);
    }

    [Fact]
    public void StripTimestamps_RemovesStampsAndKeepsText()
    {
        var r = Jobs.StripTimestamps("[00:00:05] דובר 1: שלום\n[00:00:09] דובר 2: היי\n[⚠ הקטע 00:12:00–00:15:00 לא תומלל במלואו]");
        Assert.Equal("דובר 1: שלום\nדובר 2: היי\n[⚠ הקטע 00:12:00–00:15:00 לא תומלל במלואו]", r);
    }

    [Fact]
    public void ContextTail_StripsTimestamps()
    {
        var c = Jobs.ContextTail("[00:00:05] דובר 1: שלום\n[00:00:09] דובר 2: היי");
        Assert.DoesNotContain("[00", c);
        Assert.Contains("דובר 2: היי", c);
    }

    [Fact]
    public void SplitText_CoversWholeTextWithoutLoss()
    {
        var text = string.Join("\n", Enumerable.Repeat("שורה מספר אחת", 300));
        var pieces = Jobs.SplitText(text, 500);
        Assert.True(pieces.Count > 1);
        Assert.Equal(text, string.Concat(pieces));
        Assert.All(pieces, p => Assert.True(p.Length <= 500));
    }
}

public class SrtTests
{
    [Fact]
    public void FromTranscript_BuildsCues()
    {
        var srt = Srt.FromTranscript("[00:00:05] דובר 1: שלום לכולם\nהמשך משפט\n[00:00:09] דובר 2: היי");
        Assert.Contains("00:00:05,000 --> 00:00:09,000", srt);
        Assert.Contains("דובר 1: שלום לכולם המשך משפט", srt);
        Assert.Contains("00:00:09,000 --> 00:00:14,000", srt);
    }

    [Fact]
    public void FromTranscript_EmptyWithoutTimestamps()
    {
        Assert.Equal("", Srt.FromTranscript("אין כאן חותמות זמן"));
    }
}

public class ConfigAndUpdateTests
{
    [Fact]
    public void Config_StoresApiKeyEncrypted()
    {
        var path = Path.Combine(Path.GetTempPath(), "gm-test-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            new AppConfig { ApiKey = "secret-key-123", Timestamps = false, ParallelChunks = 3 }.Save(path);
            var raw = File.ReadAllText(path);
            Assert.DoesNotContain("secret-key-123", raw);
            var loaded = AppConfig.Load(path);
            Assert.Equal("secret-key-123", loaded.ApiKey);
            Assert.False(loaded.Timestamps);
            Assert.Equal(3, loaded.ParallelChunks);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Config_MigratesPlainKeyAndClampsValues()
    {
        var path = Path.Combine(Path.GetTempPath(), "gm-test-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            File.WriteAllText(path, "{ \"apiKey\": \"old-plain-key\", \"parallelChunks\": 99, \"chunkSeconds\": 5 }");
            var loaded = AppConfig.Load(path);
            Assert.Equal("old-plain-key", loaded.ApiKey);
            Assert.Equal(4, loaded.ParallelChunks);
            Assert.Equal(60, loaded.ChunkSeconds);
            Assert.DoesNotContain("old-plain-key", File.ReadAllText(path)); // migrated to encrypted
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData("v1.0.12", 12)]
    [InlineData("1.0.7", 7)]
    [InlineData("junk", 0)]
    public void ParseBuild(string tag, int expected) => Assert.Equal(expected, UpdateCheck.ParseBuild(tag));
}

using System.Text;
using System.Text.RegularExpressions;

namespace GeminiMenu;

/// <summary>Builds .srt subtitles from a transcript whose lines start with [HH:MM:SS].</summary>
public static class Srt
{
    static readonly Regex Cue = new(@"^\s*\[(\d{2,3}):(\d{2}):(\d{2})\]\s*(.*)$", RegexOptions.Compiled);

    public static string FromTranscript(string text)
    {
        var cues = new List<(TimeSpan Start, StringBuilder Text)>();
        foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            var m = Cue.Match(line);
            if (m.Success)
                cues.Add((new TimeSpan(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value)),
                          new StringBuilder(m.Groups[4].Value.Trim())));
            else if (cues.Count > 0)
                cues[cues.Count - 1].Text.Append(' ').Append(line);
        }
        cues.RemoveAll(c => c.Text.Length == 0);
        if (cues.Count == 0) return "";

        var sb = new StringBuilder();
        for (int i = 0; i < cues.Count; i++)
        {
            var start = cues[i].Start;
            var end = i + 1 < cues.Count ? cues[i + 1].Start : start + TimeSpan.FromSeconds(5);
            if (end - start > TimeSpan.FromSeconds(12)) end = start + TimeSpan.FromSeconds(12);
            if (end <= start) end = start + TimeSpan.FromSeconds(2);
            sb.Append(i + 1).Append("\r\n")
              .Append(Format(start)).Append(" --> ").Append(Format(end)).Append("\r\n")
              .Append(cues[i].Text).Append("\r\n\r\n");
        }
        return sb.ToString();
    }

    internal static string Format(TimeSpan t) =>
        $"{(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00},{t.Milliseconds:000}";
}

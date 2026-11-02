using NAudio.Wave;

namespace GeminiMenu;

/// <summary>Uses Windows Media Foundation (via NAudio) to turn m4a/wma/amr into 16 kHz mono wav.</summary>
public static class AudioConvert
{
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

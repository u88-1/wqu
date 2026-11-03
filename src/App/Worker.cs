using System.Diagnostics;
using System.Windows.Forms;

namespace GeminiMenu;

public static class Worker
{
    [System.Runtime.InteropServices.DllImport("shell32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    static extern int SetCurrentProcessExplicitAppUserModelID(string appId);

    public static void Run(JobKind kind, string file)
    {
        try { SetCurrentProcessExplicitAppUserModelID("GeminiMenu.Worker"); } catch { }
        Jobs.AudioConverter = AudioConvert.ToWav;
        AppConfig early;
        try { early = AppConfig.Load(); } catch { early = new AppConfig(); }
        HtmlPages.OutputDir = early.OutputDir;
        var form = new WaitForm(kind == JobKind.Transcribe ? "מתמלל" : "מסכם", Path.GetFileName(file),
            allowSrt: kind == JobKind.Transcribe && early.Timestamps, defaultFormat: early.DefaultFormat);
        using var cts = new CancellationTokenSource();
        form.CancelRequested += () => cts.Cancel();
        var stopwatch = Stopwatch.StartNew();
        string? outPath = null;
        Action<string> status = m => { Log.Write(m); form.SetStatus(m); };
        Log.Write($"=== start {kind}: {file}");

        form.Shown += async (_, _) =>
        {
            try
            {
                var cfg = AppConfig.Load();
                if (string.IsNullOrWhiteSpace(cfg.ApiKey))
                    throw new AppException(ErrKind.NoKey, "לא הוגדר מפתח API");
                HtmlPages.OutputDir = cfg.OutputDir;

                // Newer release? (never blocks the job)
                _ = Task.Run(async () =>
                {
                    var upd = await UpdateCheck.CheckAsync(cfg, cts.Token);
                    if (upd != null) form.ShowUpdate($"יצאה גרסה חדשה ({upd.Tag}). לחץ כדי לפתוח את דף ההורדה.", upd.Url);
                });

                // At most N files at the same time across all windows; the rest wait here.
                using var slot = await JobSlot.AcquireAsync(cfg.MaxConcurrentJobs, status, cts.Token);
                stopwatch.Restart();

                using var client = new GeminiClient(cfg, status);
                client.ModelWorked = m => { cfg.ResolvedModel = m; try { cfg.Save(); } catch { } };
                var options = JobOptions.From(cfg);
                var result = await Task.Run(() => Jobs.RunAsync(kind, file, client, status, cts.Token, options));
                result = result with { ProcessingTime = stopwatch.Elapsed };
                var suffix = kind == JobKind.Transcribe ? "תמלול" : "סיכום";
                var srtSource = result; // SRT always needs the timestamps
                if (kind == JobKind.Transcribe && form.SelectedFormat != ResultFormat.Srt && !form.IncludeTimestamps)
                {
                    var plain = Jobs.StripTimestamps(result.PlainText);
                    result = result with { PlainText = plain, BodyHtml = HtmlPages.TranscriptBody(plain) };
                }
                var html = HtmlPages.ResultPage(result);
                outPath = form.SelectedFormat switch
                {
                    ResultFormat.Text => HtmlPages.SaveText(file, suffix, result.PlainText),
                    ResultFormat.Both => SaveBoth(file, suffix, html, result.PlainText),
                    ResultFormat.Srt => SaveSrt(file, suffix, html, srtSource),
                    _ => HtmlPages.Save(file, suffix, html),
                };
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested)
            {
                // user cancelled: no page
            }
            catch (Exception ex)
            {
                Log.Write("ERROR: " + ex);
                try { outPath = HtmlPages.Save(file, "שגיאה", HtmlPages.ErrorPage(ex, Path.GetFileName(file), kind)); }
                catch { MessageBox.Show(ex.Message, "Gemini", MessageBoxButtons.OK, MessageBoxIcon.Error, MessageBoxDefaultButton.Button1, MessageBoxOptions.RtlReading | MessageBoxOptions.RightAlign); }
            }
            finally
            {
                form.Finished = true;
                form.Close();
            }
        };

        Application.Run(form);
        if (outPath != null)
            Process.Start(new ProcessStartInfo(outPath) { UseShellExecute = true });
    }

    static string SaveSrt(string file, string suffix, string html, JobResult result)
    {
        var srt = Srt.FromTranscript(result.PlainText);
        if (srt.Length == 0) return HtmlPages.Save(file, suffix, html); // no timestamps in the result
        HtmlPages.SaveText(file, suffix, result.PlainText);
        return HtmlPages.SaveSrt(file, suffix, srt);
    }

    static string SaveBoth(string file, string suffix, string html, string text)
    {
        HtmlPages.SaveText(file, suffix, text);
        return HtmlPages.Save(file, suffix, html);
    }
}

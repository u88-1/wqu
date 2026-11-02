using System.Diagnostics;
using System.Windows.Forms;

namespace GeminiMenu;

public static class Worker
{
    public static void Run(JobKind kind, string file)
    {
        Jobs.AudioConverter = AudioConvert.ToWav;
        var form = new WaitForm(kind == JobKind.Transcribe ? "מתמלל" : "מסכם", Path.GetFileName(file));
        using var cts = new CancellationTokenSource();
        form.CancelRequested += () => cts.Cancel();
        var stopwatch = Stopwatch.StartNew();
        string? outPath = null;

        form.Shown += async (_, _) =>
        {
            try
            {
                var cfg = AppConfig.Load();
                if (string.IsNullOrWhiteSpace(cfg.ApiKey))
                    throw new AppException(ErrKind.NoKey, "לא הוגדר מפתח API");
                using var client = new GeminiClient(cfg, form.SetStatus);
                client.ModelWorked = m => { cfg.ResolvedModel = m; try { cfg.Save(); } catch { } };
                var result = await Task.Run(() => Jobs.RunAsync(kind, file, client, form.SetStatus, cts.Token));
                result = result with { ProcessingTime = stopwatch.Elapsed };
                var suffix = kind == JobKind.Transcribe ? "תמלול" : "סיכום";
                var html = HtmlPages.ResultPage(result);
                outPath = form.SelectedFormat switch
                {
                    ResultFormat.Text => HtmlPages.SaveText(file, suffix, result.PlainText),
                    ResultFormat.Both => SaveBoth(file, suffix, html, result.PlainText),
                    _ => HtmlPages.Save(file, suffix, html),
                };
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested)
            {
                // user cancelled: no page
            }
            catch (Exception ex)
            {
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

    static string SaveBoth(string file, string suffix, string html, string text)
    {
        HtmlPages.SaveText(file, suffix, text);
        return HtmlPages.Save(file, suffix, html);
    }
}

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
        HtmlPages.ShowHebrewDate = early.HebrewDates;
        var form = new WaitForm(kind == JobKind.Transcribe ? "מתמלל" : "מסכם", Path.GetFileName(file),
            allowSrt: kind == JobKind.Transcribe && early.Timestamps, defaultFormat: early.DefaultFormat, isTranscription: kind == JobKind.Transcribe);
        using var cts = new CancellationTokenSource();
        form.CancelRequested += () => cts.Cancel();
        var stopwatch = Stopwatch.StartNew();
        string? outPath = null;
        LiveSession? live = null;      // the page in the browser that fills in while the work goes on
        PartialFile? partial = null;   // every finished chunk on disk, so a crash or a cancel loses nothing
        bool succeeded = false;
        Action<string> status = m => { Log.Write(m); form.SetStatus(m); };
        OutputSelection ReadOutputSelection() => form.InvokeRequired
            ? (OutputSelection)form.Invoke(new Func<OutputSelection>(() => form.SelectedOutput))!
            : form.SelectedOutput;
        Log.Write($"=== start {kind}: {file}");

        form.Shown += async (_, _) =>
        {
            try
            {
                var cfg = AppConfig.Load();
                HtmlPages.OutputDir = cfg.OutputDir;
                HtmlPages.ShowHebrewDate = cfg.HebrewDates;
                BookStyle.CustomPrompt = cfg.BookPrompt;

                // Which engine does what. Audio: Gemini or OpenAI. Editing and summaries: Gemini, Claude or OpenAI.
                bool audioOpenAi = kind == JobKind.Transcribe && cfg.TranscriptionProvider == "openai";
                bool needsText = kind == JobKind.Summarize || ReadOutputSelection().Mode != OutputMode.TranscriptExact;
                bool needGemini = (kind == JobKind.Transcribe && !audioOpenAi) || (needsText && cfg.TextProvider == "gemini");
                bool needOpenAi = audioOpenAi || (needsText && cfg.TextProvider == "openai");
                bool needClaude = needsText && cfg.TextProvider == "claude";
                if (needGemini && string.IsNullOrWhiteSpace(cfg.ApiKey)) throw new AppException(ErrKind.NoKey, "לא הוגדר מפתח API");
                if (needOpenAi && string.IsNullOrWhiteSpace(cfg.OpenAiKey)) throw new AppException(ErrKind.NoKey, "לא הוגדר מפתח API של OpenAI") { Provider = "OpenAI" };
                if (needClaude && string.IsNullOrWhiteSpace(cfg.ClaudeKey)) throw new AppException(ErrKind.NoKey, "לא הוגדר מפתח API של Claude") { Provider = "Claude" };

                // Newer release? (never blocks the job)
                _ = Task.Run(async () =>
                {
                    var upd = await UpdateCheck.CheckAsync(cfg, cts.Token);
                    if (upd != null) form.ShowUpdate($"יצאה גרסה חדשה ({upd.Tag}). לחץ כדי לפתוח את דף ההורדה.", upd.Url);
                });

                // At most N files at the same time across all windows; the rest wait here.
                using var slot = await JobSlot.AcquireAsync(cfg.MaxConcurrentJobs, status, cts.Token);
                stopwatch.Restart();

                // ---- live page, partial file and resume (transcriptions only; none of it may stop the job)
                ResumePoint? resume = null;
                if (kind == JobKind.Transcribe)
                {
                    List<PartialChunk>? kept = null;
                    try
                    {
                        PartialFile.Purge(PartialFile.DefaultDir);
                        var prev = PartialFile.TryLoad(PartialFile.DefaultDir, file);
                        if (prev != null && prev.Engine == cfg.TranscriptionProvider && prev.Timestamps == cfg.Timestamps
                            && PartialFile.GetResumePoint(prev) is { } point)
                        {
                            var upTo = $"{(int)point.StartAt.TotalHours:00}:{point.StartAt.Minutes:00}:{point.StartAt.Seconds:00}";
                            var answer = MessageBox.Show(form,
                                $"נמצא תמלול חלקי של ההקלטה הזאת: הקטעים עד {upTo} כבר תומללו.\n\nכן = המשך משם\nלא = התחל מההתחלה",
                                "המשך תמלול", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button1,
                                MessageBoxOptions.RtlReading | MessageBoxOptions.RightAlign);
                            if (answer == DialogResult.Yes) { resume = point; kept = PartialFile.PrefixChunks(prev); }
                        }
                    }
                    catch (Exception ex) { Log.Write("resume check failed: " + ex.Message); }

                    partial = new PartialFile(PartialFile.DefaultDir, file, cfg.ChunkSeconds, cfg.Timestamps, cfg.TranscriptionProvider, kept);
                    if (cfg.LiveView) live = LiveSession.TryStart(file);
                    if (live != null && resume != null) live.View.Seed(resume.Text);
                }

                using var gemini = new GeminiClient(cfg, status);
                gemini.ModelWorked = m => { cfg.ResolvedModel = m; try { cfg.Save(); } catch { } };
                // One HTTP client for all models of a provider; each provider keeps to its own models (never switches provider).
                using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
                List<string> ModelList(string primary, string extra, string defaultModel)
                {
                    var list = new List<string> { string.IsNullOrWhiteSpace(primary) ? defaultModel : primary.Trim() };
                    if (cfg.AutoFallback) list.AddRange(ModelFallback.ParseList(extra));
                    return list.Distinct().ToList();
                }
                IModelClient? textClient = cfg.TextProvider switch
                {
                    "claude" => string.IsNullOrWhiteSpace(cfg.ClaudeKey) ? null
                        : new FallbackModelClient("Claude", ModelList(cfg.ClaudeModel, cfg.ClaudeModels, ClaudeClient.DefaultModel),
                            (model, attempts) => new ClaudeClient(cfg.ClaudeKey, model, status, http) { Attempts = attempts }, status),
                    "openai" => string.IsNullOrWhiteSpace(cfg.OpenAiKey) ? null
                        : new FallbackModelClient("OpenAI", ModelList(cfg.OpenAiModel, cfg.OpenAiModels, OpenAiClient.DefaultModel),
                            (model, attempts) => new OpenAiClient(cfg.OpenAiKey, model, cfg.OpenAiTranscribeModel, cfg.TranscriptionLanguage, status, http) { Attempts = attempts }, status),
                    _ => string.IsNullOrWhiteSpace(cfg.ApiKey) ? null : gemini,
                };
                Jobs.AudioTranscriber? audio = null;
                if (audioOpenAi)
                {
                    var transcriber = new FallbackTranscriber("OpenAI", ModelList(cfg.OpenAiTranscribeModel, cfg.OpenAiTranscribeModels, OpenAiClient.DefaultTranscribeModel),
                        (model, attempts) =>
                        {
                            var c = new OpenAiClient(cfg.OpenAiKey, cfg.OpenAiModel, model, cfg.TranscriptionLanguage, status, http) { Attempts = attempts };
                            return (path, token) => c.TranscribeAsync(path, token);
                        }, status);
                    audio = (path, token) => transcriber.TranscribeAsync(path, token);
                }
                var options = JobOptions.From(cfg) with { Live = new CompositeSink(live?.View, partial), Resume = resume };
                var geminiOrNull = string.IsNullOrWhiteSpace(cfg.ApiKey) ? null : gemini;
                var result = await Task.Run(() => Jobs.RunAsync(kind, file, geminiOrNull, status, cts.Token, options, ReadOutputSelection, textClient, audio));
                var srtSource = result;
                var suffix = kind == JobKind.Transcribe ? "תמלול" : "סיכום";
                if (kind == JobKind.Transcribe)
                {
                    var transcriptSelection = ReadOutputSelection();
                    var transcript = result.PlainText;
                    if (form.SelectedFormat != ResultFormat.Srt && !form.IncludeTimestamps)
                        transcript = Jobs.StripTimestamps(transcript);
                    if (transcriptSelection.Mode == OutputMode.TranscriptSummaryOnly)
                    {
                        HtmlPages.SaveText(file, "תמלול מלא", transcript);
                        status("התמלול המלא נשמר בנפרד. מפיק סיכום...");
                        suffix = "סיכום";
                    }
                    else if (transcriptSelection.Mode == OutputMode.TranscriptClean)
                    {
                        suffix = "תמלול נקי";
                    }
                    else if (transcriptSelection.Mode == OutputMode.TranscriptBookStyle)
                    {
                        suffix = "בסגנון ספר";
                    }
                    else if (transcriptSelection.Mode == OutputMode.TranscriptAndSummary)
                    {
                        suffix = "תמלול וסיכום";
                    }
                    else if (transcriptSelection.Mode is OutputMode.TranscriptMeetingMinutes or OutputMode.TranscriptCustomInstruction)
                    {
                        suffix = "סיכום";
                    }
                    if (transcriptSelection.Mode != OutputMode.TranscriptExact && textClient == null)
                    {
                        // The choice changed while the recording was being transcribed: keep the transcript instead of losing it.
                        HtmlPages.SaveText(file, "תמלול מלא", transcript);
                        throw new AppException(ErrKind.NoKey, "לא הוגדר מפתח API למנוע העריכה והסיכום שנבחר. התמלול המלא נשמר בקובץ נפרד.");
                    }
                    if (transcriptSelection.Mode != OutputMode.TranscriptExact) live?.View.Processing("מעבד את התמלול (עריכה או סיכום)...");
                    result = await Jobs.FormatTranscriptAsync(Path.GetFileName(file), transcript, transcriptSelection, textClient ?? geminiOrNull!, status, cts.Token, options);
                }
                else if (ReadOutputSelection().Mode == OutputMode.DocumentBookStyle)
                {
                    suffix = "בסגנון ספר";
                }
                result = result with { ProcessingTime = stopwatch.Elapsed };
                var html = HtmlPages.ResultPage(result);
                outPath = form.SelectedFormat switch
                {
                    ResultFormat.Text => HtmlPages.SaveText(file, suffix, result.PlainText),
                    ResultFormat.Both => SaveBoth(file, suffix, html, result.PlainText),
                    ResultFormat.Srt => SaveSrt(file, suffix, html, srtSource, result.SupplementaryText),
                    ResultFormat.Docx => HtmlPages.SaveDocx(file, suffix, result),
                    _ => HtmlPages.Save(file, suffix, html),
                };
                live?.View.Complete(result.Title ?? (kind == JobKind.Transcribe ? "תמלול הקלטה" : "סיכום מסמך"), result.BodyHtml, result.PlainText, result.Note, outPath);
                partial?.Delete();
                succeeded = true;
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested)
            {
                // user cancelled: no result page. What was transcribed stays in the partial file.
                live?.View.Cancel();
            }
            catch (Exception ex)
            {
                Log.Write("ERROR: " + ex);
                live?.View.Fail(ex is AppException ? ex.Message : "אירעה שגיאה.");
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

        // The result page is already in the browser tab when that tab picked up the final state: do not open it twice.
        bool shownInLiveTab = succeeded && live != null && live.ResultSeenInBrowser(TimeSpan.FromSeconds(4));
        live?.Dispose();
        if (outPath != null && !(shownInLiveTab && outPath.EndsWith(".html", StringComparison.OrdinalIgnoreCase)))
            Process.Start(new ProcessStartInfo(outPath) { UseShellExecute = true });
    }

    static string SaveSrt(string file, string suffix, string html, JobResult result, string? supplementaryText)
    {
        var srt = Srt.FromTranscript(result.PlainText);
        if (srt.Length == 0) return HtmlPages.Save(file, suffix, html); // no timestamps in the result
        HtmlPages.SaveText(file, suffix, result.PlainText);
        if (supplementaryText != null) HtmlPages.SaveText(file, "סיכום", supplementaryText);
        return HtmlPages.SaveSrt(file, suffix, srt);
    }

    static string SaveBoth(string file, string suffix, string html, string text)
    {
        HtmlPages.SaveText(file, suffix, text);
        return HtmlPages.Save(file, suffix, html);
    }
}

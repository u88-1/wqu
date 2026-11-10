using System.Text.Json.Nodes;
using Xunit;

namespace GeminiMenu;

public class BookStyleTests
{
    // ------------------------------------------------------------ helpers

    const string SourceHeader = "הטקסט לעריכה:\n\n";

    static string SourceOf(JsonArray parts) => ((string)parts[1]!["text"]!)[SourceHeader.Length..];
    static string PromptOf(JsonArray parts) => (string)parts[0]!["text"]!;

    static string Lines(int count, string prefix = "שורה") =>
        string.Join("\n", Enumerable.Range(1, count).Select(i => $"{prefix} מספר {i} עם קצת טקסט כדי שהשורה תהיה ארוכה מספיק."));

    /// <summary>Fake model that returns the source it was given (a perfect, lossless "edit").</summary>
    sealed class Echo
    {
        public readonly List<string> Prompts = new();
        public readonly List<string> Sources = new();
        public Jobs.Generate Fn => (parts, cfg, ct) =>
        {
            Prompts.Add(PromptOf(parts));
            Sources.Add(SourceOf(parts));
            return Task.FromResult(new GenResult(SourceOf(parts), "STOP"));
        };
    }

    // ------------------------------------------------------------ prompt

    [Fact]
    public void BuildPrompt_SinglePart_HasKeyRulesAndNoPartNumbering()
    {
        var p = BookStyle.BuildPrompt(1, 1, "");
        Assert.Contains("עריכה ולא סיכום", p);
        Assert.Contains("[?]", p);
        Assert.Contains("חותמות זמן", p);
        Assert.DoesNotContain("חלק 1 מתוך", p);
    }

    [Fact]
    public void BuildPrompt_MultiPart_NumbersThePartAndCarriesContext()
    {
        var p = BookStyle.BuildPrompt(2, 5, "סוף החלק הקודם");
        Assert.Contains("חלק 2 מתוך 5", p);
        Assert.Contains("סוף החלק הקודם", p);
    }

    [Fact]
    public void BuildPrompt_MultiPart_FirstPartHasNoContextBlock()
    {
        var p = BookStyle.BuildPrompt(1, 3, "");
        Assert.Contains("חלק 1 מתוך 3", p);
        Assert.DoesNotContain("<<<", p);
    }

    // ------------------------------------------------------------ text helpers

    [Fact]
    public void Prepare_StripsTimestampsAndNormalizesLineBreaks()
    {
        var r = BookStyle.Prepare("[00:00:05] דובר 1: שלום\r\n[00:00:09] דובר 1: היי");
        Assert.DoesNotContain("[00", r);
        Assert.DoesNotContain("\r", r);
        Assert.Contains("דובר 1: שלום", r);
    }

    [Fact]
    public void NormalizeOutput_RemovesCodeFenceAndExtraBlankLines()
    {
        var r = BookStyle.NormalizeOutput("```text\nפסקה ראשונה\n\n\n\n\nפסקה שנייה\n```");
        Assert.Equal("פסקה ראשונה\n\nפסקה שנייה", r);
    }

    [Fact]
    public void NormalizeOutput_LeavesPlainTextAlone()
    {
        Assert.Equal("שלום עולם", BookStyle.NormalizeOutput("  שלום עולם \n"));
        Assert.Equal("", BookStyle.NormalizeOutput(null!));
    }

    [Fact]
    public void Tail_ShortTextIsReturnedWhole_LongTextIsCutAtLineStart()
    {
        Assert.Equal("קצר", BookStyle.Tail("קצר"));
        var tail = BookStyle.Tail(Lines(100));
        Assert.True(tail.Length <= 600);
        Assert.StartsWith("שורה", tail); // not cut mid-line
    }

    [Theory]
    [InlineData(0, 0, false)]       // nothing to edit
    [InlineData(5000, 0, true)]     // empty answer
    [InlineData(5000, 1500, true)]  // 30%: summarized or stopped early
    [InlineData(5000, 4000, false)] // normal editing shrinks a little
    [InlineData(1000, 100, false)]  // too short to judge
    [InlineData(100, 0, true)]      // but empty is always a failure
    public void LooksTruncated(int inLen, int outLen, bool expected) =>
        Assert.Equal(expected, BookStyle.LooksTruncated(new string('א', inLen), new string('ב', outLen)));

    [Fact]
    public void ToPlain_RemovesHeadingMarkersAndSeparatesParagraphs()
    {
        var r = BookStyle.ToPlain("# פרק א\n## כותרת משנה\nפסקה אחת.\nפסקה שתיים.");
        Assert.Equal("פרק א\n\nכותרת משנה\n\nפסקה אחת.\n\nפסקה שתיים.", r);
    }

    // ------------------------------------------------------------ review flags

    [Fact]
    public void ExtractFlags_FindsAllMarkerKindsWithLocation()
    {
        var text = "## פתיחה\nהוא אמר שהשם הוא יוסף [?] וזה היה בשנת תשכ\"ג.\nפסקה נקייה.\n## המשך\nשם [לא ברור] מופיע כאן.\n[⚠ הקטע 00:12:00–00:15:00 לא תומלל במלואו]\nוכאן [? כתיב אפשרי אחר] עוד משהו.";
        var flags = BookStyle.ExtractFlags(text);
        Assert.Equal(4, flags.Count);
        Assert.Contains("פסקה 1", flags[0]);
        Assert.Contains("פתיחה", flags[0]);
        Assert.Contains("[?]", flags[0]);
        Assert.Contains("פסקה 3", flags[1]);
        Assert.Contains("המשך", flags[1]);
        Assert.Contains("[לא ברור]", flags[1]);
        Assert.Contains("[⚠", flags[2]);
        Assert.Contains("[? כתיב אפשרי אחר]", flags[3]);
    }

    [Fact]
    public void ExtractFlags_NoMarkers_ReturnsEmptyAndNoSection()
    {
        var flags = BookStyle.ExtractFlags("טקסט נקי לגמרי.\nעוד פסקה.");
        Assert.Empty(flags);
        Assert.Equal("", BookStyle.FlagsSection(flags));
    }

    [Fact]
    public void ExtractFlags_LongLine_SnippetDoesNotStartMidWord()
    {
        var line = string.Join(" ", Enumerable.Repeat("מילהארוכהמאוד", 12)) + " סוף [?]";
        var f = Assert.Single(BookStyle.ExtractFlags(line));
        Assert.Contains("…", f);
        Assert.EndsWith("[?]", f);
        Assert.True(f.Length < 140);
    }

    // ------------------------------------------------------------ html

    [Fact]
    public void BookBody_RendersHeadingsParagraphsAndHighlightsMarkers()
    {
        var html = HtmlPages.BookBody("# פרק\n## נושא\nשלום [?] עולם.");
        Assert.Contains("<h2 dir=\"auto\">פרק</h2>", html);
        Assert.Contains("<h3 dir=\"auto\">נושא</h3>", html);
        Assert.Contains("<mark>[?]</mark>", html);
        Assert.DoesNotContain("# ", html);
    }

    [Fact]
    public void BookBody_EscapesHtmlInTextAndFlags()
    {
        var html = HtmlPages.BookBody("<script>alert(1)</script> [?] & עוד", new List<string> { "פסקה 1: <b>x</b>" });
        Assert.DoesNotContain("<script>", html);
        Assert.DoesNotContain("<b>x</b>", html);
        Assert.Contains("&lt;script&gt;", html);
        Assert.Contains("מקומות לבירור", html);
    }

    // ------------------------------------------------------------ editing flow

    [Fact]
    public async Task BookStyleText_SinglePiece_OneCallNoWarnings()
    {
        var echo = new Echo();
        var (text, warnings) = await Jobs.BookStyleTextAsync("[00:00:01] דובר 1: שלום לכולם", echo.Fn, _ => { }, CancellationToken.None);
        Assert.Single(echo.Prompts);
        Assert.Empty(warnings);
        Assert.Equal("דובר 1: שלום לכולם", text); // timestamp stripped before sending
    }

    [Fact]
    public async Task BookStyleText_LongSource_SplitsInOrderAndChainsContext()
    {
        var source = Lines(700); // ~ 38,000 chars: 3 pieces of up to 15,000
        Assert.True(source.Length > 2 * BookStyle.PieceChars);
        var echo = new Echo();
        var statuses = new List<string>();
        var (text, warnings) = await Jobs.BookStyleTextAsync(source, echo.Fn, statuses.Add, CancellationToken.None);

        Assert.True(echo.Prompts.Count >= 3);
        Assert.Empty(warnings);
        Assert.Contains("חלק 1 מתוך", echo.Prompts[0]);
        Assert.Contains($"חלק 2 מתוך {echo.Prompts.Count}", echo.Prompts[1]);
        Assert.DoesNotContain("<<<", echo.Prompts[0]);
        Assert.Contains("<<<", echo.Prompts[1]);   // context from the previous piece
        Assert.Contains(statuses, s => s.Contains("חלק 2 מתוך"));
        // Lossless: every source line is present exactly once, in order.
        for (int i = 1; i <= 700; i++)
            Assert.Equal(1, System.Text.RegularExpressions.Regex.Matches(text, $@"שורה מספר {i} ").Count);
        Assert.True(text.IndexOf("שורה מספר 2 ") < text.IndexOf("שורה מספר 699 "));
    }

    [Fact]
    public async Task BookStyleText_MaxTokens_SplitsPieceInHalvesAndLosesNothing()
    {
        var source = Lines(60);                       // ~ 4,000 chars, one piece
        int calls = 0;
        Jobs.Generate gen = (parts, cfg, ct) =>
        {
            calls++;
            var src = SourceOf(parts);
            // The model "runs out of tokens" on anything bigger than 3,000 chars.
            return Task.FromResult(src.Length > 3000
                ? new GenResult(src[..500], "MAX_TOKENS")
                : new GenResult(src, "STOP"));
        };
        var (text, warnings) = await Jobs.BookStyleTextAsync(source, gen, _ => { }, CancellationToken.None);

        Assert.True(calls >= 3);                      // 1 failed + at least 2 halves
        Assert.Empty(warnings);
        for (int i = 1; i <= 60; i++) Assert.Contains($"שורה מספר {i} ", text);
    }

    [Fact]
    public async Task BookStyleText_MaxTokensOnSmallPiece_KeepsPartialAndWarns()
    {
        Jobs.Generate gen = (parts, cfg, ct) => Task.FromResult(new GenResult("חלקי", "MAX_TOKENS"));
        var (text, warnings) = await Jobs.BookStyleTextAsync("טקסט קצר", gen, _ => { }, CancellationToken.None);
        Assert.Equal("חלקי", text);
        Assert.Contains(warnings, w => w.Contains("נקטע"));
    }

    [Fact]
    public async Task BookStyleText_SuspiciouslyShortAnswer_RetriesOnceThenSucceeds()
    {
        var source = Lines(60);
        int calls = 0;
        Jobs.Generate gen = (parts, cfg, ct) =>
            Task.FromResult(++calls == 1 ? new GenResult("סיכום קצר מאוד", "STOP") : new GenResult(SourceOf(parts), "STOP"));
        var (text, warnings) = await Jobs.BookStyleTextAsync(source, gen, _ => { }, CancellationToken.None);
        Assert.Equal(2, calls);
        Assert.Empty(warnings);
        Assert.Contains("שורה מספר 60 ", text);
    }

    [Fact]
    public async Task BookStyleText_AlwaysShort_StopsAfterOneRetryAndWarns()
    {
        var source = Lines(60);
        int calls = 0;
        Jobs.Generate gen = (parts, cfg, ct) => { calls++; return Task.FromResult(new GenResult("סיכום קצר מאוד", "STOP")); };
        var (_, warnings) = await Jobs.BookStyleTextAsync(source, gen, _ => { }, CancellationToken.None);
        Assert.Equal(2, calls);
        Assert.Contains(warnings, w => w.Contains("הושמטו"));
    }

    [Fact]
    public async Task BookStyleText_EmptyInput_ThrowsFriendlyError()
    {
        var ex = await Assert.ThrowsAsync<AppException>(() =>
            Jobs.BookStyleTextAsync("   \n [00:00:01] ", new Echo().Fn, _ => { }, CancellationToken.None));
        Assert.Equal(ErrKind.FileProblem, ex.Kind);
    }

    [Fact]
    public async Task BookStyleText_Cancelled_StopsBeforeCallingModel()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var echo = new Echo();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Jobs.BookStyleTextAsync("טקסט", echo.Fn, _ => { }, cts.Token));
        Assert.Empty(echo.Prompts);
    }

    [Fact]
    public async Task BookStyleText_ModelErrorPropagates()
    {
        Jobs.Generate gen = (parts, cfg, ct) => throw new AppException(ErrKind.Quota, "מגבלה");
        var ex = await Assert.ThrowsAsync<AppException>(() => Jobs.BookStyleTextAsync("טקסט", gen, _ => { }, CancellationToken.None));
        Assert.Equal(ErrKind.Quota, ex.Kind);
    }

    // ------------------------------------------------------------ result

    [Fact]
    public void BookResult_AppendsReviewListToPlainTextAndIsNotLabelledSummary()
    {
        var r = Jobs.BookResult(JobKind.Transcribe, "lecture.mp3", "## פתיחה\nהשם [?] לא ברור.\nפסקה שנייה.", new List<string> { "אזהרה לדוגמה" });
        Assert.Contains("מקומות לבירור:", r.PlainText);
        Assert.DoesNotContain("##", r.PlainText);
        Assert.Contains("סומנו 1 מקומות לבירור", r.Note);
        Assert.Contains("אזהרה לדוגמה", r.Note);
        Assert.False(r.IsSummary);
        var page = HtmlPages.ResultPage(r);
        Assert.DoesNotContain("זה סיכום של Gemini", page);
        Assert.Contains("<mark>[?]</mark>", page);
        Assert.Contains("טקסט בסגנון ספר", page);
    }

    [Fact]
    public void BookResult_CleanText_HasNoReviewSection()
    {
        var r = Jobs.BookResult(JobKind.Summarize, "doc.txt", "פסקה נקייה אחת.", new List<string>());
        Assert.DoesNotContain("מקומות לבירור", r.PlainText);
        Assert.DoesNotContain("סומנו", r.Note);
    }
}

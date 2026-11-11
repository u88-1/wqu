using Xunit;

namespace GeminiMenu;

public class HebrewDateTests
{
    [Theory]
    [InlineData(2026, 9, 12, "א׳ בתשרי תשפ״ז")]    // Rosh Hashana 5787
    [InlineData(2026, 3, 3, "י״ד באדר תשפ״ו")]     // Purim 5786 (common year)
    [InlineData(2026, 4, 2, "ט״ו בניסן תשפ״ו")]    // first day of Pesach 5786
    [InlineData(2024, 3, 24, "י״ד באדר ב׳ תשפ״ד")] // Purim in the leap year 5784
    [InlineData(2025, 9, 22, "כ״ט באלול תשפ״ה")]
    public void Format_MatchesKnownDates(int y, int m, int d, string expected)
    {
        Assert.Equal(expected, HebrewDate.Format(new DateTime(y, m, d)));
    }

    [Theory]
    [InlineData(1, "א׳")]
    [InlineData(15, "ט״ו")]
    [InlineData(16, "ט״ז")]
    [InlineData(30, "ל׳")]
    [InlineData(787, "תשפ״ז")]
    [InlineData(115, "קט״ו")]
    public void Gematria_WritesHebrewNumerals(int n, string expected)
    {
        Assert.Equal(expected, HebrewDate.Gematria(n));
    }

    [Fact]
    public void Stamp_AddsHebrewDateOnlyWhenAsked()
    {
        var t = new DateTime(2026, 9, 12, 8, 30, 0);
        Assert.Equal("12/09/2026 08:30", HebrewDate.Stamp(t, false));
        Assert.Equal("12/09/2026 08:30  ·  א׳ בתשרי תשפ״ז", HebrewDate.Stamp(t, true));
    }

    [Fact]
    public void Format_OutOfRangeDate_GivesEmptyText()
    {
        Assert.Equal("", HebrewDate.Format(DateTime.MinValue));
    }

    [Fact]
    public void SummaryPrompt_MentionsTodayAndKeepsHebrewDatesAsWritten()
    {
        var prompt = Jobs.SummaryPrompt(new OutputSelection(OutputMode.DocumentTasksDatesAmounts), "normal");
        Assert.Contains("התאריך היום", prompt);
        Assert.Contains("תאריכים עבריים", prompt);
    }
}

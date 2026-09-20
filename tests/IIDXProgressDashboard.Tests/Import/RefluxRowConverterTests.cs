using System.Text;
using IIDXProgressDashboard.Import;
using Xunit;

namespace IIDXProgressDashboard.Tests.Import;

public sealed class RefluxRowConverterTests
{
    private static RefluxConvertedRow Convert(string difficulty = "SPA", string lamp = "NC", string bp = "-",
        string score = "100", string date = "2026/01/01 00:00:01", RefluxImportOptions? options = null,
        string extraHeader = "", string extraRow = "")
    {
        var text = "title\tdifficulty\tlamp\texscore\tdate\tmisscount" + extraHeader + "\n合成曲\t" +
            difficulty + "\t" + lamp + "\t" + score + "\t" + date + "\t" + bp + extraRow + "\n";
        var row = RefluxSessionReader.Parse(Encoding.UTF8.GetBytes(text)).Rows[0];
        return RefluxRowConverter.Convert(row, options ?? new());
    }
    [Theory]
    [InlineData("SPB")][InlineData("SPN")][InlineData("SPH")][InlineData("SPA")][InlineData("SPL")]
    [InlineData("DPB")][InlineData("DPN")][InlineData("DPH")][InlineData("DPA")][InlineData("DPL")]
    public void AcceptsAllChartTypes(string difficulty) => Assert.Empty(Convert(difficulty).Issues);

    [Theory]
    [InlineData("NP", 0)][InlineData("F", 1)][InlineData("AC", 2)][InlineData("EC", 3)]
    [InlineData("NC", 4)][InlineData("HC", 5)][InlineData("EX", 6)][InlineData("FC", 7)][InlineData("PFC", 7)]
    public void ConvertsLampIndependentlyFromGauge(string lamp, int expected)
    {
        var value = Convert(lamp: lamp, extraHeader: "\tgauge", extraRow: "\tEX HARD");
        Assert.Empty(value.Issues);
        Assert.Equal(expected, value.Lamp);
    }
    [Theory]
    [InlineData("-", null)][InlineData("", null)][InlineData("0", 0)][InlineData("10", 10)]
    public void DistinguishesUnknownAndZeroBp(string bp, int? expected) => Assert.Equal(expected, Convert(bp: bp).Numbers["misscount"]);

    [Fact]
    public void RejectsBadValuesWithoutSubstitutingZero()
    {
        Assert.Contains(Convert(lamp: "unknown").Issues, i => i.Code == "INVALID_LAMP");
        Assert.Contains(Convert(difficulty: "SPX").Issues, i => i.Code == "INVALID_DIFFICULTY");
        foreach (var score in new[] { "", "-", "-1", "2147483648", "1.5" })
            Assert.Contains(Convert(score: score).Issues, i => i.Code == "INVALID_EXSCORE");
        Assert.Contains(Convert(bp: "unknown").Issues, i => i.Code == "INVALID_MISSCOUNT");
        Assert.Contains(Convert(extraHeader: "\tlevel", extraRow: "\t13").Issues, i => i.Code == "INVALID_LEVEL");
        Assert.Contains(Convert(extraHeader: "\tnotecount", extraRow: "\t10").Issues, i => i.Code == "INVALID_EXSCORE");
        Assert.Contains(Convert(extraHeader: "\tplaytype", extraRow: "\tDP").Issues, i => i.Code == "INVALID_PLAYTYPE");
    }
    [Fact]
    public void TimeConversionPreservesSecondsAndRejectsAmbiguity()
    {
        Assert.Equal("2025-12-31T15:00:01Z", Convert(options: new(RefluxTimeMode.Local, "Tokyo Standard Time")).PlayedAt);
        Assert.Contains(Convert(date: "bad").Issues, i => i.Code == "INVALID_DATE");
        foreach (var date in new[] { "2026/03/08 02:30:00", "2026/11/01 01:30:00" })
            Assert.Contains(Convert(date: date, options: new(RefluxTimeMode.Local, "Eastern Standard Time")).Issues,
                i => i.Code == "INVALID_DATE");
        Assert.Throws<ArgumentException>(() => Convert(options: new(RefluxTimeMode.Local)));
    }
}

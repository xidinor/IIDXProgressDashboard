using System.Text;
using IIDXProgressDashboard.Import;
using Xunit;

namespace IIDXProgressDashboard.Tests.Import;

public sealed class RefluxSessionReaderTests
{
    private const string Header = "title\tdifficulty\tlamp\texscore\tdate";
    private const string Row = "合成曲\tDPA\tFC\t123\t2026/09/20 12:34:56";
    private static RefluxSessionSnapshot Parse(string text) => RefluxSessionReader.Parse(Encoding.UTF8.GetBytes(text));

    [Theory]
    [InlineData("\n", false)]
    [InlineData("\r\n", false)]
    [InlineData("\r\n", true)]
    public void PreservesIdenticalRowsAndOriginalText(string newline, bool bom)
    {
        // 同一内容でも2プレイを保持し、BOM・改行を値に混入させない。
        var snapshot = Parse((bom ? "\uFEFF" : "") + Header + newline + Row + newline + Row + newline);
        Assert.Equal(2, snapshot.Rows.Count);
        Assert.Equal(Row, snapshot.Rows[0].RawLine);
        Assert.Equal("合成曲", snapshot.Rows[0].Values["title"]);
        Assert.Equal(3, snapshot.Rows[1].LineNumber);
    }

    [Fact]
    public void UsesHeaderNamesAndPreservesUnknownColumnsAndQuotes()
    {
        var snapshot = Parse("date\texscore\tlamp\tdifficulty\ttitle\textra\n2026/09/20 12:34:56\t0\tNP\tSPB\t\"曲\"\tx\n");
        Assert.Equal("\"曲\"", snapshot.Rows[0].Values["title"]);
        Assert.Equal("x", snapshot.Rows[0].Values["extra"]);
    }

    [Theory]
    [InlineData("")]
    [InlineData("title\tlamp\n")]
    [InlineData("title\tdifficulty\tlamp\texscore\tdate\ttitle\n")]
    [InlineData("title\tdifficulty\tlamp\texscore\tdate\t\n")]
    [InlineData("title\tdifficulty\tlamp\texscore\tdate")]
    [InlineData("title\tdifficulty\tlamp\texscore\tdate\r\npartial")]
    public void RejectsFileBeforeRowProcessing(string input) => Assert.Throws<InvalidDataException>(() => Parse(input));

    [Fact]
    public void RetainsMalformedAndBlankRowsWithoutGuessingColumns()
    {
        var snapshot = Parse(Header + "\nwrong\trow\n\n" + Row + "\n");
        Assert.Equal(3, snapshot.Rows.Count);
        Assert.All(snapshot.Rows.Take(2), row =>
        {
            Assert.Equal("INVALID_FIELD_COUNT", row.ErrorCode);
            Assert.Empty(row.Values);
        });
        Assert.Null(snapshot.Rows[2].ErrorCode);
    }

    [Fact]
    public void RejectsInvalidEncodingAndCancellation()
    {
        Assert.Throws<DecoderFallbackException>(() => RefluxSessionReader.Parse([0xff]));
        Assert.Throws<OperationCanceledException>(() => RefluxSessionReader.Parse([], new CancellationToken(true)));
    }
}

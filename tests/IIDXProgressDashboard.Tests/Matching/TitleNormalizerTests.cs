using System.Globalization;
using IIDXProgressDashboard.Matching;
using Xunit;

namespace IIDXProgressDashboard.Tests;

public sealed class TitleNormalizerTests
{
    [Theory]
    [InlineData("  ＡｂＣ　\tＤ  ", "ABC D")]
    [InlineData("ｶﾞ", "ガ")]
    [InlineData("e\u0301", "É")]
    [InlineData("Ⅸ ①", "IX 1")]
    [InlineData("Ａ－Ｂ (Remix)†※", "A-B (REMIX)†※")]
    [InlineData("a\u00a0b\r\nc", "A B C")]
    [InlineData("\t　", "")]
    [InlineData("a\u200bb", "A\u200bB")]
    public void NormalizationIsExplicitAndIdempotent(string input, string expected)
    {
        var actual = TitleNormalizer.Normalize(input);
        Assert.Equal(expected, actual);
        Assert.Equal(actual, TitleNormalizer.Normalize(actual));
    }

    [Theory]
    [InlineData("A-B", "AB")]
    [InlineData("A B", "AB")]
    [InlineData("A†", "A")]
    [InlineData("A (Remix)", "A")]
    [InlineData("A〜B", "A~B")]
    [InlineData("<b>A</b>", "A")]
    public void UnspecifiedDifferencesArePreserved(string first, string second) =>
        Assert.NotEqual(TitleNormalizer.Normalize(first), TitleNormalizer.Normalize(second));

    [Fact]
    public void CultureDoesNotChangeKeysAndNullIsRejected()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("tr-TR");
            Assert.Equal("IIDX", TitleNormalizer.Normalize("iidx"));
            Assert.Throws<ArgumentNullException>(() => TitleNormalizer.Normalize(null!));
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }
}

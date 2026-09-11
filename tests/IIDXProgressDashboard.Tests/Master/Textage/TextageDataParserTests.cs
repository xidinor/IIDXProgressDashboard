using IIDXProgressDashboard.Master;
using Xunit;

namespace IIDXProgressDashboard.Tests;

public sealed class TextageDataParserTests
{
    [Fact]
    public void TransitionsBetweenNestedContainersStringsCommentsAndTrailingComma()
    {
        var parser = new TextageDataParser("fixture", "{/* ' } */ 'x':[1, ['a]//', 'b/*'],],}//end", default);
        var root = Assert.IsType<Dictionary<string, object>>(parser.Value());
        var array = Assert.IsType<List<object>>(root["x"]);
        Assert.Equal(new object[] { "a]//", "b/*" }, Assert.IsType<List<object>>(array[1]));
        Assert.Equal("eof", parser.Peek().Kind);
    }

    [Theory]
    [InlineData("'\\n\\r\\t\\b\\f\\v'", "\n\r\t\b\f\v")]
    [InlineData("'\\u65e5\\x41'", "日A")]
    [InlineData("'\\\'\\\\\\/\\\"'", "'\\/\"")]
    [InlineData("'a\\\nb'", "ab")]
    [InlineData("'a\\\r\nb'", "ab")]
    public void DecodesSupportedStringEscapes(string source, string expected)
        => Assert.Equal(expected, new TextageDataParser("fixture", source, default).Value());

    [Theory]
    [InlineData("'")]
    [InlineData("'a\\")]
    [InlineData("'a\nb'")]
    [InlineData("'\\u00'")]
    [InlineData("'\\xGG'")]
    [InlineData("'\\q'")]
    [InlineData("/* unfinished")]
    [InlineData("[1")]
    [InlineData("{x:1")]
    [InlineData("[1,,2]")]
    [InlineData("[1}")]
    [InlineData("{x:1,x:2}")]
    [InlineData("unknown")]
    [InlineData("`template`")]
    [InlineData("99999999999999999999999")]
    [InlineData("'s'.fontcolor(1)")]
    public void RejectsInvalidStateOrUnfinishedToken(string source)
        => Assert.Throws<InvalidDataException>(() => new TextageDataParser("fixture", source, default).Value());

    [Fact]
    public void RejectsExcessiveNesting()
        => Assert.Throws<InvalidDataException>(() => new TextageDataParser("fixture", new string('[', 66) + "0" + new string(']', 66), default).Value());

    [Theory]
    [InlineData("function f(){ x.replace(/[()'\"]/g,''); }", true)]
    [InlineData("function f(){ x.replace(/[/]/g,''); }", true)]
    [InlineData("function f(){ x.replace(/unterminated", false)]
    [InlineData("function f(){", false)]
    [InlineData("function f(){]}", false)]
    [InlineData("function f(){/*", false)]
    [InlineData("actbl={};", false)]
    [InlineData("actbl['song'][1]=2;", false)]
    [InlineData("vertbl[35]+='changed';", false)]
    [InlineData("titletbl.song[0]++;", false)]
    [InlineData("if (titletbl['song'][0]===1) { result=1; }", true)]
    public void ChecksIgnoredDisplayCodeBoundaries(string source, bool valid)
    {
        var parser = new TextageDataParser("fixture", source, default);
        if (valid) parser.SkipDisplayCode();
        else Assert.Throws<InvalidDataException>(() => parser.SkipDisplayCode());
    }
}

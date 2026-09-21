using IIDXProgressDashboard.Master;
using Xunit;

namespace IIDXProgressDashboard.Tests;

/// <summary>パーサー内部ではなく公開APIで、許可する値と全文拒否の境界を検証する。</summary>
public sealed class TextageAstContractTests
{
    [Fact]
    public void SyntaxErrorIncludesFileLineAndColumn()
    {
        var data = TextageMasterParserTests.Fixture();
        data["datatbl.js"] += "\nfunction broken(){const x;}";
        var error = Assert.Throws<InvalidDataException>(() => TextageMasterParserTests.Parse(data));
        Assert.Matches(@"datatbl\.js:2:[1-9][0-9]*:", error.Message);
        Assert.NotNull(error.InnerException);
    }

    [Theory]
    [InlineData("'\\u65e5\\x41'", "日A")]
    [InlineData("'a\\\nb'", "ab")]
    [InlineData("'a\\\r\nb'", "ab")]
    [InlineData("'a\\t\\n\\r\\b\\f\\vb'", "a\t\n\r\b\f\vb")]
    [InlineData("'a\\\'\\\\\\/\\\"b'", "a'\\/\"b")]
    public void DecodesStringsAndPreservesOriginalSource(string value, string expected)
    {
        var data = TextageMasterParserTests.Fixture();
        data["titletbl.js"] = $"titletbl={{'song':[1,1,0,'G','A',{value}]}};";
        var result = TextageMasterParserTests.Parse(data);
        Assert.Equal(expected, Assert.Single(result.Songs).Title);
        Assert.Equal(data["titletbl.js"], result.Sources.Files["titletbl.js"].Content);
    }

    [Theory]
    [InlineData("'")]
    [InlineData("'a\\")]
    [InlineData("'a\nb'")]
    [InlineData("'\\u00'")]
    [InlineData("'\\xGG'")]
    [InlineData("'\\q'")]
    [InlineData("'\\0'")]
    [InlineData("'\\u{41}'")]
    [InlineData("/* unfinished")]
    [InlineData("[1")]
    [InlineData("{x:1")]
    [InlineData("[1,,2]")]
    [InlineData("[1}")]
    [InlineData("{x:1,x:2}")]
    [InlineData("{get x(){return 1;}}")]
    [InlineData("{['x']:1}")]
    [InlineData("{...other}")]
    [InlineData("unknown")]
    [InlineData("`template`")]
    [InlineData("99999999999999999999999")]
    [InlineData("'s'.fontcolor(1)")]
    [InlineData("'s'['fontcolor']('red')")]
    [InlineData("'s'.fontcolor('red').fontcolor('blue')")]
    [InlineData("'s'.fontcolor(run())")]
    [InlineData("run()")]
    [InlineData("1+2")]
    [InlineData("0x10")]
    [InlineData("1e2")]
    [InlineData("01")]
    [InlineData("true")]
    [InlineData("null")]
    [InlineData("('text')")]
    [InlineData("{日本:1}")]
    [InlineData("{\\u0078:1}")]
    public void RejectsUnsupportedDataEvenInExcludedTag(string value)
    {
        // 意味検査で除外されるtagでも、未知の値や構文を黙って受理しない。
        var data = TextageMasterParserTests.Fixture();
        data["datatbl.js"] = data["datatbl.js"].Replace("'song':", $"'firstemo':{value},'song':");
        var error = Assert.Throws<InvalidDataException>(() => TextageMasterParserTests.Parse(data));
        Assert.Contains("datatbl.js:", error.Message);
    }

    [Theory]
    [InlineData("function f(){ x.replace(/[()'\"]/g,''); }", true)]
    [InlineData("function f(){ x.replace(/[/]/g,''); }", true)]
    [InlineData("function f(){ return /a+/g; }", true)]
    [InlineData("function f(){ x.replace(/unterminated", false)]
    [InlineData("function f(){", false)]
    [InlineData("function f(){]}", false)]
    [InlineData("function f(){/*", false)]
    [InlineData("function f(){let = ;}", false)]
    [InlineData("function f(){const x;}", false)]
    [InlineData("function f(){ return /[/; }", false)]
    [InlineData("function f(){actbl={};}", false)]
    [InlineData("function f(){actbl['song'][1]=2;}", false)]
    [InlineData("function f(){vertbl[35]+='changed';}", false)]
    [InlineData("function f(){titletbl.song[0]++;}", false)]
    [InlineData("function f(){++titletbl.song[0];}", false)]
    [InlineData("function f(){delete titletbl.song;}", false)]
    [InlineData("function f(){[actbl]=x;}", false)]
    [InlineData("function f(){actbl.song **= 2;}", false)]
    [InlineData("function f(){var actbl={};}", false)]
    [InlineData("function f(){for(actbl in x){}}", false)]
    [InlineData("function f(){if (titletbl['song'][0]===1) { result=1; }}", true)]
    public void ValidatesEntireDisplayCodeWithoutExecutingIt(string code, bool valid)
    {
        var data = TextageMasterParserTests.Fixture();
        data["datatbl.js"] += code;
        if (valid) Assert.Equal(10, TextageMasterParserTests.Parse(data).Charts.Count);
        else Assert.Throws<InvalidDataException>(() => TextageMasterParserTests.Parse(data));
    }

    [Theory]
    [InlineData(66)]
    [InlineData(10000)]
    public void RejectsDeepDataBeforeReturningCandidates(int depth)
    {
        var data = TextageMasterParserTests.Fixture();
        data["datatbl.js"] = "datatbl=" + new string('[', depth) + "0" + new string(']', depth) + ";";
        Assert.Throws<InvalidDataException>(() => TextageMasterParserTests.Parse(data));
    }

    [Fact]
    public void RejectsDeepParenthesesAndArrayConstructorWithoutArgumentsSyntax()
    {
        var data = TextageMasterParserTests.Fixture();
        data["datatbl.js"] = "datatbl=" + new string('(', 10000) + "0" + new string(')', 10000) + ";";
        Assert.Throws<InvalidDataException>(() => TextageMasterParserTests.Parse(data));
        data = TextageMasterParserTests.Fixture();
        data["cstbl.js"] = "cstbl=new Array; cstbl[1]={};";
        data["cstbl1.js"] = "cstbl[3]={};";
        data["cstbl2.js"] = "cstbl[9]={};";
        Assert.Throws<InvalidDataException>(() => TextageMasterParserTests.Parse(data));
    }

    [Fact]
    public void RejectsOversizedInputAndCancellation()
    {
        var data = TextageMasterParserTests.Fixture();
        data["datatbl.js"] += new string(' ', 8 * 1024 * 1024);
        Assert.Throws<InvalidDataException>(() => TextageMasterParserTests.Parse(data));
        Assert.Throws<OperationCanceledException>(() => new TextageMasterParser().Parse(TextageMasterParserTests.Sources(data), new CancellationToken(true)));
    }

    [Theory]
    [InlineData("vertbl=['CS','1st']; vertbl[35]='substream'")]
    [InlineData("vertbl=['CS','1st']; vertbl[35]='substream'; vertbl[35]='again';")]
    [InlineData("vertbl=['CS','1st']; vertbl[35]='substream'; referstr=''; vertbl=[];")]
    public void RejectsCutoffDuplicateAssignmentsAndDisplayWrites(string source)
    {
        var data = TextageMasterParserTests.Fixture();
        data["scrlist.js"] = source;
        Assert.Throws<InvalidDataException>(() => TextageMasterParserTests.Parse(data));
    }
}

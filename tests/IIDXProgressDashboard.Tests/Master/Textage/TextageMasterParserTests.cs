using System.Text;
using IIDXProgressDashboard.Master;
using Xunit;

namespace IIDXProgressDashboard.Tests;

public sealed class TextageMasterParserTests
{
    internal static Dictionary<string, string> Fixture()
    {
        // 10種すべてが存在する合成譜面。SBoは別のノーツ数を持つ。
        return new()
        {
            ["titletbl.js"] = "SS=35; titletbl={'song':[SS,1,0,'GENRE','作者','曲名','<br>副題'], '__dmy__':[99,4095,0,'','','　']};",
            ["scrlist.js"] = "vertbl=['CS','1st']; vertbl[35]='substream';",
            ["actbl.js"] = "A=10,B=11,C=12,D=13,E=14,F=15; actbl={'song':[3,1,1,1,7,2,7,3,7,4,7,5,7,6,7,7,7,8,7,9,7,A,7]};",
            ["datatbl.js"] = "datatbl={'song':[999,100,200,300,400,500,600,700,800,900,1000,'120']};"
        };
    }

    internal static TextageSourceSnapshot Sources(Dictionary<string, string> data)
        => new(data.ToDictionary(x => x.Key, x => new TextageSourceFile(x.Key, x.Value, Encoding.UTF8.GetByteCount(x.Value), "synthetic")));
    internal static MasterSnapshot Parse(Dictionary<string, string> data) => new TextageMasterParser().Parse(Sources(data));

    [Fact]
    public void BuildsSongsAndAllTenChartKindsWithoutMergingOldBeginner()
    {
        var result = Parse(Fixture());
        Assert.Equal(new MasterSong("song", "曲名 副題", "作者", "GENRE", "substream", 35), Assert.Single(result.Songs));
        var identities = new[] { "SP/B", "SP/N", "SP/H", "SP/A", "SP/L", "DP/B", "DP/N", "DP/H", "DP/A", "DP/L" };
        Assert.Equal(identities, result.Charts.Select(c => c.PlayStyle + "/" + c.Difficulty));
        Assert.Equal(Enumerable.Range(1, 10).Select(n => (int?)n), result.Charts.Select(c => c.Level));
        Assert.Equal(Enumerable.Range(1, 10).Select(n => (int?)(n * 100)), result.Charts.Select(c => c.TotalNotes));
        Assert.Contains(result.Diagnostics, d => d.Code == "SBO_EXCLUDED");
        Assert.Contains(result.Diagnostics, d => d.Code == "DUMMY_EXCLUDED");
        Assert.False(result.CanDeactivateMissing);
    }

    [Fact]
    public void PreservesUnknownValuesAndDoesNotCreateAbsentSlots()
    {
        var data = Fixture();
        data["actbl.js"] = "actbl={'song':[1,1,1,0,0,4,1,3,7,0,0,0,0,0,0,0,0,0,0,0,0,0,0]};";
        data["datatbl.js"] = "datatbl={'song':[999,0,200,0,400,0,0,0,0,0,0,'120']};";
        var result = Parse(data);
        Assert.Equal(new[] { "N", "H", "A" }, result.Charts.Select(c => c.Difficulty));
        Assert.Null(result.Charts[0].Level); // 旧尺度を12段階として採用しない。
        Assert.Null(result.Charts[1].TotalNotes);
        Assert.Null(result.Charts[2].Level); // notesだけ存在。
        Assert.DoesNotContain(result.Charts, c => c.Difficulty == "B");
    }

    [Fact]
    public void HandlesCommentsEscapesHtmlAndDecorationsWithoutChangingIdentity()
    {
        var data = Fixture();
        data["titletbl.js"] = "/* ' ] } */ titletbl={ // comment\n 'song':[1,1,0,'G','A','a]///*\\\"\\\\\\u65e5\\x41 &amp;'.fontcolor('#fff'),'<b>sub</b>',],};";
        Assert.Equal("a]///*\"\\日A & sub", Assert.Single(Parse(data).Songs).Title);
    }

    [Theory]
    [InlineData("titletbl.js", "titletbl={'song':[1,1,0,'G','A','T']" )]
    [InlineData("titletbl.js", "titletbl={'song':[1,1,0,'G','A','T" )]
    [InlineData("titletbl.js", "titletbl={'song':[1,1,0,'G','A','T']}; /*" )]
    [InlineData("titletbl.js", "titletbl={'song':[1,1,0,'G','A',unknown()]};" )]
    [InlineData("titletbl.js", "titletbl={'song':[1,1,0,'G','A','T'.bold()]};" )]
    [InlineData("titletbl.js", "titletbl={'song':[1,1,0,'G','A','T'],'song':[1,1,0,'G','A','U']};" )]
    [InlineData("titletbl.js", "titletbl={'song':[99,1,0,'G','A','T']};" )]
    [InlineData("titletbl.js", "titletbl={'song':[1,1,0,'G','A','T']}; run();" )]
    [InlineData("titletbl.js", "totally random UTF8 text" )]
    [InlineData("titletbl.js", "while(true){}" )]
    [InlineData("titletbl.js", "titletbl={};" )]
    [InlineData("titletbl.js", "TITLEINDEX=4; titletbl={'song':[1,1,0,'G','A','T']};" )]
    [InlineData("actbl.js", "A=9; actbl={};" )]
    [InlineData("actbl.js", "A=10," )]
    [InlineData("actbl.js", "actbl={'song':[1,0,0]};" )]
    [InlineData("actbl.js", "actbl={'song':[1,0,0,0,1,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0]};" )]
    [InlineData("actbl.js", "actbl={'song':[1,0,0,13,7,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0]};" )]
    [InlineData("datatbl.js", "datatbl={};" )]
    [InlineData("datatbl.js", "datatbl={'song':[0,1]};" )]
    [InlineData("datatbl.js", "datatbl={'song':[0,-1,0,0,0,0,0,0,0,0,0,'120']};" )]
    [InlineData("scrlist.js", "vertbl=['CS','1st']; vertbl[1]='overwrite';" )]
    public void RejectsInvalidInputAsAWhole(string file, string content)
    {
        var data = Fixture();
        data[file] = content;
        // flagsだけのslotを実データで確認した条件に合わせる。
        if (file == "actbl.js" && content.Contains("0,1,0,0")) data["datatbl.js"] = "datatbl={'song':[0,0,0,0,0,0,0,0,0,0,0,'120']};";
        Assert.Throws<InvalidDataException>(() => Parse(data));
    }

    [Fact]
    public void RejectsSwappedOrMissingFiles()
    {
        var data = Fixture();
        (data["actbl.js"], data["datatbl.js"]) = (data["datatbl.js"], data["actbl.js"]);
        Assert.Throws<InvalidDataException>(() => Parse(data));
        data = Fixture(); data.Remove("titletbl.js");
        Assert.Throws<InvalidDataException>(() => Parse(data));
    }

    [Fact]
    public void RejectsDuplicateCsVersionsAcrossFilesAndIncompleteComparisonSet()
    {
        var data = Fixture();
        data["cstbl.js"] = "cstbl[1]={};";
        data["cstbl1.js"] = "cstbl[1]={};";
        data["cstbl2.js"] = "cstbl[9]={};";
        Assert.Throws<InvalidDataException>(() => Parse(data));
        data.Remove("cstbl1.js");
        Assert.Throws<InvalidDataException>(() => Parse(data));
    }

    [Fact]
    public void CsComparisonNeverOverwritesAcOrAddsCsOnlyCharts()
    {
        var data = Fixture();
        data["cstbl.js"] = "cstbl=new Array(); cstbl[1]={'song':[1,0,0,12,7,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0]};";
        data["cstbl1.js"] = "cstbl[3]={'cs_only':[1,0,0,12,7,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0]};";
        data["cstbl2.js"] = "cstbl[9]={};";
        var result = Parse(data);
        Assert.Equal(10, result.Charts.Count);
        Assert.Equal(1, result.Charts[0].Level);
        Assert.Contains(result.Diagnostics, d => d.Code == "CS_ONLY");
        Assert.Contains(result.Diagnostics, d => d.Code == "CS_DIFFERENCE");
        data["cstbl2.js"] = "cstbl[9]={'broken':[]};";
        Assert.Throws<InvalidDataException>(() => Parse(data));
    }

    [Theory]
    [InlineData("BEGINNER", "B")]
    [InlineData("NORMAL", "N")]
    [InlineData("HYPER", "H")]
    [InlineData("ANOTHER", "A")]
    [InlineData("LEGGENDARIA", "L")]
    public void ConvertsLegacyDifficultyExplicitly(string input, string expected)
        => Assert.Equal(expected, MasterDifficulty.FromLegacyName(input));

    [Fact]
    public void RejectsUnknownDifficultyAndCancellation()
    {
        Assert.Throws<InvalidDataException>(() => MasterDifficulty.FromLegacyName("UNKNOWN"));
        Assert.Throws<OperationCanceledException>(() => new TextageMasterParser().Parse(Sources(Fixture()), new CancellationToken(true)));
    }
}

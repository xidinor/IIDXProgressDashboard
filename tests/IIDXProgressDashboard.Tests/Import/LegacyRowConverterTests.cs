using System.Text.Json;
using IIDXProgressDashboard.Import;
using Xunit;

namespace IIDXProgressDashboard.Tests;

public sealed class LegacyRowConverterTests
{
    private static Dictionary<string, object?> Row() => new()
    {
        ["id"] = 1L, ["level"] = "11", ["song_name"] = "合成曲", ["difficulty_type"] = "SPA",
        ["total_notes"] = 1000L, ["score"] = 1234L, ["miss_count"] = null,
        ["clear_type"] = "NO PLAY", ["played_option"] = "OFF", ["played_at"] = "2026-01-01-00-01",
        ["original_data"] = "['日本語', \"引用\", None]\n次の行\\"
    };

    [Theory]
    [InlineData("NO PLAY", 0)]
    [InlineData("FAILED", 1)]
    [InlineData("A-CLEAR", 2)]
    [InlineData("E-CLEAR", 3)]
    [InlineData("CLEAR", 4)]
    [InlineData("H-CLEAR", 5)]
    [InlineData("EXH-CLEAR", 6)]
    [InlineData("F-COMBO", 7)]
    public void ConvertsExplicitLampsAndJstDateBoundary(string lamp, int expected)
    {
        var row = Row();
        row["clear_type"] = lamp;
        var result = LegacyRowConverter.Convert(row);
        Assert.Empty(result.Issues);
        Assert.Equal(expected, result.Lamp);
        Assert.Equal("2025-12-31T15:01:00Z", result.PlayedAt);
        Assert.Equal(1234, result.Score);
        Assert.Null(result.MissCount);
        row["miss_count"] = 0L;
        Assert.Equal(0, LegacyRowConverter.Convert(row).MissCount);
    }

    [Theory]
    [InlineData("level", "0")]
    [InlineData("level", "13")]
    [InlineData("level", "11.0")]
    [InlineData("score", "100")]
    [InlineData("score", -1L)]
    [InlineData("total_notes", -1L)]
    [InlineData("miss_count", -1L)]
    [InlineData("clear_type", "UNKNOWN")]
    [InlineData("played_at", "2026-02-30-00-00")]
    [InlineData("played_at", "0001-01-01-00-00")]
    [InlineData("song_name", "")]
    public void InvalidValuesAreNotSilentlyCorrected(string column, object value)
    {
        var row = Row();
        row[column] = value;
        Assert.NotEmpty(LegacyRowConverter.Convert(row).Issues);
    }

    [Fact]
    public void MissingOptionalValuesAndRawStringsRemainIntact()
    {
        var row = Row();
        row["level"] = null;
        row["total_notes"] = null;
        row["played_option"] = "";
        Assert.Empty(LegacyRowConverter.Convert(row).Issues);
        using var json = JsonDocument.Parse(LegacyRowConverter.Serialize(row));
        var raw = json.RootElement.GetProperty("row");
        Assert.Equal(row["original_data"], raw.GetProperty("original_data").GetString());
        Assert.Equal(JsonValueKind.Null, raw.GetProperty("level").ValueKind);
        Assert.Equal("", raw.GetProperty("played_option").GetString());
        Assert.Equal("minute", json.RootElement.GetProperty("playedAtPrecision").GetString());
        Assert.Equal(row["played_at"], raw.GetProperty("played_at").GetString());
    }
}

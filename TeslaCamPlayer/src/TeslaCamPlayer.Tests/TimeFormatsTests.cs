using TeslaCamPlayer.BlazorHosted.Shared.Models;
using Xunit;

namespace TeslaCamPlayer.Tests;

public class TimeFormatsTests
{
    [Theory]
    [InlineData("dd MMM yy", "dd MMM yy")]
    [InlineData("yyyy-MM-dd", "yyyy-MM-dd")]
    [InlineData("MM/dd/yyyy", "MM/dd/yyyy")]
    [InlineData("dd/MM/yyyy", "dd/MM/yyyy")]
    [InlineData("garbage", "dd MMM yy")]
    [InlineData(null, "dd MMM yy")]
    public void DatePattern_WhitelistsWithDefault(string input, string expected)
        => Assert.Equal(expected, TimeFormats.DatePattern(input));

    [Theory]
    [InlineData("24h", "HH:mm:ss")]
    [InlineData("12h", "h:mm:ss tt")]
    [InlineData("garbage", "h:mm:ss tt")]
    [InlineData(null, "h:mm:ss tt")]
    public void TimePattern_MapsWithDefault(string input, string expected)
        => Assert.Equal(expected, TimeFormats.TimePattern(input));

    [Theory]
    [InlineData("24h", "HH:mm")]
    [InlineData("12h", "h:mm tt")]
    public void ShortTimePattern_Maps(string input, string expected)
        => Assert.Equal(expected, TimeFormats.ShortTimePattern(input));

    [Theory]
    [InlineData("dd MMM yy", "%d %b %y")]
    [InlineData("yyyy-MM-dd", "%Y-%m-%d")]
    [InlineData("MM/dd/yyyy", "%m/%d/%Y")]
    [InlineData("dd/MM/yyyy", "%d/%m/%Y")]
    [InlineData("garbage", "%d %b %y")]
    public void StrftimeDate_Maps(string input, string expected)
        => Assert.Equal(expected, TimeFormats.StrftimeDate(input));

    [Theory]
    [InlineData("24h", "%H:%M:%S")]
    [InlineData("12h", "%I:%M:%S %p")]
    [InlineData(null, "%I:%M:%S %p")]
    public void StrftimeTime_Maps(string input, string expected)
        => Assert.Equal(expected, TimeFormats.StrftimeTime(input));
}

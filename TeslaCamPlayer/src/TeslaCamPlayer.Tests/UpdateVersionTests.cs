using TeslaCamPlayer.BlazorHosted.Shared.Models;
using Xunit;

namespace TeslaCamPlayer.Tests;

public class UpdateVersionTests
{
    [Theory]
    [InlineData("0.5.0", "0.6.0", true)]
    [InlineData("v0.5.0", "v0.6.0", true)]
    [InlineData("0.5.0", "0.5.1", true)]
    [InlineData("0.5.0", "1.0.0", true)]
    [InlineData("0.5.0", "0.5.0", false)]
    [InlineData("0.6.0", "0.5.0", false)]
    [InlineData("0.5.0", "garbage", false)]
    [InlineData("garbage", "0.6.0", false)]
    [InlineData("0.5.0", null, false)]
    [InlineData(null, "0.6.0", false)]
    [InlineData("0.5.0", "", false)]
    public void IsNewer(string current, string latest, bool expected)
        => Assert.Equal(expected, UpdateVersion.IsNewer(current, latest));
}

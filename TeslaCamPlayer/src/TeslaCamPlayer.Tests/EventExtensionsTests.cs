using TeslaCamPlayer.BlazorHosted.Shared.Models;
using Xunit;

namespace TeslaCamPlayer.Tests;

public class EventExtensionsTests
{
    [Theory]
    [InlineData(Cameras.Front, Cameras.Front)]
    [InlineData(Cameras.Fisheye, Cameras.Front)]
    [InlineData(Cameras.Narrow, Cameras.Front)]
    [InlineData(Cameras.Back, Cameras.Back)]
    [InlineData(Cameras.LeftRepeater, Cameras.LeftRepeater)]
    [InlineData(Cameras.RightRepeater, Cameras.RightRepeater)]
    [InlineData(Cameras.LeftBPillar, Cameras.LeftBPillar)]
    [InlineData(Cameras.RightBPillar, Cameras.RightBPillar)]
    public void TriggerTileCamera_MapsToTileCamera(Cameras camera, Cameras expected)
    {
        var e = new Event { Camera = camera };
        Assert.Equal(expected, e.TriggerTileCamera());
    }

    [Theory]
    [InlineData(Cameras.Cabin)]
    [InlineData(Cameras.Unknown)]
    public void TriggerTileCamera_UnmappableCameras_ReturnNull(Cameras camera)
    {
        var e = new Event { Camera = camera };
        Assert.Null(e.TriggerTileCamera());
    }

    [Fact]
    public void TriggerTileCamera_NullEvent_ReturnsNull()
    {
        Event e = null;
        Assert.Null(e.TriggerTileCamera());
    }
}

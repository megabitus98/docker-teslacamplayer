using TeslaCamPlayer.BlazorHosted.Client.Models;
using Xunit;

namespace TeslaCamPlayer.Tests;

/// <summary>
/// ClipViewer.OnParametersSetAsync dirty-checks the camera filter with `_lastApplied == CameraFilter`.
/// CameraFilter.razor mutates that instance in place, so the snapshot must be a copy, not an alias --
/// an alias would compare an object to itself, always report "unchanged", and silently kill the filter.
/// </summary>
public class CameraFilterValuesTests
{
    [Fact]
    public void WithSnapshotDoesNotAliasTheMutatedFilter()
    {
        var filter = new CameraFilterValues();
        var snapshot = filter with { };

        Assert.Equal(snapshot, filter);

        filter.ShowFront = false;

        Assert.NotEqual(snapshot, filter);
        Assert.True(snapshot.ShowFront);
    }
}

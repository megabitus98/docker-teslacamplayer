using TeslaCamPlayer.BlazorHosted.Shared.Models;

namespace TeslaCamPlayer.BlazorHosted.Client.Models
{
    // record, not class: ClipViewer dirty-checks the filter by value against a `with { }` snapshot.
    // Properties stay settable because CameraFilter.razor mutates this instance in place.
    public record CameraFilterValues
    {
        public bool ShowFront { get; set; } = true;
        public bool ShowBack { get; set; } = true;

        // Select which left/right camera sources are eligible to show
        public bool ShowLeftRepeater { get; set; } = true;
        public bool ShowLeftPillar { get; set; } = true;
        public bool ShowRightRepeater { get; set; } = true;
        public bool ShowRightPillar { get; set; } = true;
    }
}

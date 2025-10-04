using TeslaCamPlayer.BlazorHosted.Shared.Models;

namespace TeslaCamPlayer.BlazorHosted.Client.Models
{
    public class CameraFilterValues
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

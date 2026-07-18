using TeslaCamPlayer.BlazorHosted.Shared.Models;

namespace TeslaCamPlayer.BlazorHosted.Client.Models
{
    public class EventFilterValues
    {
        public bool DashcamHonk { get; set; } = true;

        public bool DashcamSaved { get; set; } = true;

        public bool DashcamOther { get; set; } = true;

        public bool SentryObjectDetection { get; set; } = true;

        public bool SentryAccelerationDetection { get; set; } = true;

        public bool SentryOther { get; set; } = true;

        public bool Recent { get; set; } = true;
    }
}

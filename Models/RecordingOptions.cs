namespace Zink.Models
{
    public sealed class RecordingOptions
    {
        public bool IncludeSystemAudio { get; set; } = true;
        public bool IncludeMicrophone { get; set; } = false;
        public string? SelectedRenderDeviceId { get; set; }
        public string? SelectedMicDeviceId { get; set; }
        public int RetrospectiveSeconds { get; set; } = 45;
        public int OutputWidth { get; set; } = 0;
        public int OutputHeight { get; set; } = 0;
        public uint FrameRate { get; set; } = 60;
        public uint VideoBitrate { get; set; } = 0;
        public bool AllowHighFrameRateCapture { get; set; } = false;

        public RecordingOptions Clone()
        {
            return new RecordingOptions
            {
                IncludeSystemAudio = IncludeSystemAudio,
                IncludeMicrophone = IncludeMicrophone,
                SelectedRenderDeviceId = SelectedRenderDeviceId,
                SelectedMicDeviceId = SelectedMicDeviceId,
                RetrospectiveSeconds = RetrospectiveSeconds,
                OutputWidth = OutputWidth,
                OutputHeight = OutputHeight,
                FrameRate = FrameRate,
                VideoBitrate = VideoBitrate,
                AllowHighFrameRateCapture = AllowHighFrameRateCapture
            };
        }
    }
}

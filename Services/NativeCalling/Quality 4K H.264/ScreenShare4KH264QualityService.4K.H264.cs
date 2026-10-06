namespace Zink.Services.NativeCalling
{
    /// <summary>
    /// Owns the native-calling H.264 profile for 3840x2160 screen sharing.
    /// </summary>
    internal static class ScreenShare4KH264QualityService
    {
        internal static ScreenShareQualityProfile CreateProfile()
        {
            return new ScreenShareQualityProfile(
                ScreenShareQualityPreset.UltraHd4K,
                "4K",
                3840,
                2160,
                targetFps: 60,
                bitrate: 36_000_000,
                minimumBitrate: 24_000_000,
                previewFrameInterval: ScreenShareCapturePipeline.TargetFps * 10,
                previewMaxWidth: 1280,
                previewJpegQuality: 68L);
        }
    }
}

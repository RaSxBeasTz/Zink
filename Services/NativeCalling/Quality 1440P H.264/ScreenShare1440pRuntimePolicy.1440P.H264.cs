using System;

namespace Zink.Services.NativeCalling
{
    /// <summary>
    /// Isolated operational contract for 1440p calling. Changes in this file
    /// apply only when the Quad HD 1440p stack is selected.
    /// </summary>
    internal static class ScreenShare1440pRuntimePolicy
    {
        public static ScreenShareRuntimePolicy Create()
        {
            var profile = ScreenShareQualityProfile.FromPreset(ScreenShareQualityPreset.QuadHd2K);
            return new ScreenShareRuntimePolicy(
                Audio: new ScreenShareAudioPlaybackSettings(10, 2, 40, 60, 160),
                WebRtc: new ScreenSharePeerConnectionSettings(
                    102, 103, 90_000, profile.TargetFps,
                    "packetization-mode=1;profile-level-id=42e034;level-asymmetry-allowed=1"),
                Signaling: new ScreenShareSignalingSettings(
                    1, 2, 24 * 1024, TimeSpan.FromSeconds(1),
                    TimeSpan.FromMilliseconds(1600), TimeSpan.FromSeconds(2),
                    TimeSpan.FromSeconds(2), TimeSpan.FromMilliseconds(120)),
                Controls: new ScreenShareControlSettings(
                    12, 4, 2, 30, false, 3, 4, 30, 2),
                Errors: new ScreenShareErrorHandlingSettings(
                    TimeSpan.FromMilliseconds(350), TimeSpan.FromSeconds(2),
                    TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(2),
                    TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1)));
        }
    }
}

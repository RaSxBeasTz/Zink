using System;

namespace Zink.Services.NativeCalling
{
    /// <summary>
    /// Frozen operational contract for the known-good 720p calling path.
    /// Keep 1080p/1440p/4K tuning out of this file.
    /// </summary>
    internal static class ScreenShare720pRuntimePolicy
    {
        public static ScreenShareRuntimePolicy Create() => new(
            Audio: new ScreenShareAudioPlaybackSettings(
                // The receiver log shows 8-16 packet scheduler bursts. The old
                // 10-packet ceiling repeatedly discarded valid 20 ms Opus audio,
                // which was heard as stutter. Keep a modest 120 ms PCM cushion
                // and enough compressed reserve to survive a half-second burst.
                MaxBufferedMediaPackets: 25,
                StartPlaybackPacketThreshold: 4,
                DesiredLatencyMs: 60,
                TargetBufferedMs: 120,
                HardTrimBufferedMs: 320),
            WebRtc: new ScreenSharePeerConnectionSettings(
                H264PayloadType: 102,
                Av1PayloadType: 103,
                VideoClockRate: 90_000,
                RealtimeVideoFps: 30,
                H264FormatParameters: "packetization-mode=1;profile-level-id=42e034;level-asymmetry-allowed=1"),
            Signaling: new ScreenShareSignalingSettings(
                FallbackMaxFps: 1,
                // While WebRTC is connecting, every awaited fallback send holds
                // the single-slot realtime sender.  The playback log showed that
                // this discarded 89 H.264 access units (including references).
                // Keep the reliable fallback independently decodable: send IDRs,
                // but never queue dependent delta frames behind a missing peer.
                PreviewFallbackMaxFps: 1,
                WebSocketFallbackMaxDeltaBytes: 0,
                WebSocketFallbackWarmup: TimeSpan.FromSeconds(1),
                WebSocketFallbackGpuStartDelay: TimeSpan.FromMilliseconds(1600),
                RtpMetadataInterval: TimeSpan.FromSeconds(2),
                QosFeedbackInterval: TimeSpan.FromSeconds(2),
                // Bound a slow signaling fallback to less than two 30-fps frame
                // periods so it cannot turn a network pause into a long frozen
                // dependency chain. RTP sends complete synchronously.
                SendTimeout: TimeSpan.FromMilliseconds(60)),
            Controls: new ScreenShareControlSettings(
                MaxAudioSendQueue: 12,
                // A transient queue depth of three coincided with playback
                // falling below 20 fps. Preserve a few more dependent H.264
                // frames instead of dropping them at the first short burst.
                MaxDecodeQueue: 6,
                DecodeRenderBacklogThreshold: 4,
                MaxHardDecodeQueue: 30,
                UseGpuPlayback: false,
                GpuBacklogKeyFrameRequestQueue: 3,
                MaxGpuPlaybackQueue: 4,
                CompatibilityFps: 30,
                RealtimeBacklogRetainFrames: 4),
            Errors: new ScreenShareErrorHandlingSettings(
                GpuSustainedBacklogThreshold: TimeSpan.FromMilliseconds(350),
                GpuSampleWaitTimeout: TimeSpan.FromSeconds(2),
                ReceiveStallThreshold: TimeSpan.FromSeconds(3),
                RenderStallThreshold: TimeSpan.FromSeconds(2),
                DecoderResetCooldown: TimeSpan.FromSeconds(1),
                FallbackRtpSuppressWindow: TimeSpan.FromSeconds(1)));
    }
}

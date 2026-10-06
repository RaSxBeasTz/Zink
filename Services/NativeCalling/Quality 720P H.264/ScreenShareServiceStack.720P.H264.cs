using System;

namespace Zink.Services.NativeCalling
{
    internal interface IScreenShareServiceStack
    {
        ScreenShareQualityPreset Preset { get; }
        ScreenShareCapturePipeline CapturePipeline { get; }
        AudioPlaybackService AudioPlayback { get; }
        ScreenShareRuntimePolicy RuntimePolicy { get; }
        NativePeerConnectionService CreatePeer(string? diagnosticName = null);
    }

    public sealed record ScreenShareAudioPlaybackSettings(
        int MaxBufferedMediaPackets,
        int StartPlaybackPacketThreshold,
        int DesiredLatencyMs,
        int TargetBufferedMs,
        int HardTrimBufferedMs);

    public sealed record ScreenSharePeerConnectionSettings(
        int H264PayloadType,
        int Av1PayloadType,
        uint VideoClockRate,
        int RealtimeVideoFps,
        string H264FormatParameters);

    public sealed record ScreenShareSignalingSettings(
        int FallbackMaxFps,
        int PreviewFallbackMaxFps,
        int WebSocketFallbackMaxDeltaBytes,
        TimeSpan WebSocketFallbackWarmup,
        TimeSpan WebSocketFallbackGpuStartDelay,
        TimeSpan RtpMetadataInterval,
        TimeSpan QosFeedbackInterval,
        TimeSpan SendTimeout);

    public sealed record ScreenShareControlSettings(
        int MaxAudioSendQueue,
        int MaxDecodeQueue,
        int DecodeRenderBacklogThreshold,
        int MaxHardDecodeQueue,
        bool UseGpuPlayback,
        int GpuBacklogKeyFrameRequestQueue,
        int MaxGpuPlaybackQueue,
        int CompatibilityFps,
        int RealtimeBacklogRetainFrames);

    public sealed record ScreenShareErrorHandlingSettings(
        TimeSpan GpuSustainedBacklogThreshold,
        TimeSpan GpuSampleWaitTimeout,
        TimeSpan ReceiveStallThreshold,
        TimeSpan RenderStallThreshold,
        TimeSpan DecoderResetCooldown,
        TimeSpan FallbackRtpSuppressWindow);

    public sealed record ScreenShareRuntimePolicy(
        ScreenShareAudioPlaybackSettings Audio,
        ScreenSharePeerConnectionSettings WebRtc,
        ScreenShareSignalingSettings Signaling,
        ScreenShareControlSettings Controls,
        ScreenShareErrorHandlingSettings Errors);

    internal sealed class ScreenShareServiceStack : IScreenShareServiceStack
    {
        internal ScreenShareServiceStack(ScreenShareQualityPreset preset, ScreenShareRuntimePolicy runtimePolicy)
        {
            Preset = preset;
            RuntimePolicy = runtimePolicy;
            CapturePipeline = new ScreenShareCapturePipeline(preset);
            AudioPlayback = new AudioPlaybackService(runtimePolicy.Audio);
        }

        public ScreenShareQualityPreset Preset { get; }
        public ScreenShareCapturePipeline CapturePipeline { get; }
        public AudioPlaybackService AudioPlayback { get; }
        public ScreenShareRuntimePolicy RuntimePolicy { get; }
        public NativePeerConnectionService CreatePeer(string? diagnosticName = null) =>
            new(diagnosticName, Preset, RuntimePolicy.WebRtc);
    }

    internal static class ScreenShareServiceSelector
    {
        private static readonly object Sync = new();
        private static readonly IScreenShareServiceStack Stack720p =
            new ScreenShareServiceStack(ScreenShareQualityPreset.Hd720p, ScreenShare720pRuntimePolicy.Create());
        private static readonly IScreenShareServiceStack Stack1080p =
            new ScreenShareServiceStack(ScreenShareQualityPreset.FullHd1080p, ScreenShare1080pRuntimePolicy.Create());
        private static readonly IScreenShareServiceStack Stack1440p =
            new ScreenShareServiceStack(ScreenShareQualityPreset.QuadHd2K, ScreenShare1440pRuntimePolicy.Create());
        private static readonly IScreenShareServiceStack Stack4K =
            new ScreenShareServiceStack(ScreenShareQualityPreset.UltraHd4K, ScreenShare4KRuntimePolicy.Create());
        private static IScreenShareServiceStack _active = Stack720p;

        public static IScreenShareServiceStack Active
        {
            get
            {
                lock (Sync)
                    return _active;
            }
        }

        public static IScreenShareServiceStack Select(ScreenShareQualityPreset preset)
        {
            IScreenShareServiceStack selected = preset switch
            {
                ScreenShareQualityPreset.FullHd1080p => Stack1080p,
                ScreenShareQualityPreset.QuadHd2K => Stack1440p,
                ScreenShareQualityPreset.UltraHd4K => Stack4K,
                _ => Stack720p
            };

            lock (Sync)
                _active = selected;

            return selected;
        }
    }
}

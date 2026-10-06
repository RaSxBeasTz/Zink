using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Windows.Graphics.Capture;
using Zink.Models;

namespace Zink.Services.Recording
{
    internal interface IZinkReplayBackend : IAsyncDisposable
    {
        bool IsRunning { get; }
        TimeSpan BufferedDuration { get; }
        ZinkReplayBackendCapabilities Capabilities { get; }

        Task StartAsync(
            GraphicsCaptureItem captureItem,
            RecordingOptions options,
            TimeSpan bufferDuration,
            CancellationToken cancellationToken = default);

        void AddSystemAudio(AudioPacket packet);
        void AddMicrophoneAudio(AudioPacket packet);

        Task<ZinkReplayExportResult> SaveAsync(
            ZinkReplayExportRequest request,
            CancellationToken cancellationToken = default);

        Task StopAsync(CancellationToken cancellationToken = default);
    }

    internal sealed record ZinkReplayBackendCapabilities(
        bool SupportsSdr,
        bool SupportsHdr10,
        bool SupportsH264,
        bool SupportsHevcMain10,
        bool SupportsNvidia,
        bool SupportsIntel,
        bool SupportsSeparateAudioTracks,
        int MaximumFrameRate,
        TimeSpan MaximumBufferDuration);

    internal sealed record ZinkReplayExportRequest(
        string OutputPath,
        TimeSpan Duration,
        bool IncludeSystemAudio,
        bool IncludeMicrophone,
        bool IncludeCombinedTrack,
        bool IncludeSeparateSystemTrack,
        bool IncludeSeparateMicrophoneTrack,
        int AudioBitrateKbps);

    internal sealed record ZinkReplayExportResult(
        string OutputPath,
        TimeSpan Duration,
        int Width,
        int Height,
        double FrameRate,
        bool IsHdr10,
        string VideoCodec,
        string HardwareEncoder,
        IReadOnlyList<string> AudioTrackNames);
}

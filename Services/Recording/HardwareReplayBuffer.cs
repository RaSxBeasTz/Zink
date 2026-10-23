using SharpDX.Direct3D11;
using SharpDX.DXGI;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Zink.Models;
using Zink.Services.NativeCalling;

namespace Zink.Services.Recording
{
    internal sealed class HardwareReplayBuffer : IDisposable
    {
        // Windows' linear-scRGB desktop contract defines 1.0 as D65 white at
        // 80 nits. Keep this absolute scale so native HDR highlights are not
        // lifted or clipped during conversion to PQ.

        private readonly object _gate = new();
        private readonly TimeSpan _duration;
        private readonly RecordingOptions _options;
        // Encoded replay frames are consumed from the front as the rolling window
        // advances. A List made every capture callback scan thousands of frames and
        // periodically shift the entire backing array with RemoveRange. Queue keeps
        // expiry incremental and avoids those CPU/GC spikes while preserving every
        // encoded frame at its original quality.
        private readonly Queue<ReplayFrameStorage.Entry> _frames = new();
        private readonly ReplayFrameStorage _frameStorage = new();
        private readonly Queue<long> _keyFrameTimestamps = new();
        private ReplayFrameStorage.Snapshot? _exportFrames;
        private IH264VideoEncoder? _encoder;
        private SharpDX.Direct3D11.Device? _dedicatedEncoderDevice;
        private int _width;
        private int _height;
        private int _fps;
        private double _exportFps;
        private int _exportBitrate;
        private bool _hdrDetected;
        private bool _loggedHdrCompatibilityFallback;
        private bool _exportHdr10;
        private TimeSpan _exportStartTimestamp;
        private TimeSpan _exportDuration;
        private long _originMilliseconds = -1;
        private long _latestTimestampMilliseconds;
        private long _lastKeyFrameRequestMilliseconds = -1;
        private Process? _directNvencProcess;
        private string? _directNvencFolder;
        private DateTime _directNvencStartedUtc;
        private long? _directNvencCaptureClockOriginMilliseconds;
        private int _directAddCount;
        private bool _useDirectNvencCapture;
        private bool _directNvencHdr;
        private int _directNvencFps = 120;
        private TimeSpan _directExportDuration;
        private CancellationTokenSource? _directTrimCancellation;
        private readonly HashSet<string> _directExportSegments =
            new(StringComparer.OrdinalIgnoreCase);
        private DateTime _encoderRetryAfterUtc;
        private string? _lastEncoderInitializationError;
        private long _receivedGpuPacketCount;
        private long _nextCfrFrameIndex;
        private bool _usedHdrEncoderCompatibilityRecovery;
        private long _liveEncodedBytes;

        public HardwareReplayBuffer(TimeSpan duration, RecordingOptions options)
        {
            _duration = duration;
            _options = options.Clone();

            // The in-process Media Foundation path is lightweight for ordinary SDR
            // replay capture. At 120 fps, however, encoding is currently synchronous
            // with DXGI duplication; a 3840x2400 desktop can consequently fall to
            // ~18 fps. The same path receives an 8-bit BGRA duplication surface and
            // cannot preserve an HDR10 desktop's scRGB/PQ highlight information.
            // Keep the proven direct GPU path for high-frame-rate/HDR-capable replay
            // until the native path has an asynchronous texture queue and P010 HDR
            // conversion. UsesDirectCapture prevents a second capture session.
            DirectCaptureOutput detectedOutput = GetPrimaryDirectCaptureOutput();
            bool primaryOutputIsHdr = detectedOutput.IsHdr;
            _hdrDetected = primaryOutputIsHdr;

            // Zink recording is entirely in-process: D3D11 capture and HDR
            // conversion, hardware HEVC encoding, and Media Foundation muxing.
            // External capture/encoding executables are intentionally disabled.
            _useDirectNvencCapture = false;

            const string selectedBackend = "Zink in-process Media Foundation";

            _ = RecordingDiagnostics.WriteHdrAsync(
                "ReplaySessionCreated",
                new
                {
                    DetectedHdr = primaryOutputIsHdr,
                    RequestedFrameRate = _options.FrameRate,
                    RequestedWidth = _options.OutputWidth,
                    RequestedHeight = _options.OutputHeight,
                    CaptureAdapterIndex = detectedOutput.CaptureAdapterIndex,
                    OutputIndex = detectedOutput.OutputIndex,
                    EncoderAdapterIndex = detectedOutput.EncoderAdapterIndex,
                    EncoderVendorId = $"0x{detectedOutput.EncoderVendorId:X4}",
                    SourceWidth = detectedOutput.SourceWidth,
                    SourceHeight = detectedOutput.SourceHeight,
                    Backend = selectedBackend,
                    ExternalCaptureProcessAllowed = false
                });
            _ = RecordingDiagnostics.WriteRecordingAsync(
                "ReplaySessionCreated",
                new
                {
                    BufferSeconds = _duration.TotalSeconds,
                    RequestedFrameRate = _options.FrameRate,
                    SystemAudio = _options.IncludeSystemAudio,
                    Microphone = _options.IncludeMicrophone,
                    Backend = selectedBackend,
                    ExternalCaptureProcessAllowed = false
                });
            _ = GameReplayLog.WriteAsync(nameof(HardwareReplayBuffer),
                $"Replay session created. Buffer={_duration}, RequestedFps={_options.FrameRate}, HDRDetected={_hdrDetected}");
        }

        public void Add(VideoFramePacket packet)
        {
            try
            {
                if (packet.Bgra32Bytes is null || packet.Width <= 0 || packet.Height <= 0)
                    return;

                lock (_gate)
                {
                    if (!TryEnsureEncoderNoLock(
                            packet.Width,
                            packet.Height,
                            sourceFormat: SharpDX.DXGI.Format.B8G8R8A8_UNorm))
                        return;
                    long timestampMs = Math.Max(0, (long)packet.Timestamp.TotalMilliseconds);
                    _originMilliseconds = _originMilliseconds < 0 ? timestampMs : _originMilliseconds;

                    GCHandle pinned = GCHandle.Alloc(packet.Bgra32Bytes, GCHandleType.Pinned);
                    try
                    {
                        using var bitmap = new Bitmap(
                            packet.Width,
                            packet.Height,
                            packet.Width * 4,
                            PixelFormat.Format32bppArgb,
                            pinned.AddrOfPinnedObject());

                        foreach (var frame in _encoder!.Encode(bitmap, timestampMs - _originMilliseconds))
                        {
                            BufferEncodedFrameNoLock(frame);
                        }
                    }
                    finally
                    {
                        pinned.Free();
                    }

                    TrimNoLock();
                }
            }
            finally
            {
                packet.Dispose();
            }
        }

        public void Add(GpuVideoFramePacket packet)
        {
            lock (_gate)
            {
                long receivedPacketCount = ++_receivedGpuPacketCount;
                if (receivedPacketCount == 1)
                {
                    Texture2DDescription description = packet.Texture.Description;
                    _ = GameReplayLog.WriteAsync(nameof(HardwareReplayBuffer),
                        $"First GPU packet received. Size={packet.Width}x{packet.Height}, Format={packet.Format}, TextureFormat={description.Format}, Bind={description.BindFlags}, Usage={description.Usage}, Duplicate={packet.IsDuplicate}");
                }

                if (_useDirectNvencCapture)
                {
                    long directTimestampMs = Math.Max(0, (long)packet.Timestamp.TotalMilliseconds);
                    if (_directNvencCaptureClockOriginMilliseconds is null)
                    {
                        long elapsedSinceNvencStartMs = Math.Max(
                            0,
                            (long)(DateTime.UtcNow - _directNvencStartedUtc).TotalMilliseconds);
                        _directNvencCaptureClockOriginMilliseconds =
                            directTimestampMs - elapsedSinceNvencStartMs;
                    }
                    _originMilliseconds = _originMilliseconds < 0 ? directTimestampMs : _originMilliseconds;
                    _latestTimestampMilliseconds = directTimestampMs - _originMilliseconds;
                    if (++_directAddCount % 120 == 0)
                        TrimDirectNvencSegmentsNoLock();
                    return;
                }

                if (!TryEnsureEncoderNoLock(
                        packet.Width,
                        packet.Height,
                        packet.Texture.Device,
                        packet.Format))
                    return;
                long timestampMs = Math.Max(0, (long)packet.Timestamp.TotalMilliseconds);
                _originMilliseconds = _originMilliseconds < 0 ? timestampMs : _originMilliseconds;
                _latestTimestampMilliseconds = timestampMs - _originMilliseconds;

                long targetFrameIndex = (long)Math.Round(
                    _latestTimestampMilliseconds * Math.Max(1, _fps) / 1000.0);
                while (_nextCfrFrameIndex <= targetFrameIndex)
                {
                    long cfrTimestampMilliseconds = (long)Math.Round(
                        _nextCfrFrameIndex * 1000.0 / Math.Max(1, _fps));
                    if (_lastKeyFrameRequestMilliseconds < 0 ||
                        cfrTimestampMilliseconds - _lastKeyFrameRequestMilliseconds >= 2000)
                    {
                        _encoder!.ForceNextKeyFrame();
                        _lastKeyFrameRequestMilliseconds = cfrTimestampMilliseconds;
                    }

                    IReadOnlyList<H264EncodedFrame> encodedFrames;
                    try
                    {
                        // Reusing the encoder-owned converted NV12 surface is an
                        // NVIDIA-specific fast path. Qualcomm's asynchronous H.264
                        // MFT can still own that surface when the next duplicate is
                        // submitted; CopyResource then fails with E_UNEXPECTED and
                        // leaves the transform generating an exception every frame.
                        // Resubmit the captured BGRA texture on non-NVIDIA hardware
                        // so the encoder's normal input-request and surface-lifetime
                        // handling remains in control. This preserves the configured
                        // replay resolution, frame rate and bitrate.
                        var repeatedFrameEncoder = _encoder as MediaFoundationH264Encoder;
                        bool canReuseConvertedSurface =
                            packet.IsDuplicate &&
                            repeatedFrameEncoder != null &&
                            NvidiaH264EncoderPolicy.MatchesEncoderMode(repeatedFrameEncoder.EncoderMode);
                        encodedFrames = canReuseConvertedSurface
                            ? repeatedFrameEncoder!.EncodeRepeatedGpuFrame(cfrTimestampMilliseconds)
                            : _encoder!.EncodeGpuBgraTexture(
                                packet.Texture, packet.Width, packet.Height, cfrTimestampMilliseconds);
                    }
                    catch (SharpDX.SharpDXException ex) when (
                        _exportHdr10 &&
                        _encoder is MediaFoundationH264Encoder &&
                        ex.ResultCode.Code == unchecked((int)0xC00D6D76))
                    {
                        // NVIDIA's HEVC MFT can accept the declared P010 media type and
                        // still reject its first ProcessInput call. Bypass that wrapper
                        // with Zink's native D3D11 NVENC bridge so P010 remains on the
                        // RTX GPU. Retain oneVPL only if native NVENC cannot initialize.
                        _encoder.Dispose();
                        try
                        {
                            _encoder = new ZinkNvencHevcEncoder(
                                packet.Texture.Device, _width, _height, _exportBitrate, _fps);
                        }
                        catch (Exception nvencException)
                        {
                            _ = GameReplayLog.WriteAsync(nameof(HardwareReplayBuffer),
                                $"Zink native NVENC initialization failed; retaining Intel oneVPL recovery. Error={nvencException}");
                            _encoder = new IntelVplHevcEncoder(
                                _width, _height, _exportBitrate, _fps);
                        }
                        _usedHdrEncoderCompatibilityRecovery = true;
                        _ = GameReplayLog.WriteAsync(nameof(HardwareReplayBuffer),
                            $"NVIDIA Media Foundation rejected P010 with 0xC00D6D76; replay recovered in-session with {_encoder.EncoderMode}. Output={_width}x{_height}@{_fps}, HDR10=True");
                        encodedFrames = _encoder.EncodeGpuBgraTexture(
                            packet.Texture, packet.Width, packet.Height, cfrTimestampMilliseconds);
                    }
                    catch (SharpDX.SharpDXException ex)
                    {
                        // Some hardware MFTs (notably the QCOM H.264 encoder) can
                        // initialize successfully and then enter a permanent
                        // E_UNEXPECTED state. Retrying that dead transform for every
                        // 60 FPS replay frame creates an exception/logging storm that
                        // starves call audio and GPU video playback. Treat a runtime
                        // device failure like an initialization failure and retry only
                        // after the existing bounded cooldown.
                        string error = ex.GetBaseException().Message;
                        _encoder?.Dispose();
                        _encoder = null;
                        _encoderRetryAfterUtc = DateTime.UtcNow.AddSeconds(30);
                        _lastEncoderInitializationError = error;
                        _nextCfrFrameIndex = targetFrameIndex + 1;
                        _ = GameReplayLog.WriteAsync(nameof(HardwareReplayBuffer),
                            $"Hardware replay encoder failed at runtime; retrying in 30 seconds. HRESULT=0x{ex.ResultCode.Code:X8}; {error}");
                        break;
                    }

                    foreach (var frame in encodedFrames)
                    {
                        BufferEncodedFrameNoLock(frame);
                        if (_frames.Count == 1)
                            _ = GameReplayLog.WriteAsync(nameof(HardwareReplayBuffer),
                                $"First usable encoded frame. Bytes={frame.Data.Length}, TimestampMs={frame.TimestampMilliseconds}, KeyFrame={frame.IsKeyFrame}");
                    }
                    _nextCfrFrameIndex++;
                }
                TrimNoLock();

                if (receivedPacketCount % 600 == 0)
                {
                    _ = GameReplayLog.WriteAsync(nameof(HardwareReplayBuffer),
                        $"Replay buffer health. ReceivedGpuPackets={receivedPacketCount}, EncodedFrames={_frames.Count}, LiveEncodedMB={_liveEncodedBytes / 1048576d:0.0}, ExportFrames={(_exportFrames?.Count ?? 0)}, ExportMB={(_exportFrames?.TotalBytes ?? 0) / 1048576d:0.0}, LatestTimestampMs={_latestTimestampMilliseconds}, BufferedDuration={BufferedDuration}, Encoder={_encoder?.EncoderMode ?? "not created"}, {GetProcessMemorySnapshot()}");
                }
            }
        }

        public TimeSpan BufferedDuration
        {
            get
            {
                lock (_gate)
                {
                    if (_useDirectNvencCapture)
                    {
                        TimeSpan elapsed = DateTime.UtcNow - _directNvencStartedUtc;
                        return elapsed < _duration ? elapsed : _duration;
                    }

                    if (_originMilliseconds < 0)
                        return TimeSpan.Zero;
                    return TimeSpan.FromMilliseconds(Math.Min(_latestTimestampMilliseconds, (long)_duration.TotalMilliseconds));
                }
            }
        }

        internal bool UsesDirectCapture => _useDirectNvencCapture;

        internal void SynchronizeDirectCaptureClock()
        {
            lock (_gate)
            {
                if (!_useDirectNvencCapture)
                    return;

                // Audio timestamps begin at zero when WASAPI starts. Express the
                // already-running FFmpeg segment clock on that same timeline.
                _directNvencCaptureClockOriginMilliseconds =
                    -(long)Math.Max(0, (DateTime.UtcNow - _directNvencStartedUtc).TotalMilliseconds);
            }
        }

        public TimeSpan ExportStartTimestamp
        {
            get
            {
                lock (_gate)
                    return _exportStartTimestamp;
            }
        }

        public TimeSpan ExportDuration
        {
            get
            {
                lock (_gate)
                    return _exportDuration;
            }
        }

        public async Task WriteVideoAsync(
            string outputPath,
            TimeSpan requestedDuration,
            bool writeElementaryStream = true)
        {
            if (_useDirectNvencCapture)
            {
                await WriteDirectNvencVideoAsync(outputPath, requestedDuration);
                return;
            }

            ReplayFrameStorage.Snapshot frames;
            lock (_gate)
            {
                TimeSpan selectedDuration = requestedDuration <= _duration ? requestedDuration : _duration;
                ReplayFrameStorage.Entry[] bufferedFrames = _frames.ToArray();
                long cutoff = bufferedFrames.Length == 0
                    ? 0
                    : bufferedFrames[^1].TimestampMilliseconds - (long)selectedDuration.TotalMilliseconds;
                int start = Array.FindLastIndex(
                    bufferedFrames,
                    f => f.TimestampMilliseconds <= cutoff && f.IsKeyFrame);
                if (start < 0)
                    start = Array.FindIndex(bufferedFrames, f => f.IsKeyFrame);
                if (start < 0)
                    start = 0;
                int exactFrameCount = Math.Max(
                    1,
                    (int)Math.Round(selectedDuration.TotalSeconds * Math.Max(1, _fps)));
                var selectedFrames = bufferedFrames
                    .Skip(start)
                    .Take(exactFrameCount)
                    .ToArray();
                _exportFrames?.Dispose();
                _exportFrames = new ReplayFrameStorage.Snapshot(selectedFrames);
                _exportDuration = TimeSpan.FromSeconds(
                    selectedFrames.Length / (double)Math.Max(1, _fps));
                frames = _exportFrames;
                if (selectedFrames.Length > 0)
                {
                    _exportStartTimestamp = TimeSpan.FromMilliseconds(
                        Math.Max(0, _originMilliseconds) + selectedFrames[0].TimestampMilliseconds);
                    double timestampSpanSeconds = selectedFrames.Length > 1
                        ? Math.Max(0.001, (selectedFrames[^1].TimestampMilliseconds - selectedFrames[0].TimestampMilliseconds) / 1000.0)
                        : 0;
                    _exportFps = timestampSpanSeconds > 0
                        ? Math.Max(0.001, (selectedFrames.Length - 1) / timestampSpanSeconds)
                        : _fps;
                }

                _ = GameReplayLog.WriteAsync(nameof(HardwareReplayBuffer),
                    $"Replay save snapshot selected. RequestedDuration={requestedDuration}, ExactFrameBudget={exactFrameCount}, AvailableFrames={_frames.Count}, LiveEncodedMB={_liveEncodedBytes / 1048576d:0.0}, SelectedFrames={selectedFrames.Length}, SelectedMB={selectedFrames.Sum(frame => (long)frame.Length) / 1048576d:0.0}, StartIndex={start}, CutoffTimestampMs={cutoff}, ExportStart={_exportStartTimestamp}, MeasuredFps={_exportFps:0.###}, TargetFps={_fps}, HDR10={_exportHdr10}, {GetProcessMemorySnapshot()}");
            }

            if (frames.Count == 0)
            {
                await GameReplayLog.WriteAsync(nameof(HardwareReplayBuffer),
                    $"Save rejected: no encoded frames. Encoder={_encoder?.EncoderMode ?? "not created"}, HDR10={_exportHdr10}, Output={_width}x{_height}@{_fps}");
                throw new InvalidOperationException("No hardware-encoded replay frames are available yet.");
            }

            if (!writeElementaryStream)
                return;

            await using var stream = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 1024, true);
            foreach (H264EncodedFrame frame in frames)
                await stream.WriteAsync(frame.Data);
        }

        public async Task MuxAsync(string h264Path, string? wavePath, string outputPath, TimeSpan requestedDuration)
        {
            await MuxAsync(
                h264Path,
                wavePath,
                null,
                null,
                outputPath,
                requestedDuration,
                320);
        }

        public async Task MuxAsync(
            string h264Path,
            string? combinedWavePath,
            string? desktopWavePath,
            string? microphoneWavePath,
            string outputPath,
            TimeSpan requestedDuration,
            int audioBitrateKbps)
        {
            string? outputDirectory = Path.GetDirectoryName(outputPath);
            if (string.IsNullOrWhiteSpace(outputDirectory))
                throw new InvalidOperationException("The replay output folder path is invalid.");
            Directory.CreateDirectory(outputDirectory);

            if (_useDirectNvencCapture)
            {
                await MuxDirectCaptureAsync(
                    h264Path,
                    combinedWavePath,
                    desktopWavePath,
                    microphoneWavePath,
                    outputPath,
                    audioBitrateKbps);
                return;
            }

            await GameReplayLog.WriteAsync(nameof(HardwareReplayBuffer),
                $"Mux starting. Frames={(_exportFrames?.Count ?? 0)}, Size={_width}x{_height}, MeasuredFps={_exportFps:0.###}, TargetFps={_fps}, Bitrate={_exportBitrate}, HDR10={_exportHdr10}, CombinedAudio={File.Exists(combinedWavePath)}, DesktopAudio={File.Exists(desktopWavePath)}, MicrophoneAudio={File.Exists(microphoneWavePath)}");

            ReplayFrameStorage.Snapshot exportFrames = _exportFrames ??
                throw new InvalidOperationException("No replay export snapshot is available.");
            try
            {
                await ZinkNativeMultiTrackMuxer.MuxAsync(
                    exportFrames,
                    _width,
                    _height,
                    _fps,
                    _exportBitrate,
                    _exportHdr10,
                    ExistingPathOrNull(combinedWavePath),
                    ExistingPathOrNull(desktopWavePath),
                    ExistingPathOrNull(microphoneWavePath),
                    outputPath,
                    audioBitrateKbps);
            }
            finally
            {
                lock (_gate)
                {
                    exportFrames.Dispose();
                    if (ReferenceEquals(_exportFrames, exportFrames))
                        _exportFrames = null;
                }
            }

            await GameReplayLog.WriteAsync(nameof(HardwareReplayBuffer),
                $"Mux completed. Output={outputPath}, Exists={File.Exists(outputPath)}, Bytes={(File.Exists(outputPath) ? new FileInfo(outputPath).Length : 0)}");

            await RecordingDiagnostics.WriteRecordingAsync(
                "NativeMultiTrackMuxCompleted",
                new
                {
                    OutputPath = outputPath,
                    HasCombinedTrack = File.Exists(combinedWavePath),
                    HasDesktopTrack = File.Exists(desktopWavePath),
                    HasMicrophoneTrack = File.Exists(microphoneWavePath),
                    AudioBitrateKbps = audioBitrateKbps,
                    EncodedFrameCount = exportFrames.Count,
                    MeasuredFrameRate = _exportFps,
                    TargetFrameRate = _fps,
                    GenuineHdr = _exportHdr10,
                    VideoCodec = _exportHdr10 ? "HEVC Main10" : "H.264",
                    PixelFormat = _exportHdr10 ? "P010 10-bit" : "NV12 8-bit",
                    Backend = "Zink Native Media Foundation"
                });

            static string? ExistingPathOrNull(string? path) =>
                !string.IsNullOrWhiteSpace(path) && File.Exists(path) ? path : null;
        }

        internal void ReleaseExportSnapshot()
        {
            lock (_gate)
            {
                _exportFrames?.Dispose();
                _exportFrames = null;
            }
        }

        private void EnsureEncoder(
            int width,
            int height,
            SharpDX.Direct3D11.Device? preferredDevice = null,
            SharpDX.DXGI.Format sourceFormat = SharpDX.DXGI.Format.B8G8R8A8_UNorm)
        {
            if (_encoder != null)
                return;

            bool hdrSourceTexture = _hdrDetected && HdrRecordingContract.IsHdrCaptureTexture(sourceFormat);
            if (preferredDevice == null)
                _dedicatedEncoderDevice ??= CreateBestHardwareEncodingDevice();

            // Encode GPU packets on their owning capture adapter. Cross-adapter
            // OpenSharedResource1 is not supported for every capture texture and
            // leaves replay running with an empty frame buffer when it fails.
            SharpDX.Direct3D11.Device encoderDevice = preferredDevice ?? _dedicatedEncoderDevice ??
                throw new InvalidOperationException("No D3D11 hardware encoding device is available.");

            // Use SDR compatibility only if Windows reports an HDR display but
            // the capture source did not supply the required FP16 HDR texture.
            if (_hdrDetected && !hdrSourceTexture && !_loggedHdrCompatibilityFallback)
            {
                _loggedHdrCompatibilityFallback = true;
                _ = RecordingDiagnostics.WriteHdrAsync(
                    "HdrDesktopUsingSdrCompatibilityEncoder",
                    new
                    {
                        GenuineHdr = false,
                        OutputCodec = "H.264",
                        OutputPixelFormat = "NV12 8-bit",
                        OutputColourSpace = "BT.709",
                        RequiredCaptureFormat = HdrRecordingContract.CaptureFormat,
                        RequiredEncoderInput = HdrRecordingContract.EncoderInputFormat,
                        RequiredCodec = HdrRecordingContract.VideoCodec,
                        RequiredPrimaries = HdrRecordingContract.ColourPrimaries,
                        RequiredTransfer = HdrRecordingContract.TransferFunction
                    });
            }

            GamingReplayProfile profile = GamingReplayProfileService.Select(
                encoderDevice,
                width,
                height,
                _options,
                GetPrimaryDirectCaptureOutput().IsHdr);
            _width = profile.OutputWidth;
            _height = profile.OutputHeight;
            _fps = profile.FrameRate;
            int bitrate = profile.VideoBitrate;
            const long replayMemoryBudgetBytes = 1280L * 1024 * 1024;
            int memorySafeBitrate = (int)Math.Clamp(
                replayMemoryBudgetBytes * 8d / Math.Max(1d, _duration.TotalSeconds),
                16_000_000d,
                160_000_000d);
            bitrate = Math.Min(bitrate, memorySafeBitrate);
            _exportBitrate = bitrate;
            _exportHdr10 = hdrSourceTexture;
            // The display/capture adapter is not necessarily the best encoder.
            // On hybrid laptops the desktop commonly belongs to Intel while the
            // discrete NVIDIA GPU owns substantially more VRAM. Follow the
            // hardware profile's selected adapter for the encoder family.
            ScreenShareH264EncoderFamily encoderFamily = GetEncoderFamilyForVendor(profile.VendorId);
            _encoder = _exportHdr10 && encoderFamily == ScreenShareH264EncoderFamily.Intel
                ? new IntelVplHevcEncoder(_width, _height, bitrate, _fps)
                : new MediaFoundationH264Encoder(
                    _width, _height, bitrate, preferHardware: true, requireHardware: true,
                    preferredMediaFoundationDevice: encoderDevice, frameRate: _fps,
                    preferredEncoderFamily: encoderFamily,
                    enableHdr10: _exportHdr10);
            _ = GameReplayLog.WriteAsync(nameof(HardwareReplayBuffer),
                $"Encoder started. Mode={_encoder.EncoderMode}, HDR10={_exportHdr10}, Input={_encoder.InputFormat}, DeviceManager={_encoder.GpuDeviceManagerMode}, Output={_width}x{_height}@{_fps}, Bitrate={bitrate}, ProfileBitrate={profile.VideoBitrate}, ReplayMemoryBudgetMB={replayMemoryBudgetBytes / 1048576}, SourceFormat={sourceFormat}");

            _ = RecordingDiagnostics.WriteRecordingAsync(
                "HardwareEncoderStarted",
                new
                {
                    Width = _width,
                    Height = _height,
                    FrameRate = _fps,
                    Bitrate = bitrate,
                    RequestedFamily = encoderFamily.ToString(),
                    Encoder = _encoder.EncoderMode,
                    HardwareAccelerated = _encoder.IsHardwareAccelerated,
                    GpuTextureInput = _encoder.CanEncodeGpuTexture,
                    InputFormat = _encoder.InputFormat,
                    DeviceManager = _encoder.GpuDeviceManagerMode
                    ,ProfileReason = profile.Reason
                    ,SourceWidth = profile.SourceWidth
                    ,SourceHeight = profile.SourceHeight
                    ,OutputWidth = profile.OutputWidth
                    ,OutputHeight = profile.OutputHeight
                    ,DetectedVendorId = $"0x{profile.VendorId:X4}"
                    ,Adapter = profile.AdapterName
                    ,DedicatedVideoMemoryBytes = profile.DedicatedVideoMemoryBytes
                    ,HdrDetected = profile.IsHdr
                    ,GenuineHdrPipeline = _exportHdr10
                    ,CaptureTextureFormat = sourceFormat.ToString()
                    ,OutputCodec = _exportHdr10 ? HdrRecordingContract.VideoCodec : "H.264"
                    ,EncoderInputFormat = _exportHdr10 ? HdrRecordingContract.EncoderInputFormat : "NV12"
                    ,CrossAdapterHdrTransfer = false
                    ,HdrEncoderCompatibilityRecovery = _usedHdrEncoderCompatibilityRecovery
                    ,AdapterSelectionReason = preferredDevice != null
                        ? "Capture adapter selected as the highest-throughput GPU compatible with the captured display texture"
                        : "Best available dedicated hardware adapter selected"
                });
        }

        private bool TryEnsureEncoderNoLock(
            int width,
            int height,
            SharpDX.Direct3D11.Device? preferredDevice = null,
            SharpDX.DXGI.Format sourceFormat = SharpDX.DXGI.Format.B8G8R8A8_UNorm)
        {
            if (_encoder != null)
                return true;

            DateTime now = DateTime.UtcNow;
            if (now < _encoderRetryAfterUtc)
                return false;

            try
            {
                EnsureEncoder(width, height, preferredDevice, sourceFormat);
                _lastEncoderInitializationError = null;
                return true;
            }
            catch (Exception ex)
            {
                // A failed hardware configuration used to be retried and logged on
                // every frame. During gaming that produced a continuous disk-write
                // storm. Keep replay enabled and retry after a bounded cooldown.
                _encoderRetryAfterUtc = now.AddSeconds(30);
                string error = ex.GetBaseException().Message;
                if (!string.Equals(error, _lastEncoderInitializationError, StringComparison.Ordinal))
                {
                    _lastEncoderInitializationError = error;
                    _ = GameReplayLog.WriteAsync(nameof(HardwareReplayBuffer),
                        $"Hardware replay encoder initialization failed; retrying in 30 seconds. {error}");
                }
                return false;
            }
        }

        private static ScreenShareH264EncoderFamily GetEncoderFamilyForCaptureDevice(
            SharpDX.Direct3D11.Device? captureDevice)
        {
            if (captureDevice == null)
                return ScreenShareH264EncoderFamily.Auto;

            try
            {
                using var dxgiDevice = captureDevice.QueryInterface<SharpDX.DXGI.Device>();
                using Adapter adapter = dxgiDevice.Adapter;
                int vendorId = adapter.Description.VendorId;

                // PCI vendor IDs cover integrated Intel graphics, Arc, and future
                // Intel adapters without maintaining a model-name allow-list.
                return vendorId switch
                {
                    NvidiaH264EncoderPolicy.AdapterVendorId => ScreenShareH264EncoderFamily.Nvidia,
                    IntelH264EncoderPolicy.AdapterVendorId => ScreenShareH264EncoderFamily.Intel,
                    _ => ScreenShareH264EncoderFamily.Auto
                };
            }
            catch
            {
                // Media Foundation still enumerates the available hardware MFTs
                // when an adapter identity cannot be obtained.
                return ScreenShareH264EncoderFamily.Auto;
            }
        }

        private static ScreenShareH264EncoderFamily GetEncoderFamilyForVendor(int vendorId) =>
            vendorId switch
            {
                NvidiaH264EncoderPolicy.AdapterVendorId => ScreenShareH264EncoderFamily.Nvidia,
                IntelH264EncoderPolicy.AdapterVendorId => ScreenShareH264EncoderFamily.Intel,
                AmdH264EncoderPolicy.AdapterVendorId => ScreenShareH264EncoderFamily.Auto,
                _ => ScreenShareH264EncoderFamily.Auto
            };

        private static SharpDX.Direct3D11.Device? CreateBestHardwareEncodingDevice()
        {
            using var factory = new Factory1();
            int bestIndex = FindBestHardwareAdapterIndex(factory);
            try
            {
                using Adapter1 adapter = factory.GetAdapter1(bestIndex);
                return new SharpDX.Direct3D11.Device(
                    adapter,
                    DeviceCreationFlags.BgraSupport | DeviceCreationFlags.VideoSupport);
            }
            catch (Exception ex)
            {
                _ = RecorderLog.InfoAsync(nameof(HardwareReplayBuffer),
                    $"Dedicated encoder device creation failed; using the capture adapter. {ex.Message}");
                return null;
            }
        }

        private void TrimNoLock()
        {
            if (_frames.Count < 2 || _keyFrameTimestamps.Count < 2)
                return;

            long cutoff = _latestTimestampMilliseconds - (long)_duration.TotalMilliseconds;
            // Always retain the newest keyframe at or before the rolling cutoff so
            // the oldest buffered frame remains independently decodable. Only the
            // small keyframe queue is inspected; ordinary frames are dequeued once.
            while (_keyFrameTimestamps.Count > 1 &&
                   _keyFrameTimestamps.ElementAt(1) <= cutoff)
            {
                _keyFrameTimestamps.Dequeue();
                long retainedKeyFrameTimestamp = _keyFrameTimestamps.Peek();
                while (_frames.Count > 0 &&
                       _frames.Peek().TimestampMilliseconds < retainedKeyFrameTimestamp)
                {
                    var expired = _frames.Dequeue();
                    _liveEncodedBytes -= expired.Length;
                    expired.Release();
                }
            }

            if (_liveEncodedBytes < 0)
                _liveEncodedBytes = 0;
        }

        private void BufferEncodedFrameNoLock(H264EncodedFrame frame)
        {
            _frames.Enqueue(_frameStorage.Append(frame));
            _liveEncodedBytes += frame.Data.Length;
            _latestTimestampMilliseconds = Math.Max(
                _latestTimestampMilliseconds,
                frame.TimestampMilliseconds);
            if (frame.IsKeyFrame)
                _keyFrameTimestamps.Enqueue(frame.TimestampMilliseconds);
        }

        private static string GetProcessMemorySnapshot()
        {
            using var process = Process.GetCurrentProcess();
            return $"WorkingSetMB={process.WorkingSet64 / 1048576d:0.0}, PrivateMB={process.PrivateMemorySize64 / 1048576d:0.0}, ManagedHeapMB={GC.GetTotalMemory(false) / 1048576d:0.0}, Gen2Collections={GC.CollectionCount(2)}";
        }

        private async Task MuxDirectCaptureAsync(
            string videoPath,
            string? combinedWavePath,
            string? desktopWavePath,
            string? microphoneWavePath,
            string outputPath,
            int audioBitrateKbps)
        {
            string ffmpeg = Path.Combine(AppContext.BaseDirectory, "Tools", "ffmpeg.exe");
            if (!File.Exists(ffmpeg))
                ffmpeg = Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe");
            if (!File.Exists(ffmpeg))
                throw new FileNotFoundException("The bundled HDR replay muxer was not found.", ffmpeg);

            var audioInputs = new List<(string Path, string Title)>();
            if (!string.IsNullOrWhiteSpace(combinedWavePath) && File.Exists(combinedWavePath))
                audioInputs.Add((combinedWavePath, "Game and microphone"));
            if (!string.IsNullOrWhiteSpace(desktopWavePath) && File.Exists(desktopWavePath))
                audioInputs.Add((desktopWavePath, "Game audio"));
            if (!string.IsNullOrWhiteSpace(microphoneWavePath) && File.Exists(microphoneWavePath))
                audioInputs.Add((microphoneWavePath, "Microphone"));

            var startInfo = new ProcessStartInfo
            {
                FileName = ffmpeg,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true
            };
            startInfo.ArgumentList.Add("-hide_banner");
            startInfo.ArgumentList.Add("-loglevel");
            startInfo.ArgumentList.Add("error");
            startInfo.ArgumentList.Add("-y");
            startInfo.ArgumentList.Add("-f");
            startInfo.ArgumentList.Add("mpegts");
            startInfo.ArgumentList.Add("-i");
            startInfo.ArgumentList.Add(videoPath);
            foreach (var audioInput in audioInputs)
            {
                startInfo.ArgumentList.Add("-i");
                startInfo.ArgumentList.Add(audioInput.Path);
            }
            startInfo.ArgumentList.Add("-map");
            startInfo.ArgumentList.Add("0:v:0");
            for (int index = 0; index < audioInputs.Count; index++)
            {
                startInfo.ArgumentList.Add("-map");
                startInfo.ArgumentList.Add($"{index + 1}:a:0");
            }
            startInfo.ArgumentList.Add("-c:v");
            startInfo.ArgumentList.Add("copy");
            startInfo.ArgumentList.Add("-tag:v");
            startInfo.ArgumentList.Add("hvc1");
            if (audioInputs.Count > 0)
            {
                startInfo.ArgumentList.Add("-c:a");
                startInfo.ArgumentList.Add("aac");
                startInfo.ArgumentList.Add("-b:a");
                startInfo.ArgumentList.Add($"{audioBitrateKbps}k");
                startInfo.ArgumentList.Add("-disposition:a:0");
                startInfo.ArgumentList.Add("default");
                for (int index = 0; index < audioInputs.Count; index++)
                {
                    startInfo.ArgumentList.Add($"-metadata:s:a:{index}");
                    startInfo.ArgumentList.Add($"title={audioInputs[index].Title}");
                }
            }
            startInfo.ArgumentList.Add("-movflags");
            startInfo.ArgumentList.Add("+faststart");
            startInfo.ArgumentList.Add(outputPath);

            await GameReplayLog.WriteAsync(nameof(HardwareReplayBuffer),
                $"HDR mux starting. Video={videoPath}, AudioTracks={audioInputs.Count}, Output={outputPath}");
            using Process process = Process.Start(startInfo) ??
                throw new InvalidOperationException("The HDR replay muxer could not start.");
            Task<string> errorTask = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            string error = await errorTask;
            if (process.ExitCode != 0 || !File.Exists(outputPath) || new FileInfo(outputPath).Length == 0)
                throw new InvalidOperationException(
                    $"HDR replay muxing failed with exit code {process.ExitCode}. {error}".Trim());

            await GameReplayLog.WriteAsync(nameof(HardwareReplayBuffer),
                $"HDR mux completed. Output={outputPath}, Bytes={new FileInfo(outputPath).Length}, HDR10={_directNvencHdr}, Fps={_directNvencFps}");
        }

        private bool TryStartDirectNvencCapture()
        {
            try
            {
                string ffmpeg = Path.Combine(AppContext.BaseDirectory, "Tools", "ffmpeg.exe");
                if (!File.Exists(ffmpeg))
                    ffmpeg = Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe");
                if (!File.Exists(ffmpeg))
                    return false;

                string root = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Zink",
                    "Replay120",
                    Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(root);
                string segmentPattern = Path.Combine(root, "segment_%08d.ts");

                DirectCaptureOutput captureOutput = GetPrimaryDirectCaptureOutput();
                _directNvencHdr = captureOutput.IsHdr;
                RecordingHardwareProfile hardwareProfile;
                using (var profileFactory = new Factory1())
                {
                    hardwareProfile = RecordingHardwareProfileService.Detect(
                        profileFactory,
                        captureOutput.EncoderAdapterIndex,
                        captureOutput.SourceWidth,
                        captureOutput.SourceHeight);
                }
                int bitrate = _directNvencHdr
                    ? hardwareProfile.Bitrate
                    : (int)(_options.VideoBitrate > 0 ? _options.VideoBitrate : 30_000_000);
                int maxRate = Math.Max(bitrate, (int)Math.Min(int.MaxValue, bitrate * 3L / 2L));
                long bufferSize = Math.Min(int.MaxValue, bitrate * 2L);
                // The desktop duplication 10-bit RGB surface cannot be passed straight
                // to HEVC and merely labelled as BT.2020/PQ. NVENC otherwise treats the
                // RGB values with its default conversion, clipping white UI and making
                // colours neon. This FFmpeg build cannot create a compatible D3D11 P010
                // conversion surface on every supported GPU, so use the verified scRGB
                // conversion path at a rate/resolution it can sustain in real time.
                int captureFps = _directNvencHdr ? 15 : 120;
                _directNvencFps = captureFps;
                int hdrOutputWidth = captureOutput.SourceWidth;
                int hdrOutputHeight = captureOutput.SourceHeight - captureOutput.SourceHeight % 2;
                string captureFilter = _directNvencHdr
                    ? $"ddagrab=output_idx={captureOutput.OutputIndex}:framerate={captureFps}:dup_frames=1:output_fmt=16bit," +
                      "hwdownload,format=rgbaf16le," +
                      $"zscale=w={hdrOutputWidth}:h={hdrOutputHeight}:pin=bt709:tin=linear:min=gbr:" +
                      // Windows HDR composition uses 203 nits for SDR reference
                      // white. Using scRGB's base 80-nit unit here made ordinary
                      // white UI render as dull grey in the saved HDR replay.
                      "p=bt2020:t=smpte2084:m=bt2020nc:r=limited:npl=203," +
                      "format=p010le,setparams=range=limited:color_primaries=bt2020:" +
                      "color_trc=smpte2084:colorspace=bt2020nc:chroma_location=left"
                    : $"ddagrab=output_idx={captureOutput.OutputIndex}:framerate={captureFps}:dup_frames=1";
                bool useIntelEncoder =
                    captureOutput.EncoderVendorId == IntelH264EncoderPolicy.AdapterVendorId;
                string videoEncoderArguments = _directNvencHdr
                    ? useIntelEncoder
                        ? "-c:v hevc_qsv -profile:v main10 -preset veryfast -scenario gamestreaming -async_depth 6 " +
                          "-color_range tv -bsf:v hevc_metadata=video_full_range_flag=0:colour_primaries=9:" +
                          "transfer_characteristics=16:matrix_coefficients=9"
                        : "-c:v hevc_nvenc -profile:v main10 -preset p1 -tune ull " +
                          "-color_range tv -bsf:v hevc_metadata=video_full_range_flag=0:colour_primaries=9:" +
                          "transfer_characteristics=16:matrix_coefficients=9"
                    : useIntelEncoder
                        ? "-c:v h264_qsv -preset veryfast -scenario gamestreaming -async_depth 6"
                        : "-c:v h264_nvenc";
                string encoderTuningArguments = _directNvencHdr || useIntelEncoder
                    ? string.Empty
                    : "-preset p1 -tune ull ";
                var startInfo = new ProcessStartInfo
                {
                    FileName = ffmpeg,
                    Arguments =
                        $"-hide_banner -loglevel error " +
                        (_directNvencHdr ? "-filter_complex_threads 8 " : string.Empty) +
                        (_directNvencHdr
                            ? $"-init_hw_device d3d11va=cap:{captureOutput.CaptureAdapterIndex} " +
                              $"-filter_hw_device cap " +
                              (useIntelEncoder ? $"-qsv_device {captureOutput.EncoderAdapterIndex} " : string.Empty)
                            : string.Empty) +
                        $"-filter_complex \"{captureFilter}\" " +
                        $"-an {videoEncoderArguments} {encoderTuningArguments}" +
                        $"-rc vbr -b:v {bitrate} -maxrate {maxRate} -bufsize {bufferSize} " +
                        $"-g {captureFps} -keyint_min {captureFps} -bf 0 -forced-idr 1 " +
                        $"-fps_mode cfr -r {captureFps} -f segment -segment_time 1 " +
                        $"-segment_format mpegts \"{segmentPattern}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardInput = true,
                    RedirectStandardError = true
                };

                _directNvencStartedUtc = DateTime.UtcNow;
                _directNvencCaptureClockOriginMilliseconds = null;
                _directNvencProcess = Process.Start(startInfo);
                if (_directNvencProcess == null)
                    return false;

                _directNvencProcess.ErrorDataReceived += (_, eventArgs) =>
                {
                    if (!string.IsNullOrWhiteSpace(eventArgs.Data))
                        _ = RecorderLog.InfoAsync(nameof(HardwareReplayBuffer), $"NVENC: {eventArgs.Data}");
                };
                _directNvencProcess.BeginErrorReadLine();

                // Starting a process does not prove that capture and encoding opened.
                // Do not suppress the existing replay path until FFmpeg has written
                // an actual segment; encoder initialization failures can arrive after
                // the previous short process-exit check completed.
                if (!WaitForFirstDirectSegment(_directNvencProcess, root, TimeSpan.FromSeconds(8)))
                {
                    int? exitCode = _directNvencProcess.HasExited
                        ? _directNvencProcess.ExitCode
                        : null;
                    _ = RecorderLog.InfoAsync(nameof(HardwareReplayBuffer),
                        $"Direct replay capture did not produce a segment during startup" +
                        (exitCode.HasValue ? $" and exited with code {exitCode.Value}" : string.Empty) +
                        "; retaining the existing encoder path.");
                    StopDirectNvencCaptureNoLock();
                    return false;
                }

                _directNvencFolder = root;
                _width = _directNvencHdr ? hdrOutputWidth : captureOutput.SourceWidth;
                _height = _directNvencHdr ? hdrOutputHeight : captureOutput.SourceHeight;
                _fps = captureFps;
                _exportBitrate = bitrate;
                _exportHdr10 = _directNvencHdr;
                _exportFps = captureFps;
                StartDirectSegmentTrimming();
                _ = RecorderLog.InfoAsync(nameof(HardwareReplayBuffer),
                    $"Direct hardware replay capture started at {captureFps} FPS. " +
                    $"CaptureAdapter={captureOutput.CaptureAdapterIndex}, Output={captureOutput.OutputIndex}, " +
                    $"EncoderAdapter={captureOutput.EncoderAdapterIndex}, HDR={_directNvencHdr}, " +
                    $"Profile={hardwareProfile.Width}x{hardwareProfile.Height}@{captureFps}, " +
                    $"RAM={hardwareProfile.MemoryGeneration}/{hardwareProfile.InstalledMemoryBytes / (1024 * 1024 * 1024)}GB, " +
                    $"CPU='{hardwareProfile.Processor}'/{hardwareProfile.ProcessorCount} threads, " +
                    $"GPU='{hardwareProfile.GraphicsCard}', " +
                    $"HighEnd={hardwareProfile.IsHighEnd}. Folder='{root}'");
                return true;
            }
            catch (Exception ex)
            {
                _ = RecorderLog.ErrorAsync(nameof(HardwareReplayBuffer), ex,
                    "Direct NVENC capture could not start; retaining the existing encoder path.");
                StopDirectNvencCaptureNoLock();
                return false;
            }
        }

        private static bool WaitForFirstDirectSegment(
            Process process,
            string folder,
            TimeSpan timeout)
        {
            var stopwatch = Stopwatch.StartNew();
            while (stopwatch.Elapsed < timeout)
            {
                if (process.HasExited)
                    return false;

                try
                {
                    if (Directory.EnumerateFiles(folder, "segment_*.ts")
                        .Any(path => new FileInfo(path).Length > 0))
                    {
                        return true;
                    }
                }
                catch
                {
                }

                Thread.Sleep(50);
            }

            return false;
        }

        private static DirectCaptureOutput GetPrimaryDirectCaptureOutput()
        {
            try
            {
                string? primaryDeviceName = CaptureEngine.GetPrimaryMonitorDeviceName();
                if (string.IsNullOrWhiteSpace(primaryDeviceName))
                    throw new InvalidOperationException("Could not determine the primary monitor device name.");

                using var factory = new Factory1();
                int encoderAdapterIndex = FindBestHardwareAdapterIndex(factory);
                for (int adapterIndex = 0; ; adapterIndex++)
                {
                    Adapter1? adapter = null;
                    try
                    {
                        adapter = factory.GetAdapter1(adapterIndex);
                    }
                    catch
                    {
                        break;
                    }

                    using (adapter)
                    {
                        for (int outputIndex = 0; ; outputIndex++)
                        {
                            Output? output = null;
                            try
                            {
                                output = adapter.GetOutput(outputIndex);
                            }
                            catch
                            {
                                break;
                            }

                            using (output)
                            using (var output6 = output.QueryInterface<Output6>())
                            {
                                OutputDescription1 description = output6.Description1;
                                var bounds = description.DesktopCoordinates;
                                if (!description.AttachedToDesktop ||
                                    !string.Equals(description.DeviceName?.Trim(), primaryDeviceName.Trim(), StringComparison.OrdinalIgnoreCase))
                                    continue;

                                bool isHdr = description.BitsPerColor >= 10 &&
                                    description.ColorSpace == ColorSpaceType.RgbFullG2084NoneP2020;
                                int encoderVendorId = 0;
                                try
                                {
                                    using Adapter1 encoderAdapter = factory.GetAdapter1(encoderAdapterIndex);
                                    encoderVendorId = encoderAdapter.Description1.VendorId;
                                }
                                catch
                                {
                                }
                                return new DirectCaptureOutput(
                                    adapterIndex,
                                    outputIndex,
                                    encoderAdapterIndex,
                                    encoderVendorId,
                                    bounds.Right - bounds.Left,
                                    bounds.Bottom - bounds.Top,
                                    isHdr);
                            }
                        }
                    }
                }

                throw new InvalidOperationException("The primary desktop output was not found on the capture adapter.");
            }
            catch (Exception ex)
            {
                _ = RecorderLog.InfoAsync(nameof(HardwareReplayBuffer),
                    $"HDR output detection was unavailable; replay capture will use SDR. {ex.Message}");
                return new DirectCaptureOutput(0, 0, 0, 0, 1920, 1080, false);
            }
        }

        private static int FindBestHardwareAdapterIndex(Factory1 factory)
        {
            int bestIndex = 0;
            long bestScore = long.MinValue;
            for (int adapterIndex = 0; ; adapterIndex++)
            {
                Adapter1? adapter = null;
                try
                {
                    adapter = factory.GetAdapter1(adapterIndex);
                }
                catch
                {
                    break;
                }

                using (adapter)
                {
                    AdapterDescription1 description = adapter.Description1;
                    bool software = (description.Flags & AdapterFlags.Software) != 0;
                    if (software)
                        continue;

                    long score = description.DedicatedVideoMemory;
                    if (score > bestScore)
                    {
                        bestScore = score;
                        bestIndex = adapterIndex;
                    }
                }
            }
            return bestIndex;
        }

        private readonly record struct DirectCaptureOutput(
            int CaptureAdapterIndex,
            int OutputIndex,
            int EncoderAdapterIndex,
            int EncoderVendorId,
            int SourceWidth,
            int SourceHeight,
            bool IsHdr);

        private async Task WriteDirectNvencVideoAsync(string outputPath, TimeSpan requestedDuration)
        {
            string[] selectedSegments;
            lock (_gate)
            {
                if (string.IsNullOrWhiteSpace(_directNvencFolder) ||
                    !Directory.Exists(_directNvencFolder))
                {
                    throw new InvalidOperationException("The direct NVENC replay process is not running.");
                }

                bool directNvencProcessRunning =
                    _directNvencProcess != null && !_directNvencProcess.HasExited;

                var completedSegments = Directory.GetFiles(_directNvencFolder, "segment_*.ts")
                    .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                // FFmpeg is still writing the newest segment, so snapshot only closed files.
                if (directNvencProcessRunning && completedSegments.Count > 0)
                    completedSegments.RemoveAt(completedSegments.Count - 1);

                int requiredSegments = Math.Max(1, (int)Math.Ceiling(requestedDuration.TotalSeconds));
                selectedSegments = completedSegments
                    .Skip(Math.Max(0, completedSegments.Count - requiredSegments))
                    .ToArray();

                if (selectedSegments.Length == 0)
                    throw new InvalidOperationException("No completed direct replay segments are available yet.");

                _directExportSegments.Clear();
                foreach (string segment in selectedSegments)
                    _directExportSegments.Add(segment);
                _exportFps = _directNvencFps;
                _directExportDuration = TimeSpan.FromSeconds(selectedSegments.Length);
                _exportStartTimestamp = TimeSpan.FromMilliseconds(
                    CalculateDirectNvencExportStartMilliseconds(selectedSegments));
            }

            var selectedInputs = new List<FileStream>(selectedSegments.Length);
            try
            {
                foreach (string segment in selectedSegments)
                {
                    selectedInputs.Add(new FileStream(
                        segment, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
                        1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan));
                }

                await using var output = new FileStream(
                    outputPath, FileMode.Create, FileAccess.Write, FileShare.None,
                    1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);

                foreach (FileStream input in selectedInputs)
                {
                    await input.CopyToAsync(output);
                }
            }
            finally
            {
                foreach (FileStream input in selectedInputs)
                    input.Dispose();

                lock (_gate)
                {
                    _directExportSegments.Clear();
                    TrimDirectNvencSegmentsNoLock();
                }
            }
        }

        private long CalculateDirectNvencExportStartMilliseconds(string[] selectedSegments)
        {
            if (selectedSegments.Length > 0 &&
                _directNvencCaptureClockOriginMilliseconds is long captureClockOrigin &&
                TryGetDirectSegmentIndex(selectedSegments[0], out long firstSegmentIndex))
            {
                // FFmpeg segments are one second long and start on forced 120-frame
                // keyframes. Map the selected segment index onto the same capture clock
                // used by loopback audio rather than assuming the last closed segment
                // ends at the current frame timestamp.
                return Math.Max(0, captureClockOrigin + firstSegmentIndex * 1000L);
            }

            // Retain a safe fallback for an unexpected segment filename or a replay
            // saved before the first GPU-frame clock sample was observed.
            return Math.Max(0, _originMilliseconds) +
                Math.Max(0, _latestTimestampMilliseconds - selectedSegments.Length * 1000L);
        }

        private static bool TryGetDirectSegmentIndex(string segmentPath, out long index)
        {
            index = 0;
            string fileName = Path.GetFileNameWithoutExtension(segmentPath);
            const string prefix = "segment_";
            return fileName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
                long.TryParse(fileName.AsSpan(prefix.Length), out index);
        }

        private void TrimDirectNvencSegmentsNoLock()
        {
            if (string.IsNullOrWhiteSpace(_directNvencFolder) ||
                !Directory.Exists(_directNvencFolder))
                return;

            try
            {
                var segments = Directory.GetFiles(_directNvencFolder, "segment_*.ts")
                    .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                int keep = Math.Max(5, (int)Math.Ceiling(_duration.TotalSeconds) + 3);
                int removeCount = Math.Max(0, segments.Count - keep);
                for (int index = 0; index < removeCount; index++)
                {
                    if (_directExportSegments.Contains(segments[index]))
                        continue;

                    try
                    {
                        File.Delete(segments[index]);
                    }
                    catch
                    {
                    }
                }
            }
            catch
            {
            }
        }

        private void StartDirectSegmentTrimming()
        {
            _directTrimCancellation?.Cancel();
            _directTrimCancellation?.Dispose();
            _directTrimCancellation = new CancellationTokenSource();
            CancellationToken cancellationToken = _directTrimCancellation.Token;

            _ = Task.Run(async () =>
            {
                try
                {
                    while (!cancellationToken.IsCancellationRequested)
                    {
                        await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
                        lock (_gate)
                        {
                            TrimDirectNvencSegmentsNoLock();
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                }
            }, cancellationToken);
        }

        private void StopDirectNvencCaptureNoLock()
        {
            _directTrimCancellation?.Cancel();
            _directTrimCancellation?.Dispose();
            _directTrimCancellation = null;

            Process? process = _directNvencProcess;
            _directNvencProcess = null;

            if (process != null)
            {
                try
                {
                    if (!process.HasExited)
                    {
                        process.StandardInput.WriteLine("q");
                        process.StandardInput.Flush();
                        if (!process.WaitForExit(3000))
                            process.Kill(entireProcessTree: true);
                    }
                }
                catch
                {
                }
                finally
                {
                    process.Dispose();
                }
            }

            if (!string.IsNullOrWhiteSpace(_directNvencFolder))
            {
                try
                {
                    if (Directory.Exists(_directNvencFolder))
                        Directory.Delete(_directNvencFolder, true);
                }
                catch
                {
                }
                _directNvencFolder = null;
            }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                StopDirectNvencCaptureNoLock();
                _encoder?.Dispose();
                _encoder = null;
                _dedicatedEncoderDevice?.Dispose();
                _dedicatedEncoderDevice = null;
                while (_frames.Count > 0) _frames.Dequeue().Release();
                _frameStorage.Dispose();
                _keyFrameTimestamps.Clear();
                _exportFrames?.Dispose();
                _exportFrames = null;
                _liveEncodedBytes = 0;
            }
        }
    }
}

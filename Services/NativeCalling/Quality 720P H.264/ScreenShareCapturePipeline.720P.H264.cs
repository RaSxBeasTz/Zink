using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Zink.Services;
using SharpDX;

namespace Zink.Services.NativeCalling.Quality720p
{
    public sealed class ScreenShareCapturePipeline : IAsyncDisposable
    {
        public static ScreenShareCapturePipeline Instance => ScreenShareServiceSelector.Active.CapturePipeline;

        // 720p is deliberately a fixed 30-fps mode. This constant is consumed
        // by capture pacing, encoder timestamps, decoder media types and RTP
        // diagnostics, so none of those paths can silently expect 60 fps.
        public const int TargetFps = 30;
        private const int LivePreviewFps = 30;
        public const long JpegQuality = 88L;
        private const int ReceiverSafe1080pFps = 24;
        private const int ReceiverSafeRealtimeFps = 30;
        private const int NonNvidiaSafe1080pFps = IntelH264EncoderPolicy.Safe1080pTwitchFps;
        private const int NonNvidiaSafe1080pBitrate = IntelH264EncoderPolicy.Safe1080pTwitchBitrate;
        internal const bool EnableDirectGpuTexturePath = true;
        private static readonly TimeSpan AdaptationWarmup = TimeSpan.FromSeconds(2);
        private static readonly TimeSpan AdaptationCooldown = TimeSpan.FromSeconds(1);
        private static readonly TimeSpan ReceiverPressurePacingWindow = TimeSpan.FromSeconds(30);
        private static readonly TimeSpan StartupRecoveryKeyFrameThrottle = TimeSpan.FromMilliseconds(2200);
        private static readonly TimeSpan RecoveryKeyFrameThrottle = TimeSpan.FromMilliseconds(900);
        private static readonly TimeSpan HealthyStreamIdrInterval = TimeSpan.FromSeconds(4);
        // NVENC can acknowledge repeated force-keyframe requests without ever
        // exposing a clean point. Bound the lifetime of that broken delta chain.
        private static readonly TimeSpan NvidiaIdrRecoveryRefreshTimeout = TimeSpan.FromSeconds(12);
        private static readonly TimeSpan EncoderStarvationRefreshThrottle = TimeSpan.FromSeconds(3);
        private static readonly TimeSpan IntelSystemMemoryEncoderFallbackDelay = TimeSpan.FromSeconds(3);
        // A short scheduling gap is normal while WGC and an asynchronous GPU encoder
        // contend for the D3D device. Restarting WGC after only two seconds tears down
        // the live encoder and creates the very freeze the recovery path is intended
        // to cure. Keep encoding the last good surface through transient gaps and only
        // restart after a sustained capture outage.
        private static readonly TimeSpan FreshCaptureStallThreshold = TimeSpan.FromSeconds(5);
        private static readonly TimeSpan FreshCaptureRestartCooldown = TimeSpan.FromSeconds(6);
        private static readonly TimeSpan LowFreshCaptureWarmup = TimeSpan.FromSeconds(10);
        private const double LowFreshCaptureFpsThresholdScale = 0.80;
        private const double LowFreshCaptureEncodedFpsThresholdScale = 0.90;
        private const int LowFreshCaptureWindowsBeforeRestart = 3;
        private const double AdaptiveFpsPressureThreshold = 0.90;
        private const double AdaptiveSevereFpsPressureThreshold = 0.82;
        private const int ReceiverPressureSignalsBeforeResolutionDrop = 2;

        private readonly object _qualitySync = new();
        private CancellationTokenSource? _cts;
        private Task? _captureTask;
        private ScreenShareQualityPreset _qualityPreset = ScreenShareQualityPreset.Hd720p;
        private ScreenShareQualityPreset _effectiveQualityPreset = ScreenShareQualityPreset.Hd720p;
        private int _bitrateScalePercent = 100;
        private int? _bitrateOverride;
        private int? _targetFpsOverride;
        private int _emptyEncodeCount;
        private bool _nonNvidia1080pHardwareLimitActive;
        private WindowsGraphicsCaptureScreenSource? _wgcCapture;
        private DxgiScreenCaptureService? _dxgiCapture;
        private DateTimeOffset _streamStartedAtUtc;
        private DateTimeOffset _adaptationWarmupUntilUtc;
        private DateTimeOffset _lastAdaptedAtUtc = DateTimeOffset.MinValue;
        private DateTimeOffset _lastEmptyEncodeLogUtc = DateTimeOffset.MinValue;
        private DateTimeOffset _receiverPressurePacingUntilUtc = DateTimeOffset.MinValue;
        private DateTimeOffset _lastReceiverPacingLogUtc = DateTimeOffset.MinValue;
        private DateTimeOffset _lastReceiverPressureKeyFrameQueuedUtc = DateTimeOffset.MinValue;
        private DateTimeOffset _lastRecoveryKeyFrameQueuedUtc = DateTimeOffset.MinValue;
        private DateTimeOffset _lastEncoderStarvationRefreshUtc = DateTimeOffset.MinValue;
        private int _healthyWindows;
        private int _pendingRecoveryKeyFrame;
        private int _pendingEncoderRefresh;
        private int _receiverPressureSignals;
        private DateTimeOffset _lastH264StutterDiagnosticLogUtc = DateTimeOffset.MinValue;
        private DateTimeOffset _lastH264OutputAtUtc = DateTimeOffset.MinValue;
        private long _h264EncodedFrameCount;
        private long _h264EmptyPollCount;
        private long _h264OutputGapCount;

        public bool IsRunning { get; private set; }
        public ScreenShareQualityPreset QualityPreset => _qualityPreset;
        public ScreenShareQualityProfile RequestedQuality => ScreenShareQualityProfile.FromPreset(_qualityPreset);
        public ScreenShareQualityProfile CurrentQuality
        {
            get
            {
                lock (_qualitySync)
                {
                    return ScreenShareQualityProfile.FromPreset(_effectiveQualityPreset);
                }
            }
        }

        public bool IsAdaptiveLatencyModeEnabled { get; private set; } = false;
        public int CurrentBitrate { get; private set; } = ScreenShareQualityProfile.FromPreset(ScreenShareQualityPreset.Hd720p).Bitrate;
        public int AutoDowngradeCount { get; private set; }
        public int CongestionSignals { get; private set; }
        public string AdaptiveState { get; private set; } = "Locked realtime mode ready";
        public int CurrentTargetFps
        {
            get
            {
                lock (_qualitySync)
                {
                    return Math.Clamp(_targetFpsOverride ?? TargetFps, 1, TargetFps);
                }
            }
        }

        public double CaptureFps { get; private set; }
        public double EncodedFps { get; private set; }
        public double LastCaptureMilliseconds { get; private set; }
        public double LastEncodeMilliseconds { get; private set; }
        public double LastLoopMilliseconds { get; private set; }
        public double LastPreviewMilliseconds { get; private set; }
        public string TransportPipeline { get; private set; } = "WebRTC RTP AV1X/H.264 media track";
        public string EncoderMode { get; private set; } = "Not started";
        public string EncoderInputFormat { get; private set; } = "Unknown";
        public string EncoderGpuDeviceMode { get; private set; } = "Not attached";
        public bool RequireHardwareEncoder { get; set; } = true;
        public bool RequireDirectX12CapturePath { get; set; } = true;
        public bool EnablePreviewFrames { get; set; } = true;
        public bool PublishPreviewOnlyFrames { get; set; }
        public bool RequiresRealtimeBitmapFrames { get; private set; }
        public ScreenShareVideoCodec PreferredVideoCodec { get; set; } = ScreenShareVideoCodec.Auto;
        public ScreenShareH264EncoderFamily PreferredH264EncoderFamily { get; set; } = ScreenShareH264EncoderFamily.Nvidia;
        public string ActiveVideoCodec { get; private set; } = ScreenShareCodecNames.H264;
        public bool PrioritizeStreamingPerformance { get; set; }
        public bool DropLateDuplicateFrames { get; set; }
        public NativeCaptureSourceMode PreferredCaptureSourceMode { get; set; } = NativeCaptureSourceMode.Desktop;
        public int RecoveryKeyFrameInterval { get; private set; }
        public bool EncoderRealtimeModeEnabled { get; private set; }
        public bool EncoderLowLatencyOutputEnabled { get; private set; }
        public int EncoderPendingHardwareInputs { get; private set; }
        public int EncoderHardwareInputRequests { get; private set; }
        public int EncoderHardwareOutputRequests { get; private set; }
        public bool EncoderUsesHardwareEventPump { get; private set; }
        public int RecoveryKeyFrameRequests { get; private set; }
        public int HardwareEncoderFallbackCount { get; private set; }
        public string? LastFailureMessage { get; private set; }

        public ScreenShareVideoCodec SelectedVideoCodec
        {
            get
            {
                if (PreferredVideoCodec != ScreenShareVideoCodec.Auto)
                    return PreferredVideoCodec;

                // H.264 is the WebRTC compatibility baseline. In particular,
                // Snapdragon receivers may expose Qualcomm H.264 decode but no
                // usable AV1 output transform.
                return ScreenShareVideoCodec.H264;
            }
        }

        public string SelectedCodecName => SelectedVideoCodec == ScreenShareVideoCodec.AV1X
            ? ScreenShareCodecNames.Av1
            : ScreenShareCodecNames.H264;

        public event EventHandler<NativeScreenFrameEventArgs>? FrameReady;
        public event EventHandler<string>? StreamingFailed;

        internal ScreenShareCapturePipeline(ScreenShareQualityPreset preset)
        {
            _qualityPreset = preset;
            _effectiveQualityPreset = preset;
            var profile = ScreenShareQualityProfile.FromPreset(preset);
            _targetFpsOverride = profile.TargetFps;
            CurrentBitrate = profile.Bitrate;
        }

        public async Task StartAsync()
        {
            if (IsRunning)
                return;

            var sessionLogPath = DiagnosticLogService.StartScreenShareSession("local sender start");
            _cts = new CancellationTokenSource();
            IsRunning = true;
            LastFailureMessage = null;
            _streamStartedAtUtc = DateTimeOffset.UtcNow;
            _adaptationWarmupUntilUtc = _streamStartedAtUtc + AdaptationWarmup;
            lock (_qualitySync)
            {
                _effectiveQualityPreset = _qualityPreset;
                _bitrateScalePercent = 100;
                _nonNvidia1080pHardwareLimitActive = false;
                CurrentBitrate = GetConfiguredBitrate(ScreenShareQualityProfile.FromPreset(_effectiveQualityPreset));
                AdaptiveState = $"Locked {ScreenShareQualityProfile.FromPreset(_effectiveQualityPreset).Name} @ {CurrentTargetFps} FPS";
                AutoDowngradeCount = 0;
                CongestionSignals = 0;
                _receiverPressureSignals = 0;
                _healthyWindows = 0;
                _lastAdaptedAtUtc = DateTimeOffset.MinValue;
                _receiverPressurePacingUntilUtc = DateTimeOffset.MinValue;
                _lastReceiverPacingLogUtc = DateTimeOffset.MinValue;
                _lastReceiverPressureKeyFrameQueuedUtc = DateTimeOffset.MinValue;
                _lastRecoveryKeyFrameQueuedUtc = _streamStartedAtUtc;
                EncoderMode = "Starting";
                EncoderInputFormat = "Unknown";
                EncoderGpuDeviceMode = RequireHardwareEncoder
                    ? "DirectX 12 GPU hardware required; no software fallback"
                    : "GPU preferred with software fallback";
                RecoveryKeyFrameInterval = 0;
                EncoderRealtimeModeEnabled = false;
                EncoderLowLatencyOutputEnabled = false;
                EncoderPendingHardwareInputs = 0;
                EncoderHardwareInputRequests = 0;
                EncoderHardwareOutputRequests = 0;
                EncoderUsesHardwareEventPump = false;
                RequiresRealtimeBitmapFrames = false;
                RecoveryKeyFrameRequests = 0;
                HardwareEncoderFallbackCount = 0;
                _lastEmptyEncodeLogUtc = DateTimeOffset.MinValue;
                _lastEncoderStarvationRefreshUtc = DateTimeOffset.MinValue;
                _emptyEncodeCount = 0;
                _lastH264StutterDiagnosticLogUtc = DateTimeOffset.MinValue;
                _lastH264OutputAtUtc = DateTimeOffset.MinValue;
                _h264EncodedFrameCount = 0;
                _h264EmptyPollCount = 0;
                _h264OutputGapCount = 0;
                Interlocked.Exchange(ref _pendingRecoveryKeyFrame, 0);
                Interlocked.Exchange(ref _pendingEncoderRefresh, 0);
            }

            DiagnosticLogService.WriteLine("[ScreenShare:UI] Starting Windows Graphics Capture source.");
            DiagnosticLogService.WriteLine($"[ScreenShare:SESSION] sessionLog={sessionLogPath}; role=sender; until=screen-share-ended");
            DiagnosticLogService.WriteLine($"[ScreenShare:CAPS] processArch={RuntimeInformation.ProcessArchitecture}; osArch={RuntimeInformation.OSArchitecture}; selectedCodec={SelectedCodecName}; preferredCodec={PreferredVideoCodec}; hardwareRequired={RequireHardwareEncoder}; directX12Required={RequireDirectX12CapturePath}");
            ScreenShareTestLogService.WriteLine($"start codec={SelectedCodecName}; preferredCodec={PreferredVideoCodec}; quality={CurrentQuality.Name}; targetFps={CurrentTargetFps}; bitrate={CurrentBitrate}; requireHardware={RequireHardwareEncoder}; requireDx12={RequireDirectX12CapturePath}");
            DiagnosticLogService.Flush();
            _wgcCapture = new WindowsGraphicsCaptureScreenSource();
            var wgcStarted = await _wgcCapture.StartAsync();
            DiagnosticLogService.WriteLine($"[ScreenShare:UI] Windows Graphics Capture source start result: {wgcStarted}; available={_wgcCapture.IsAvailable}.");
                ScreenShareTestLogService.WriteLine($"capture-start result={wgcStarted}; available={_wgcCapture.IsAvailable}");
            if (!wgcStarted)
            {
                IsRunning = false;
                EncoderMode = "Capture unavailable";
                EncoderInputFormat = "Unknown";
                EncoderGpuDeviceMode = "Windows Graphics Capture did not start";
                LastFailureMessage = "Could not start Windows Graphics Capture. Choose a different window or screen and try again.";
                _cts?.Dispose();
                _cts = null;
                _wgcCapture?.Dispose();
                _wgcCapture = null;
                StreamingFailed?.Invoke(this, LastFailureMessage);
                DiagnosticLogService.Flush();
                DiagnosticLogService.StopScreenShareSession("local sender capture start failed");
                return;
            }

            WriteGpuStreamDiagnostics("start");
            DiagnosticLogService.Flush();
            _captureTask = Task.Factory
                .StartNew(
                    () => CaptureLoopAsync(_cts.Token),
                    _cts.Token,
                    TaskCreationOptions.LongRunning,
                    TaskScheduler.Default)
                .Unwrap();
        }

        public void SetQuality(ScreenShareQualityPreset preset)
        {
            lock (_qualitySync)
            {
                var profile = ScreenShareQualityProfile.FromPreset(preset);
                _qualityPreset = preset;
                _effectiveQualityPreset = preset;
                _targetFpsOverride = profile.TargetFps;
                _bitrateScalePercent = 100;
                _nonNvidia1080pHardwareLimitActive = false;
                CurrentBitrate = GetConfiguredBitrate(ScreenShareQualityProfile.FromPreset(_effectiveQualityPreset));
                AdaptiveState = $"Locked {profile.Name} @ {CurrentTargetFps} FPS";
                _healthyWindows = 0;
                _receiverPressureSignals = 0;
                // Encoder recreation has a large one-off setup sample (typically
                // 100-150 ms).  Do not treat that sample as sustained pressure and
                // immediately undo the quality change.
                _adaptationWarmupUntilUtc = DateTimeOffset.UtcNow + AdaptationWarmup;
                if (IsRunning)
                    RequestRecoveryKeyFrame($"screen-share quality changed to {ScreenShareQualityProfile.FromPreset(preset).Name}");
            }
        }

        public void SetBitrateOverride(int? bitrate)
        {
            lock (_qualitySync)
            {
                _bitrateOverride = bitrate is > 0 ? bitrate : null;
                CurrentBitrate = GetConfiguredBitrate(ScreenShareQualityProfile.FromPreset(_effectiveQualityPreset));
                _bitrateScalePercent = 100;
                if (IsRunning)
                    RequestRecoveryKeyFrame("streaming bitrate changed");
            }
        }

        public void SetTargetFpsOverride(int? fps)
        {
            lock (_qualitySync)
            {
                _targetFpsOverride = fps.HasValue
                    ? Math.Clamp(fps.Value, 1, TargetFps)
                    : null;
                _healthyWindows = 0;
                _receiverPressureSignals = 0;
                _lastAdaptedAtUtc = DateTimeOffset.MinValue;
                AdaptiveState = IsAdaptiveLatencyModeEnabled
                    ? $"Adaptive realtime {CurrentQuality.Name} @ {CurrentTargetFps} FPS"
                    : $"Locked {CurrentQuality.Name} @ {CurrentTargetFps} FPS";
                if (IsRunning)
                    RequestEncoderRefresh($"target FPS changed to {CurrentTargetFps}");
            }
        }

        public void SetAdaptiveLatencyMode(bool enabled)
        {
            lock (_qualitySync)
            {
                IsAdaptiveLatencyModeEnabled = enabled;
                _healthyWindows = 0;
                _receiverPressureSignals = 0;
                _lastAdaptedAtUtc = DateTimeOffset.MinValue;
                _receiverPressurePacingUntilUtc = DateTimeOffset.MinValue;
                AdaptiveState = enabled
                    ? $"Adaptive realtime {CurrentQuality.Name} @ {CurrentTargetFps} FPS"
                    : $"Locked {CurrentQuality.Name} @ {CurrentTargetFps} FPS";
            }
        }

        public void ReportSendCongestion(string reason, int droppedReceiveFrames = 0, int renderBacklog = 0)
        {
            CongestionSignals++;
            var recoveryReason = IsReceiverRecoveryReason(reason);
            var startupKeyFrameRequest = IsStartupKeyFrameRequest(reason);
            var receiverPressure =
                (!startupKeyFrameRequest && recoveryReason) ||
                droppedReceiveFrames >= TargetFps ||
                renderBacklog > 0;
            var receiverPressureSignals = receiverPressure
                ? Interlocked.Increment(ref _receiverPressureSignals)
                : Volatile.Read(ref _receiverPressureSignals);

            if (recoveryReason)
                RequestRecoveryKeyFrame(reason);

            if (receiverPressure && IsAdaptiveLatencyModeEnabled)
            {
                _receiverPressurePacingUntilUtc = DateTimeOffset.UtcNow + ReceiverPressurePacingWindow;
                var now = DateTimeOffset.UtcNow;
                if (now - _lastReceiverPacingLogUtc >= TimeSpan.FromSeconds(2))
                {
                    _lastReceiverPacingLogUtc = now;
                    var pacingFps = CurrentQuality.Height >= 1080
                        ? ReceiverSafe1080pFps
                        : ReceiverSafeRealtimeFps;
                    Debug.WriteLine(
                        $"[ScreenShare:H264] Receiver decode pressure active; pacing capture/encode to {pacingFps}fps at {CurrentQuality.Name} without dropping encoded H.264 frames. reason={reason}; dropped={droppedReceiveFrames}; renderBacklog={renderBacklog}.");
                    Debug.WriteLine(
                        $"[ScreenShare:H264] Receiver-safe 30fps cadence will stay active for {ReceiverPressurePacingWindow.TotalSeconds:0}s after the latest pressure signal.");
                }
            }
            else if (!IsAdaptiveLatencyModeEnabled)
            {
                _receiverPressurePacingUntilUtc = DateTimeOffset.MinValue;
            }

            if (IsAdaptiveLatencyModeEnabled)
            {
                var severe =
                    receiverPressure &&
                    (receiverPressureSignals >= ReceiverPressureSignalsBeforeResolutionDrop ||
                     droppedReceiveFrames >= 60 ||
                     renderBacklog > 0);
                ApplyAdaptivePressure(
                    $"{reason}; receiverDropped={droppedReceiveFrames}; renderBacklog={renderBacklog}",
                    severe,
                    receiverPressure);
                return;
            }

            AdaptiveState = IsAdaptiveLatencyModeEnabled && DateTimeOffset.UtcNow < _receiverPressurePacingUntilUtc
                ? $"Locked {CurrentQuality.Name} receiver-safe @ {ReceiverSafe1080pFps} FPS"
                : $"Locked {CurrentQuality.Name} @ {CurrentTargetFps} FPS";
        }

        public void RequestRecoveryKeyFrame(string reason)
        {
            if (!IsRunning)
                return;

            var now = DateTimeOffset.UtcNow;
            // The encoder can produce its startup IDR before WebRTC has finished
            // connecting.  That IDR is useful to the JPEG fallback, but it never
            // reaches the RTP receiver.  A newly-connected transport therefore
            // needs a fresh IDR immediately; applying the normal warm-up throttle
            // here leaves the receiver decoding dependent P-frames for seconds.
            var bypassThrottle = IsQualityChangeReason(reason) ||
                IsTransportConnectedKeyFrameRequest(reason);
            var startupKeyFrameRequest = IsStartupKeyFrameRequest(reason);
            if (!bypassThrottle)
            {
                var throttle = startupKeyFrameRequest && now - _streamStartedAtUtc < TimeSpan.FromSeconds(8)
                    ? StartupRecoveryKeyFrameThrottle
                    : RecoveryKeyFrameThrottle;

                if (now - _lastRecoveryKeyFrameQueuedUtc < throttle)
                {
                    Debug.WriteLine($"[ScreenShare:H264] Recovery keyframe request throttled during warmup/pacing: {reason}");
                    return;
                }
            }

            if (IsReceiverPlaybackPressureReason(reason))
            {
                if (now - _lastReceiverPressureKeyFrameQueuedUtc < TimeSpan.FromSeconds(3))
                {
                    Debug.WriteLine($"[ScreenShare:H264] Receiver pressure keyframe request throttled to avoid repeated hardware encoder restarts: {reason}");
                    return;
                }

                _lastReceiverPressureKeyFrameQueuedUtc = now;
            }

            _lastRecoveryKeyFrameQueuedUtc = now;
            RecoveryKeyFrameRequests++;
            Interlocked.Exchange(ref _pendingRecoveryKeyFrame, 1);
            Debug.WriteLine($"[ScreenShare:H264] Recovery keyframe queued: {reason}");
        }

        public void RequestEncoderRefresh(string reason)
        {
            if (!IsRunning)
                return;

            Interlocked.Exchange(ref _pendingEncoderRefresh, 1);
            Debug.WriteLine($"[ScreenShare:H264] Encoder refresh queued: {reason}");
        }

        public async Task StopAsync()
        {
            if (!IsRunning)
                return;

            Debug.WriteLine("[ScreenShare:H264] Stop requested.");
            ScreenShareTestLogService.WriteLine($"stop activeCodec={ActiveVideoCodec}; captureFps={CaptureFps:0.0}; encodedFps={EncodedFps:0.0}; lastFailure={LastFailureMessage ?? "none"}");
            DiagnosticLogService.Flush();
            IsRunning = false;

            try
            {
                _cts?.Cancel();

                if (_captureTask != null)
                    await _captureTask;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ScreenShare:H264] Stop capture task failed: {ex}");
            }
            finally
            {
                _captureTask = null;
                _cts?.Dispose();
                _cts = null;
                Debug.WriteLine("[ScreenShare:H264] Stop completed.");
                DiagnosticLogService.Flush();
            }
        }

        private async Task CaptureLoopAsync(CancellationToken cancellationToken)
        {
            var mmcssHandle = IntPtr.Zero;
            try
            {
                Thread.CurrentThread.Priority = ThreadPriority.Highest;
                mmcssHandle = TryEnableStreamingThreadScheduling();
            }
            catch
            {
            }

            var highResolutionTimerEnabled = TryBeginHighResolutionTimer();
            var outputClock = Stopwatch.StartNew();
            var nextFrameDueTicks = outputClock.ElapsedTicks;
                IH264VideoEncoder? encoder = null;
            MediaFoundationAv1Encoder? av1Encoder = null;
            ScreenShareQualityProfile? encoderQuality = null;
            ScreenShareQualityProfile? av1EncoderQuality = null;
            var encoderBitrate = 0;
            var av1EncoderBitrate = 0;
            var encoderFrameRate = 0;
            var av1EncoderFrameRate = 0;
            var av1Unavailable = false;
            byte[]? latestPreview = null;
            long latestPreviewTimestampMs = 0;
            var previewFrameInterval = GetPreviewFrameInterval(CurrentQuality);
            var captureFrameIndex = 0;
            var statsWindowStartedAt = DateTimeOffset.UtcNow;
            var capturedInWindow = 0;
            var encodedInWindow = 0;
            var encodedFramesSinceLastIdr = 0;
            var lastIdrOutputAtUtc = DateTimeOffset.MinValue;
            var lastPeriodicIdrRequestAtUtc = DateTimeOffset.MinValue;
            var lastFrameEventTimestampMs = -1L;
            var lastEncodedSampleTimestampMs = -1L;
            var nextSyntheticVideoTimestampMs = 0L;
            var preferHardwareEncoder = true;
            var forceSoftwareH264Encoder = false;
            var intelSystemMemoryNoOutputStartedAtUtc = DateTimeOffset.MinValue;
            var missingGpuFramePolls = 0;
            CapturedGpuFrame? latestReusableGpuFrame = null;
            var lastFreshGpuFrameAtUtc = DateTimeOffset.UtcNow;
            var lastWgcRestartAtUtc = DateTimeOffset.MinValue;
            var lowFreshCaptureWindows = 0;

            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    var frameStartedAt = DateTimeOffset.UtcNow;
                    var effectiveTargetFps = GetEffectiveTargetFps();
                    var frameBudgetTicks = Math.Max(1L, Stopwatch.Frequency / effectiveTargetFps);

                    try
                    {
                        var quality = CurrentQuality;
                        var bitrate = CurrentBitrate;
                        var encoderResolutionChanged =
                            encoder == null ||
                            encoderQuality == null ||
                            encoderQuality.Width != quality.Width ||
                            encoderQuality.Height != quality.Height;
                        var encoderBitrateChanged = encoder != null && encoderBitrate != bitrate;
                        var encoderFrameRateChanged = encoder != null && encoderFrameRate != effectiveTargetFps;
                        var recoveryKeyFrameRequested = Interlocked.Exchange(ref _pendingRecoveryKeyFrame, 0) == 1;
                        var encoderRefreshRequested = Interlocked.Exchange(ref _pendingEncoderRefresh, 0) == 1;

                        if (SelectedVideoCodec == ScreenShareVideoCodec.AV1X && !av1Unavailable)
                        {
                            var av1ResolutionChanged =
                                av1Encoder == null ||
                                av1EncoderQuality == null ||
                                av1EncoderQuality.Width != quality.Width ||
                                av1EncoderQuality.Height != quality.Height;
                            var av1BitrateChanged = av1Encoder != null && av1EncoderBitrate != bitrate;
                            var av1FrameRateChanged = av1Encoder != null && av1EncoderFrameRate != effectiveTargetFps;

                            if (av1ResolutionChanged || av1BitrateChanged || av1FrameRateChanged || encoderRefreshRequested)
                            {
                                av1Encoder?.Dispose();
                                av1Encoder = null;
                                av1EncoderQuality = quality;
                                av1EncoderBitrate = bitrate;
                                av1EncoderFrameRate = 0;
                            }

                            if (av1Encoder == null)
                            {
                                try
                                {
                                    av1Encoder = new MediaFoundationAv1Encoder(quality.Width, quality.Height, bitrate, effectiveTargetFps);
                                    av1EncoderBitrate = bitrate;
                                    av1EncoderFrameRate = effectiveTargetFps;
                                    av1Encoder.ForceNextKeyFrame();
                                    ActiveVideoCodec = ScreenShareCodecNames.Av1;
                                    EncoderMode = av1Encoder.EncoderMode;
                                    EncoderInputFormat = av1Encoder.InputFormat;
                                    EncoderGpuDeviceMode = av1Encoder.IsHardwareAccelerated
                                        ? "Windows AV1X hardware Media Foundation MFT"
                                        : "Windows AV1X software Media Foundation MFT";
                                     TransportPipeline = "WebRTC RTP AV1X media track";
                                     Debug.WriteLine($"[ScreenShare:AV1X] Encoder created for {quality.Width}x{quality.Height}; automatic codec fallback disabled.");
                                }
                                catch (Exception ex)
                                {
                                    av1Encoder?.Dispose();
                                    av1Encoder = null;
                                     LastFailureMessage = $"AV1 screen sharing could not start: {ex.Message}";
                                     Debug.WriteLine($"[ScreenShare:AV1X] Windows AV1X encoder unavailable; automatic codec fallback disabled. {ex.Message}");
                                     DiagnosticLogService.WriteLine($"[ScreenShare:AV1X] Windows AV1X encoder unavailable; automatic codec fallback disabled. {ex}");
                                     ScreenShareTestLogService.WriteLine($"av1-start-failed fallback=false error={ex.Message}");
                                     throw new InvalidOperationException(LastFailureMessage, ex);
                                }
                            }

                            if (av1Encoder != null)
                            {
                                if (recoveryKeyFrameRequested)
                                    av1Encoder.ForceNextKeyFrame();

                                previewFrameInterval = GetPreviewFrameInterval(quality);
                                var av1CaptureStartedAt = DateTimeOffset.UtcNow;
                                using var bitmap = CaptureBitmapWithBestAvailablePath(quality);
                                LastCaptureMilliseconds = (DateTimeOffset.UtcNow - av1CaptureStartedAt).TotalMilliseconds;
                                if (bitmap == null)
                                {
                                    LastLoopMilliseconds = (DateTimeOffset.UtcNow - frameStartedAt).TotalMilliseconds;
                                    nextFrameDueTicks += frameBudgetTicks;
                                    await WaitForNextOutputFrameAsync(outputClock, nextFrameDueTicks, frameBudgetTicks, cancellationToken);
                                    continue;
                                }

                                capturedInWindow++;

                                if (EnablePreviewFrames &&
                                    ShouldGeneratePreview(quality, latestPreview, captureFrameIndex, previewFrameInterval))
                                {
                                    var previewStartedAt = DateTimeOffset.UtcNow;
                                    latestPreview = EncodePreviewJpeg(bitmap, quality, PrioritizeStreamingPerformance);
                                    latestPreviewTimestampMs = previewStartedAt.ToUnixTimeMilliseconds();
                                    LastPreviewMilliseconds = (DateTimeOffset.UtcNow - previewStartedAt).TotalMilliseconds;
                                }

                                captureFrameIndex++;

                                var av1EncodeStartedAt = DateTimeOffset.UtcNow;
                                IReadOnlyList<EncodedVideoFrame> av1Frames;
                                try
                                {
                                    av1Frames = av1Encoder.Encode(bitmap);
                                }
                                catch (Exception ex)
                                {
                                    av1Encoder.Dispose();
                                    av1Encoder = null;
                                     LastFailureMessage = $"AV1 screen sharing failed during encoding: {ex.Message}";
                                     Debug.WriteLine($"[ScreenShare:AV1X] AV1X encode failed; automatic codec fallback disabled. {ex.Message}");
                                     DiagnosticLogService.WriteLine($"[ScreenShare:AV1X] AV1X encode failed; automatic codec fallback disabled. {ex}");
                                     ScreenShareTestLogService.WriteLine($"av1-encode-failed fallback=false error={ex.Message}");
                                     throw new InvalidOperationException(LastFailureMessage, ex);
                                }
                                LastEncodeMilliseconds = (DateTimeOffset.UtcNow - av1EncodeStartedAt).TotalMilliseconds;
                                encodedInWindow += av1Frames.Count;

                                foreach (var encodedFrame in av1Frames)
                                {
                                    var frameEventTimestampMs = GetEncodedFrameEventTimestampMilliseconds(
                                        _streamStartedAtUtc,
                                        encodedFrame.TimestampMilliseconds,
                                        effectiveTargetFps,
                                        ref lastEncodedSampleTimestampMs,
                                        ref nextSyntheticVideoTimestampMs,
                                        ref lastFrameEventTimestampMs);

                                    FrameReady?.Invoke(this, new NativeScreenFrameEventArgs(
                                        encodedFrame.Data,
                                        quality.Width,
                                        quality.Height,
                                        quality.Name,
                                        frameEventTimestampMs,
                                        encodedFrame.Codec,
                                        encodedFrame.IsKeyFrame,
                                        latestPreview,
                                        latestPreviewTimestampMs));
                                }

                                var av1Now = DateTimeOffset.UtcNow;
                                var av1StatsElapsed = av1Now - statsWindowStartedAt;
                                if (av1StatsElapsed >= TimeSpan.FromSeconds(1))
                                {
                                    var seconds = Math.Max(0.001, av1StatsElapsed.TotalSeconds);
                                    CaptureFps = capturedInWindow / seconds;
                                    EncodedFps = encodedInWindow / seconds;
                                    capturedInWindow = 0;
                                    encodedInWindow = 0;
                                    statsWindowStartedAt = av1Now;
                                    Debug.WriteLine($"[ScreenShare:AV1X:STATS] capture={CaptureFps:0.0}fps encoded={EncodedFps:0.0}fps captureMs={LastCaptureMilliseconds:0.0} encodeMs={LastEncodeMilliseconds:0.0} previewMs={LastPreviewMilliseconds:0.0} loopMs={LastLoopMilliseconds:0.0}");
                                     DiagnosticLogService.WriteLine($"[ScreenShare:STATS] codec=av1x capture={CaptureFps:0.0}fps encoded={EncodedFps:0.0}fps captureMs={LastCaptureMilliseconds:0.0} encodeMs={LastEncodeMilliseconds:0.0} previewMs={LastPreviewMilliseconds:0.0} loopMs={LastLoopMilliseconds:0.0} emptyPolls={_emptyEncodeCount}");
                                    ScreenShareTestLogService.WriteLine($"stats codec=av1x captureFps={CaptureFps:0.0}; encodedFps={EncodedFps:0.0}; captureMs={LastCaptureMilliseconds:0.0}; encodeMs={LastEncodeMilliseconds:0.0}; previewMs={LastPreviewMilliseconds:0.0}; loopMs={LastLoopMilliseconds:0.0}");
                                    UpdateAdaptiveState();
                                }

                                LastLoopMilliseconds = (DateTimeOffset.UtcNow - frameStartedAt).TotalMilliseconds;
                                nextFrameDueTicks += frameBudgetTicks;
                                await WaitForNextOutputFrameAsync(outputClock, nextFrameDueTicks, frameBudgetTicks, cancellationToken);
                                continue;
                            }
                        }

                        // Recreating an asynchronous hardware MFT solely to apply a bitrate
                        // adjustment stops its event pump while frames are live. NVENC takes
                        // several hundred milliseconds to shut down/start again, which creates
                        // a visible freeze and also races pending Transform events with
                        // Shutdown(). Keep the active encoder for bitrate-only adaptation; the
                        // latest target will be used the next time a structural refresh is
                        // genuinely required (resolution, frame rate, or explicit refresh).
                        if (encoderBitrateChanged &&
                            !encoderResolutionChanged &&
                            !encoderFrameRateChanged &&
                            !encoderRefreshRequested)
                        {
                            Debug.WriteLine(
                                $"[ScreenShare:H264] Bitrate target changed {encoderBitrate} -> {bitrate}; deferring hardware application until the next structural encoder refresh to keep live video continuous.");
                            encoderBitrate = bitrate;
                            encoderBitrateChanged = false;
                        }

                        if (encoderResolutionChanged || encoderFrameRateChanged || encoderRefreshRequested)
                        {
                            if (encoderFrameRateChanged)
                                Debug.WriteLine($"[ScreenShare:H264] Target FPS changed {encoderFrameRate} -> {effectiveTargetFps}; recreating encoder so timestamps and GOP cadence match output.");
                            if (encoderRefreshRequested)
                                Debug.WriteLine("[ScreenShare:H264] Recreating encoder for a fresh stream keyframe boundary.");

                            encoder?.Dispose();
                            encoder = null;
                            encoderQuality = quality;
                            encoderBitrate = bitrate;
                            encoderFrameRate = 0;
                            missingGpuFramePolls = 0;
                            latestReusableGpuFrame?.Dispose();
                            latestReusableGpuFrame = null;
                            encodedFramesSinceLastIdr = 0;
                            lastIdrOutputAtUtc = DateTimeOffset.MinValue;
                            lastPeriodicIdrRequestAtUtc = DateTimeOffset.MinValue;
                        }

                        if (recoveryKeyFrameRequested)
                        {
                            if (encoder != null)
                            {
                                var recoveryNow = DateTimeOffset.UtcNow;
                                if (IsNvidiaEncoder(encoder))
                                {
                                    Debug.WriteLine("[ScreenShare:H264] Recovery keyframe requested; forcing a GPU keyframe without recreating the encoder.");
                                    encoder.ForceNextKeyFrame();
                                    lastPeriodicIdrRequestAtUtc = recoveryNow;
                                }
                                else
                                {
                                    var idrRequestAge = lastPeriodicIdrRequestAtUtc == DateTimeOffset.MinValue
                                        ? TimeSpan.MaxValue
                                        : recoveryNow - lastPeriodicIdrRequestAtUtc;
                                    if (idrRequestAge >= TimeSpan.FromSeconds(2))
                                    {
                                        Debug.WriteLine(
                                            $"[ScreenShare:H264] Recovery keyframe requested; forcing a spaced non-NVIDIA GPU keyframe. encoder='{encoder.EncoderMode}'; ageMs={idrRequestAge.TotalMilliseconds:0}.");
                                        DiagnosticLogService.WriteLine(
                                            $"[ScreenShare:H264] Spaced non-NVIDIA recovery keyframe requested for Twitch cadence. encoder='{encoder.EncoderMode}'; ageMs={idrRequestAge.TotalMilliseconds:0}.");
                                        encoder.ForceNextKeyFrame();
                                        lastPeriodicIdrRequestAtUtc = recoveryNow;
                                    }
                                    else
                                    {
                                        Debug.WriteLine(
                                            $"[ScreenShare:H264] Recovery keyframe request throttled for non-NVIDIA encoder. encoder='{encoder.EncoderMode}'; ageMs={idrRequestAge.TotalMilliseconds:0}.");
                                    }
                                }
                            }
                            else
                            {
                                Debug.WriteLine("[ScreenShare:H264] Recovery keyframe requested while encoder is starting; next encoder output should begin with an IDR.");
                            }
                        }

                        if (encoder == null)
                        {
                            encoder = CreateEncoderWithFallback(
                                quality,
                                bitrate,
                                effectiveTargetFps,
                                preferHardware: preferHardwareEncoder && !forceSoftwareH264Encoder,
                                requireHardware: RequireHardwareEncoder && !forceSoftwareH264Encoder);
                            encoderBitrate = bitrate;
                            encoderFrameRate = effectiveTargetFps;
                            encoder.ForceNextKeyFrame();
                            ActiveVideoCodec = ScreenShareCodecNames.H264;
                            TransportPipeline = "WebRTC RTP H.264 media track";
                            Debug.WriteLine($"[ScreenShare:H264] Encoder created for {quality.Width}x{quality.Height}; forcing first GPU output to IDR.");
                            ApplyEncoderDetails(encoder);
                            TryApplyNonNvidia1080pFpsLimit(encoder, quality, effectiveTargetFps);
                            WriteGpuStreamDiagnostics("encoder-created");
                        }

                        RequiresRealtimeBitmapFrames = !encoder.CanEncodeGpuTexture;
                        previewFrameInterval = GetPreviewFrameInterval(quality);
                        if (PrioritizeStreamingPerformance)
                            previewFrameInterval = Math.Max(previewFrameInterval, Math.Max(1, effectiveTargetFps / 12));

                        if (!encoder.CanEncodeGpuTexture)
                        {
                            RequiresRealtimeBitmapFrames = true;
                            var bitmapCaptureStartedAt = DateTimeOffset.UtcNow;
                            using var bitmapFrame = CaptureBitmapWithBestAvailablePath(quality);
                            LastCaptureMilliseconds = (DateTimeOffset.UtcNow - bitmapCaptureStartedAt).TotalMilliseconds;
                            if (bitmapFrame == null)
                            {
                                missingGpuFramePolls++;
                                LastLoopMilliseconds = (DateTimeOffset.UtcNow - frameStartedAt).TotalMilliseconds;
                                nextFrameDueTicks += frameBudgetTicks;
                                await WaitForNextOutputFrameAsync(outputClock, nextFrameDueTicks, frameBudgetTicks, cancellationToken);
                                continue;
                            }

                            missingGpuFramePolls = 0;
                            capturedInWindow++;

                            if (EnablePreviewFrames &&
                                ShouldGeneratePreview(quality, latestPreview, captureFrameIndex, previewFrameInterval))
                            {
                                var previewStartedAt = DateTimeOffset.UtcNow;
                                latestPreview = EncodePreviewJpeg(bitmapFrame, quality, PrioritizeStreamingPerformance);
                                latestPreviewTimestampMs = previewStartedAt.ToUnixTimeMilliseconds();
                                LastPreviewMilliseconds = (DateTimeOffset.UtcNow - previewStartedAt).TotalMilliseconds;

                                if (PublishPreviewOnlyFrames)
                                {
                                    FrameReady?.Invoke(this, new NativeScreenFrameEventArgs(
                                        Array.Empty<byte>(),
                                        quality.Width,
                                        quality.Height,
                                        quality.Name,
                                        Math.Max(0L, (long)outputClock.Elapsed.TotalMilliseconds),
                                        "preview",
                                        false,
                                        latestPreview,
                                        latestPreviewTimestampMs));
                                }
                            }

                            captureFrameIndex++;

                            var bitmapEncodeStartedAt = DateTimeOffset.UtcNow;
                            var bitmapEncodeTimestampMs = Math.Max(0L, (long)outputClock.Elapsed.TotalMilliseconds);
                            IReadOnlyList<H264EncodedFrame> bitmapEncodedFrames;
                            try
                            {
                                bitmapEncodedFrames = encoder.Encode(bitmapFrame, bitmapEncodeTimestampMs);
                            }
                            catch (Exception ex) when (encoder.IsHardwareAccelerated)
                            {
                                DiagnosticLogService.WriteLine(
                                    $"[ScreenShare:H264] System-memory hardware encode failed. encoder='{EncoderMode}'; input='{EncoderInputFormat}'; gpu='{EncoderGpuDeviceMode}'; canEncodeGpuTexture={encoder.CanEncodeGpuTexture}; error={ex}");
                                throw new InvalidOperationException(
                                    "GPU hardware H.264 encoder failed during system-memory encode.",
                                    ex);
                            }

                            LastEncodeMilliseconds = (DateTimeOffset.UtcNow - bitmapEncodeStartedAt).TotalMilliseconds;
                            UpdateEncoderRuntimeCounters(encoder);
                            encodedInWindow += bitmapEncodedFrames.Count;
                            if (bitmapEncodedFrames.Count == 0)
                            {
                                _emptyEncodeCount++;
                                if (encoder.IsHardwareAccelerated && IsIntelEncoder(encoder))
                                {
                                    var noOutputNow = DateTimeOffset.UtcNow;
                                    if (intelSystemMemoryNoOutputStartedAtUtc == DateTimeOffset.MinValue)
                                        intelSystemMemoryNoOutputStartedAtUtc = noOutputNow;

                                    if (!forceSoftwareH264Encoder &&
                                        noOutputNow - intelSystemMemoryNoOutputStartedAtUtc >= IntelSystemMemoryEncoderFallbackDelay)
                                    {
                                        HardwareEncoderFallbackCount++;
                                        forceSoftwareH264Encoder = true;
                                        intelSystemMemoryNoOutputStartedAtUtc = DateTimeOffset.MinValue;
                                        Debug.WriteLine(
                                            $"[ScreenShare:H264] Intel Quick Sync system-memory path produced no H.264 output for {IntelSystemMemoryEncoderFallbackDelay.TotalSeconds:0}s; recreating with Microsoft software H.264 so Twitch receives video frames.");
                                        DiagnosticLogService.WriteLine(
                                            $"[ScreenShare:H264] Intel Quick Sync system-memory encoder stalled with zero H.264 output; falling back to Microsoft software H.264. encoder='{encoder.EncoderMode}'; input='{encoder.InputFormat}'; quality={quality.Width}x{quality.Height}; fps={effectiveTargetFps}; bitrate={bitrate}.");
                                        encoder.Dispose();
                                        encoder = null;
                                        encoderQuality = quality;
                                        encoderBitrate = 0;
                                        encoderFrameRate = 0;
                                        _emptyEncodeCount = 0;
                                        encodedFramesSinceLastIdr = 0;
                                        lastIdrOutputAtUtc = DateTimeOffset.MinValue;
                                        lastPeriodicIdrRequestAtUtc = DateTimeOffset.MinValue;
                                        ApplyEncoderDetailsForSoftwareFallback();
                                        continue;
                                    }
                                }
                            }
                            else
                            {
                                _emptyEncodeCount = 0;
                                intelSystemMemoryNoOutputStartedAtUtc = DateTimeOffset.MinValue;
                                if (bitmapEncodedFrames.Any(encoded => encoded.IsKeyFrame))
                                {
                                    encodedFramesSinceLastIdr = 0;
                                    lastIdrOutputAtUtc = DateTimeOffset.UtcNow;
                                    lastPeriodicIdrRequestAtUtc = DateTimeOffset.MinValue;
                                }
                                else
                                {
                                    encodedFramesSinceLastIdr += bitmapEncodedFrames.Count;
                                }
                            }

                            foreach (var encodedFrame in bitmapEncodedFrames)
                            {
                                var frameEventTimestampMs = GetEncodedFrameEventTimestampMilliseconds(
                                    _streamStartedAtUtc,
                                    encodedFrame.TimestampMilliseconds,
                                    effectiveTargetFps,
                                    ref lastEncodedSampleTimestampMs,
                                    ref nextSyntheticVideoTimestampMs,
                                    ref lastFrameEventTimestampMs);

                                FrameReady?.Invoke(this, new NativeScreenFrameEventArgs(
                                    encodedFrame.Data,
                                    quality.Width,
                                    quality.Height,
                                    quality.Name,
                                    frameEventTimestampMs,
                                    ScreenShareCodecNames.H264,
                                    encodedFrame.IsKeyFrame,
                                    latestPreview,
                                    latestPreviewTimestampMs));
                            }

                            var bitmapStatsNow = DateTimeOffset.UtcNow;
                            var bitmapStatsElapsed = bitmapStatsNow - statsWindowStartedAt;
                            if (bitmapStatsElapsed >= TimeSpan.FromSeconds(1))
                            {
                                var seconds = Math.Max(0.001, bitmapStatsElapsed.TotalSeconds);
                                CaptureFps = capturedInWindow / seconds;
                                EncodedFps = encodedInWindow / seconds;
                                capturedInWindow = 0;
                                encodedInWindow = 0;
                                statsWindowStartedAt = bitmapStatsNow;
                                Debug.WriteLine($"[ScreenShare:H264:STATS] capture={CaptureFps:0.0}fps encoded={EncodedFps:0.0}fps captureMs={LastCaptureMilliseconds:0.0} encodeMs={LastEncodeMilliseconds:0.0} previewMs={LastPreviewMilliseconds:0.0} loopMs={LastLoopMilliseconds:0.0}; systemMemoryInput=True");
                                if (EncodedFps < effectiveTargetFps * 0.85 || LastEncodeMilliseconds > 10 || LastLoopMilliseconds > 20)
                                {
                                    Debug.WriteLine($"[ScreenShare:GPU:VIDEO] pressure capture={CaptureFps:0.0}fps encoded={EncodedFps:0.0}fps target={effectiveTargetFps}; captureMs={LastCaptureMilliseconds:0.0}; encodeMs={LastEncodeMilliseconds:0.0}; previewMs={LastPreviewMilliseconds:0.0}; loopMs={LastLoopMilliseconds:0.0}; encoder='{EncoderMode}'; input='{EncoderInputFormat}'; gpu='{EncoderGpuDeviceMode}'; bitrate={CurrentBitrate}; quality={CurrentQuality.Width}x{CurrentQuality.Height}; directGpuTexture=False; hardwareRequired={RequireHardwareEncoder}; dx12Required={RequireDirectX12CapturePath}.");
                                    DiagnosticLogService.Flush();
                                }
                                UpdateAdaptiveState();
                            }

                            var bitmapLoopElapsed = DateTimeOffset.UtcNow - frameStartedAt;
                            LastLoopMilliseconds = bitmapLoopElapsed.TotalMilliseconds;
                            nextFrameDueTicks += frameBudgetTicks;
                            await WaitForNextOutputFrameAsync(outputClock, nextFrameDueTicks, frameBudgetTicks, cancellationToken);

                            if (outputClock.ElapsedTicks - nextFrameDueTicks > frameBudgetTicks * 2)
                            {
                                nextFrameDueTicks = outputClock.ElapsedTicks;
                                Debug.WriteLine($"[ScreenShare:H264] {effectiveTargetFps} FPS output clock resynced after system-memory encode/capture overrun.");
                            }

                            continue;
                        }

                        var captureStartedAt = DateTimeOffset.UtcNow;
                        var gpuWaitMilliseconds = latestReusableGpuFrame == null ? 15 : 0;
                        using var newGpuFrame = CaptureGpuFrameWithBestAvailablePath(encoder, gpuWaitMilliseconds);
                        if (newGpuFrame != null)
                        {
                            latestReusableGpuFrame?.Dispose();
                            latestReusableGpuFrame = newGpuFrame.Detach();
                        }

                        var hasFreshGpuFrame = newGpuFrame != null;
                        var gpuFrame = newGpuFrame ?? latestReusableGpuFrame;
                        LastCaptureMilliseconds = (DateTimeOffset.UtcNow - captureStartedAt).TotalMilliseconds;
                        var freshFrameNow = DateTimeOffset.UtcNow;
                        if (hasFreshGpuFrame)
                        {
                            lastFreshGpuFrameAtUtc = freshFrameNow;
                        }
                        else if (gpuFrame != null &&
                                 freshFrameNow - lastFreshGpuFrameAtUtc >= FreshCaptureStallThreshold &&
                                 freshFrameNow - lastWgcRestartAtUtc >= FreshCaptureRestartCooldown)
                        {
                            lastWgcRestartAtUtc = freshFrameNow;
                            Debug.WriteLine($"[ScreenShare:WGC] No fresh GPU capture frame for {(freshFrameNow - lastFreshGpuFrameAtUtc).TotalSeconds:0.0}s; restarting Windows Graphics Capture source to recover a frozen stream.");
                            DiagnosticLogService.WriteLine($"[ScreenShare:WGC] No fresh GPU capture frame for {(freshFrameNow - lastFreshGpuFrameAtUtc).TotalSeconds:0.0}s; restarting capture source for stale-frame recovery.");
                            latestReusableGpuFrame?.Dispose();
                            latestReusableGpuFrame = null;
                            encoder?.Dispose();
                            encoder = null;
                            encoderQuality = quality;
                            encoderBitrate = 0;
                            encoderFrameRate = 0;
                            encodedFramesSinceLastIdr = 0;
                            lastIdrOutputAtUtc = DateTimeOffset.MinValue;
                            lastPeriodicIdrRequestAtUtc = DateTimeOffset.MinValue;
                            missingGpuFramePolls = 0;
                            lowFreshCaptureWindows = 0;
                            if (_wgcCapture is not null && await _wgcCapture.RestartAsync())
                            {
                                lastFreshGpuFrameAtUtc = DateTimeOffset.UtcNow;
                                Interlocked.Exchange(ref _pendingEncoderRefresh, 1);
                                Interlocked.Exchange(ref _pendingRecoveryKeyFrame, 1);
                            }

                            gpuFrame = null;
                        }

                        if (gpuFrame == null)
                        {
                            missingGpuFramePolls++;
                            if (RequireHardwareEncoder && missingGpuFramePolls == TargetFps)
                            {
                                Debug.WriteLine("[ScreenShare:WGC] Waiting for Windows Graphics Capture GPU textures; keeping the stream alive instead of falling back to bitmap readback.");
                            }

                            if (RequireHardwareEncoder && missingGpuFramePolls >= TargetFps * 5)
                            {
                                throw new InvalidOperationException(
                                    "Windows Graphics Capture did not deliver GPU textures for 5 seconds. Restart the stream or close apps that are holding exclusive capture/display resources.");
                            }

                            LastLoopMilliseconds = (DateTimeOffset.UtcNow - frameStartedAt).TotalMilliseconds;
                            nextFrameDueTicks += frameBudgetTicks;
                            await WaitForNextOutputFrameAsync(outputClock, nextFrameDueTicks, frameBudgetTicks, cancellationToken);
                            continue;
                        }

                        if (!hasFreshGpuFrame && DropLateDuplicateFrames)
                        {
                            LastLoopMilliseconds = (DateTimeOffset.UtcNow - frameStartedAt).TotalMilliseconds;
                            var skippedNow = DateTimeOffset.UtcNow;
                            var skippedStatsElapsed = skippedNow - statsWindowStartedAt;
                            if (skippedStatsElapsed >= TimeSpan.FromSeconds(1))
                            {
                                var seconds = Math.Max(0.001, skippedStatsElapsed.TotalSeconds);
                                CaptureFps = capturedInWindow / seconds;
                                EncodedFps = encodedInWindow / seconds;
                                capturedInWindow = 0;
                                encodedInWindow = 0;
                                statsWindowStartedAt = skippedNow;
                                Debug.WriteLine($"[ScreenShare:H264:STATS] capture={CaptureFps:0.0}fps encoded={EncodedFps:0.0}fps captureMs={LastCaptureMilliseconds:0.0} encodeMs={LastEncodeMilliseconds:0.0} previewMs={LastPreviewMilliseconds:0.0} loopMs={LastLoopMilliseconds:0.0}; duplicate GPU frame dropped to protect game-stream performance.");
                                if (EncodedFps < effectiveTargetFps * 0.85)
                                    DiagnosticLogService.Flush();
                            }

                            nextFrameDueTicks += frameBudgetTicks;
                            await WaitForNextOutputFrameAsync(outputClock, nextFrameDueTicks, frameBudgetTicks, cancellationToken);
                            continue;
                        }

                        missingGpuFramePolls = newGpuFrame == null ? missingGpuFramePolls + 1 : 0;
                        if (gpuFrame != null)
                            capturedInWindow++;

                        if (EnablePreviewFrames &&
                            !PrioritizeStreamingPerformance &&
                            ShouldGeneratePreview(quality, latestPreview, captureFrameIndex, previewFrameInterval))
                        {
                            using var previewBitmap = CaptureBitmapWithBestAvailablePath(quality);
                            if (previewBitmap != null)
                            {
                                var previewStartedAt = DateTimeOffset.UtcNow;
                                latestPreview = EncodePreviewJpeg(previewBitmap, quality, PrioritizeStreamingPerformance);
                                latestPreviewTimestampMs = previewStartedAt.ToUnixTimeMilliseconds();
                                LastPreviewMilliseconds = (DateTimeOffset.UtcNow - previewStartedAt).TotalMilliseconds;
                            }
                            else if (latestPreview == null)
                            {
                                LastPreviewMilliseconds = 0;
                            }
                        }
                        else if (EnablePreviewFrames && latestPreview == null)
                        {
                            LastPreviewMilliseconds = 0;
                        }

                        captureFrameIndex++;

                        var encodeStartedAt = DateTimeOffset.UtcNow;
                        var encodeTimestampMs = Math.Max(0L, (long)outputClock.Elapsed.TotalMilliseconds);
                        IReadOnlyList<H264EncodedFrame> encodedFrames;
                        var restartEncoderAfterFrame = false;
                        try
                        {
                            encodedFrames = encoder.EncodeGpuBgraTexture(gpuFrame.Texture, gpuFrame.Width, gpuFrame.Height, encodeTimestampMs);
                        }
                        catch (Exception ex) when (encoder.IsHardwareAccelerated)
                        {
                            DiagnosticLogService.WriteLine(
                                $"[ScreenShare:H264] Direct GPU texture hardware encode failed. encoder='{EncoderMode}'; input='{EncoderInputFormat}'; gpu='{EncoderGpuDeviceMode}'; canEncodeGpuTexture={encoder.CanEncodeGpuTexture}; error={ex}");
                            throw new InvalidOperationException(
                                "GPU hardware H.264 encoder failed during direct GPU texture encode. Software and bitmap fallbacks are disabled.",
                                ex);
                        }

                        LastEncodeMilliseconds = (DateTimeOffset.UtcNow - encodeStartedAt).TotalMilliseconds;
                        UpdateEncoderRuntimeCounters(encoder);
                        encodedInWindow += encodedFrames.Count;
                        if (encodedFrames.Count == 0)
                        {
                            _emptyEncodeCount++;
                            _h264EmptyPollCount++;
                            var emptyEncodeNow = DateTimeOffset.UtcNow;
                            if (_lastH264OutputAtUtc != DateTimeOffset.MinValue &&
                                emptyEncodeNow - _lastH264OutputAtUtc > TimeSpan.FromMilliseconds(80))
                            {
                                _h264OutputGapCount++;
                                if (emptyEncodeNow - _lastH264StutterDiagnosticLogUtc >= TimeSpan.FromSeconds(1))
                                {
                                    _lastH264StutterDiagnosticLogUtc = emptyEncodeNow;
                                    Debug.WriteLine($"[ScreenShare:H264:STUTTER] Encoder output gap; gapMs={(emptyEncodeNow - _lastH264OutputAtUtc).TotalMilliseconds:0.0}; emptyPolls={_emptyEncodeCount}; totalEmptyPolls={_h264EmptyPollCount}; totalOutputGaps={_h264OutputGapCount}; encodedFrames={_h264EncodedFrameCount}; targetFps={effectiveTargetFps}; encoder='{EncoderMode}'.");
                                }
                            }
                            if (emptyEncodeNow - _lastEmptyEncodeLogUtc >= TimeSpan.FromSeconds(2))
                            {
                                _lastEmptyEncodeLogUtc = emptyEncodeNow;
                                Debug.WriteLine($"[ScreenShare:H264] Encoder produced no output for this poll; consecutiveEmptyPolls={_emptyEncodeCount}.");
                            }

                            if (encoder.IsHardwareAccelerated && _emptyEncodeCount >= Math.Max(4, effectiveTargetFps / 3))
                            {
                                var refreshAge = emptyEncodeNow - _lastEncoderStarvationRefreshUtc;
                                if (refreshAge >= EncoderStarvationRefreshThrottle)
                                {
                                    _lastEncoderStarvationRefreshUtc = emptyEncodeNow;
                                    if (IsNvidiaEncoder(encoder))
                                    {
                                        Debug.WriteLine(
                                            $"[ScreenShare:H264] NVENC produced no output for {_emptyEncodeCount} consecutive polls under load; refreshing the encoder instead of stopping the stream.");
                                        restartEncoderAfterFrame = true;
                                    }
                                    else
                                    {
                                        Debug.WriteLine(
                                            $"[ScreenShare:H264] GPU encoder produced no output for {_emptyEncodeCount} consecutive polls under load; keeping the encoder warm to avoid Intel/AMD restart stalls.");
                                    }
                                }
                            }
                        }
                        else
                        {
                            _emptyEncodeCount = 0;
                        }

                        if (encodedFrames.Count > 0)
                        {
                            _h264EncodedFrameCount += encodedFrames.Count;
                            _lastH264OutputAtUtc = DateTimeOffset.UtcNow;
                            if (encodedFrames.Count > 1 &&
                                DateTimeOffset.UtcNow - _lastH264StutterDiagnosticLogUtc >= TimeSpan.FromSeconds(1))
                            {
                                _lastH264StutterDiagnosticLogUtc = DateTimeOffset.UtcNow;
                                Debug.WriteLine($"[ScreenShare:H264:STUTTER] Encoder output burst; frames={encodedFrames.Count}; totalEncoded={_h264EncodedFrameCount}; emptyPolls={_h264EmptyPollCount}; targetFps={effectiveTargetFps}; encoder='{EncoderMode}'.");
                            }
                            if (encodedFrames.Any(encoded => encoded.IsKeyFrame))
                            {
                                encodedFramesSinceLastIdr = 0;
                                lastIdrOutputAtUtc = DateTimeOffset.UtcNow;
                                lastPeriodicIdrRequestAtUtc = DateTimeOffset.MinValue;
                            }
                            else
                            {
                                encodedFramesSinceLastIdr += encodedFrames.Count;
                                // A healthy screen share does not need a full IDR every two
                                // seconds. At 720p these frames can exceed 300 KB and create a
                                // recurring RTP burst that looks like a freeze on the receiver.
                                // Four seconds still keeps that burst modest, while bounding how
                                // long a decoder can display damaged dependent macroblocks after
                                // an RTP loss that did not produce timely PLI feedback.
                                // Real loss recovery is handled immediately by the explicit
                                // PLI/QoS recovery-keyframe path above; this is only the normal
                                // safety cadence for an otherwise healthy stream.
                                var recoveryInterval = Math.Max(
                                    encoder.RecoveryKeyFrameInterval,
                                    effectiveTargetFps * (int)HealthyStreamIdrInterval.TotalSeconds);
                                var idrAge = lastIdrOutputAtUtc == DateTimeOffset.MinValue
                                    ? TimeSpan.Zero
                                    : DateTimeOffset.UtcNow - lastIdrOutputAtUtc;
                                var idrRequestAge = lastPeriodicIdrRequestAtUtc == DateTimeOffset.MinValue
                                    ? TimeSpan.MaxValue
                                    : DateTimeOffset.UtcNow - lastPeriodicIdrRequestAtUtc;

                                var isNvidiaEncoder = IsNvidiaEncoder(encoder);
                                if (isNvidiaEncoder &&
                                    lastPeriodicIdrRequestAtUtc != DateTimeOffset.MinValue &&
                                    idrAge >= NvidiaIdrRecoveryRefreshTimeout)
                                {
                                    DiagnosticLogService.WriteLine(
                                        $"[ScreenShare:H264] NVENC ignored recovery keyframe requests for {idrAge.TotalSeconds:0.0}s ({encodedFramesSinceLastIdr} delta outputs); refreshing encoder to restore a clean IDR dependency chain.");
                                    restartEncoderAfterFrame = true;
                                }
                                else if (encodedFramesSinceLastIdr >= Math.Max(effectiveTargetFps * 2, recoveryInterval) &&
                                    !encoder.RecoveryKeyFrameRequestPending)
                                {
                                    if (isNvidiaEncoder)
                                    {
                                        lastPeriodicIdrRequestAtUtc = DateTimeOffset.UtcNow;
                                        encoder.ForceNextKeyFrame();
                                        Debug.WriteLine(
                                            $"[ScreenShare:H264] NVENC reached the recovery boundary without an exposed IDR ({encodedFramesSinceLastIdr} delta outputs); requesting another keyframe while keeping the live async encoder steady.");
                                    }
                                    else
                                    {
                                        lastPeriodicIdrRequestAtUtc = DateTimeOffset.UtcNow;
                                        Debug.WriteLine(
                                            $"[ScreenShare:H264] Non-NVIDIA encoder has not exposed an IDR/clean point after {encodedFramesSinceLastIdr} outputs; refreshing the encoder to restore realtime recovery.");
                                        DiagnosticLogService.WriteLine(
                                            $"[ScreenShare:H264] Non-NVIDIA encoder recovery failed after {encodedFramesSinceLastIdr} delta outputs and {idrRequestAge.TotalSeconds:0.0}s; refreshing encoder.");
                                        restartEncoderAfterFrame = true;
                                    }
                                }
                                else if (isNvidiaEncoder &&
                                         lastPeriodicIdrRequestAtUtc != DateTimeOffset.MinValue &&
                                         idrRequestAge >= TimeSpan.FromSeconds(3) &&
                                         encodedFramesSinceLastIdr >= effectiveTargetFps * 2 &&
                                         encoder.RecoveryKeyFrameRequestPending)
                                {
                                    lastPeriodicIdrRequestAtUtc = DateTimeOffset.UtcNow;
                                    encoder.ForceNextKeyFrame();
                                    Debug.WriteLine(
                                        $"[ScreenShare:H264] NVENC has not exposed the forced IDR after {idrRequestAge.TotalMilliseconds:0}ms and {encodedFramesSinceLastIdr} delta outputs; retrying the request without tearing down the live async encoder.");
                                }
                                else if (isNvidiaEncoder &&
                                         idrAge >= HealthyStreamIdrInterval &&
                                         idrRequestAge >= HealthyStreamIdrInterval)
                                {
                                    lastPeriodicIdrRequestAtUtc = DateTimeOffset.UtcNow;
                                    encoder.ForceNextKeyFrame();
                                    Debug.WriteLine(
                                        $"[ScreenShare:H264] GPU encoder has produced no recovery IDR for {idrAge.TotalSeconds:0.0}s ({encodedFramesSinceLastIdr} delta outputs); requesting a keyframe and keeping the stream steady.");
                                }
                            }

                        }

                        foreach (var encodedFrame in encodedFrames)
                        {
                            // Async hardware encoders can return several completed
                            // access units in one drain. Their timestamps already carry
                            // the presentation spacing used by the RTP sender. Sleeping
                            // here blocks capture, lets the GPU input queue fill again,
                            // and turns ordinary backpressure into a repeating freeze.
                            // Forward the complete dependency chain immediately; the
                            // receiver jitter buffer paces presentation by RTP time.

                            var frameEventTimestampMs = GetEncodedFrameEventTimestampMilliseconds(
                                _streamStartedAtUtc,
                                encodedFrame.TimestampMilliseconds,
                                effectiveTargetFps,
                                ref lastEncodedSampleTimestampMs,
                                ref nextSyntheticVideoTimestampMs,
                                ref lastFrameEventTimestampMs);

                            FrameReady?.Invoke(this, new NativeScreenFrameEventArgs(
                                encodedFrame.Data,
                                quality.Width,
                                quality.Height,
                                quality.Name,
                                frameEventTimestampMs,
                                ScreenShareCodecNames.H264,
                                encodedFrame.IsKeyFrame,
                                latestPreview,
                                latestPreviewTimestampMs));
                        }

                        if (restartEncoderAfterFrame)
                        {
                            encoder.Dispose();
                            encoder = null;
                            encoderQuality = quality;
                            encoderBitrate = 0;
                            encoderFrameRate = 0;
                            _emptyEncodeCount = 0;
                            encodedFramesSinceLastIdr = 0;
                            lastIdrOutputAtUtc = DateTimeOffset.MinValue;
                            lastPeriodicIdrRequestAtUtc = DateTimeOffset.MinValue;
                        }

                        var now = DateTimeOffset.UtcNow;
                        var statsElapsed = now - statsWindowStartedAt;
                        if (statsElapsed >= TimeSpan.FromSeconds(1))
                        {
                            var seconds = Math.Max(0.001, statsElapsed.TotalSeconds);
                            CaptureFps = capturedInWindow / seconds;
                            EncodedFps = encodedInWindow / seconds;
                            capturedInWindow = 0;
                            encodedInWindow = 0;
                            statsWindowStartedAt = now;
                            var lowFreshCaptureFpsThreshold = Math.Max(1.0, effectiveTargetFps * LowFreshCaptureFpsThresholdScale);
                            var lowFreshCaptureEncodedFpsThreshold = Math.Max(1.0, effectiveTargetFps * LowFreshCaptureEncodedFpsThresholdScale);
                            var lowFreshCaptureWindow =
                                now - _streamStartedAtUtc >= LowFreshCaptureWarmup &&
                                CaptureFps > 0 &&
                                CaptureFps < lowFreshCaptureFpsThreshold &&
                                EncodedFps >= lowFreshCaptureEncodedFpsThreshold;
                            if (lowFreshCaptureWindow)
                            {
                                lowFreshCaptureWindows++;
                                Debug.WriteLine($"[ScreenShare:WGC] Fresh GPU capture under target for {lowFreshCaptureWindows} window(s): capture={CaptureFps:0.0}fps; encoded={EncodedFps:0.0}fps; keeping WGC alive to avoid restart stutter.");
                            }
                            else
                            {
                                lowFreshCaptureWindows = 0;
                            }

                                Debug.WriteLine($"[ScreenShare:H264:STATS] capture={CaptureFps:0.0}fps encoded={EncodedFps:0.0}fps captureMs={LastCaptureMilliseconds:0.0} encodeMs={LastEncodeMilliseconds:0.0} previewMs={LastPreviewMilliseconds:0.0} loopMs={LastLoopMilliseconds:0.0}");
                                DiagnosticLogService.WriteLine($"[ScreenShare:STATS] codec=h264 capture={CaptureFps:0.0}fps encoded={EncodedFps:0.0}fps captureMs={LastCaptureMilliseconds:0.0} encodeMs={LastEncodeMilliseconds:0.0} previewMs={LastPreviewMilliseconds:0.0} loopMs={LastLoopMilliseconds:0.0} emptyPolls={_emptyEncodeCount}");
                                ScreenShareTestLogService.WriteLine($"stats codec=h264 captureFps={CaptureFps:0.0}; encodedFps={EncodedFps:0.0}; captureMs={LastCaptureMilliseconds:0.0}; encodeMs={LastEncodeMilliseconds:0.0}; previewMs={LastPreviewMilliseconds:0.0}; loopMs={LastLoopMilliseconds:0.0}");
                            if (EncodedFps < effectiveTargetFps * 0.85 || LastEncodeMilliseconds > 10 || LastLoopMilliseconds > 20)
                            {
                                Debug.WriteLine($"[ScreenShare:GPU:VIDEO] pressure capture={CaptureFps:0.0}fps encoded={EncodedFps:0.0}fps target={effectiveTargetFps}; captureMs={LastCaptureMilliseconds:0.0}; encodeMs={LastEncodeMilliseconds:0.0}; previewMs={LastPreviewMilliseconds:0.0}; loopMs={LastLoopMilliseconds:0.0}; encoder='{EncoderMode}'; input='{EncoderInputFormat}'; gpu='{EncoderGpuDeviceMode}'; bitrate={CurrentBitrate}; quality={CurrentQuality.Width}x{CurrentQuality.Height}; directGpuTexture={EnableDirectGpuTexturePath}; hardwareRequired={RequireHardwareEncoder}; dx12Required={RequireDirectX12CapturePath}.");
                                DiagnosticLogService.Flush();
                            }
                            UpdateAdaptiveState();
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[ScreenShare:H264] Capture or encode failed: {ex}");
                        DiagnosticLogService.WriteLine($"[ScreenShare:H264] Capture or encode failed: {ex}");
                        IsRunning = false;
                        LastFailureMessage = GetStreamingFailureMessage(ex);
                        StreamingFailed?.Invoke(this, LastFailureMessage);
                        DiagnosticLogService.StopScreenShareSession("local sender capture or encode failed");
                        return;
                    }

                    var elapsed = DateTimeOffset.UtcNow - frameStartedAt;
                    LastLoopMilliseconds = elapsed.TotalMilliseconds;
                    nextFrameDueTicks += frameBudgetTicks;
                    await WaitForNextOutputFrameAsync(outputClock, nextFrameDueTicks, frameBudgetTicks, cancellationToken);

                    if (outputClock.ElapsedTicks - nextFrameDueTicks > frameBudgetTicks * 2)
                    {
                        nextFrameDueTicks = outputClock.ElapsedTicks;
                        Debug.WriteLine($"[ScreenShare:H264] {effectiveTargetFps} FPS output clock resynced after encode/capture overrun.");
                    }
                }
            }
            finally
            {
                if (mmcssHandle != IntPtr.Zero)
                    NativeMethods.AvRevertMmThreadCharacteristics(mmcssHandle);

                encoder?.Dispose();
                av1Encoder?.Dispose();
                latestReusableGpuFrame?.Dispose();
                _wgcCapture?.Dispose();
                _wgcCapture = null;
                _dxgiCapture?.Dispose();
                _dxgiCapture = null;
                EncoderMode = IsRunning ? EncoderMode : "Stopped";
                if (highResolutionTimerEnabled)
                    NativeMethods.timeEndPeriod(1);

                DiagnosticLogService.StopScreenShareSession(
                    cancellationToken.IsCancellationRequested
                        ? "local sender cancelled"
                        : IsRunning
                            ? "local sender capture loop ended unexpectedly"
                            : "local sender ended");
            }
        }

        private static bool TryBeginHighResolutionTimer()
        {
            try
            {
                var result = NativeMethods.timeBeginPeriod(1);
                Debug.WriteLine($"[ScreenShare:H264] Realtime output clock enabled; high-resolution timer result={result}.");
                return result == 0;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ScreenShare:H264] High-resolution timer unavailable; using default timer. {ex.Message}");
                return false;
            }
        }

        private static IntPtr TryEnableStreamingThreadScheduling()
        {
            try
            {
                var handle = NativeMethods.AvSetMmThreadCharacteristics("Capture", out var taskIndex);
                if (handle == IntPtr.Zero)
                {
                    Debug.WriteLine($"[ScreenShare:H264] MMCSS capture scheduling unavailable; lastError={Marshal.GetLastWin32Error()}.");
                    return IntPtr.Zero;
                }

                NativeMethods.AvSetMmThreadPriority(handle, AvrtPriority.High);
                Debug.WriteLine($"[ScreenShare:H264] MMCSS capture scheduling enabled for realtime streaming; taskIndex={taskIndex}.");
                return handle;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ScreenShare:H264] MMCSS capture scheduling unavailable: {ex.Message}");
                return IntPtr.Zero;
            }
        }

        private int GetEffectiveTargetFps()
        {
            var targetFps = CurrentTargetFps;
            var quality = CurrentQuality;
            if (IsAdaptiveLatencyModeEnabled && DateTimeOffset.UtcNow < _receiverPressurePacingUntilUtc)
            {
                var pressureLimit = quality.Height >= 1080
                    ? ReceiverSafe1080pFps
                    : ReceiverSafeRealtimeFps;
                return Math.Min(targetFps, pressureLimit);
            }

            return targetFps;
        }

        private bool TryApplyNonNvidia1080pFpsLimit(
            IH264VideoEncoder encoder,
            ScreenShareQualityProfile quality,
            int effectiveTargetFps)
        {
            if (RequireHardwareEncoder)
                return false;

            if (!encoder.IsHardwareAccelerated ||
                IsNvidiaEncoder(encoder) ||
                quality.Height < 1080 ||
                (effectiveTargetFps <= NonNvidiaSafe1080pFps && CurrentBitrate <= NonNvidiaSafe1080pBitrate))
            {
                return false;
            }

            lock (_qualitySync)
            {
                var requestedFps = _targetFpsOverride ?? TargetFps;
                var needsFpsCap = requestedFps > NonNvidiaSafe1080pFps;
                var needsBitrateCap = !_nonNvidia1080pHardwareLimitActive ||
                                      CurrentBitrate > NonNvidiaSafe1080pBitrate;
                if (!needsFpsCap && !needsBitrateCap)
                    return false;

                if (needsFpsCap)
                    _targetFpsOverride = NonNvidiaSafe1080pFps;
                _nonNvidia1080pHardwareLimitActive = true;
                CurrentBitrate = GetConfiguredBitrate(quality);
                _healthyWindows = 0;
                _receiverPressureSignals = 0;
                _lastAdaptedAtUtc = DateTimeOffset.MinValue;
                AdaptiveState = $"Locked {quality.Name} @ {NonNvidiaSafe1080pFps} FPS / {CurrentBitrate / 1000}k (non-NVIDIA hardware encoder)";
            }

            Debug.WriteLine(
                $"[ScreenShare:H264] Non-NVIDIA hardware encoder selected at {quality.Width}x{quality.Height}; pacing Twitch output to {NonNvidiaSafe1080pFps} FPS and {CurrentBitrate / 1000}k to avoid realtime encoder stalls. encoder='{encoder.EncoderMode}'.");
            DiagnosticLogService.WriteLine(
                $"[ScreenShare:H264] Non-NVIDIA hardware encoder selected; output capped to {NonNvidiaSafe1080pFps} FPS / {CurrentBitrate / 1000}k. The 720p stack remains limited to {TargetFps} FPS on every encoder. encoder='{encoder.EncoderMode}'.");
            RequestEncoderRefresh($"non-NVIDIA hardware encoder selected; recreate stream at {NonNvidiaSafe1080pFps} FPS / {CurrentBitrate / 1000}k");
            return true;
        }

        private static async Task WaitForNextOutputFrameAsync(
            Stopwatch clock,
            long dueTicks,
            long frameBudgetTicks,
            CancellationToken cancellationToken)
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var remainingTicks = dueTicks - clock.ElapsedTicks;
                if (remainingTicks <= 0)
                    return;

                var remainingMs = remainingTicks * 1000.0 / Stopwatch.Frequency;
                if (remainingMs > 2.0)
                {
                    await Task.Delay(Math.Max(1, (int)Math.Floor(remainingMs - 1.0)), cancellationToken);
                    continue;
                }

                if (remainingTicks > frameBudgetTicks / 24)
                    Thread.Sleep(0);
                else
                    Thread.SpinWait(64);
            }
        }

        private static long GetEncodedFrameEventTimestampMilliseconds(
            DateTimeOffset streamStartedAtUtc,
            long encodedTimestampMilliseconds,
            int targetFps,
            ref long lastEncodedSampleTimestampMilliseconds,
            ref long nextSyntheticVideoTimestampMilliseconds,
            ref long lastFrameEventTimestampMilliseconds)
        {
            var frameDurationMilliseconds = Math.Max(1L, (long)Math.Round(1000.0 / Math.Clamp(targetFps, 1, TargetFps)));
            var sampleTimestampMilliseconds = Math.Max(0L, encodedTimestampMilliseconds);
            long relativeTimestampMilliseconds;

            if (sampleTimestampMilliseconds > lastEncodedSampleTimestampMilliseconds)
            {
                relativeTimestampMilliseconds = sampleTimestampMilliseconds;
                lastEncodedSampleTimestampMilliseconds = sampleTimestampMilliseconds;
                nextSyntheticVideoTimestampMilliseconds = Math.Max(
                    nextSyntheticVideoTimestampMilliseconds,
                    relativeTimestampMilliseconds + frameDurationMilliseconds);
            }
            else
            {
                relativeTimestampMilliseconds = nextSyntheticVideoTimestampMilliseconds;
                nextSyntheticVideoTimestampMilliseconds += frameDurationMilliseconds;
            }

            var frameEventTimestampMilliseconds =
                streamStartedAtUtc.ToUnixTimeMilliseconds() + relativeTimestampMilliseconds;
            if (frameEventTimestampMilliseconds <= lastFrameEventTimestampMilliseconds)
                frameEventTimestampMilliseconds = lastFrameEventTimestampMilliseconds + 1;

            lastFrameEventTimestampMilliseconds = frameEventTimestampMilliseconds;
            return frameEventTimestampMilliseconds;
        }

        private static bool IsReceiverRecoveryReason(string reason)
        {
            return reason.Contains("decoder", StringComparison.OrdinalIgnoreCase) ||
                reason.Contains("stalled", StringComparison.OrdinalIgnoreCase) ||
                reason.Contains("no RTP", StringComparison.OrdinalIgnoreCase) ||
                reason.Contains("keyframe", StringComparison.OrdinalIgnoreCase) ||
                reason.Contains("IDR", StringComparison.OrdinalIgnoreCase) ||
                reason.Contains("first visible frame", StringComparison.OrdinalIgnoreCase) ||
                reason.Contains("no frame", StringComparison.OrdinalIgnoreCase) ||
                reason.Contains("stale decoded", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsStartupKeyFrameRequest(string reason)
        {
            return reason.Contains("first visible frame", StringComparison.OrdinalIgnoreCase) ||
                reason.Contains("waiting for keyframe", StringComparison.OrdinalIgnoreCase) ||
                reason.Contains("waiting for IDR", StringComparison.OrdinalIgnoreCase) ||
                reason.Contains("needs an IDR to start", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsQualityChangeReason(string reason)
        {
            return reason.Contains("quality changed", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsTransportConnectedKeyFrameRequest(string reason)
        {
            return reason.Contains("RTP transport connected", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsReceiverPlaybackPressureReason(string reason)
        {
            return reason.Contains("receiver GPU playback keyframe requested after realtime queue pressure", StringComparison.OrdinalIgnoreCase) ||
                reason.Contains("receiver RTP backlog", StringComparison.OrdinalIgnoreCase);
        }

        private static string GetStreamingFailureMessage(Exception exception)
        {
            var message = exception.Message;
            var details = exception.ToString();
            if (details.Contains(nameof(MediaFoundationAv1Encoder), StringComparison.OrdinalIgnoreCase))
            {
                return "The AV1X GPU encoder failed while preparing the preview. Twitch streaming uses H.264, so try again with the Twitch H.264 path.";
            }

            if (details.Contains(nameof(MediaFoundationH264Encoder), StringComparison.OrdinalIgnoreCase) ||
                details.Contains("EncodeGpuBgraTexture", StringComparison.OrdinalIgnoreCase) ||
                details.Contains("Transform.ProcessOutput", StringComparison.OrdinalIgnoreCase))
            {
                return "The GPU video encoder could not process the selected source. Try a different quality setting or restart the stream preview.";
            }

            if (exception is COMException { HResult: unchecked((int)0x8000FFFF) } ||
                message.Contains("Catastrophic failure", StringComparison.OrdinalIgnoreCase))
            {
                return "Windows Graphics Capture could not access the selected source. Choose a different window or screen and try again.";
            }

            return message;
        }

        private IH264VideoEncoder CreateEncoderWithFallback(
            ScreenShareQualityProfile quality,
            int bitrate,
            int frameRate,
            bool preferHardware,
            bool requireHardware)
        {
            try
            {
                if (ShouldUseOfficialIntelVplEncoder(preferHardware, requireHardware))
                {
                    DiagnosticLogService.WriteLine(
                        $"[ScreenShare:H264:IntelVPL] Creating official Intel oneVPL encoder with no Media Foundation fallback. quality={quality.Width}x{quality.Height}; fps={frameRate}; bitrate={bitrate / 1000}k.");
                    return new IntelVplH264Encoder(quality.Width, quality.Height, bitrate, frameRate);
                }

                var encoder = new MediaFoundationH264Encoder(
                    quality.Width,
                    quality.Height,
                    bitrate,
                    preferHardware,
                    requireHardware,
                    EnableDirectGpuTexturePath ? _wgcCapture?.CaptureDevice : null,
                    frameRate,
                    PreferredH264EncoderFamily);

                return encoder;
            }
            catch (Exception ex) when (preferHardware && !ShouldUseOfficialIntelVplEncoder(preferHardware, requireHardware))
            {
                HardwareEncoderFallbackCount++;
                Debug.WriteLine($"[ScreenShare:H264] Hardware encoder startup failed, falling back to software MFT: {ex.Message}");
                DiagnosticLogService.WriteLine(
                    $"[ScreenShare:H264] Hardware encoder startup failed; falling back to Microsoft software H.264 so capture/stream can continue. requireHardware={requireHardware}; error={ex.Message}");
                return new MediaFoundationH264Encoder(
                    quality.Width,
                    quality.Height,
                    bitrate,
                    preferHardware: false,
                    frameRate: frameRate,
                    preferredEncoderFamily: PreferredH264EncoderFamily);
            }
        }

        private bool ShouldUseOfficialIntelVplEncoder(bool preferHardware, bool requireHardware)
        {
            if (!preferHardware && !requireHardware)
                return false;

            if (PreferredH264EncoderFamily == ScreenShareH264EncoderFamily.Intel)
                return HasDisplayAdapter(IntelH264EncoderPolicy.AdapterVendorId);

            if (PreferredH264EncoderFamily == ScreenShareH264EncoderFamily.Nvidia)
                return false;

            return !HasDisplayAdapter(NvidiaH264EncoderPolicy.AdapterVendorId) &&
                HasDisplayAdapter(IntelH264EncoderPolicy.AdapterVendorId);
        }

        private static bool HasDisplayAdapter(int vendorId)
        {
            try
            {
                using var factory = new SharpDX.DXGI.Factory1();
                for (var i = 0; ; i++)
                {
                    SharpDX.DXGI.Adapter1? adapter = null;
                    try
                    {
                        adapter = factory.GetAdapter1(i);
                        var description = adapter.Description1;
                        if (description.VendorId == vendorId)
                            return true;
                    }
                    catch (SharpDXException ex) when (ex.ResultCode.Code == unchecked((int)0x887A0002))
                    {
                        return false;
                    }
                    finally
                    {
                        adapter?.Dispose();
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ScreenShare:H264] Could not enumerate DXGI adapters for hardware encoder selection: {ex.Message}");
                return false;
            }
        }

        private CapturedGpuFrame? CaptureGpuFrameWithBestAvailablePath(IH264VideoEncoder encoder, int waitMilliseconds)
        {
            if (!EnableDirectGpuTexturePath ||
                _wgcCapture?.IsAvailable != true ||
                !encoder.CanEncodeGpuTexture)
            {
                return null;
            }

            var immediateFrame = _wgcCapture.TryGetLatestGpuFrame();
            if (immediateFrame != null || waitMilliseconds <= 0)
                return immediateFrame;

            var waitStartedAt = Stopwatch.StartNew();
            while (waitStartedAt.ElapsedMilliseconds < waitMilliseconds)
            {
                var gpuFrame = _wgcCapture.TryGetLatestGpuFrame();
                if (gpuFrame != null)
                    return gpuFrame;

                Thread.Yield();
            }

            return null;
        }

        private void ApplyEncoderDetails(IH264VideoEncoder encoder)
        {
            EncoderMode = encoder.EncoderMode;
            EncoderInputFormat = encoder.InputFormat;
            EncoderGpuDeviceMode = encoder.IsHardwareAccelerated
                ? encoder.GpuDeviceManagerMode
                : "Software H.264 fallback active";
            RecoveryKeyFrameInterval = encoder.RecoveryKeyFrameInterval;
            EncoderRealtimeModeEnabled = encoder.RealtimeModeEnabled;
            EncoderLowLatencyOutputEnabled = encoder.LowLatencyOutputEnabled;
            UpdateEncoderRuntimeCounters(encoder);
        }

        private void ApplyEncoderDetailsForSoftwareFallback()
        {
            EncoderMode = "Microsoft software H.264 fallback";
            EncoderInputFormat = "System-memory bitmap input";
            EncoderGpuDeviceMode = "Software H.264 fallback active";
            EncoderRealtimeModeEnabled = false;
            EncoderLowLatencyOutputEnabled = false;
            EncoderPendingHardwareInputs = 0;
            EncoderHardwareInputRequests = 0;
            EncoderHardwareOutputRequests = 0;
            EncoderUsesHardwareEventPump = false;
        }

        private void UpdateEncoderRuntimeCounters(IH264VideoEncoder encoder)
        {
            EncoderPendingHardwareInputs = encoder.PendingHardwareInputs;
            EncoderHardwareInputRequests = encoder.HardwareInputRequests;
            EncoderHardwareOutputRequests = encoder.HardwareOutputRequests;
            EncoderUsesHardwareEventPump = encoder.UsesHardwareEventPump;
        }

        private static bool IsNvidiaEncoder(IH264VideoEncoder encoder)
        {
            var mode = encoder.EncoderMode;
            return mode.Contains("NVENC", StringComparison.OrdinalIgnoreCase) ||
                mode.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsIntelEncoder(IH264VideoEncoder encoder)
        {
            return IntelH264EncoderPolicy.MatchesEncoderMode(encoder.EncoderMode);
        }

        private void WriteGpuStreamDiagnostics(string stage)
        {
            var quality = CurrentQuality;
            Debug.WriteLine($"[ScreenShare:GPU:VIDEO] {stage}; device={Environment.MachineName}; target={CurrentTargetFps}fps; quality={quality.Width}x{quality.Height}; bitrate={CurrentBitrate}; requestedPreset={_qualityPreset}; effectivePreset={_effectiveQualityPreset}; captureDx12Required={RequireDirectX12CapturePath}; hardwareEncoderRequired={RequireHardwareEncoder}; directGpuTexture={EnableDirectGpuTexturePath}; dropLateDuplicateFrames={DropLateDuplicateFrames}; encoder='{EncoderMode}'; input='{EncoderInputFormat}'; gpu='{EncoderGpuDeviceMode}'; adaptive='{AdaptiveState}'; log='{DiagnosticLogService.CurrentLogPath}'.");
        }

        private static Bitmap CaptureBitmap(ScreenShareQualityProfile quality)
        {
            var bounds = GetVirtualScreenBounds();
            var target = new Bitmap(quality.Width, quality.Height, PixelFormat.Format32bppArgb);
            var screenDc = IntPtr.Zero;

            using var graphics = Graphics.FromImage(target);
            var targetDc = graphics.GetHdc();
            try
            {
                screenDc = NativeMethods.GetDC(IntPtr.Zero);
                NativeMethods.SetStretchBltMode(targetDc, NativeMethods.COLORONCOLOR);
                var copied = NativeMethods.StretchBlt(
                    targetDc,
                    0,
                    0,
                    quality.Width,
                    quality.Height,
                    screenDc,
                    bounds.Left,
                    bounds.Top,
                    bounds.Width,
                    bounds.Height,
                    NativeMethods.SRCCOPY | NativeMethods.CAPTUREBLT);

                if (!copied)
                    throw new InvalidOperationException($"Screen capture failed: {Marshal.GetLastWin32Error()}");
            }
            finally
            {
                graphics.ReleaseHdc(targetDc);
                if (screenDc != IntPtr.Zero)
                    NativeMethods.ReleaseDC(IntPtr.Zero, screenDc);
            }

            return target;
        }

        private Bitmap? CaptureBitmapWithBestAvailablePath(ScreenShareQualityProfile quality)
        {
            if (_wgcCapture?.IsAvailable == true)
            {
                var waitStartedAt = Stopwatch.StartNew();
                while (waitStartedAt.ElapsedMilliseconds < 3)
                {
                    var wgcFrame = _wgcCapture.TryGetLatestFrame();
                    if (wgcFrame != null)
                        return wgcFrame;

                    Thread.Yield();
                }

                return null;
            }

            if (RequireDirectX12CapturePath)
                throw new InvalidOperationException("DirectX 12 Windows Graphics Capture is required, but it is not available.");

            _dxgiCapture ??= new DxgiScreenCaptureService();
            if (_dxgiCapture.IsAvailable)
            {
                var dxgiFrame = _dxgiCapture.TryCapture(quality);
                if (dxgiFrame != null)
                    return dxgiFrame;
            }

            return CaptureBitmap(quality);
        }

        private static int GetPreviewFrameInterval(ScreenShareQualityProfile quality)
        {
            return Math.Max(1, quality.PreviewFrameInterval);
        }

        internal static int GetLivePreviewFrameInterval()
        {
            return Math.Max(1, TargetFps / LivePreviewFps);
        }

        private static bool ShouldGeneratePreview(
            ScreenShareQualityProfile quality,
            byte[]? latestPreview,
            int captureFrameIndex,
            int previewFrameInterval)
        {
            if (latestPreview == null)
                return true;

            if (quality.Height >= 1440)
                return captureFrameIndex > 0 && captureFrameIndex % previewFrameInterval == 0;

            return captureFrameIndex % previewFrameInterval == 0;
        }

        private static byte[] EncodePreviewJpeg(Bitmap bitmap, ScreenShareQualityProfile quality, bool prioritizeStreamingPerformance)
        {
            var jpegQuality = prioritizeStreamingPerformance
                ? Math.Min(quality.PreviewJpegQuality, 76L)
                : quality.PreviewJpegQuality;
            var previewBitmap = bitmap;
            Bitmap? scaledPreview = null;
            var maxPreviewWidth = prioritizeStreamingPerformance
                ? Math.Min(quality.PreviewMaxWidth, quality.Height >= 1080 ? 960 : 854)
                : quality.PreviewMaxWidth;
            if (bitmap.Width > maxPreviewWidth)
            {
                var scale = (double)maxPreviewWidth / bitmap.Width;
                var previewWidth = Math.Max(1, (int)Math.Round(bitmap.Width * scale));
                var previewHeight = Math.Max(1, (int)Math.Round(bitmap.Height * scale));
                scaledPreview = new Bitmap(previewWidth, previewHeight, PixelFormat.Format32bppArgb);
                using var graphics = Graphics.FromImage(scaledPreview);
                graphics.CompositingQuality = System.Drawing.Drawing2D.CompositingQuality.HighSpeed;
                graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBilinear;
                graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.HighSpeed;
                graphics.DrawImage(bitmap, 0, 0, previewWidth, previewHeight);
                previewBitmap = scaledPreview;
            }

            try
            {
                return EncodeJpeg(previewBitmap, jpegQuality);
            }
            finally
            {
                scaledPreview?.Dispose();
            }
        }

        private static byte[] EncodeJpeg(Bitmap bitmap, long quality)
        {
            using var ms = new MemoryStream();
            var encoder = ImageCodecInfo.GetImageEncoders().First(x => x.MimeType == "image/jpeg");
            using var parameters = new EncoderParameters(1);
            parameters.Param[0] = new EncoderParameter(Encoder.Quality, quality);
            bitmap.Save(ms, encoder, parameters);
            return ms.ToArray();
        }

        private static int GetBitrate(ScreenShareQualityProfile quality)
        {
            return quality.Bitrate;
        }

        private int GetConfiguredBitrate(ScreenShareQualityProfile quality)
        {
            var configured = _bitrateOverride.HasValue
                ? Math.Max(1_000_000, _bitrateOverride.Value)
                : GetBitrate(quality);

            if (_nonNvidia1080pHardwareLimitActive && quality.Height >= 1080)
                return configured;

            return configured;
        }

        private static int GetMinimumBitrate(ScreenShareQualityProfile quality)
        {
            return quality.MinimumBitrate;
        }

        private int GetAdaptiveBitrate(ScreenShareQualityProfile quality, int scalePercent)
        {
            var target = GetConfiguredBitrate(quality) * Math.Clamp(scalePercent, 45, 100) / 100;
            if (_bitrateOverride.HasValue)
                return Math.Max(1_000_000, target);

            return Math.Max(GetMinimumBitrate(quality), target);
        }

        private void UpdateAdaptiveState()
        {
            if (!IsAdaptiveLatencyModeEnabled ||
                DateTimeOffset.UtcNow < _adaptationWarmupUntilUtc)
            {
                return;
            }

            var targetFps = CurrentTargetFps;
            var frameBudgetMs = 1000.0 / targetFps;
            var fpsPressure = EncodedFps > 0 && EncodedFps < targetFps * AdaptiveFpsPressureThreshold;
            var encodePressure = LastEncodeMilliseconds > frameBudgetMs * 1.35;
            var loopPressure = LastLoopMilliseconds > frameBudgetMs * 1.75;
            var pressure = fpsPressure || encodePressure || loopPressure;

            if (pressure)
            {
                ApplyAdaptivePressure(
                    $"{targetFps}fps latency budget exceeded",
                    severe:
                        LastEncodeMilliseconds > frameBudgetMs * 1.8 ||
                        LastLoopMilliseconds > frameBudgetMs * 2.4 ||
                        (EncodedFps > 0 && EncodedFps < targetFps * AdaptiveSevereFpsPressureThreshold));
                return;
            }

            lock (_qualitySync)
            {
                _healthyWindows++;
                if (_healthyWindows < 8 || DateTimeOffset.UtcNow - _lastAdaptedAtUtc < TimeSpan.FromSeconds(4))
                    return;

                if (_bitrateScalePercent < 100)
                {
                    _bitrateScalePercent = Math.Min(100, _bitrateScalePercent + 10);
                    CurrentBitrate = GetAdaptiveBitrate(ScreenShareQualityProfile.FromPreset(_effectiveQualityPreset), _bitrateScalePercent);
                    AdaptiveState = $"Realtime recovering bitrate ({_bitrateScalePercent}%)";
                    _healthyWindows = 0;
                    _lastAdaptedAtUtc = DateTimeOffset.UtcNow;
                    return;
                }

                AdaptiveState = _effectiveQualityPreset == _qualityPreset
                    ? "Realtime stable"
                    : $"Realtime stable at {ScreenShareQualityProfile.FromPreset(_effectiveQualityPreset).Name}";
                _healthyWindows = 0;
                _lastAdaptedAtUtc = DateTimeOffset.UtcNow;
            }
        }

        private void ApplyAdaptivePressure(string reason, bool severe, bool receiverPressure = false)
        {
            if (!IsAdaptiveLatencyModeEnabled || !IsRunning)
                return;

            lock (_qualitySync)
            {
                var now = DateTimeOffset.UtcNow;
                if (now < _adaptationWarmupUntilUtc ||
                    now - _lastAdaptedAtUtc < AdaptationCooldown)
                {
                    return;
                }

                _bitrateScalePercent = Math.Max(55, _bitrateScalePercent - (severe ? 15 : 10));

                if (severe && _effectiveQualityPreset != ScreenShareQualityPreset.Hd720p)
                {
                    _effectiveQualityPreset = ScreenShareQualityPreset.Hd720p;
                    _bitrateScalePercent = 100;
                    AutoDowngradeCount++;
                    CurrentBitrate = GetAdaptiveBitrate(ScreenShareQualityProfile.FromPreset(_effectiveQualityPreset), _bitrateScalePercent);
                    AdaptiveState = $"Realtime switched to 720p60: {reason}";
                    _healthyWindows = 0;
                    _lastAdaptedAtUtc = now;
                    Debug.WriteLine($"[ScreenShare:Adaptive] {AdaptiveState}; bitrate={CurrentBitrate}; congestion={CongestionSignals}");
                    return;
                }

                AdaptiveState = receiverPressure
                    ? $"Realtime receiver relief bitrate ({_bitrateScalePercent}%): {reason}"
                    : $"Realtime reduced bitrate ({_bitrateScalePercent}%): {reason}";

                CurrentBitrate = GetAdaptiveBitrate(ScreenShareQualityProfile.FromPreset(_effectiveQualityPreset), _bitrateScalePercent);
                _healthyWindows = 0;
                _lastAdaptedAtUtc = now;
                Debug.WriteLine($"[ScreenShare:Adaptive] {AdaptiveState}; bitrate={CurrentBitrate}; congestion={CongestionSignals}");
            }

        }

        private static ScreenShareQualityPreset GetLowerRealtimePreset(ScreenShareQualityPreset preset)
        {
            return preset switch
            {
                ScreenShareQualityPreset.UltraHd4K => ScreenShareQualityPreset.QuadHd2K,
                ScreenShareQualityPreset.QuadHd2K => ScreenShareQualityPreset.FullHd1080p,
                ScreenShareQualityPreset.FullHd1080p => ScreenShareQualityPreset.Hd720p,
                ScreenShareQualityPreset.Hd720p => ScreenShareQualityPreset.Performance540p,
                _ => preset
            };
        }

        private static Rectangle GetVirtualScreenBounds()
        {
            var left = NativeMethods.GetSystemMetrics(NativeMethods.SM_XVIRTUALSCREEN);
            var top = NativeMethods.GetSystemMetrics(NativeMethods.SM_YVIRTUALSCREEN);
            var width = NativeMethods.GetSystemMetrics(NativeMethods.SM_CXVIRTUALSCREEN);
            var height = NativeMethods.GetSystemMetrics(NativeMethods.SM_CYVIRTUALSCREEN);

            if (width <= 0 || height <= 0)
                throw new InvalidOperationException("Unable to read desktop bounds.");

            return new Rectangle(left, top, width, height);
        }

        private static Bitmap ScaleFrame(Bitmap source, ScreenShareQualityProfile quality)
        {
            if (source.Width == quality.Width && source.Height == quality.Height)
                return new Bitmap(source);

            var scale = Math.Min((double)quality.Width / source.Width, (double)quality.Height / source.Height);
            var width = Math.Max(1, (int)Math.Round(source.Width * scale));
            var height = Math.Max(1, (int)Math.Round(source.Height * scale));

            var scaled = new Bitmap(width, height);
            using var graphics = Graphics.FromImage(scaled);
            graphics.CompositingQuality = System.Drawing.Drawing2D.CompositingQuality.HighSpeed;
            graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.Low;
            graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.HighSpeed;
            graphics.DrawImage(source, 0, 0, width, height);

            return scaled;
        }

        public async ValueTask DisposeAsync()
        {
            await StopAsync();
        }
    }

    public sealed class NativeScreenFrameEventArgs : EventArgs
    {
        public NativeScreenFrameEventArgs(
            byte[] frameData,
            int width,
            int height,
            string qualityName,
            long timestamp,
            string codec = "jpeg",
            bool isKeyFrame = true,
            byte[]? previewFrameData = null,
            long previewTimestamp = 0)
        {
            FrameData = frameData;
            Width = width;
            Height = height;
            QualityName = qualityName;
            Timestamp = timestamp;
            Codec = codec;
            IsKeyFrame = isKeyFrame;
            PreviewFrameData = previewFrameData ??
                (ScreenShareCodecNames.IsH264(codec) || ScreenShareCodecNames.IsAv1(codec) ? Array.Empty<byte>() : frameData);
            PreviewTimestamp = previewTimestamp > 0 ? previewTimestamp : timestamp;
        }

        public byte[] FrameData { get; }
        public int Width { get; }
        public int Height { get; }
        public string QualityName { get; }
        public long Timestamp { get; }
        public string Codec { get; }
        public bool IsKeyFrame { get; }
        public byte[] PreviewFrameData { get; }
        public long PreviewTimestamp { get; }
    }

    public enum ScreenShareQualityPreset
    {
        Performance540p,
        Hd720p,
        FullHd1080p,
        QuadHd2K,
        UltraHd4K
    }

    public enum NativeCaptureSourceMode
    {
        Desktop,
        GameOrWindow
    }

    public sealed class ScreenShareQualityProfile
    {
        internal ScreenShareQualityProfile(
            ScreenShareQualityPreset preset,
            string name,
            int width,
            int height,
            int targetFps,
            int bitrate,
            int minimumBitrate,
            int previewFrameInterval,
            int previewMaxWidth,
            long previewJpegQuality)
        {
            Preset = preset;
            Name = name;
            Width = width;
            Height = height;
            TargetFps = targetFps;
            Bitrate = bitrate;
            MinimumBitrate = minimumBitrate;
            PreviewFrameInterval = previewFrameInterval;
            PreviewMaxWidth = previewMaxWidth;
            PreviewJpegQuality = previewJpegQuality;
        }

        public ScreenShareQualityPreset Preset { get; }
        public string Name { get; }
        public int Width { get; }
        public int Height { get; }
        public int TargetFps { get; }
        public int Bitrate { get; }
        public int MinimumBitrate { get; }
        public int PreviewFrameInterval { get; }
        public int PreviewMaxWidth { get; }
        public long PreviewJpegQuality { get; }

        public static ScreenShareQualityProfile FromPreset(ScreenShareQualityPreset preset)
        {
            return preset switch
            {
                ScreenShareQualityPreset.Performance540p => new ScreenShareQualityProfile(
                    preset,
                    "540p realtime",
                    960,
                    540,
                    targetFps: 30,
                    bitrate: 3_500_000,
                    minimumBitrate: 2_500_000,
                    previewFrameInterval: ScreenShareCapturePipeline.GetLivePreviewFrameInterval(),
                    previewMaxWidth: 960,
                    previewJpegQuality: ScreenShareCapturePipeline.JpegQuality),
                ScreenShareQualityPreset.Hd720p => ScreenShare720pService.CreateProfile(),
                ScreenShareQualityPreset.FullHd1080p => ScreenShare1080pService.CreateProfile(),
                ScreenShareQualityPreset.QuadHd2K => ScreenShare1440pService.CreateProfile(),
                ScreenShareQualityPreset.UltraHd4K => new ScreenShareQualityProfile(
                    preset,
                    "4K",
                    3840,
                    2160,
                    targetFps: ScreenShareCapturePipeline.TargetFps,
                    bitrate: 36_000_000,
                    minimumBitrate: 24_000_000,
                    previewFrameInterval: ScreenShareCapturePipeline.TargetFps * 10,
                    previewMaxWidth: 1280,
                    previewJpegQuality: 68L),
                _ => FromPreset(ScreenShareQualityPreset.Hd720p)
            };
        }
    }
}

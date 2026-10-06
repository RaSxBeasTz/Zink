using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Security;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Zink.Services.NativeCalling;
using Zink.Services.Recording;

namespace Zink.Services.Streaming
{
    public sealed class NativeTwitchStreamingService
    {
        public const int OutputWidth = 1280;
        public const int OutputHeight = 720;
        public const int OutputFps = 60;
        public const int VideoBitrateKbps = 6000;
        public const int FullHdVideoBitrateKbps = 6000;
        private const int AudioStartupDelayMilliseconds = 250;
        private const bool PublishRawAacFrames = true;
        private static readonly TimeSpan TwitchKeyFrameRefreshInterval = TimeSpan.FromSeconds(2);
        private static readonly TimeSpan TwitchSequenceHeaderRefreshInterval = TimeSpan.FromSeconds(2);
        private static readonly TimeSpan VideoStallRecoveryInterval = TimeSpan.FromSeconds(2);
        private static readonly TimeSpan VideoStallReconnectThreshold = TimeSpan.FromSeconds(8);
        public const string VideoCodecName = "H.264";
        public const string EncoderName = "Media Foundation H.264";
        public const string H264Profile = "GPU hardware H.264";
        public const string WindowsLoopbackAudioDeviceName = "Windows system audio (loopback)";
        public static NativeTwitchStreamingService Instance { get; } = new("Twitch", "rtmp://live.twitch.tv/app");
        public static NativeTwitchStreamingService YouTubeInstance { get; } = new("YouTube", "rtmp://a.rtmp.youtube.com/live2");
        public static NativeTwitchStreamingService KickInstance { get; } = new("Kick", "rtmps://fa723fc1b171.global-contribute.live-video.net:443/app");
        public static NativeTwitchStreamingService InstagramInstance { get; } = new("Instagram", "rtmps://live-upload.instagram.com:443/rtmp/");
        public static NativeTwitchStreamingService TikTokInstance { get; } = new("TikTok", string.Empty);
        public static NativeTwitchStreamingService FacebookInstance { get; } = new("Facebook Live", "rtmps://live-api-s.facebook.com:443/rtmp/");
        public static NativeTwitchStreamingService XInstance { get; } = new("X Live", string.Empty);

        private readonly StreamingVideoCapturePipeline _captureService = StreamingVideoCapturePipeline.Instance;
        private readonly NativeStreamingStats _currentStats = new();
        private readonly object _stateLock = new();
        private readonly object _logLock = new();
        private readonly Process _process = Process.GetCurrentProcess();
        private readonly string _platformName;
        private readonly string _defaultServerUrl;

        private NativeRtmpClient? _rtmpClient;
        private NativeRtmpTarget? _rtmpTarget;
        private Channel<NativeScreenFrameEventArgs>? _videoFrames;
        private Channel<NativeAudioFrame>? _audioFrames;
        private CancellationTokenSource? _cts;
        private Task? _publishTask;
        private NativeAudioMixerSession? _audioSession;
        private FdkAacStreamingEncoder? _aacEncoder;
        private StreamWriter? _logWriter;
        private string? _currentLogPath;
        private long _firstFrameTimestamp;
        private double _nextVideoTimestampMilliseconds;
        private DateTimeOffset _startedAtUtc;
        private bool _sequenceHeaderSent;
        private long _droppedVideoFrames;
        private long _videoKeyFramesSent;
        private long _sequenceHeadersSent;
        private long _videoFramesDroppedBeforeHeader;
        private long _videoFramesSinceLastKeyFrame;
        private long _videoFramesPublished;
        private long _repeatedEncodedFrames;
        private uint _lastEncodedFingerprint;
        private long _lastPublishedVideoTimestamp = -1;
        private DateTimeOffset _lastPublishedVideoAtUtc = DateTimeOffset.MinValue;
        private DateTimeOffset _lastVideoKeyFrameAtUtc = DateTimeOffset.MinValue;
        private DateTimeOffset _lastVideoKeyFrameRefreshAtUtc = DateTimeOffset.MinValue;
        private DateTimeOffset _lastSequenceHeaderSentAtUtc = DateTimeOffset.MinValue;
        private DateTimeOffset _lastVideoStallRecoveryAtUtc = DateTimeOffset.MinValue;
        private byte[]? _lastSequenceHeaderSps;
        private byte[]? _lastSequenceHeaderPps;
        private long _lastStatsFrameCount;
        private long _lastStatsVideoBytes;
        private long _videoBytesSent;
        private TimeSpan _lastStatsProcessCpuTime = TimeSpan.Zero;
        private int _lastStatsGen0Collections;
        private int _lastStatsGen1Collections;
        private int _lastStatsGen2Collections;
        private DateTimeOffset _lastStatsPublishedAtUtc = DateTimeOffset.MinValue;
        private int _configuredOutputFps = OutputFps;

        public event EventHandler<string>? StatusChanged;
        public event EventHandler<bool>? StreamingStateChanged;
        public event EventHandler<NativeStreamingStats>? StatsChanged;

        public bool IsStreaming { get; private set; }
        public string LastStatus { get; private set; } = "Ready.";
        public string? CurrentLogPath => _currentLogPath;
        public NativeStreamingStats CurrentStats => _currentStats.Clone();

        private NativeTwitchStreamingService(string platformName, string defaultServerUrl)
        {
            _platformName = platformName;
            _defaultServerUrl = defaultServerUrl;
        }

        public async Task StartAsync(
            string streamKey,
            string serverUrl,
            ScreenShareQualityPreset qualityPreset,
            string desktopAudioDeviceId,
            double desktopAudioVolume,
            bool desktopAudioMuted,
            string microphoneDeviceId,
            double microphoneVolume,
            bool microphoneMuted,
            bool lowLatency,
            bool includeDiscordSound = true,
            bool includeSpotifySound = true)
        {
            if (IsStreaming)
            {
                PublishStatus("Stream is already running.");
                return;
            }

            if (string.IsNullOrWhiteSpace(streamKey))
            {
                PublishStatus($"Enter your {_platformName} stream key first.");
                return;
            }

            if (string.IsNullOrWhiteSpace(serverUrl) && string.IsNullOrWhiteSpace(_defaultServerUrl))
            {
                PublishStatus($"Enter your {_platformName} server URL first.");
                return;
            }

            try
            {
                var target = NativeRtmpTarget.From(serverUrl, streamKey, _defaultServerUrl);
                _rtmpTarget = target;
                _captureService.SetQuality(qualityPreset);
                var quality = _captureService.CurrentQuality;
                _configuredOutputFps = OutputFps;
                // Hardware MFTs can return several encoded frames in one event-pump
                // callback. A one-frame queue discarded most of such a burst before the
                // RTMP writer could consume it, producing uneven motion despite fast I/O.
                var videoQueueDepth = Math.Max(4, _configuredOutputFps / 10);
                var audioQueueDepth = qualityPreset == ScreenShareQualityPreset.FullHd1080p ? 80 : 240;
                var videoBitrateKbps = GetVideoBitrateKbps(qualityPreset);
                StartLog(target.SafeUrl, quality, videoBitrateKbps, lowLatency);
                ResetStats();

                _cts = new CancellationTokenSource();

                _videoFrames = Channel.CreateBounded<NativeScreenFrameEventArgs>(new BoundedChannelOptions(videoQueueDepth)
                {
                    FullMode = BoundedChannelFullMode.DropOldest,
                    SingleReader = true,
                    SingleWriter = false
                });
                _audioFrames = Channel.CreateBounded<NativeAudioFrame>(new BoundedChannelOptions(audioQueueDepth)
                {
                    FullMode = BoundedChannelFullMode.DropOldest,
                    SingleReader = true,
                    SingleWriter = false
                });

                _rtmpClient = new NativeRtmpClient();
                await _rtmpClient.ConnectAndPublishAsync(target, _cts.Token);
                WriteObsStylePipelineLog(quality, videoBitrateKbps);

                _captureService.SetTargetFpsOverride(_configuredOutputFps);
                _captureService.SetBitrateOverride(videoBitrateKbps * 1000);
                _captureService.SetAdaptiveLatencyMode(false);
                _captureService.EnablePreviewFrames = false;
                _captureService.PublishPreviewOnlyFrames = false;
                _captureService.PrioritizeStreamingPerformance = true;
                _captureService.DropLateDuplicateFrames = false;
                _captureService.PreferredVideoCodec = ScreenShareVideoCodec.H264;
                _captureService.PreferredCaptureSourceMode = NativeCaptureSourceMode.GameOrWindow;
                _captureService.RequireHardwareEncoder = true;
                _captureService.RequireDirectX12CapturePath = true;
                WriteLog($"Quality selected: {quality.Name}; bitrate={videoBitrateKbps}k; videoQueue={videoQueueDepth}; audioQueue={audioQueueDepth}; lowLatency={lowLatency}.");
                WriteLog("Live preview frame generation disabled while streaming so capture and encoder stay on the 60 FPS path.");
                if (_captureService.IsRunning)
                {
                    WriteLog($"Restarting capture service before {_platformName} streaming so selected window capture settings are applied.");
                    await _captureService.StopAsync();
                }

                _captureService.FrameReady += CaptureService_FrameReady;
                _captureService.StreamingFailed += CaptureService_StreamingFailed;
                await _captureService.StartAsync();

                _firstFrameTimestamp = 0;
                _nextVideoTimestampMilliseconds = 0;
                _startedAtUtc = DateTimeOffset.UtcNow;
                _sequenceHeaderSent = false;
                IsStreaming = true;
                StreamingStateChanged?.Invoke(this, true);
                Zink.Services.DiscordPresenceService.Instance.SetStreamingPresence(_platformName, isLive: true);
                _publishTask = Task.Run(() => PublishLoopWithLoggingAsync(_cts.Token), _cts.Token);
                _captureService.RequestEncoderRefresh($"{_platformName} stream starting; publish needs fresh SPS/PPS and IDR");

                _audioSession = new NativeAudioMixerSession(
                    desktopAudioEnabled: !desktopAudioMuted && !string.IsNullOrWhiteSpace(desktopAudioDeviceId),
                    desktopAudioDeviceId: desktopAudioDeviceId,
                    desktopVolume: desktopAudioVolume,
                    excludedDesktopProcessName: !includeDiscordSound ? "Discord" : !includeSpotifySound ? "Spotify" : null,
                    microphoneEnabled: !microphoneMuted && !string.IsNullOrWhiteSpace(microphoneDeviceId),
                    microphoneDeviceId: microphoneDeviceId,
                    microphoneVolume: microphoneVolume,
                    frameReady: frame => _audioFrames?.Writer.TryWrite(frame));
                await _audioSession.StartAsync();

                PublishStatus($"Native {_platformName} stream started with OBS-style staged pipeline: source -> compositor clock -> H.264 -> RTMP.");
                WriteLog("Native stream started.");
                WriteLog($"Audio active: desktop={!desktopAudioMuted && !string.IsNullOrWhiteSpace(desktopAudioDeviceId)}, mic={!microphoneMuted && !string.IsNullOrWhiteSpace(microphoneDeviceId)}.");
            }
            catch (Exception ex)
            {
                WriteLog("Native start failed: " + ex);
                await StopAsync("start failed: " + ex.Message);
                PublishStatus($"Native streaming failed: {ex.Message}");
            }
        }

        private static int GetVideoBitrateKbps(ScreenShareQualityPreset qualityPreset)
        {
            return qualityPreset == ScreenShareQualityPreset.FullHd1080p
                ? FullHdVideoBitrateKbps
                : VideoBitrateKbps;
        }

        public async Task StopAsync(string reason = "stop requested")
        {
            CancellationTokenSource? cts;
            Task? publishTask;
            WriteLog("Native stop requested: " + reason);

            lock (_stateLock)
            {
                cts = _cts;
                publishTask = _publishTask;
                _cts = null;
                _publishTask = null;
            }

            try
            {
                cts?.Cancel();
                _videoFrames?.Writer.TryComplete();
                _audioFrames?.Writer.TryComplete();
            }
            catch
            {
            }

            if (publishTask is not null)
            {
                try
                {
                    await publishTask;
                }
                catch
                {
                }
            }

            _captureService.FrameReady -= CaptureService_FrameReady;
            _captureService.StreamingFailed -= CaptureService_StreamingFailed;
            _captureService.EnablePreviewFrames = true;
            _captureService.PrioritizeStreamingPerformance = false;
            _captureService.DropLateDuplicateFrames = false;
            try
            {
                if (_captureService.IsRunning)
                    await _captureService.StopAsync();
            }
            catch (Exception ex)
            {
                WriteLog("Capture service stop failed: " + ex);
            }

            _captureService.SetTargetFpsOverride(null);
            Zink.Services.DiscordPresenceService.Instance.SetStreamingPresence(_platformName, isLive: false);

            if (_audioSession is not null)
                await _audioSession.DisposeAsync();
            _audioSession = null;
            _aacEncoder?.Dispose();
            _aacEncoder = null;
            _rtmpClient?.Dispose();
            _rtmpClient = null;
            _rtmpTarget = null;
            _videoFrames = null;
            _audioFrames = null;
            cts?.Dispose();

            IsStreaming = false;
            StreamingStateChanged?.Invoke(this, false);
            WriteLog("Native stream stopped. reason=" + reason);
            PublishStatus("Native stream stopped.");
            CloseLog();
        }

        public static Task<IReadOnlyList<string>> GetDirectShowAudioDevicesAsync()
        {
            return TwitchStreamingService.GetDirectShowAudioDevicesAsync();
        }

        private void CaptureService_FrameReady(object? sender, NativeScreenFrameEventArgs e)
        {
            if (!IsStreaming || !string.Equals(e.Codec, "h264", StringComparison.OrdinalIgnoreCase))
                return;

            if (_videoFrames?.Writer.TryWrite(e) != true)
                Interlocked.Increment(ref _droppedVideoFrames);
        }

        private void CaptureService_StreamingFailed(object? sender, string message)
        {
            WriteLog("Capture service failed: " + message);
            PublishStatus($"Native streaming failed: {message}");
            _ = Task.Run(() => StopAsync("capture service failed: " + message));
        }

        private async Task PublishLoopWithLoggingAsync(CancellationToken token)
        {
            try
            {
                await PublishLoopWithReconnectAsync(token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                WriteLog("Native publish loop failed: " + ex);
                PublishStatus($"Native publish failed: {ex.Message}");
                _currentStats.LogPath = _currentLogPath ?? string.Empty;
                StatsChanged?.Invoke(this, _currentStats.Clone());
                _ = Task.Run(() => StopAsync("publish loop failed: " + ex.Message));
            }
        }

        private async Task PublishLoopWithReconnectAsync(CancellationToken token)
        {
            var reconnectAttempts = 0;
            while (!token.IsCancellationRequested)
            {
                try
                {
                    await PublishLoopAsync(token);
                    return;
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex) when (IsTransportException(ex) && reconnectAttempts < 4)
                {
                    reconnectAttempts++;
                    WriteLog($"RTMP transport disconnected; reconnecting attempt {reconnectAttempts}/4. error={ex.Message}");
                    PublishStatus($"{_platformName} connection dropped. Reconnecting ({reconnectAttempts}/4)...");
                    await ReconnectPublisherAsync(token);
                    _captureService.RequestEncoderRefresh($"{_platformName} RTMP reconnected; publish needs fresh SPS/PPS and IDR");
                }
            }
        }

        private async Task PublishLoopAsync(CancellationToken token)
        {
            if (_videoFrames is null || _rtmpClient is null)
                return;

            using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(token);
            var videoTask = PublishVideoLoopAsync(attemptCts.Token);
            var audioTask = PublishAudioLoopAsync(attemptCts.Token);
            var completed = await Task.WhenAny(videoTask, audioTask);

            if (completed.IsFaulted)
            {
                attemptCts.Cancel();
                await AwaitSilentlyAsync(videoTask);
                await AwaitSilentlyAsync(audioTask);
                await completed;
            }

            await Task.WhenAll(videoTask, audioTask);
        }

        private async Task ReconnectPublisherAsync(CancellationToken token)
        {
            var target = _rtmpTarget ?? throw new InvalidOperationException("RTMP target is not available.");

            _rtmpClient?.Dispose();
            _rtmpClient = new NativeRtmpClient();
            _aacEncoder?.Dispose();
            _aacEncoder = null;
            _firstFrameTimestamp = 0;
            _nextVideoTimestampMilliseconds = 0;
            _sequenceHeaderSent = false;
            _lastSequenceHeaderSps = null;
            _lastSequenceHeaderPps = null;
            _lastVideoKeyFrameAtUtc = DateTimeOffset.MinValue;
            _lastVideoKeyFrameRefreshAtUtc = DateTimeOffset.MinValue;
            _lastEncodedFingerprint = 0;
            _lastPublishedVideoTimestamp = -1;
            _lastPublishedVideoAtUtc = DateTimeOffset.MinValue;
            Interlocked.Exchange(ref _videoFramesSinceLastKeyFrame, 0);
            _lastVideoStallRecoveryAtUtc = DateTimeOffset.MinValue;

            await Task.Delay(TimeSpan.FromSeconds(1), token);
            await _rtmpClient.ConnectAndPublishAsync(target, token);
            WriteLog("RTMP publisher reconnected.");
            PublishStatus($"{_platformName} connection restored.");
        }

        private static async Task AwaitSilentlyAsync(Task task)
        {
            try
            {
                await task;
            }
            catch
            {
            }
        }

        private static bool IsTransportException(Exception ex)
        {
            return ex is IOException ||
                   ex is SocketException ||
                   ex.InnerException is IOException ||
                   ex.InnerException is SocketException;
        }

        private async Task PublishVideoLoopAsync(CancellationToken token)
        {
            if (_videoFrames is null || _rtmpClient is null)
                return;

            await foreach (var frame in _videoFrames.Reader.ReadAllAsync(token))
            {
                token.ThrowIfCancellationRequested();
                var publishFrame = frame;

                var nalUnits = H264AnnexB.SplitNalUnits(publishFrame.FrameData);
                if (nalUnits.Count == 0)
                    continue;

                if (_firstFrameTimestamp == 0)
                    _firstFrameTimestamp = publishFrame.Timestamp;

                var timestamp = Math.Max(0, publishFrame.Timestamp - _firstFrameTimestamp);
                if (_lastPublishedVideoTimestamp >= 0 && timestamp <= _lastPublishedVideoTimestamp)
                    timestamp = _lastPublishedVideoTimestamp + 1;
                var parameterSets = H264AnnexB.ExtractParameterSets(nalUnits);
                var sequenceHeaderChanged =
                    parameterSets.Sps is not null &&
                    parameterSets.Pps is not null &&
                    (_lastSequenceHeaderSps is null ||
                     _lastSequenceHeaderPps is null ||
                     !parameterSets.Sps.AsSpan().SequenceEqual(_lastSequenceHeaderSps) ||
                     !parameterSets.Pps.AsSpan().SequenceEqual(_lastSequenceHeaderPps));
                if ((!_sequenceHeaderSent || sequenceHeaderChanged) &&
                    parameterSets.Sps is not null &&
                    parameterSets.Pps is not null)
                {
                    await _rtmpClient.SendVideoAsync(
                        RtmpH264Packet.BuildSequenceHeader(parameterSets.Sps, parameterSets.Pps),
                        (uint)timestamp,
                        isKeyFrame: true,
                        token);
                    _sequenceHeaderSent = true;
                    _lastSequenceHeaderSps = parameterSets.Sps;
                    _lastSequenceHeaderPps = parameterSets.Pps;
                    _lastSequenceHeaderSentAtUtc = DateTimeOffset.UtcNow;
                    var headers = Interlocked.Increment(ref _sequenceHeadersSent);
                    WriteLog($"H.264 sequence header sent={headers}; timestampMs={timestamp}; keyFrame={publishFrame.IsKeyFrame}.");
                }
                else if (!_sequenceHeaderSent)
                {
                    var waitingDrops = Interlocked.Increment(ref _videoFramesDroppedBeforeHeader);
                    var activeOutputFps = GetActiveOutputFps();
                    if (waitingDrops == 1 || waitingDrops % activeOutputFps == 0)
                        WriteLog($"Waiting for fresh H.264 SPS/PPS before publishing video; dropped pre-header frames={waitingDrops}.");
                    continue;
                }

                var videoPayload = RtmpH264Packet.BuildVideoFrame(nalUnits, includeParameterSets: false, publishFrame.IsKeyFrame);
                if (videoPayload.Length == 0)
                    continue;

                var previousFingerprint = _lastEncodedFingerprint;
                var fingerprint = SampleFrameFingerprint(publishFrame.FrameData);
                var repeatedEncodedFrame = previousFingerprint != 0 && fingerprint == previousFingerprint;
                if (repeatedEncodedFrame)
                    Interlocked.Increment(ref _repeatedEncodedFrames);
                _lastEncodedFingerprint = fingerprint;

                if (publishFrame.IsKeyFrame)
                {
                    await RefreshSequenceHeaderForTwitchAsync(parameterSets.Sps, parameterSets.Pps, timestamp, token);
                    var keyFrames = Interlocked.Increment(ref _videoKeyFramesSent);
                    Interlocked.Exchange(ref _videoFramesSinceLastKeyFrame, 0);
                    _lastVideoKeyFrameAtUtc = DateTimeOffset.UtcNow;
                    _lastVideoKeyFrameRefreshAtUtc = DateTimeOffset.MinValue;
                    WriteLog($"H.264 keyframe sent={keyFrames}; timestampMs={timestamp}; bytes={publishFrame.FrameData.Length}.");
                }
                else
                {
                    WatchForMissingTwitchKeyFrame(timestamp);
                }

                var now = DateTimeOffset.UtcNow;
                var wallGapMs = _lastPublishedVideoAtUtc == DateTimeOffset.MinValue
                    ? 0
                    : (now - _lastPublishedVideoAtUtc).TotalMilliseconds;
                var timestampGapMs = _lastPublishedVideoTimestamp < 0
                    ? 0
                    : timestamp - _lastPublishedVideoTimestamp;
                _lastPublishedVideoAtUtc = now;
                _lastPublishedVideoTimestamp = timestamp;
                var published = Interlocked.Increment(ref _videoFramesPublished);
                if (published == 1 ||
                    wallGapMs > 120 ||
                    timestampGapMs > 120 ||
                    repeatedEncodedFrame && Interlocked.Read(ref _repeatedEncodedFrames) % GetActiveOutputFps() == 0)
                {
                    WriteLog($"video publish diagnostic frame={published}; key={publishFrame.IsKeyFrame}; bytes={publishFrame.FrameData.Length}; hash=0x{fingerprint:X8}; timestampMs={timestamp}; tsGapMs={timestampGapMs:0}; wallGapMs={wallGapMs:0}; queue={_videoFrames.Reader.Count}; repeated={Interlocked.Read(ref _repeatedEncodedFrames)}.");
                }

                await _rtmpClient.SendVideoAsync(videoPayload, (uint)timestamp, publishFrame.IsKeyFrame, token);
                Interlocked.Add(ref _videoBytesSent, videoPayload.Length);
                UpdateStats(publishFrame, timestamp);
            }
        }

        private NativeScreenFrameEventArgs DrainToNewestVideoFrame(NativeScreenFrameEventArgs current)
        {
            if (_videoFrames is null)
                return current;

            var dropped = 0;
            while (_videoFrames.Reader.TryRead(out var newerFrame))
            {
                current = newerFrame;
                dropped++;
            }

            if (dropped > 0)
            {
                var totalDropped = Interlocked.Add(ref _droppedVideoFrames, dropped);
                if (dropped > 1 || totalDropped % OutputFps == 0)
                    WriteLog($"Dropped {dropped} stale encoded video frame(s) before RTMP publish to keep {_platformName} close to live; totalDropped={totalDropped}.");
            }

            return current;
        }

        private async Task RefreshSequenceHeaderForTwitchAsync(byte[]? sps, byte[]? pps, long timestamp, CancellationToken token)
        {
            if (_rtmpClient is null)
                return;

            sps ??= _lastSequenceHeaderSps;
            pps ??= _lastSequenceHeaderPps;
            if (sps is null || pps is null)
                return;

            var now = DateTimeOffset.UtcNow;
            if (_lastSequenceHeaderSentAtUtc != DateTimeOffset.MinValue &&
                now - _lastSequenceHeaderSentAtUtc < TwitchSequenceHeaderRefreshInterval)
            {
                return;
            }

            await _rtmpClient.SendVideoAsync(
                RtmpH264Packet.BuildSequenceHeader(sps, pps),
                (uint)timestamp,
                isKeyFrame: true,
                token);
            _lastSequenceHeaderSentAtUtc = now;
            var headers = Interlocked.Increment(ref _sequenceHeadersSent);
            WriteLog($"H.264 sequence header refreshed for {_platformName} decoder recovery={headers}; timestampMs={timestamp}.");
        }

        private void WatchForMissingTwitchKeyFrame(long timestamp)
        {
            var deltaFrames = Interlocked.Increment(ref _videoFramesSinceLastKeyFrame);
            var now = DateTimeOffset.UtcNow;
            var keyFrameAge = _lastVideoKeyFrameAtUtc == DateTimeOffset.MinValue
                ? TimeSpan.MaxValue
                : now - _lastVideoKeyFrameAtUtc;
            var refreshAge = _lastVideoKeyFrameRefreshAtUtc == DateTimeOffset.MinValue
                ? TimeSpan.MaxValue
                : now - _lastVideoKeyFrameRefreshAtUtc;

            var keyFrameIntervalFrames = GetTwitchKeyFrameIntervalFrames();
            if (deltaFrames < keyFrameIntervalFrames ||
                keyFrameAge < TwitchKeyFrameRefreshInterval ||
                refreshAge < TwitchKeyFrameRefreshInterval)
            {
                return;
            }

            _lastVideoKeyFrameRefreshAtUtc = now;
            WriteLog($"No {_platformName} H.264 keyframe for {keyFrameAge.TotalSeconds:0.0}s / {deltaFrames} frames; requesting a GPU IDR without recreating capture. timestampMs={timestamp}; encoderEvents=input:{_captureService.EncoderHardwareInputRequests} output:{_captureService.EncoderHardwareOutputRequests} pending:{_captureService.EncoderPendingHardwareInputs} pump:{_captureService.EncoderUsesHardwareEventPump}.");
            _captureService.RequestRecoveryKeyFrame($"{_platformName} ingest needs a fresh 2-second H.264 IDR/keyframe cadence");
        }

        private int GetActiveOutputFps()
        {
            return Math.Clamp(_captureService.CurrentTargetFps, 1, OutputFps);
        }

        private int GetTwitchKeyFrameIntervalFrames()
        {
            return Math.Max(1, GetActiveOutputFps() * 2);
        }

        private static uint SampleFrameFingerprint(byte[] data)
        {
            unchecked
            {
                var hash = 2166136261u;
                if (data.Length == 0)
                    return hash;

                var stride = Math.Max(1, data.Length / 64);
                for (var i = 0; i < data.Length; i += stride)
                {
                    hash ^= data[i];
                    hash *= 16777619u;
                }

                hash ^= (uint)data.Length;
                hash *= 16777619u;
                return hash;
            }
        }

        private async Task PublishAudioLoopAsync(CancellationToken token)
        {
            if (_audioFrames is null || _rtmpClient is null)
                return;

            if (!PublishRawAacFrames)
            {
                WriteLog($"AAC audio publishing is disabled for {_platformName} stability; video RTMP publishing will continue without sending AAC headers or raw AAC frames.");
                await foreach (var _ in _audioFrames.Reader.ReadAllAsync(token))
                {
                    token.ThrowIfCancellationRequested();
                }

                return;
            }

            var audioPacketsSent = 0L;
            var audioPacketsSkipped = 0L;
            var audioEncodeFailures = 0;
            var skippedStartupFrames = 0L;
            var sequenceHeaderSent = false;
            double? audioTimestampMilliseconds = null;
            var audioFrameDurationMilliseconds = 1024.0 * 1000.0 / NativeAudioMixerSession.TargetSampleRate;

            try
            {
                CreateAacEncoder();
            }
            catch (Exception ex) when (!token.IsCancellationRequested)
            {
                WriteLog($"AAC startup failed; disabling audio so video can continue. error={ex.Message}");
                return;
            }

            await foreach (var frame in _audioFrames.Reader.ReadAllAsync(token))
            {
                token.ThrowIfCancellationRequested();

                if (_firstFrameTimestamp == 0)
                    continue;

                GuardAgainstVideoStall();

                var liveTimestamp = Math.Max(0, Volatile.Read(ref _lastPublishedVideoTimestamp));
                if (Interlocked.Read(ref _videoKeyFramesSent) == 0 ||
                    Volatile.Read(ref _lastPublishedVideoTimestamp) < 0 ||
                    liveTimestamp < AudioStartupDelayMilliseconds)
                {
                    skippedStartupFrames++;
                    if (skippedStartupFrames == 1 || skippedStartupFrames % 100 == 0)
                        WriteLog($"Delaying AAC audio until video ingest is stable; skippedStartupFrames={skippedStartupFrames}; liveTimestampMs={liveTimestamp}.");
                    continue;
                }

                try
                {
                    if (_aacEncoder is null)
                        CreateAacEncoder();

                    if (!sequenceHeaderSent)
                    {
                        await SendAacSequenceHeaderAsync(0, token);
                        sequenceHeaderSent = true;
                    }

                    foreach (var encodedAacFrame in _aacEncoder!.Encode(frame.Pcm16Stereo))
                    {
                        var aacFrame = RtmpAudioPacket.NormalizeRawAacFrame(encodedAacFrame);
                        if (aacFrame.Length == 0)
                            continue;

                        if (audioTimestampMilliseconds is null || audioTimestampMilliseconds.Value + 250 < liveTimestamp)
                        {
                            audioTimestampMilliseconds = liveTimestamp;
                            if (audioPacketsSent > 0)
                                WriteLog($"AAC audio clock resynced to live timestamp {liveTimestamp}ms.");
                        }

                        var timestamp = (uint)Math.Max(0, Math.Round(audioTimestampMilliseconds.Value));
                        if (audioPacketsSent == 0)
                            WriteLog($"First AAC frame ready; bytes={aacFrame.Length}; timestampMs={timestamp}; skippedStartupFrames={skippedStartupFrames}.");
                        if (!PublishRawAacFrames)
                        {
                            audioPacketsSkipped++;
                            if (audioPacketsSkipped == 1 || audioPacketsSkipped % 100 == 0)
                                WriteLog($"AAC raw frame skipped to keep RTMP video live; skipped={audioPacketsSkipped}; bytes={aacFrame.Length}; timestampMs={timestamp}. Raw AAC publishing is disabled after {_platformName} aborted on the first AAC frame.");
                            audioTimestampMilliseconds += audioFrameDurationMilliseconds;
                            continue;
                        }

                        if (!sequenceHeaderSent)
                        {
                            await SendAacSequenceHeaderAsync(timestamp, token);
                            sequenceHeaderSent = true;
                        }

                        await _rtmpClient.SendAudioAsync(RtmpAudioPacket.BuildAacFrame(aacFrame), timestamp, token);
                        audioPacketsSent++;
                        if (audioPacketsSent == 1 || audioPacketsSent % 100 == 0)
                            WriteLog($"AAC audio packets sent={audioPacketsSent}; lastBytes={aacFrame.Length}; timestampMs={timestamp}.");

                        audioTimestampMilliseconds += audioFrameDurationMilliseconds;
                    }
                }
                catch (Exception ex) when (!token.IsCancellationRequested)
                {
                    if (ex is IOException || ex is SocketException)
                        throw;

                    audioEncodeFailures++;
                    WriteLog($"AAC encode failed; recreating audio encoder. failures={audioEncodeFailures}; packetsSent={audioPacketsSent}; error={ex.Message}");
                    _aacEncoder?.Dispose();
                    _aacEncoder = null;

                    if (audioEncodeFailures <= 5)
                    {
                        CreateAacEncoder();
                        sequenceHeaderSent = false;
                    }
                    else
                    {
                        WriteLog("AAC audio disabled after repeated encoder failures. Video publishing will continue.");
                        return;
                    }
                }
            }
        }

        private void GuardAgainstVideoStall()
        {
            var lastVideoAt = _lastPublishedVideoAtUtc;
            if (lastVideoAt == DateTimeOffset.MinValue)
                return;

            var now = DateTimeOffset.UtcNow;
            var videoAge = now - lastVideoAt;
            if (videoAge < VideoStallRecoveryInterval)
                return;

            if (now - _lastVideoStallRecoveryAtUtc >= VideoStallRecoveryInterval)
            {
                _lastVideoStallRecoveryAtUtc = now;
                WriteLog($"Video publish stalled for {videoAge.TotalSeconds:0.0}s while audio is still active; requesting WGC/encoder recovery.");
                _captureService.RequestEncoderRefresh($"{_platformName} video stalled while audio continued; refresh encoder and WGC boundary");
                _captureService.RequestRecoveryKeyFrame($"{_platformName} video stalled while audio continued");
            }

            if (videoAge >= VideoStallReconnectThreshold)
                throw new IOException($"No video frames published for {videoAge.TotalSeconds:0.0}s while audio continued.");
        }

        private void CreateAacEncoder()
        {
            _aacEncoder = new FdkAacStreamingEncoder(sampleRate: NativeAudioMixerSession.TargetSampleRate, channels: 2, bitrate: 160000);
            WriteLog($"AAC encoder created. sampleRate={NativeAudioMixerSession.TargetSampleRate}; asc={Convert.ToHexString(_aacEncoder.AudioSpecificConfig)}.");
        }

        private async Task SendAacSequenceHeaderAsync(uint timestamp, CancellationToken token)
        {
            if (_rtmpClient is null || _aacEncoder is null)
                throw new InvalidOperationException("RTMP client is not connected.");

            await _rtmpClient.SendAudioAsync(RtmpAudioPacket.BuildAacSequenceHeader(_aacEncoder.AudioSpecificConfig), timestamp, token);
            WriteLog($"AAC sequence header sent. timestampMs={timestamp}; asc={Convert.ToHexString(_aacEncoder.AudioSpecificConfig)}.");
        }

        private void UpdateStats(NativeScreenFrameEventArgs frame, long timestamp)
        {
            _currentStats.Frame++;
            var now = DateTimeOffset.UtcNow;
            var elapsed = now - _lastStatsPublishedAtUtc;
            var shouldPublish = _lastStatsPublishedAtUtc == DateTimeOffset.MinValue ||
                                elapsed >= TimeSpan.FromSeconds(1);

            _currentStats.CaptureFps = _captureService.CaptureFps;
            _currentStats.EncodedFps = _captureService.EncodedFps;
            _currentStats.Fps = _captureService.EncodedFps > 0 ? _captureService.EncodedFps : _captureService.CaptureFps;
            _currentStats.Bitrate = $"{_captureService.CurrentBitrate / 1000}k";
            _currentStats.Speed = $"{_captureService.LastEncodeMilliseconds:0.0}ms encode";
            _currentStats.EncodeMilliseconds = _captureService.LastEncodeMilliseconds;
            _currentStats.CaptureMilliseconds = _captureService.LastCaptureMilliseconds;
            _currentStats.PreviewMilliseconds = _captureService.LastPreviewMilliseconds;
            _currentStats.SendFps = shouldPublish && elapsed.TotalSeconds > 0
                ? (_currentStats.Frame - _lastStatsFrameCount) / elapsed.TotalSeconds
                : _currentStats.SendFps;
            _currentStats.DroppedFrames = Interlocked.Read(ref _droppedVideoFrames);
            _currentStats.DuplicatedFrames = Interlocked.Read(ref _repeatedEncodedFrames);
            _currentStats.OutputTime = TimeSpan.FromMilliseconds(timestamp).ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture);
            _currentStats.LogPath = _currentLogPath ?? string.Empty;
            if (shouldPublish)
            {
                var queueDepth = _videoFrames?.Reader.Count ?? 0;
                var seconds = elapsed.TotalSeconds > 0 ? elapsed.TotalSeconds : 1.0;
                var processCpuTime = _process.TotalProcessorTime;
                var cpuDeltaMilliseconds = Math.Max(0.0, (processCpuTime - _lastStatsProcessCpuTime).TotalMilliseconds);
                var processCpuPercent = cpuDeltaMilliseconds / (seconds * Environment.ProcessorCount * 1000.0) * 100.0;
                var workingSetMegabytes = _process.WorkingSet64 / 1024.0 / 1024.0;
                var privateMegabytes = _process.PrivateMemorySize64 / 1024.0 / 1024.0;
                var managedMegabytes = GC.GetTotalMemory(forceFullCollection: false) / 1024.0 / 1024.0;
                var gen0Collections = GC.CollectionCount(0);
                var gen1Collections = GC.CollectionCount(1);
                var gen2Collections = GC.CollectionCount(2);
                var gen0Delta = gen0Collections - _lastStatsGen0Collections;
                var gen1Delta = gen1Collections - _lastStatsGen1Collections;
                var gen2Delta = gen2Collections - _lastStatsGen2Collections;
                var videoBytesSent = Interlocked.Read(ref _videoBytesSent);
                var videoBytesPerSecond = (videoBytesSent - _lastStatsVideoBytes) / seconds;
                var videoMegabitsPerSecond = videoBytesPerSecond * 8.0 / 1_000_000.0;
                var rtmpLastSendMilliseconds = _rtmpClient?.LastSendMilliseconds ?? 0.0;
                var rtmpMaxSendMilliseconds = _rtmpClient?.MaxSendMilliseconds ?? 0.0;
                var rtmpSlowSends = _rtmpClient?.SlowSendCount ?? 0;
                var rtmpSendBytes = _rtmpClient?.BytesSent ?? 0;
                var bottleneck = ClassifyBottleneck(
                    _currentStats,
                    queueDepth,
                    processCpuPercent,
                    rtmpLastSendMilliseconds,
                    rtmpMaxSendMilliseconds,
                    rtmpSlowSends);
                _lastStatsFrameCount = _currentStats.Frame;
                _lastStatsVideoBytes = videoBytesSent;
                _lastStatsProcessCpuTime = processCpuTime;
                _lastStatsGen0Collections = gen0Collections;
                _lastStatsGen1Collections = gen1Collections;
                _lastStatsGen2Collections = gen2Collections;
                _lastStatsPublishedAtUtc = now;
                StatsChanged?.Invoke(this, _currentStats.Clone());
                PublishStatus($"Native live: capture {_currentStats.CaptureFps:0.0} fps, encode {_currentStats.EncodedFps:0.0} fps, send {_currentStats.SendFps:0.0} fps.");
                WriteLog($"FPS_MONITOR capture={_currentStats.CaptureFps:0.0} encode={_currentStats.EncodedFps:0.0} send={_currentStats.SendFps:0.0} target={GetActiveOutputFps()} queue={queueDepth} encodeMs={_currentStats.EncodeMilliseconds:0.0} captureMs={_currentStats.CaptureMilliseconds:0.0} rtmpSendMs={rtmpLastSendMilliseconds:0.0} rtmpMaxMs={rtmpMaxSendMilliseconds:0.0} rtmpSlow={rtmpSlowSends} dropped={_currentStats.DroppedFrames} bottleneck={bottleneck}");
                WriteLog($"stats capture={_currentStats.CaptureFps:0.0}fps encode={_currentStats.EncodedFps:0.0}fps send={_currentStats.SendFps:0.0}fps target={GetActiveOutputFps()}fps bitrate={_currentStats.Bitrate} quality={_captureService.CurrentQuality.Name} encodeMs={_currentStats.EncodeMilliseconds:0.0} captureMs={_currentStats.CaptureMilliseconds:0.0} previewMs={_currentStats.PreviewMilliseconds:0.0} queue={queueDepth} repeated={Interlocked.Read(ref _repeatedEncodedFrames)} keyframes={Interlocked.Read(ref _videoKeyFramesSent)} headers={Interlocked.Read(ref _sequenceHeadersSent)} dropped={_currentStats.DroppedFrames} bottleneck={bottleneck} perf cpu={processCpuPercent:0.0}% threads={_process.Threads.Count} workingSet={workingSetMegabytes:0}MB private={privateMegabytes:0}MB managed={managedMegabytes:0}MB gc={gen0Delta}/{gen1Delta}/{gen2Delta} videoMbps={videoMegabitsPerSecond:0.00} rtmpSendMs={rtmpLastSendMilliseconds:0.0} rtmpMaxMs={rtmpMaxSendMilliseconds:0.0} rtmpSlow={rtmpSlowSends} rtmpBytes={rtmpSendBytes} encoderEvents=input:{_captureService.EncoderHardwareInputRequests} output:{_captureService.EncoderHardwareOutputRequests} pending:{_captureService.EncoderPendingHardwareInputs} pump:{_captureService.EncoderUsesHardwareEventPump} encoder='{_captureService.EncoderMode}' input='{_captureService.EncoderInputFormat}' gpu='{_captureService.EncoderGpuDeviceMode}'");
            }
        }

        private string ClassifyBottleneck(
            NativeStreamingStats stats,
            int queueDepth,
            double processCpuPercent,
            double rtmpLastSendMilliseconds,
            double rtmpMaxSendMilliseconds,
            long rtmpSlowSends)
        {
            var targetFps = Math.Max(1, GetActiveOutputFps());
            if (rtmpLastSendMilliseconds >= 80 || rtmpMaxSendMilliseconds >= 200 || queueDepth > 0 || stats.SendFps < targetFps * 0.75 && rtmpSlowSends > 0)
                return "rtmp/network";

            if (stats.EncodedFps > 0 && stats.EncodedFps < targetFps * 0.80 || stats.EncodeMilliseconds > 12)
                return "encoder/gpu";

            if (stats.CaptureFps > 0 && stats.CaptureFps < targetFps * 0.80)
                return "capture/gpu";

            if (processCpuPercent >= 85)
                return "cpu";

            if (stats.SendFps > 0 && stats.SendFps < targetFps * 0.85)
                return "publisher";

            return "none";
        }

        private void ResetStats()
        {
            _currentStats.Frame = 0;
            _currentStats.Fps = 0;
            _currentStats.CaptureFps = 0;
            _currentStats.EncodedFps = 0;
            _currentStats.SendFps = 0;
            _currentStats.Bitrate = "--";
            _currentStats.Speed = "--";
            _currentStats.DroppedFrames = 0;
            _currentStats.DuplicatedFrames = 0;
            _currentStats.EncodeMilliseconds = 0;
            _currentStats.CaptureMilliseconds = 0;
            _currentStats.PreviewMilliseconds = 0;
            _currentStats.OutputTime = "--";
            _currentStats.LogPath = _currentLogPath ?? string.Empty;
            _sequenceHeaderSent = false;
            _lastSequenceHeaderSps = null;
            _lastSequenceHeaderPps = null;
            _droppedVideoFrames = 0;
            _videoKeyFramesSent = 0;
            _sequenceHeadersSent = 0;
            _videoFramesDroppedBeforeHeader = 0;
            _videoFramesSinceLastKeyFrame = 0;
            _videoFramesPublished = 0;
            _repeatedEncodedFrames = 0;
            _lastEncodedFingerprint = 0;
            _lastPublishedVideoTimestamp = -1;
            _lastPublishedVideoAtUtc = DateTimeOffset.MinValue;
            _lastVideoKeyFrameAtUtc = DateTimeOffset.MinValue;
            _lastVideoKeyFrameRefreshAtUtc = DateTimeOffset.MinValue;
            _lastSequenceHeaderSentAtUtc = DateTimeOffset.MinValue;
            _lastVideoStallRecoveryAtUtc = DateTimeOffset.MinValue;
            _nextVideoTimestampMilliseconds = 0;
            _firstFrameTimestamp = 0;
            _lastStatsFrameCount = 0;
            _lastStatsVideoBytes = 0;
            _videoBytesSent = 0;
            _lastStatsProcessCpuTime = _process.TotalProcessorTime;
            _lastStatsGen0Collections = GC.CollectionCount(0);
            _lastStatsGen1Collections = GC.CollectionCount(1);
            _lastStatsGen2Collections = GC.CollectionCount(2);
            _lastStatsPublishedAtUtc = DateTimeOffset.MinValue;
            StatsChanged?.Invoke(this, _currentStats.Clone());
        }

        private void PublishStatus(string message)
        {
            LastStatus = message;
            StatusChanged?.Invoke(this, message);
        }

        private void StartLog(
            string safeUrl,
            StreamingQualityProfile quality,
            int videoBitrateKbps,
            bool lowLatency)
        {
            CloseLog();

            var logFolder = ResolveStreamingLogFolder();
            Directory.CreateDirectory(logFolder);
            _currentLogPath = Path.Combine(logFolder, $"native-streaming-{DateTimeOffset.Now:yyyyMMdd-HHmmss}.txt");
            lock (_logLock)
            {
                _logWriter = new StreamWriter(
                    new FileStream(
                        _currentLogPath,
                        FileMode.Create,
                        FileAccess.Write,
                        FileShare.ReadWrite,
                        bufferSize: 64 * 1024,
                        options: FileOptions.SequentialScan),
                    Encoding.UTF8,
                    bufferSize: 16 * 1024)
                {
                    // Do not force a disk/OneDrive write from capture, encoder, and RTMP
                    // threads for every diagnostic line. CloseLog still flushes the buffer.
                    AutoFlush = false
                };
            }

            WriteLog("Zink Native Streaming Log");
            WriteLog("Started: " + DateTimeOffset.Now);
            WriteLog($"Output request: {quality.Width}x{quality.Height} @ {_configuredOutputFps}fps; quality={quality.Name}; bitrate={videoBitrateKbps}k; lowLatency={lowLatency}");
            WriteLog("Capture: Windows Graphics Capture");
            WriteLog("Encoder: Media Foundation H.264");
            WriteLog("Transport: native RTMP publisher");
            WriteLog("target: " + safeUrl);
        }

        private void WriteObsStylePipelineLog(
            StreamingQualityProfile quality,
            int videoBitrateKbps)
        {
            var pipeline = new ObsStyleStreamingPipeline(new ObsStyleStreamingPipelineOptions
            {
                Width = quality.Width,
                Height = quality.Height,
                Fps = _configuredOutputFps,
                VideoBitrateKbps = videoBitrateKbps,
                VideoEncoder = "GPU H.264 hardware MFT",
                CaptureMode = "Strict Windows Graphics Capture GPU desktop source",
                OutputProtocol = "RTMP",
                UseDedicatedRenderLoop = true,
                UseDirectNvenc = false,
                UseGameCaptureHook = false
            });

            WriteLog(pipeline.Describe());
            foreach (var stage in pipeline.Stages)
            {
                WriteLog(
                    $"OBS stage: {stage.Name}; backend={stage.Backend}; active={stage.Active}; notes={stage.Notes}");
            }
        }

        private void WriteLog(string message)
        {
            try
            {
                lock (_logLock)
                    _logWriter?.WriteLine($"[{DateTimeOffset.Now:O}] {message}");
            }
            catch
            {
            }
        }

        private void CloseLog()
        {
            try
            {
                lock (_logLock)
                {
                    _logWriter?.Flush();
                    _logWriter?.Dispose();
                    _logWriter = null;
                }
            }
            catch
            {
            }
        }

        private static string ResolveStreamingLogFolder()
        {
            var current = new DirectoryInfo(AppContext.BaseDirectory);
            while (current != null)
            {
                if (string.Equals(current.Name, "Main file", StringComparison.OrdinalIgnoreCase))
                {
                    var projectLogsPath = Path.Combine(current.FullName, "Logs");
                    if (Directory.Exists(projectLogsPath))
                        return Path.Combine(projectLogsPath, "Streaming Logs");
                }

                var logsPath = Path.Combine(current.FullName, "Logs");
                if (Directory.Exists(logsPath) &&
                    !current.FullName.Contains(Path.Combine("bin", "x64", "Debug"), StringComparison.OrdinalIgnoreCase))
                {
                    return Path.Combine(logsPath, "Streaming Logs");
                }

                current = current.Parent;
            }

            return Path.Combine(Zink.Services.DiagnosticLogService.LogDirectoryPath, "Streaming Logs");
        }
    }

    public sealed class NativeStreamingStats
    {
        public long Frame { get; set; }
        public double Fps { get; set; }
        public double CaptureFps { get; set; }
        public double EncodedFps { get; set; }
        public double SendFps { get; set; }
        public string Bitrate { get; set; } = "--";
        public string Speed { get; set; } = "--";
        public long DroppedFrames { get; set; }
        public long DuplicatedFrames { get; set; }
        public double CaptureMilliseconds { get; set; }
        public double EncodeMilliseconds { get; set; }
        public double PreviewMilliseconds { get; set; }
        public string OutputTime { get; set; } = "--";
        public string LogPath { get; set; } = string.Empty;

        public NativeStreamingStats Clone()
        {
            return new NativeStreamingStats
            {
                Frame = Frame,
                Fps = Fps,
                CaptureFps = CaptureFps,
                EncodedFps = EncodedFps,
                SendFps = SendFps,
                Bitrate = Bitrate,
                Speed = Speed,
                DroppedFrames = DroppedFrames,
                DuplicatedFrames = DuplicatedFrames,
                CaptureMilliseconds = CaptureMilliseconds,
                EncodeMilliseconds = EncodeMilliseconds,
                PreviewMilliseconds = PreviewMilliseconds,
                OutputTime = OutputTime,
                LogPath = LogPath
            };
        }
    }

    internal sealed class NativeRtmpTarget
    {
        private NativeRtmpTarget(Uri serverUri, string app, string streamName)
        {
            ServerUri = serverUri;
            App = app;
            StreamName = streamName;
        }

        public Uri ServerUri { get; }
        public string Host => ServerUri.Host;
        public bool UsesTls => string.Equals(ServerUri.Scheme, "rtmps", StringComparison.OrdinalIgnoreCase);
        public int Port => ServerUri.Port > 0 ? ServerUri.Port : UsesTls ? 443 : 1935;
        public string App { get; }
        public string StreamName { get; }
        public string TcUrl => $"{ServerUri.Scheme}://{Host}/{App}";
        public string SafeUrl => $"{TcUrl}/***stream-key-hidden***";

        public static NativeRtmpTarget From(string serverUrl, string streamKey, string defaultServerUrl)
        {
            var baseUrl = string.IsNullOrWhiteSpace(serverUrl)
                ? defaultServerUrl
                : serverUrl.Trim().TrimEnd('/');

            var uri = new Uri(baseUrl);
            var app = uri.AbsolutePath.Trim('/');
            if (string.IsNullOrWhiteSpace(app))
                app = "app";

            return new NativeRtmpTarget(uri, app, streamKey.Trim());
        }
    }

    internal sealed class NativeRtmpClient : IDisposable
    {
        private const int DefaultChunkSize = 65536;
        private readonly SemaphoreSlim _sendLock = new(1, 1);
        private readonly Dictionary<int, RtmpHeader> _receiveHeaders = new();
        private TcpClient? _tcpClient;
        private Stream? _stream;
        private uint _transactionId = 1;
        private uint _streamId = 1;
        private int _receiveChunkSize = 128;
        private long _bytesSent;
        private long _sendCount;
        private long _slowSendCount;
        private long _lastSendElapsedTicks;
        private long _maxSendElapsedTicks;

        public long BytesSent => Interlocked.Read(ref _bytesSent);
        public long SendCount => Interlocked.Read(ref _sendCount);
        public long SlowSendCount => Interlocked.Read(ref _slowSendCount);
        public double LastSendMilliseconds => TicksToMilliseconds(Interlocked.Read(ref _lastSendElapsedTicks));
        public double MaxSendMilliseconds => TicksToMilliseconds(Interlocked.Read(ref _maxSendElapsedTicks));

        public async Task ConnectAndPublishAsync(NativeRtmpTarget target, CancellationToken token)
        {
            _tcpClient = new TcpClient
            {
                NoDelay = true,
                SendBufferSize = 1024 * 1024,
                ReceiveBufferSize = 64 * 1024
            };
            await _tcpClient.ConnectAsync(target.Host, target.Port, token);
            var tcpStream = _tcpClient.GetStream();
            if (target.UsesTls)
            {
                var sslStream = new SslStream(tcpStream, leaveInnerStreamOpen: false);
                await sslStream.AuthenticateAsClientAsync(target.Host);
                _stream = sslStream;
            }
            else
            {
                _stream = tcpStream;
            }

            await HandshakeAsync(token);
            await SendSetChunkSizeAsync(DefaultChunkSize, token);
            await SendCommandAsync(3, 20, 0, Amf0.Connect(_transactionId++, target), token);
            await WaitForCommandResponseAsync("connect", token);
            await SendCommandAsync(3, 20, 0, Amf0.ReleaseStream(_transactionId++, target.StreamName), token);
            await DrainOptionalResponsesAsync(TimeSpan.FromMilliseconds(150), token);
            await SendCommandAsync(3, 20, 0, Amf0.FCPublish(_transactionId++, target.StreamName), token);
            await DrainOptionalResponsesAsync(TimeSpan.FromMilliseconds(150), token);
            var createStreamTransactionId = _transactionId++;
            await SendCommandAsync(3, 20, 0, Amf0.CreateStream(createStreamTransactionId), token);
            _streamId = await WaitForCreateStreamResponseAsync(createStreamTransactionId, token);
            await SendCommandAsync(3, 20, _streamId, Amf0.Publish(0, target.StreamName), token);
            await WaitForPublishAcceptedAsync(token);
        }

        public Task SendVideoAsync(byte[] payload, uint timestamp, bool isKeyFrame, CancellationToken token)
        {
            return SendMessageAsync(6, 9, _streamId, timestamp, payload, token);
        }

        public Task SendAudioAsync(byte[] payload, uint timestamp, CancellationToken token)
        {
            return SendMessageAsync(4, 8, _streamId, timestamp, payload, token);
        }

        private async Task HandshakeAsync(CancellationToken token)
        {
            if (_stream is null)
                throw new InvalidOperationException("RTMP stream is not connected.");

            var random = new Random();
            var c0c1 = new byte[1537];
            c0c1[0] = 3;
            random.NextBytes(c0c1.AsSpan(1));
            await _stream.WriteAsync(c0c1, token);

            var s0s1s2 = new byte[3073];
            await ReadExactlyAsync(_stream, s0s1s2, token);

            var c2 = new byte[1536];
            Buffer.BlockCopy(s0s1s2, 1, c2, 0, c2.Length);
            await _stream.WriteAsync(c2, token);
        }

        private async Task SendSetChunkSizeAsync(int chunkSize, CancellationToken token)
        {
            var payload = new byte[4];
            BinaryPrimitives.WriteInt32BigEndian(payload, chunkSize);
            await SendMessageAsync(2, 1, 0, 0, payload, token);
        }

        private Task SendCommandAsync(byte chunkStreamId, byte messageType, uint messageStreamId, byte[] payload, CancellationToken token)
        {
            return SendMessageAsync(chunkStreamId, messageType, messageStreamId, 0, payload, token);
        }

        private async Task SendMessageAsync(byte chunkStreamId, byte messageType, uint messageStreamId, uint timestamp, byte[] payload, CancellationToken token)
        {
            if (_stream is null)
                throw new InvalidOperationException("RTMP stream is not connected.");

            var sendStartedAt = Stopwatch.GetTimestamp();
            await _sendLock.WaitAsync(token);
            try
            {
                if (_stream is null)
                    throw new InvalidOperationException("RTMP stream is not connected.");

                var offset = 0;
                var first = true;
                while (offset < payload.Length || first)
                {
                    if (first)
                    {
                        var header = new byte[12];
                        header[0] = chunkStreamId;
                        WriteUInt24(header.AsSpan(1, 3), (int)Math.Min(timestamp, 0xFFFFFF));
                        WriteUInt24(header.AsSpan(4, 3), payload.Length);
                        header[7] = messageType;
                        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(8, 4), messageStreamId);
                        await _stream.WriteAsync(header, token);
                        first = false;
                    }
                    else
                    {
                        await _stream.WriteAsync(new[] { (byte)(0xC0 | chunkStreamId) }, token);
                    }

                    var count = Math.Min(DefaultChunkSize, payload.Length - offset);
                    if (count > 0)
                    {
                        await _stream.WriteAsync(payload.AsMemory(offset, count), token);
                        offset += count;
                    }

                    if (payload.Length == 0)
                        break;
                }
            }
            finally
            {
                _sendLock.Release();
                RecordSend(Stopwatch.GetTimestamp() - sendStartedAt, payload.Length);
            }
        }

        private void RecordSend(long elapsedTicks, int payloadBytes)
        {
            Interlocked.Exchange(ref _lastSendElapsedTicks, elapsedTicks);
            Interlocked.Increment(ref _sendCount);
            Interlocked.Add(ref _bytesSent, Math.Max(0, payloadBytes));
            if (TicksToMilliseconds(elapsedTicks) >= 50)
                Interlocked.Increment(ref _slowSendCount);

            while (true)
            {
                var currentMax = Interlocked.Read(ref _maxSendElapsedTicks);
                if (elapsedTicks <= currentMax)
                    return;

                if (Interlocked.CompareExchange(ref _maxSendElapsedTicks, elapsedTicks, currentMax) == currentMax)
                    return;
            }
        }

        private static double TicksToMilliseconds(long ticks)
        {
            return ticks <= 0
                ? 0.0
                : ticks * 1000.0 / Stopwatch.Frequency;
        }

        private async Task WaitForCommandResponseAsync(string commandName, CancellationToken token)
        {
            if (_stream is null)
                throw new InvalidOperationException("RTMP stream is not connected.");

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            try
            {
                while (!timeout.IsCancellationRequested)
                {
                    var message = await ReadMessageAsync(timeout.Token);
                    if (message.MessageType != 20 && message.MessageType != 17)
                        continue;

                    var values = Amf0.ReadValues(message.Payload);
                    if (values.Count == 0)
                        continue;

                    var command = values[0] as string;
                    if (string.Equals(command, "_error", StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException(Amf0.Describe(values, $"{commandName} failed"));

                    if (string.Equals(command, "_result", StringComparison.OrdinalIgnoreCase))
                        return;
                }
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                throw new TimeoutException($"Timed out waiting for RTMP {commandName} response.");
            }
        }

        private async Task<uint> WaitForCreateStreamResponseAsync(uint transactionId, CancellationToken token)
        {
            if (_stream is null)
                throw new InvalidOperationException("RTMP stream is not connected.");

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            try
            {
                while (!timeout.IsCancellationRequested)
                {
                    var message = await ReadMessageAsync(timeout.Token);
                    if (message.MessageType != 20 && message.MessageType != 17)
                        continue;

                    var values = Amf0.ReadValues(message.Payload);
                    if (values.Count == 0)
                        continue;

                    var command = values[0] as string;
                    if (string.Equals(command, "_error", StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException(Amf0.Describe(values, "createStream failed"));

                    if (!string.Equals(command, "_result", StringComparison.OrdinalIgnoreCase) ||
                        values.Count < 4 ||
                        values[1] is not double responseTransaction ||
                        Math.Abs(responseTransaction - transactionId) > 0.001 ||
                        values[3] is not double streamId)
                    {
                        continue;
                    }

                    return Math.Max(1u, (uint)Math.Round(streamId));
                }
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                throw new TimeoutException("Timed out waiting for RTMP createStream response.");
            }

            throw new TimeoutException("Timed out waiting for RTMP createStream response.");
        }

        private async Task WaitForPublishAcceptedAsync(CancellationToken token)
        {
            if (_stream is null)
                throw new InvalidOperationException("RTMP stream is not connected.");

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            try
            {
                while (!timeout.IsCancellationRequested)
                {
                    var message = await ReadMessageAsync(timeout.Token);
                    if (message.MessageType != 20 && message.MessageType != 17)
                        continue;

                    var values = Amf0.ReadValues(message.Payload);
                    if (values.Count == 0)
                        continue;

                    var command = values[0] as string;
                    if (string.Equals(command, "_error", StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException(Amf0.Describe(values, "publish failed"));

                    var description = Amf0.Describe(values, string.Empty);
                    if (description.Contains("NetStream.Publish.Start", StringComparison.OrdinalIgnoreCase))
                        return;

                    if (description.Contains("NetStream.Publish.BadName", StringComparison.OrdinalIgnoreCase) ||
                        description.Contains("NetStream.Publish.Denied", StringComparison.OrdinalIgnoreCase) ||
                        description.Contains("error", StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidOperationException(Amf0.Describe(values, "publish failed"));
                    }
                }
            }
            catch (EndOfStreamException ex)
            {
                throw new InvalidOperationException("The RTMP server rejected the publish request. Check that the saved stream key is current, then paste the stream key again.", ex);
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                throw new TimeoutException("Timed out waiting for the RTMP server to accept the publish command.");
            }
        }

        private async Task DrainOptionalResponsesAsync(TimeSpan duration, CancellationToken token)
        {
            if (_stream is null)
                return;

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(duration);
            try
            {
                while (!timeout.IsCancellationRequested && _tcpClient?.Available > 0)
                    await ReadMessageAsync(timeout.Token);
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
            }
        }

        private async Task<RtmpMessage> ReadMessageAsync(CancellationToken token)
        {
            if (_stream is null)
                throw new InvalidOperationException("RTMP stream is not connected.");

            var (fmt, csid) = await ReadBasicHeaderAsync(token);
            var header = await ReadMessageHeaderAsync(fmt, csid, token);

            var payload = new byte[header.Length];
            var offset = 0;
            while (offset < header.Length)
            {
                var chunk = Math.Min(_receiveChunkSize, header.Length - offset);
                await ReadExactlyAsync(_stream, payload, offset, chunk, token);
                offset += chunk;
                if (offset < header.Length)
                    await ReadContinuationHeaderAsync(csid, header, token);
            }

            if (header.MessageType == 1 && payload.Length >= 4)
                _receiveChunkSize = Math.Max(1, BinaryPrimitives.ReadInt32BigEndian(payload));

            return new RtmpMessage(header.MessageType, payload);
        }

        private async Task<(int Fmt, int Csid)> ReadBasicHeaderAsync(CancellationToken token)
        {
            if (_stream is null)
                throw new InvalidOperationException("RTMP stream is not connected.");

            var first = new byte[1];
            await ReadExactlyAsync(_stream, first, token);
            var fmt = first[0] >> 6;
            var csid = first[0] & 0x3F;
            if (csid == 0)
            {
                var extended = new byte[1];
                await ReadExactlyAsync(_stream, extended, token);
                csid = 64 + extended[0];
            }
            else if (csid == 1)
            {
                var extended = new byte[2];
                await ReadExactlyAsync(_stream, extended, token);
                csid = 64 + extended[0] + (extended[1] * 256);
            }

            return (fmt, csid);
        }

        private async Task<RtmpHeader> ReadMessageHeaderAsync(int fmt, int csid, CancellationToken token)
        {
            if (_stream is null)
                throw new InvalidOperationException("RTMP stream is not connected.");

            if (fmt == 0)
            {
                var bytes = new byte[11];
                await ReadExactlyAsync(_stream, bytes, token);
                var timestamp = ReadUInt24(bytes.AsSpan(0, 3));
                var length = ReadUInt24(bytes.AsSpan(3, 3));
                var messageType = bytes[6];
                var streamId = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(7, 4));
                if (timestamp == 0xFFFFFF)
                    await ReadExactlyAsync(_stream, new byte[4], token);

                var header = new RtmpHeader(timestamp, length, messageType, streamId);
                _receiveHeaders[csid] = header;
                return header;
            }

            if (!_receiveHeaders.TryGetValue(csid, out var previous))
                throw new InvalidDataException($"RTMP compressed header used before full header. csid={csid}; fmt={fmt}.");

            if (fmt == 1)
            {
                var bytes = new byte[7];
                await ReadExactlyAsync(_stream, bytes, token);
                var timestamp = ReadUInt24(bytes.AsSpan(0, 3));
                var length = ReadUInt24(bytes.AsSpan(3, 3));
                var messageType = bytes[6];
                if (timestamp == 0xFFFFFF)
                    await ReadExactlyAsync(_stream, new byte[4], token);

                var header = new RtmpHeader(timestamp, length, messageType, previous.MessageStreamId);
                _receiveHeaders[csid] = header;
                return header;
            }

            if (fmt == 2)
            {
                var bytes = new byte[3];
                await ReadExactlyAsync(_stream, bytes, token);
                var timestamp = ReadUInt24(bytes.AsSpan(0, 3));
                if (timestamp == 0xFFFFFF)
                    await ReadExactlyAsync(_stream, new byte[4], token);

                var header = previous with { Timestamp = timestamp };
                _receiveHeaders[csid] = header;
                return header;
            }

            if (previous.Timestamp == 0xFFFFFF)
                await ReadExactlyAsync(_stream, new byte[4], token);

            return previous;
        }

        private async Task ReadContinuationHeaderAsync(int expectedCsid, RtmpHeader header, CancellationToken token)
        {
            var (fmt, csid) = await ReadBasicHeaderAsync(token);
            if (fmt != 3 || csid != expectedCsid)
                throw new InvalidDataException($"Unexpected RTMP continuation header. expectedCsid={expectedCsid}; actualCsid={csid}; fmt={fmt}.");

            if (header.Timestamp == 0xFFFFFF && _stream is not null)
                await ReadExactlyAsync(_stream, new byte[4], token);
        }

        private static async Task ReadExactlyAsync(Stream stream, byte[] buffer, CancellationToken token)
        {
            await ReadExactlyAsync(stream, buffer, 0, buffer.Length, token);
        }

        private static async Task ReadExactlyAsync(Stream stream, byte[] buffer, int offset, int count, CancellationToken token)
        {
            var end = offset + count;
            while (offset < end)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(offset, end - offset), token);
                if (read <= 0)
                    throw new EndOfStreamException("RTMP server closed the connection while reading a response.");

                offset += read;
            }
        }

        private static void WriteUInt24(Span<byte> destination, int value)
        {
            destination[0] = (byte)((value >> 16) & 0xFF);
            destination[1] = (byte)((value >> 8) & 0xFF);
            destination[2] = (byte)(value & 0xFF);
        }

        private static int ReadUInt24(ReadOnlySpan<byte> source)
        {
            return (source[0] << 16) | (source[1] << 8) | source[2];
        }

        private readonly record struct RtmpMessage(byte MessageType, byte[] Payload);
        private readonly record struct RtmpHeader(int Timestamp, int Length, byte MessageType, uint MessageStreamId);

        public void Dispose()
        {
            _stream?.Dispose();
            _tcpClient?.Dispose();
            _stream = null;
            _tcpClient = null;
            _sendLock.Dispose();
        }
    }

    internal static class Amf0
    {
        public static byte[] Connect(uint transactionId, NativeRtmpTarget target)
        {
            using var stream = new MemoryStream();
            WriteString(stream, "connect");
            WriteNumber(stream, transactionId);
            WriteObjectStart(stream);
            WriteNamedString(stream, "app", target.App);
            WriteNamedString(stream, "type", "nonprivate");
            WriteNamedString(stream, "flashVer", "FMLE/3.0 (compatible; Zink)");
            WriteNamedString(stream, "tcUrl", target.TcUrl);
            WriteNamedBoolean(stream, "fpad", false);
            WriteNamedNumber(stream, "capabilities", 15);
            WriteNamedNumber(stream, "audioCodecs", 4071);
            WriteNamedNumber(stream, "videoCodecs", 252);
            WriteNamedNumber(stream, "videoFunction", 1);
            WriteNamedNumber(stream, "objectEncoding", 0);
            WriteObjectEnd(stream);
            return stream.ToArray();
        }

        public static byte[] CreateStream(uint transactionId)
        {
            using var stream = new MemoryStream();
            WriteString(stream, "createStream");
            WriteNumber(stream, transactionId);
            WriteNull(stream);
            return stream.ToArray();
        }

        public static byte[] ReleaseStream(uint transactionId, string streamName)
        {
            using var stream = new MemoryStream();
            WriteString(stream, "releaseStream");
            WriteNumber(stream, transactionId);
            WriteNull(stream);
            WriteString(stream, streamName);
            return stream.ToArray();
        }

        public static byte[] FCPublish(uint transactionId, string streamName)
        {
            using var stream = new MemoryStream();
            WriteString(stream, "FCPublish");
            WriteNumber(stream, transactionId);
            WriteNull(stream);
            WriteString(stream, streamName);
            return stream.ToArray();
        }

        public static byte[] Publish(uint transactionId, string streamName)
        {
            using var stream = new MemoryStream();
            WriteString(stream, "publish");
            WriteNumber(stream, transactionId);
            WriteNull(stream);
            WriteString(stream, streamName);
            WriteString(stream, "live");
            return stream.ToArray();
        }

        private static void WriteString(Stream stream, string value)
        {
            stream.WriteByte(0x02);
            WriteUtf8(stream, value);
        }

        private static void WriteNumber(Stream stream, double value)
        {
            stream.WriteByte(0x00);
            Span<byte> bytes = stackalloc byte[8];
            BinaryPrimitives.WriteInt64BigEndian(bytes, BitConverter.DoubleToInt64Bits(value));
            stream.Write(bytes);
        }

        private static void WriteNull(Stream stream)
        {
            stream.WriteByte(0x05);
        }

        private static void WriteObjectStart(Stream stream)
        {
            stream.WriteByte(0x03);
        }

        private static void WriteObjectEnd(Stream stream)
        {
            stream.WriteByte(0x00);
            stream.WriteByte(0x00);
            stream.WriteByte(0x09);
        }

        private static void WriteNamedString(Stream stream, string name, string value)
        {
            WritePropertyName(stream, name);
            WriteString(stream, value);
        }

        private static void WriteNamedNumber(Stream stream, string name, double value)
        {
            WritePropertyName(stream, name);
            WriteNumber(stream, value);
        }

        private static void WriteNamedBoolean(Stream stream, string name, bool value)
        {
            WritePropertyName(stream, name);
            stream.WriteByte(0x01);
            stream.WriteByte(value ? (byte)1 : (byte)0);
        }

        private static void WritePropertyName(Stream stream, string name)
        {
            WriteUtf8(stream, name, includeTypeMarker: false);
        }

        private static void WriteUtf8(Stream stream, string value, bool includeTypeMarker = false)
        {
            if (includeTypeMarker)
                stream.WriteByte(0x02);

            var bytes = Encoding.UTF8.GetBytes(value);
            stream.WriteByte((byte)((bytes.Length >> 8) & 0xFF));
            stream.WriteByte((byte)(bytes.Length & 0xFF));
            stream.Write(bytes);
        }

        public static IReadOnlyList<object?> ReadValues(byte[] payload)
        {
            var values = new List<object?>();
            var offset = 0;
            while (offset < payload.Length)
            {
                values.Add(ReadValue(payload, ref offset));
            }

            return values;
        }

        public static string Describe(IReadOnlyList<object?> values, string fallback)
        {
            var parts = new List<string>();
            foreach (var value in values)
            {
                switch (value)
                {
                    case string text when !string.IsNullOrWhiteSpace(text):
                        parts.Add(text);
                        break;
                    case Dictionary<string, object?> obj:
                        AddObjectText(parts, obj, "code");
                        AddObjectText(parts, obj, "level");
                        AddObjectText(parts, obj, "description");
                        break;
                }
            }

            return parts.Count > 0
                ? string.Join("; ", parts)
                : fallback;
        }

        private static void AddObjectText(List<string> parts, Dictionary<string, object?> obj, string key)
        {
            if (obj.TryGetValue(key, out var value) &&
                value is string text &&
                !string.IsNullOrWhiteSpace(text))
            {
                parts.Add(text);
            }
        }

        private static object? ReadValue(byte[] payload, ref int offset)
        {
            if (offset >= payload.Length)
                return null;

            var marker = payload[offset++];
            return marker switch
            {
                0x00 => ReadNumber(payload, ref offset),
                0x01 => offset < payload.Length && payload[offset++] != 0,
                0x02 => ReadUtf8(payload, ref offset),
                0x03 => ReadObject(payload, ref offset),
                0x05 => null,
                0x06 => null,
                0x08 => ReadEcmaArray(payload, ref offset),
                _ => null
            };
        }

        private static double ReadNumber(byte[] payload, ref int offset)
        {
            if (offset + 8 > payload.Length)
            {
                offset = payload.Length;
                return 0;
            }

            var bits = BinaryPrimitives.ReadInt64BigEndian(payload.AsSpan(offset, 8));
            offset += 8;
            return BitConverter.Int64BitsToDouble(bits);
        }

        private static string ReadUtf8(byte[] payload, ref int offset)
        {
            if (offset + 2 > payload.Length)
            {
                offset = payload.Length;
                return string.Empty;
            }

            var length = BinaryPrimitives.ReadUInt16BigEndian(payload.AsSpan(offset, 2));
            offset += 2;
            if (offset + length > payload.Length)
            {
                offset = payload.Length;
                return string.Empty;
            }

            var value = Encoding.UTF8.GetString(payload, offset, length);
            offset += length;
            return value;
        }

        private static Dictionary<string, object?> ReadObject(byte[] payload, ref int offset)
        {
            var result = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            while (offset + 3 <= payload.Length)
            {
                if (payload[offset] == 0 && payload[offset + 1] == 0 && payload[offset + 2] == 9)
                {
                    offset += 3;
                    break;
                }

                var name = ReadUtf8(payload, ref offset);
                result[name] = ReadValue(payload, ref offset);
            }

            return result;
        }

        private static Dictionary<string, object?> ReadEcmaArray(byte[] payload, ref int offset)
        {
            if (offset + 4 <= payload.Length)
                offset += 4;

            return ReadObject(payload, ref offset);
        }
    }

    internal static class H264AnnexB
    {
        public static IReadOnlyList<byte[]> SplitNalUnits(byte[] annexB)
        {
            var units = new List<byte[]>();
            var starts = new List<(int StartCode, int NalStart)>();

            for (var i = 0; i < annexB.Length - 3; i++)
            {
                if (annexB[i] == 0 && annexB[i + 1] == 0 && annexB[i + 2] == 1)
                {
                    starts.Add((i, i + 3));
                    i += 2;
                }
                else if (i < annexB.Length - 4 &&
                         annexB[i] == 0 && annexB[i + 1] == 0 && annexB[i + 2] == 0 && annexB[i + 3] == 1)
                {
                    starts.Add((i, i + 4));
                    i += 3;
                }
            }

            for (var i = 0; i < starts.Count; i++)
            {
                var nalStart = starts[i].NalStart;
                var nalEnd = i + 1 < starts.Count ? starts[i + 1].StartCode : annexB.Length;
                while (nalEnd > nalStart && annexB[nalEnd - 1] == 0)
                    nalEnd--;

                if (nalEnd > nalStart)
                {
                    var unit = new byte[nalEnd - nalStart];
                    Buffer.BlockCopy(annexB, nalStart, unit, 0, unit.Length);
                    units.Add(unit);
                }
            }

            return units;
        }

        public static (byte[]? Sps, byte[]? Pps) ExtractParameterSets(IReadOnlyList<byte[]> nalUnits)
        {
            byte[]? sps = null;
            byte[]? pps = null;
            foreach (var unit in nalUnits)
            {
                if (unit.Length == 0)
                    continue;

                var type = unit[0] & 0x1F;
                if (type == 7)
                    sps = unit;
                else if (type == 8)
                    pps = unit;
            }

            return (sps, pps);
        }
    }

    internal static class RtmpH264Packet
    {
        public static byte[] BuildSequenceHeader(byte[] sps, byte[] pps)
        {
            using var stream = new MemoryStream();
            stream.WriteByte(0x17);
            stream.WriteByte(0x00);
            stream.WriteByte(0x00);
            stream.WriteByte(0x00);
            stream.WriteByte(0x00);
            stream.WriteByte(0x01);
            stream.WriteByte(sps.Length > 3 ? sps[1] : (byte)0x64);
            stream.WriteByte(sps.Length > 3 ? sps[2] : (byte)0x00);
            stream.WriteByte(sps.Length > 3 ? sps[3] : (byte)0x1F);
            stream.WriteByte(0xFF);
            stream.WriteByte(0xE1);
            WriteUInt16(stream, sps.Length);
            stream.Write(sps);
            stream.WriteByte(0x01);
            WriteUInt16(stream, pps.Length);
            stream.Write(pps);
            return stream.ToArray();
        }

        public static byte[] BuildVideoFrame(IReadOnlyList<byte[]> nalUnits, bool includeParameterSets, bool isKeyFrame)
        {
            using var stream = new MemoryStream();
            stream.WriteByte(isKeyFrame ? (byte)0x17 : (byte)0x27);
            stream.WriteByte(0x01);
            stream.WriteByte(0x00);
            stream.WriteByte(0x00);
            stream.WriteByte(0x00);

            foreach (var unit in nalUnits)
            {
                if (unit.Length == 0)
                    continue;

                var type = unit[0] & 0x1F;
                if (!includeParameterSets && (type == 7 || type == 8 || type == 9))
                    continue;

                WriteUInt32(stream, unit.Length);
                stream.Write(unit);
            }

            return stream.Length > 5 ? stream.ToArray() : Array.Empty<byte>();
        }

        private static void WriteUInt16(Stream stream, int value)
        {
            stream.WriteByte((byte)((value >> 8) & 0xFF));
            stream.WriteByte((byte)(value & 0xFF));
        }

        private static void WriteUInt32(Stream stream, int value)
        {
            stream.WriteByte((byte)((value >> 24) & 0xFF));
            stream.WriteByte((byte)((value >> 16) & 0xFF));
            stream.WriteByte((byte)((value >> 8) & 0xFF));
            stream.WriteByte((byte)(value & 0xFF));
        }
    }

    internal static class RtmpAudioPacket
    {
        public static byte[] BuildAacSequenceHeader(byte[] audioSpecificConfig)
        {
            var payload = new byte[audioSpecificConfig.Length + 2];
            payload[0] = 0xAF; // AAC, 44 kHz bucket, 16-bit, stereo.
            payload[1] = 0x00; // AAC sequence header.
            Buffer.BlockCopy(audioSpecificConfig, 0, payload, 2, audioSpecificConfig.Length);
            return payload;
        }

        public static byte[] BuildAacFrame(byte[] rawAacFrame)
        {
            var payload = new byte[rawAacFrame.Length + 2];
            payload[0] = 0xAF;
            payload[1] = 0x01; // Raw AAC frame.
            Buffer.BlockCopy(rawAacFrame, 0, payload, 2, rawAacFrame.Length);
            return payload;
        }

        public static byte[] NormalizeRawAacFrame(byte[] aacFrame)
        {
            if (aacFrame.Length < 2)
                return Array.Empty<byte>();

            var hasAdtsHeader =
                aacFrame.Length >= 7 &&
                aacFrame[0] == 0xFF &&
                (aacFrame[1] & 0xF0) == 0xF0;

            if (!hasAdtsHeader)
                return aacFrame;

            var hasCrc = (aacFrame[1] & 0x01) == 0;
            var headerLength = hasCrc ? 9 : 7;
            return aacFrame.Length > headerLength
                ? aacFrame[headerLength..]
                : Array.Empty<byte>();
        }
    }

    internal sealed class FdkAacStreamingEncoder : IDisposable
    {
        private const int AacObjectTypeLowComplexity = 2;
        private const int ChannelModeStereo = 2;
        private const int TransportRaw = 0;
        private const int InputIdentifierAudioData = 0;
        private const int OutputIdentifierBitstreamData = 3;
        private const int SamplesPerAacFrame = 1024;
        private const int BytesPerSample = 2;

        private IntPtr _encoder;
        private readonly int _channels;
        private readonly int _bytesPerFrame;
        private readonly List<byte> _pendingPcm = new();
        private bool _disposed;

        public FdkAacStreamingEncoder(int sampleRate, int channels, int bitrate)
        {
            _channels = channels;
            _bytesPerFrame = SamplesPerAacFrame * channels * BytesPerSample;

            ThrowIfFdkFailed(FdkAacNative.aacEncOpen(ref _encoder, 0, (uint)channels), "aacEncOpen");
            SetParameter(AacEncoderParameter.Aot, AacObjectTypeLowComplexity);
            SetParameter(AacEncoderParameter.SampleRate, sampleRate);
            SetParameter(AacEncoderParameter.ChannelMode, ChannelModeStereo);
            SetParameter(AacEncoderParameter.ChannelOrder, 1);
            SetParameter(AacEncoderParameter.BitRate, bitrate);
            SetParameter(AacEncoderParameter.TransportMux, TransportRaw);
            SetParameter(AacEncoderParameter.Afterburner, 1);

            ThrowIfFdkFailed(FdkAacNative.aacEncEncode(_encoder, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero), "aacEncEncode(init)");
            var info = new AacEncInfoStruct();
            info.ConfBuf = new byte[AacEncInfoStruct.ConfBufLength];
            ThrowIfFdkFailed(FdkAacNative.aacEncInfo(_encoder, ref info), "aacEncInfo");

            AudioSpecificConfig = new byte[Math.Max(0, info.ConfSize)];
            if (AudioSpecificConfig.Length > 0)
                Buffer.BlockCopy(info.ConfBuf, 0, AudioSpecificConfig, 0, Math.Min(AudioSpecificConfig.Length, info.ConfBuf.Length));

            if (AudioSpecificConfig.Length == 0)
                AudioSpecificConfig = BuildAudioSpecificConfig(sampleRate, channels);
        }

        public byte[] AudioSpecificConfig { get; }

        public IReadOnlyList<byte[]> Encode(byte[] pcm16Stereo)
        {
            if (_disposed || pcm16Stereo.Length == 0)
                return Array.Empty<byte[]>();

            _pendingPcm.AddRange(pcm16Stereo);
            var frames = new List<byte[]>();
            while (_pendingPcm.Count >= _bytesPerFrame)
            {
                var input = _pendingPcm.GetRange(0, _bytesPerFrame).ToArray();
                _pendingPcm.RemoveRange(0, _bytesPerFrame);
                var encoded = EncodeFrame(input);
                if (encoded.Length > 0)
                    frames.Add(encoded);
            }

            return frames;
        }

        private byte[] EncodeFrame(byte[] pcmFrame)
        {
            var inputHandle = GCHandle.Alloc(pcmFrame, GCHandleType.Pinned);
            var output = new byte[8192];
            var outputHandle = GCHandle.Alloc(output, GCHandleType.Pinned);

            var inBufs = IntPtr.Zero;
            var inIds = IntPtr.Zero;
            var inSizes = IntPtr.Zero;
            var inElemSizes = IntPtr.Zero;
            var outBufs = IntPtr.Zero;
            var outIds = IntPtr.Zero;
            var outSizes = IntPtr.Zero;
            var outElemSizes = IntPtr.Zero;

            try
            {
                inBufs = AllocIntPtrArray(inputHandle.AddrOfPinnedObject());
                inIds = AllocIntArray(InputIdentifierAudioData);
                inSizes = AllocIntArray(pcmFrame.Length);
                inElemSizes = AllocIntArray(BytesPerSample);
                outBufs = AllocIntPtrArray(outputHandle.AddrOfPinnedObject());
                outIds = AllocIntArray(OutputIdentifierBitstreamData);
                outSizes = AllocIntArray(output.Length);
                outElemSizes = AllocIntArray(1);

                var inputDesc = new AacEncBufDesc { NumBufs = 1, Bufs = inBufs, BufferIdentifiers = inIds, BufSizes = inSizes, BufElSizes = inElemSizes };
                var outputDesc = new AacEncBufDesc { NumBufs = 1, Bufs = outBufs, BufferIdentifiers = outIds, BufSizes = outSizes, BufElSizes = outElemSizes };
                var inArgs = new AacEncInArgs { NumInSamples = SamplesPerAacFrame * _channels };
                var outArgs = new AacEncOutArgs();

                ThrowIfFdkFailed(FdkAacNative.aacEncEncode(_encoder, ref inputDesc, ref outputDesc, ref inArgs, ref outArgs), "aacEncEncode(frame)");
                if (outArgs.NumOutBytes <= 0)
                    return Array.Empty<byte>();

                var encoded = new byte[outArgs.NumOutBytes];
                Buffer.BlockCopy(output, 0, encoded, 0, encoded.Length);
                return encoded;
            }
            finally
            {
                FreeHGlobal(inBufs);
                FreeHGlobal(inIds);
                FreeHGlobal(inSizes);
                FreeHGlobal(inElemSizes);
                FreeHGlobal(outBufs);
                FreeHGlobal(outIds);
                FreeHGlobal(outSizes);
                FreeHGlobal(outElemSizes);
                inputHandle.Free();
                outputHandle.Free();
            }
        }

        private void SetParameter(AacEncoderParameter parameter, int value)
        {
            ThrowIfFdkFailed(FdkAacNative.aacEncoder_SetParam(_encoder, parameter, value), $"aacEncoder_SetParam({parameter})");
        }

        private static byte[] BuildAudioSpecificConfig(int sampleRate, int channels)
        {
            var sampleRateIndex = sampleRate == 48000 ? 3 : 4;
            var config = (AacObjectTypeLowComplexity << 11) | (sampleRateIndex << 7) | (channels << 3);
            return new[] { (byte)((config >> 8) & 0xFF), (byte)(config & 0xFF) };
        }

        private static IntPtr AllocIntPtrArray(IntPtr value)
        {
            var ptr = Marshal.AllocHGlobal(IntPtr.Size);
            Marshal.WriteIntPtr(ptr, value);
            return ptr;
        }

        private static IntPtr AllocIntArray(int value)
        {
            var ptr = Marshal.AllocHGlobal(sizeof(int));
            Marshal.WriteInt32(ptr, value);
            return ptr;
        }

        private static void FreeHGlobal(IntPtr ptr)
        {
            if (ptr != IntPtr.Zero)
                Marshal.FreeHGlobal(ptr);
        }

        private static void ThrowIfFdkFailed(int result, string operation)
        {
            if (result != 0)
                throw new InvalidOperationException($"{operation} failed with FDK AAC error {result}.");
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            FdkAacNative.aacEncClose(ref _encoder);
            _encoder = IntPtr.Zero;
        }
    }

    internal enum AacEncoderParameter
    {
        Aot = 0x0100,
        BitRate = 0x0101,
        SampleRate = 0x0103,
        ChannelMode = 0x0106,
        ChannelOrder = 0x0107,
        Afterburner = 0x0200,
        TransportMux = 0x0300
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct AacEncBufDesc
    {
        public int NumBufs;
        public IntPtr Bufs;
        public IntPtr BufferIdentifiers;
        public IntPtr BufSizes;
        public IntPtr BufElSizes;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct AacEncInArgs
    {
        public int NumInSamples;
        public int NumAncBytes;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct AacEncOutArgs
    {
        public int NumOutBytes;
        public int NumInSamples;
        public int NumAncBytes;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct AacEncInfoStruct
    {
        public const int ConfBufLength = 64;

        public int MaxOutBufBytes;
        public int MaxAncBytes;
        public int InBufFillLevel;
        public int InputChannels;
        public int FrameLength;
        public int NDelay;
        public int NDelayCore;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = ConfBufLength)]
        public byte[] ConfBuf;
        public int ConfSize;
    }

    internal static class FdkAacNative
    {
        [DllImport("libAACenc.dll", CallingConvention = CallingConvention.Cdecl)]
        public static extern int aacEncOpen(ref IntPtr encoder, uint encModules, uint maxChannels);

        [DllImport("libAACenc.dll", CallingConvention = CallingConvention.Cdecl)]
        public static extern int aacEncoder_SetParam(IntPtr encoder, AacEncoderParameter param, int value);

        [DllImport("libAACenc.dll", CallingConvention = CallingConvention.Cdecl)]
        public static extern int aacEncEncode(IntPtr encoder, IntPtr inputDesc, IntPtr outputDesc, IntPtr inputArgs, IntPtr outputArgs);

        [DllImport("libAACenc.dll", CallingConvention = CallingConvention.Cdecl)]
        public static extern int aacEncEncode(IntPtr encoder, ref AacEncBufDesc inputDesc, ref AacEncBufDesc outputDesc, ref AacEncInArgs inputArgs, ref AacEncOutArgs outputArgs);

        [DllImport("libAACenc.dll", CallingConvention = CallingConvention.Cdecl)]
        public static extern int aacEncInfo(IntPtr encoder, ref AacEncInfoStruct info);

        [DllImport("libAACenc.dll", CallingConvention = CallingConvention.Cdecl)]
        public static extern int aacEncClose(ref IntPtr encoder);
    }

    internal sealed class NativeAudioMixerSession : IAsyncDisposable
    {
        public const int TargetSampleRate = 44100;
        private const int TargetChannels = 2;
        private readonly bool _desktopAudioEnabled;
        private readonly bool _microphoneEnabled;
        private readonly string? _desktopAudioDeviceId;
        private readonly string? _microphoneDeviceId;
        private readonly double _desktopVolume;
        private readonly string? _excludedDesktopProcessName;
        private readonly double _microphoneVolume;
        private readonly Action<NativeAudioFrame> _frameReady;
        private readonly IAudioCaptureService? _desktopCapture;
        private readonly MicrophoneCaptureService? _microphoneCapture;
        private readonly object _syncRoot = new();
        private readonly Channel<AudioCaptureWorkItem> _captureQueue = Channel.CreateBounded<AudioCaptureWorkItem>(
            new BoundedChannelOptions(96)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = false
            });
        private short[]? _latestMicrophoneSamples;
        private CancellationTokenSource? _cts;
        private Task? _mixerTask;

        public NativeAudioMixerSession(
            bool desktopAudioEnabled,
            string? desktopAudioDeviceId,
            double desktopVolume,
            string? excludedDesktopProcessName,
            bool microphoneEnabled,
            string? microphoneDeviceId,
            double microphoneVolume,
            Action<NativeAudioFrame> frameReady)
        {
            _desktopAudioEnabled = desktopAudioEnabled;
            _microphoneEnabled = microphoneEnabled;
            _desktopAudioDeviceId = desktopAudioDeviceId;
            _microphoneDeviceId = microphoneDeviceId;
            _desktopVolume = Math.Clamp(desktopVolume, 0, 1);
            _excludedDesktopProcessName = excludedDesktopProcessName;
            _microphoneVolume = Math.Clamp(microphoneVolume, 0, 1);
            _frameReady = frameReady;

            if (_desktopAudioEnabled)
                _desktopCapture = !string.IsNullOrWhiteSpace(_excludedDesktopProcessName)
                    ? ProcessExcludingLoopbackCaptureService.CreateForApplicationOrSystemLoopback(_excludedDesktopProcessName)
                    : new SystemLoopbackCaptureService();

            if (_microphoneEnabled)
                _microphoneCapture = new MicrophoneCaptureService();
        }

        public async Task StartAsync()
        {
            _cts = new CancellationTokenSource();
            _mixerTask = Task.Run(() => MixAudioLoopAsync(_cts.Token), _cts.Token);

            if (_desktopCapture is not null)
            {
                _desktopCapture.AudioPacketArrived += DesktopCapture_AudioPacketArrived;
                await _desktopCapture.StartAsync(_desktopAudioDeviceId);
            }

            if (_microphoneCapture is not null)
            {
                _microphoneCapture.AudioPacketArrived += MicrophoneCapture_AudioPacketArrived;
                await _microphoneCapture.StartAsync(_microphoneDeviceId);
            }
        }

        private void DesktopCapture_AudioPacketArrived(object? sender, AudioPacket packet)
        {
            _captureQueue.Writer.TryWrite(new AudioCaptureWorkItem(AudioCaptureKind.Desktop, packet));
        }

        private void MicrophoneCapture_AudioPacketArrived(object? sender, AudioPacket packet)
        {
            _captureQueue.Writer.TryWrite(new AudioCaptureWorkItem(AudioCaptureKind.Microphone, packet));
        }

        private async Task MixAudioLoopAsync(CancellationToken token)
        {
            try
            {
                await foreach (var item in _captureQueue.Reader.ReadAllAsync(token))
                {
                    token.ThrowIfCancellationRequested();

                    if (item.Kind == AudioCaptureKind.Desktop)
                    {
                        var desktopSamples = AudioPacketConverter.ToStereo16(item.Packet, TargetSampleRate);
                        short[]? micSamples;
                        lock (_syncRoot)
                        {
                            micSamples = _latestMicrophoneSamples;
                            _latestMicrophoneSamples = null;
                        }

                        var mixed = Mix(desktopSamples, _desktopVolume, micSamples, _microphoneVolume);
                        _frameReady(new NativeAudioFrame(ToBytes(mixed), (long)item.Packet.Timestamp.TotalMilliseconds));
                    }
                    else
                    {
                        var micSamples = AudioPacketConverter.ToStereo16(item.Packet, TargetSampleRate);
                        if (_desktopAudioEnabled)
                        {
                            lock (_syncRoot)
                            {
                                _latestMicrophoneSamples = micSamples;
                            }

                            continue;
                        }

                        var mixed = Mix(null, _desktopVolume, micSamples, _microphoneVolume);
                        _frameReady(new NativeAudioFrame(ToBytes(mixed), (long)item.Packet.Timestamp.TotalMilliseconds));
                    }
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
            }
        }

        private static short[] Mix(short[]? desktop, double desktopVolume, short[]? mic, double micVolume)
        {
            var length = Math.Max(desktop?.Length ?? 0, mic?.Length ?? 0);
            var output = new short[length];
            for (var i = 0; i < output.Length; i++)
            {
                var sample = 0.0;
                if (desktop is not null && i < desktop.Length)
                    sample += desktop[i] * desktopVolume;
                if (mic is not null && i < mic.Length)
                    sample += mic[i] * micVolume;

                output[i] = (short)Math.Clamp((int)Math.Round(sample), short.MinValue, short.MaxValue);
            }

            return output;
        }

        private static byte[] ToBytes(short[] samples)
        {
            var bytes = new byte[samples.Length * 2];
            for (var i = 0; i < samples.Length; i++)
                BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(i * 2, 2), samples[i]);
            return bytes;
        }

        public async ValueTask DisposeAsync()
        {
            if (_desktopCapture is not null)
            {
                _desktopCapture.AudioPacketArrived -= DesktopCapture_AudioPacketArrived;
                await _desktopCapture.StopAsync();
            }

            if (_microphoneCapture is not null)
            {
                _microphoneCapture.AudioPacketArrived -= MicrophoneCapture_AudioPacketArrived;
                await _microphoneCapture.StopAsync();
            }

            try
            {
                _cts?.Cancel();
                _captureQueue.Writer.TryComplete();
            }
            catch
            {
            }

            if (_mixerTask is not null)
            {
                try
                {
                    await _mixerTask;
                }
                catch
                {
                }
            }

            _cts?.Dispose();
            _cts = null;
            _mixerTask = null;
        }

        private enum AudioCaptureKind
        {
            Desktop,
            Microphone
        }

        private readonly record struct AudioCaptureWorkItem(AudioCaptureKind Kind, AudioPacket Packet);
    }

    internal sealed class NativeAudioFrame
    {
        public NativeAudioFrame(byte[] pcm16Stereo, long timestampMilliseconds)
        {
            Pcm16Stereo = pcm16Stereo;
            TimestampMilliseconds = timestampMilliseconds;
        }

        public byte[] Pcm16Stereo { get; }
        public long TimestampMilliseconds { get; }
    }

    internal static class AudioPacketConverter
    {
        public static short[] ToStereo16(AudioPacket packet, int targetSampleRate)
        {
            var sourceFrames = GetSourceFrameCount(packet);
            if (sourceFrames <= 0)
                return Array.Empty<short>();

            var targetFrames = Math.Max(1, (int)Math.Round(sourceFrames * (targetSampleRate / (double)packet.SampleRate)));
            var output = new short[targetFrames * 2];
            for (var frame = 0; frame < targetFrames; frame++)
            {
                var sourceFrame = Math.Min(sourceFrames - 1, (int)Math.Round(frame * (packet.SampleRate / (double)targetSampleRate)));
                var left = ReadSample(packet, sourceFrame, 0);
                var right = packet.Channels > 1 ? ReadSample(packet, sourceFrame, 1) : left;
                output[frame * 2] = left;
                output[(frame * 2) + 1] = right;
            }

            return output;
        }

        private static int GetSourceFrameCount(AudioPacket packet)
        {
            var bytesPerSample = Math.Max(1, packet.BitsPerSample / 8);
            var blockAlign = Math.Max(1, bytesPerSample * Math.Max(1, packet.Channels));
            return packet.PcmData.Length / blockAlign;
        }

        private static short ReadSample(AudioPacket packet, int frame, int channel)
        {
            var channels = Math.Max(1, packet.Channels);
            var bytesPerSample = Math.Max(1, packet.BitsPerSample / 8);
            var offset = ((frame * channels) + Math.Min(channel, channels - 1)) * bytesPerSample;
            if (offset < 0 || offset + bytesPerSample > packet.PcmData.Length)
                return 0;

            if (packet.IsFloatFormat && bytesPerSample >= 4)
            {
                var value = BitConverter.ToSingle(packet.PcmData, offset);
                return (short)Math.Clamp((int)Math.Round(value * short.MaxValue), short.MinValue, short.MaxValue);
            }

            return bytesPerSample switch
            {
                1 => (short)((packet.PcmData[offset] - 128) << 8),
                2 => BinaryPrimitives.ReadInt16LittleEndian(packet.PcmData.AsSpan(offset, 2)),
                3 => (short)(ReadInt24(packet.PcmData.AsSpan(offset, 3)) >> 8),
                4 => (short)(BinaryPrimitives.ReadInt32LittleEndian(packet.PcmData.AsSpan(offset, 4)) >> 16),
                _ => 0
            };
        }

        private static int ReadInt24(ReadOnlySpan<byte> bytes)
        {
            var value = bytes[0] | (bytes[1] << 8) | (bytes[2] << 16);
            if ((value & 0x800000) != 0)
                value |= unchecked((int)0xFF000000);
            return value;
        }
    }
}

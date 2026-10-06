using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Navigation;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Windows.ApplicationModel.DataTransfer;
using Windows.Media.Core;
using Windows.Media.MediaProperties;
using Windows.Media.Playback;
using Windows.Storage.Streams;
using Windows.System;
using Zink.Services;
using Zink.Services.NativeCalling;
using MediaFoundationH264Decoder = Zink.Services.NativeCalling.Quality720p.MediaFoundationH264Decoder;
using Zink.Services.NativeCalling.Models;
using Zink.Services.Social;

namespace Zink.Pages.Social
{
    public sealed partial class CallPage : Page
    {
        private long _targetUserId;
        private bool _isScreenShare;

        private bool _isMicEnabled = true;
        private bool _isMuted = false;
        private bool _isDeafened = false;
        private bool _isSharingScreen = false;
        private string _selectedAudioOutput = "Default";
        private string _selectedMicrophone = "Default microphone";
        private bool _screenShareHooksAttached;
        private bool _isRemoteVideoVisible;
        private bool _isRemoteScreenShareLoading;
        private bool _isWatchingRemoteScreenShare;
        private bool _isFullscreen;
        private bool _isLocalPreviewHidden;
        private bool _isScreenShareSoundEnabled = true;
        private bool _screenShareFeedbackDialogOpen;
        private bool _callFeedbackDialogOpen;
        private string _lastCallFeedbackPromptKey = "";
        private long _localUserId;
        private const int MaxCallParticipants = 10;
        private readonly HashSet<long> _callParticipantIds = new();
        private readonly HashSet<long> _leftParticipantIds = new();
        private readonly Dictionary<long, string> _participantDisplayNames = new();
        private readonly Dictionary<long, NativePeerConnectionService> _screenSharePeers = new();
        private readonly object _screenShareViewersSync = new();
        private readonly HashSet<long> _screenShareViewerIds = new();
        private readonly SemaphoreSlim _screenShareAudioSendLock = new(1, 1);
        private readonly object _screenShareAudioQueueSync = new();
        private readonly Queue<(byte[] Data, int Generation)> _screenShareAudioQueue = new();
        private static int MaxScreenShareAudioQueue => ScreenShareServiceSelector.Active.RuntimePolicy.Controls.MaxAudioSendQueue;
        private int _screenShareAudioPumpRunning;
        private int _screenShareAudioSendDrops;
        private int _screenShareLifecycleGeneration;
        private MediaFoundationH264Decoder? _h264Decoder;
        private int _screenShareFrameCounter;
        private double _screenShareObservedFps;
        private int _screenShareLastWidth;
        private int _screenShareLastHeight;
        private long _screenShareLastBytes;
        private string _screenShareLastQuality = ScreenShareCapturePipeline.Instance.CurrentQuality.Name;
        private string _selectedScreenShareSoundDevice = SystemAudioShareService.Instance.SelectedDeviceName;
        private DateTimeOffset _screenShareFpsWindowStartedUtc = DateTimeOffset.UtcNow;
        private DateTimeOffset? _screenShareLastFrameAtUtc;
        private int _screenShareTransmitCounter;
        private int _screenShareDroppedFrames;
        private int _screenShareSendRecoveryPending;
        private int _screenShareDroppedReceiveFrames;
        private DateTimeOffset _localPreviewLastRenderedUtc = DateTimeOffset.MinValue;
        private DateTimeOffset _localStagePreviewLastRenderedUtc = DateTimeOffset.MinValue;
        private DateTimeOffset _fallbackFrameLastSentUtc = DateTimeOffset.MinValue;
        private DateTimeOffset _previewFallbackLastSentUtc = DateTimeOffset.MinValue;
        private DateTimeOffset? _screenShareStartedAtUtc;
        private int _screenShareRtpFrames;
        private int _screenShareFallbackFrames;
        private int _screenSharePreviewFallbackFrames;
        private int _screenShareRtpMetadataFrames;
        private int _screenShareSoundPacketsSent;
        private DateTimeOffset? _screenShareSoundLastPacketAtUtc;
        private DateTimeOffset _lastRtpMetadataSentUtc = DateTimeOffset.MinValue;
        private int _lastRtpMetadataWidth;
        private int _lastRtpMetadataHeight;
        private int _lastRemoteBitstreamWidth;
        private int _lastRemoteBitstreamHeight;
        private long _remoteScreenShareSenderId;
        private int _remoteScreenShareStopGeneration;
        private string _remoteScreenShareCodec = ScreenShareCodecNames.H264;
        private DateTimeOffset _lastQosSentUtc = DateTimeOffset.MinValue;
        private int _lastQosDroppedReceiveFrames;
        private DateTimeOffset _lastScreenShareSendSummaryUtc = DateTimeOffset.MinValue;
        private DateTimeOffset? _remoteScreenLastReceivedAtUtc;
        private DateTimeOffset? _remoteScreenLastRenderedAtUtc;
        private DateTimeOffset _lastRemoteDecoderResetAtUtc = DateTimeOffset.MinValue;
        private DateTimeOffset _lastRemoteReceiveStallRecoveryUtc = DateTimeOffset.MinValue;
        private DateTimeOffset _lastRemoteBitstreamResolutionAtUtc = DateTimeOffset.MinValue;
        private DateTimeOffset _remoteFallbackLastReceivedAtUtc = DateTimeOffset.MinValue;
        private DateTimeOffset _remoteFallbackFirstReceivedAtUtc = DateTimeOffset.MinValue;
        private long _remoteRtpFrameCount;
        private long _remoteFallbackFrameCount;
        private long _remotePreviewFrameCount;
        private long _remoteRenderedFrameCount;
        private DateTimeOffset _remoteReceiveFpsWindowStartedUtc = DateTimeOffset.UtcNow;
        private DateTimeOffset _remotePlaybackFpsWindowStartedUtc = DateTimeOffset.UtcNow;
        private int _remoteReceiveFramesInWindow;
        private int _remotePlaybackFramesInWindow;
        private double _remoteReceiveObservedFps;
        private double _remotePlaybackObservedFps;
        private int _remoteGpuPlaybackQueuePeak;
        private long _remoteGpuPlaybackQueueTotal;
        private long _remoteGpuPlaybackQueueSamples;
        private double _remoteGpuPlaybackAverageQueue;
        private DateTimeOffset _lastRemoteRealtimeStatsLogUtc = DateTimeOffset.MinValue;
        private int _remoteDecoderResetCount;
        private int _remoteEmptyDecodeCount;
        private int _remoteDecodeFailureCount;
        private bool _remoteH264WaitingForKeyFrame = true;
        private int _remoteH264FramesDroppedWaitingForKeyFrame;
        private DateTimeOffset _lastLocalFrameFlowLogUtc = DateTimeOffset.MinValue;
        private DateTimeOffset _lastRemoteReceiveFlowLogUtc = DateTimeOffset.MinValue;
        private DateTimeOffset _lastRemoteQueueFlowLogUtc = DateTimeOffset.MinValue;
        private DateTimeOffset _lastRemoteDecodeFlowLogUtc = DateTimeOffset.MinValue;
        private DateTimeOffset _lastRemoteRenderFlowLogUtc = DateTimeOffset.MinValue;
        private DateTimeOffset _lastRemoteRenderCoalescedLogUtc = DateTimeOffset.MinValue;
        private DateTimeOffset _lastRemotePreviewFlowLogUtc = DateTimeOffset.MinValue;
        private DateTimeOffset _lastRemoteFallbackHoldLogUtc = DateTimeOffset.MinValue;
        private DateTimeOffset _lastRemoteKeyFrameRequestAtUtc = DateTimeOffset.MinValue;
        private DateTimeOffset _lastRemoteBacklogTrimLogUtc = DateTimeOffset.MinValue;
        private DateTimeOffset _lastRemoteBacklogJumpLogUtc = DateTimeOffset.MinValue;
        private DateTimeOffset _lastRemoteStutterDiagnosticLogUtc = DateTimeOffset.MinValue;
        private long _lastRemoteQueuedSequence;
        private DateTimeOffset _lastRemoteQueuedAtUtc = DateTimeOffset.MinValue;
        private long _remoteSequenceGapCount;
        private long _remoteDecodeSpikeCount;
        private long _remoteRenderCoalesceCount;
        private long _remoteRenderGapCount;
        private DateTimeOffset _lastRemoteRenderAtUtc = DateTimeOffset.MinValue;
        private int _remoteH264RecoveryInFlight;
        private int _remoteBacklogTrimmedSinceLastLog;
        private int _remoteBacklogJumpedSinceLastLog;
        private long _lastScreenShareMetadataTimestamp;
        private long _remoteDecodeGeneration;
        private uint _lastLocalEncodedFingerprint;
        private int _localEncodedFingerprintRepeat;
        private uint _lastLocalPreviewFingerprint;
        private int _localPreviewFingerprintRepeat;
        private uint _lastRemoteEncodedFingerprint;
        private int _remoteEncodedFingerprintRepeat;
        private uint _lastRemoteDecodedFingerprint;
        private int _remoteDecodedFingerprintRepeat;
        private uint _lastRemoteRenderedFingerprint;
        private int _remoteRenderedFingerprintRepeat;
        private uint _lastRemotePreviewFingerprint;
        private int _remotePreviewFingerprintRepeat;
        private static ScreenShareRuntimePolicy ScreenSharePolicy => ScreenShareServiceSelector.Active.RuntimePolicy;
        private static int FallbackMaxFps => ScreenSharePolicy.Signaling.FallbackMaxFps;
        private static int PreviewFallbackMaxFps => ScreenSharePolicy.Signaling.PreviewFallbackMaxFps;
        private static int WebSocketFallbackMaxDeltaBytes => ScreenSharePolicy.Signaling.WebSocketFallbackMaxDeltaBytes;
        private static TimeSpan WebSocketFallbackWarmupDuration => ScreenSharePolicy.Signaling.WebSocketFallbackWarmup;
        private static TimeSpan WebSocketFallbackGpuStartDelay => ScreenSharePolicy.Signaling.WebSocketFallbackGpuStartDelay;
        // Normal Media Foundation decoder spikes can briefly accumulate four or
        // five frames. Keep the queue below roughly 130 ms so playback remains
        // live instead of displaying stale compressed frames.
        private static int MaxRemoteH264DecodeQueue => ScreenSharePolicy.Controls.MaxDecodeQueue;
        // Preserve H.264 decoder state while catching up, but avoid the expensive
        // NV12-to-BGRA conversion for intermediate frames. This keeps startup
        // decoder warm-up and short decode spikes from becoming visible freezes.
        private static int RemoteDecodeRenderBacklogThreshold => ScreenSharePolicy.Controls.DecodeRenderBacklogThreshold;
        // Do not throw away a still-decodable dependent frame chain during a
        // short decoder spike. The normal pruning path requests a fresh IDR at
        // four frames, but some hardware encoders do not honour that request
        // immediately. Allow up to one second of compressed frames before the
        // hard recovery path resets the decoder.
        private static int MaxRemoteH264HardDecodeQueue => ScreenSharePolicy.Controls.MaxHardDecodeQueue;
        // MediaStreamSource can continue accepting samples after MediaPlayerElement
        // silently stops presenting them. Use the existing MF decoder/render path.
        private static bool UseRemoteGpuPlayback => ScreenSharePolicy.Controls.UseGpuPlayback;
        private static int RemoteGpuBacklogKeyFrameRequestQueue => ScreenSharePolicy.Controls.GpuBacklogKeyFrameRequestQueue;
        private static int MaxRemoteGpuPlaybackQueue => ScreenSharePolicy.Controls.MaxGpuPlaybackQueue;
        // Keep the sender, RTP clock and MediaStreamSource on one cadence. The
        // ARM64 receiver in the field cannot sustain the former 50 FPS stream
        // and repeatedly discarded dependent H.264 frames while catching up.
        private static int RemoteGpuCompatibilityFps => ScreenSharePolicy.Controls.CompatibilityFps;
        private static int RealtimeBacklogRetainFrames => ScreenSharePolicy.Controls.RealtimeBacklogRetainFrames;
        private static TimeSpan RemoteGpuSustainedBacklogThreshold => ScreenSharePolicy.Errors.GpuSustainedBacklogThreshold;
        private static TimeSpan RemoteGpuFrameDuration => TimeSpan.FromTicks(TimeSpan.TicksPerSecond / RemoteGpuCompatibilityFps);
        private static readonly TimeSpan RemoteGpuMinFrameDuration = TimeSpan.FromTicks(TimeSpan.TicksPerSecond / 75);
        private static readonly TimeSpan RemoteGpuMaxFrameDuration = TimeSpan.FromTicks(TimeSpan.TicksPerSecond / 24);
        private static TimeSpan RemoteGpuSampleWaitTimeout => ScreenSharePolicy.Errors.GpuSampleWaitTimeout;
        private static TimeSpan RtpMetadataInterval => ScreenSharePolicy.Signaling.RtpMetadataInterval;
        private static TimeSpan QosFeedbackInterval => ScreenSharePolicy.Signaling.QosFeedbackInterval;
        private static TimeSpan RemoteFrameReceiveStallThreshold => ScreenSharePolicy.Errors.ReceiveStallThreshold;
        private static TimeSpan RemoteRenderStallThreshold => ScreenSharePolicy.Errors.RenderStallThreshold;
        private static TimeSpan RemoteDecoderResetCooldown => ScreenSharePolicy.Errors.DecoderResetCooldown;
        private static TimeSpan RemoteFallbackRtpSuppressWindow => ScreenSharePolicy.Errors.FallbackRtpSuppressWindow;
        private static TimeSpan ScreenShareSendTimeout => ScreenSharePolicy.Signaling.SendTimeout;
        private static readonly TimeSpan FullscreenChromeHideDelay = TimeSpan.FromSeconds(4);
        private readonly SemaphoreSlim _screenShareSendLock = new(1, 1);
        private readonly SemaphoreSlim _screenShareDecodeLock = new(1, 1);
        private readonly SemaphoreSlim _screenShareLifecycleLock = new(1, 1);
        private readonly object _screenShareSendQueueSync = new();
        private PendingLocalScreenShareSend? _pendingLocalScreenShareSend;
        private int _localScreenShareSendPumpRunning;
        private readonly object _screenSharePeerRestartSync = new();
        private readonly object _rtpFrameSync = new();
        private readonly object _remoteDecoderSync = new();
        private readonly Queue<PendingRemoteH264Frame> _pendingRemoteH264Frames = new();
        private readonly Dictionary<long, DateTimeOffset> _screenSharePeerRestartLastAttemptUtc = new();
        private long _pendingRtpFrameSequence;
        private int _rtpDecodePumpRunning;
        private int _remoteRenderQueued;
        private readonly object _remoteRenderSync = new();
        private byte[]? _pendingRemoteRenderFrame;
        private int _pendingRemoteRenderWidth;
        private int _pendingRemoteRenderHeight;
        private DateTimeOffset _pendingRemoteRenderQueuedAtUtc;
        private WriteableBitmap? _remoteScreenBitmap;
        private int _remoteScreenBitmapWidth;
        private int _remoteScreenBitmapHeight;
        private MediaFoundationH264Decoder? _rtpH264Decoder;
        private int _rtpDecoderWidth;
        private int _rtpDecoderHeight;
        private string _rtpDecoderCodec = ScreenShareCodecNames.H264;
        private DateTimeOffset _remoteDecodeBacklogStartedAtUtc = DateTimeOffset.MinValue;
        private readonly object _remoteGpuPlaybackSync = new();
        private readonly Queue<PendingRemoteH264Frame> _pendingRemoteGpuPlaybackFrames = new();
        private MediaPlayer? _remoteGpuMediaPlayer;
        private MediaStreamSource? _remoteGpuMediaStreamSource;
        private bool _remoteGpuPlaybackEnabled;
        private bool _remoteGpuPlaybackStarting;
        private bool _remoteGpuPlaybackStopping;
        private int _remoteGpuPlaybackWidth;
        private int _remoteGpuPlaybackHeight;
        private long _remoteGpuPlaybackSequence;
        private TimeSpan _remoteGpuPlaybackSampleTime;
        private TimeSpan _remoteGpuEstimatedFrameDuration = RemoteGpuFrameDuration;
        private DateTimeOffset? _remoteGpuLastQueuedAtUtc;
        private DateTimeOffset _remoteGpuBacklogStartedAtUtc = DateTimeOffset.MinValue;
        private bool _remoteGpuWaitingForKeyFrame;
        private bool _remoteGpuBacklogKeyFrameRequested;
        private bool _remoteGpuFirstSampleSubmitted;
        private int _remoteGpuConsecutiveSampleStarvations;
        private DateTimeOffset _lastRemoteGpuPlaybackDropLogUtc = DateTimeOffset.MinValue;

        private DispatcherTimer? _callTimer;
        private DispatcherTimer? _fullscreenChromeTimer;
        private CancellationTokenSource? _sidebarNoticeCancellation;
        private DateTimeOffset? _connectedAtUtc;

        public CallPage()
        {
            this.InitializeComponent();
            Loaded += CallPage_Loaded;
            Unloaded += CallPage_Unloaded;
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);

            if (e.Parameter is CallPageArgs args)
            {
                _targetUserId = args.TargetUserId;
                _isScreenShare = args.IsScreenShare;
            }

            _isSharingScreen = _isScreenShare;

            var session = NativeCallCoordinator.Instance.CurrentSession;

            if (CallLaunchState.TryConsumeOutgoing(_targetUserId, _isScreenShare, out var consumedCallId))
            {
                session.CallId = consumedCallId;
                session.TargetUserId = _targetUserId;
                session.RemoteUserId = _targetUserId;
                session.IsScreenShare = _isScreenShare;
                session.State = NativeCallState.Calling;
                session.StatusText = "Calling...";
                session.PeerText = $"Calling user {_targetUserId}";
            }

            if (_targetUserId > 0)
                AddCallParticipant(_targetUserId);

            _ = LoadLocalUserIdAsync();

            SyncPageStateFromSession(session);
            ApplySessionToUi(session);
            UpdateDockVisualStates();
            UpdateSpeakingIndicators(session, AudioActivityService.Instance.Current);
        }

        private void CallPage_Loaded(object sender, RoutedEventArgs e)
        {
            App.MainWindow?.CollapseSidebar();
            ShowSidebarCollapsedNotice();

            NativeSignalingBridge.Instance.EnsureHooked();

            NativeCallCoordinator.Instance.SessionChanged += NativeCallCoordinator_SessionChanged;
            NativeSignalingBridge.Instance.IncomingCallReceived += NativeSignalingBridge_IncomingCallReceived;
            NativeSignalingBridge.Instance.CallAnsweredReceived += NativeSignalingBridge_CallAnsweredReceived;
            NativeSignalingBridge.Instance.CallRejectedReceived += NativeSignalingBridge_CallRejectedReceived;
            NativeSignalingBridge.Instance.CallEndedReceived += NativeSignalingBridge_CallEndedReceived;

            AudioActivityService.Instance.ActivityChanged += AudioActivityService_ActivityChanged;
            CallLayoutRoot.PointerMoved += CallMediaStage_PointerMoved;
            CallStageBorder.PointerMoved += CallMediaStage_PointerMoved;
            CallMediaStage.PointerMoved += CallMediaStage_PointerMoved;
            RemoteScreenImage.PointerMoved += CallMediaStage_PointerMoved;
            RemoteScreenPlayer.PointerMoved += CallMediaStage_PointerMoved;
            FullscreenPointerSurface.PointerMoved += CallMediaStage_PointerMoved;
            FullscreenButton.PointerMoved += CallMediaStage_PointerMoved;
            FullscreenDockButton.PointerMoved += CallMediaStage_PointerMoved;
            FullscreenExitHotspot.PointerMoved += CallMediaStage_PointerMoved;
            KeyDown += CallPage_KeyDown;
            HookScreenShare();

            if (string.IsNullOrWhiteSpace(TokenStateText.Text) || TokenStateText.Text == "Token: not loaded")
                TokenStateText.Text = "Token: loaded";

            RealtimeStateText.Text = "Realtime: connected";

            _selectedAudioOutput = AudioPlaybackService.Instance.GetCurrentOutputDisplayName();
            _selectedMicrophone = GetCurrentMicDisplayName();
            _selectedScreenShareSoundDevice = SystemAudioShareService.Instance.SelectedDeviceName;

            EnsureTimerCreated();
            UpdateCallTimerText();

            SyncPageStateFromSession(NativeCallCoordinator.Instance.CurrentSession);
            ApplySessionToUi(NativeCallCoordinator.Instance.CurrentSession);
        }

        private async void ShowSidebarCollapsedNotice()
        {
            _sidebarNoticeCancellation?.Cancel();
            _sidebarNoticeCancellation?.Dispose();
            var cancellation = new CancellationTokenSource();
            _sidebarNoticeCancellation = cancellation;
            SidebarCollapsedInfoBar.IsOpen = true;

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(5), cancellation.Token);
                if (!cancellation.IsCancellationRequested)
                    SidebarCollapsedInfoBar.IsOpen = false;
            }
            catch (OperationCanceledException)
            {
            }
        }

        private void CallPage_Unloaded(object sender, RoutedEventArgs e)
        {
            _sidebarNoticeCancellation?.Cancel();
            _sidebarNoticeCancellation?.Dispose();
            _sidebarNoticeCancellation = null;
            SidebarCollapsedInfoBar.IsOpen = false;

            NativeCallCoordinator.Instance.SessionChanged -= NativeCallCoordinator_SessionChanged;
            NativeSignalingBridge.Instance.IncomingCallReceived -= NativeSignalingBridge_IncomingCallReceived;
            NativeSignalingBridge.Instance.CallAnsweredReceived -= NativeSignalingBridge_CallAnsweredReceived;
            NativeSignalingBridge.Instance.CallRejectedReceived -= NativeSignalingBridge_CallRejectedReceived;
            NativeSignalingBridge.Instance.CallEndedReceived -= NativeSignalingBridge_CallEndedReceived;

            AudioActivityService.Instance.ActivityChanged -= AudioActivityService_ActivityChanged;
            CallLayoutRoot.PointerMoved -= CallMediaStage_PointerMoved;
            CallStageBorder.PointerMoved -= CallMediaStage_PointerMoved;
            CallMediaStage.PointerMoved -= CallMediaStage_PointerMoved;
            RemoteScreenImage.PointerMoved -= CallMediaStage_PointerMoved;
            RemoteScreenPlayer.PointerMoved -= CallMediaStage_PointerMoved;
            FullscreenPointerSurface.PointerMoved -= CallMediaStage_PointerMoved;
            FullscreenButton.PointerMoved -= CallMediaStage_PointerMoved;
            FullscreenDockButton.PointerMoved -= CallMediaStage_PointerMoved;
            FullscreenExitHotspot.PointerMoved -= CallMediaStage_PointerMoved;
            KeyDown -= CallPage_KeyDown;
            UnhookScreenShare();
            _ = StopLocalScreenShareAsync(true);
            if (_isFullscreen)
                App.MainWindow?.ExitFullscreenMode();
            _h264Decoder?.Dispose();
            _h264Decoder = null;
            _rtpH264Decoder?.Dispose();
            _rtpH264Decoder = null;
            _rtpDecoderWidth = 0;
            _rtpDecoderHeight = 0;
            _rtpDecoderCodec = ScreenShareCodecNames.H264;

            StopCallTimer();
            StopFullscreenChromeTimer();
        }

        private void HookScreenShare()
        {
            if (_screenShareHooksAttached)
                return;

            _screenShareHooksAttached = true;
            ScreenShareCapturePipeline.Instance.FrameReady += NativeScreenShare_FrameReady;
            ScreenShareCapturePipeline.Instance.StreamingFailed += NativeScreenShare_StreamingFailed;
            SystemAudioShareService.Instance.AudioCaptured += SystemAudioShare_AudioCaptured;
            SocialManager.Instance.Realtime.ScreenShareStarted += Realtime_ScreenShareStarted;
            SocialManager.Instance.Realtime.ScreenShareStopped += Realtime_ScreenShareStopped;
            SocialManager.Instance.Realtime.ScreenFrameReceived += Realtime_ScreenFrameReceived;
            SocialManager.Instance.Realtime.EncodedScreenFrameReceived += Realtime_EncodedScreenFrameReceived;
            SocialManager.Instance.Realtime.ScreenShareMetadataReceived += Realtime_ScreenShareMetadataReceived;
            SocialManager.Instance.Realtime.ScreenShareCapabilitiesReceived += Realtime_ScreenShareCapabilitiesReceived;
            SocialManager.Instance.Realtime.ScreenShareQosReceived += Realtime_ScreenShareQosReceived;
            SocialManager.Instance.Realtime.WebRtcOfferReceived += Realtime_WebRtcOfferReceived;
            SocialManager.Instance.Realtime.WebRtcAnswerReceived += Realtime_WebRtcAnswerReceived;
            SocialManager.Instance.Realtime.WebRtcIceReceived += Realtime_WebRtcIceReceived;
        }

        private void UnhookScreenShare()
        {
            if (!_screenShareHooksAttached)
                return;

            _screenShareHooksAttached = false;
            ScreenShareCapturePipeline.Instance.FrameReady -= NativeScreenShare_FrameReady;
            ScreenShareCapturePipeline.Instance.StreamingFailed -= NativeScreenShare_StreamingFailed;
            SystemAudioShareService.Instance.AudioCaptured -= SystemAudioShare_AudioCaptured;
            SocialManager.Instance.Realtime.ScreenShareStarted -= Realtime_ScreenShareStarted;
            SocialManager.Instance.Realtime.ScreenShareStopped -= Realtime_ScreenShareStopped;
            SocialManager.Instance.Realtime.ScreenFrameReceived -= Realtime_ScreenFrameReceived;
            SocialManager.Instance.Realtime.EncodedScreenFrameReceived -= Realtime_EncodedScreenFrameReceived;
            SocialManager.Instance.Realtime.ScreenShareMetadataReceived -= Realtime_ScreenShareMetadataReceived;
            SocialManager.Instance.Realtime.ScreenShareCapabilitiesReceived -= Realtime_ScreenShareCapabilitiesReceived;
            SocialManager.Instance.Realtime.ScreenShareQosReceived -= Realtime_ScreenShareQosReceived;
            SocialManager.Instance.Realtime.WebRtcOfferReceived -= Realtime_WebRtcOfferReceived;
            SocialManager.Instance.Realtime.WebRtcAnswerReceived -= Realtime_WebRtcAnswerReceived;
            SocialManager.Instance.Realtime.WebRtcIceReceived -= Realtime_WebRtcIceReceived;
        }

        private async Task StartLocalScreenShareAsync()
        {
            var startupTimer = Stopwatch.StartNew();
            DiagnosticLogService.WriteLine(
                $"[ScreenShare:STARTUP] requested elapsedMs={startupTimer.Elapsed.TotalMilliseconds:0.0}; processArch={System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}; osArch={System.Runtime.InteropServices.RuntimeInformation.OSArchitecture}; is64Bit={Environment.Is64BitProcess}; packageBase={AppContext.BaseDirectory}");

            await _screenShareLifecycleLock.WaitAsync();
            try
            {
                var session = NativeCallCoordinator.Instance.CurrentSession;
                DiagnosticLogService.WriteLine(
                    $"[ScreenShare:STARTUP] entered elapsedMs={startupTimer.Elapsed.TotalMilliseconds:0.0}; state={session.State}; callId={session.CallId}; localSharing={_isSharingScreen}; nativeRunning={ScreenShareCapturePipeline.Instance.IsRunning}; requestedQuality={ScreenShareCapturePipeline.Instance.RequestedQuality.Name}");
                if (session.State != NativeCallState.Connected)
                    throw new InvalidOperationException("Start or accept the call before sharing your screen.");

                var participants = GetCallParticipants(session).ToList();
                DiagnosticLogService.WriteLine($"[ScreenShare:UI] Start participants={participants.Count}; participantIds={string.Join(",", participants)}");
                if (participants.Count == 0 || string.IsNullOrWhiteSpace(session.CallId))
                    throw new InvalidOperationException("No remote users are connected for screen share.");

                if (ScreenShareCapturePipeline.Instance.IsRunning)
                    return;

                ResetScreenShareStats();
                var lifecycleGeneration = Interlocked.Increment(ref _screenShareLifecycleGeneration);
                lock (_screenShareViewersSync)
                    _screenShareViewerIds.Clear();
                ScreenShareCapturePipeline.Instance.SetAdaptiveLatencyMode(true);
                ScreenShareCapturePipeline.Instance.SetTargetFpsOverride(
                    ScreenShareCapturePipeline.Instance.RequestedQuality.TargetFps);
                _screenShareStartedAtUtc = DateTimeOffset.UtcNow;
                Debug.WriteLine($"[ScreenShare:H264] Starting with locked selected quality: {ScreenShareCapturePipeline.Instance.RequestedQuality.Name}.");
                TryEnqueueOnUi(() =>
                {
                    _remoteScreenShareSenderId = 0;
            _remoteScreenShareCodec = ScreenShareCodecNames.H264;
                    _isRemoteVideoVisible = false;
                    _isRemoteScreenShareLoading = false;
                    RemoteScreenImage.Source = null;
                    RemotePlaceholderTitleText.Text = "You are sharing";
                    RemotePlaceholderSubtitleText.Text = "Starting live local preview...";
                    MediaOverlayText.Text = "Starting screen share...";
                    UpdateMediaLayerVisibility();
                });

                var captureStartTask = ScreenShareCapturePipeline.Instance.StartAsync();
                DiagnosticLogService.WriteLine($"[ScreenShare:STARTUP] capture-start-requested elapsedMs={startupTimer.Elapsed.TotalMilliseconds:0.0}");
                var capabilityTasks = participants
                    .Select(participantId => SocialManager.Instance.Realtime.SendScreenShareCapabilitiesAsync(
                        participantId,
                        session.CallId,
                        GetLocalScreenShareCodecs(),
                        RuntimeInformation.ProcessArchitecture.ToString()))
                    .ToList();
                var signalingTasks = participants
                    .Select(participantId => StartScreenShareParticipantSignalingAsync(participantId, session.CallId))
                    .ToList();
                await Task.WhenAll(capabilityTasks.Concat(signalingTasks).Append(captureStartTask));
                DiagnosticLogService.WriteLine(
                    $"[ScreenShare:STARTUP] capture-and-signaling-complete elapsedMs={startupTimer.Elapsed.TotalMilliseconds:0.0}; captureRunning={ScreenShareCapturePipeline.Instance.IsRunning}; activeCodec={ScreenShareCapturePipeline.Instance.ActiveVideoCodec}; targetFps={ScreenShareCapturePipeline.Instance.CurrentTargetFps}");
                Debug.WriteLine("[ScreenShare:H264] Using WebRTC RTP as the primary screen-share transport with low-bandwidth WebSocket fallback.");

                if (_isScreenShareSoundEnabled)
                    await TryStartScreenShareSoundAsync(session, showSuccessStatus: false);

                App.MainWindow?.CollapseSidebar();
                DiagnosticLogService.WriteLine($"[ScreenShare:STARTUP] completed elapsedMs={startupTimer.Elapsed.TotalMilliseconds:0.0}; audioEnabled={_isScreenShareSoundEnabled}");
                DiagnosticLogService.Flush();
            }
            catch (Exception ex)
            {
                DiagnosticLogService.WriteLine($"[ScreenShare:STARTUP] failed elapsedMs={startupTimer.Elapsed.TotalMilliseconds:0.0}; error={ex}");
                DiagnosticLogService.Flush();
                throw;
            }
            finally
            {
                _screenShareLifecycleLock.Release();
            }
        }

        private async Task StartScreenShareParticipantSignalingAsync(long participantId, string callId)
        {
            try
            {
                Debug.WriteLine($"[ScreenShare:RTP] Notifying participant {participantId} that screen share is starting in call {callId}.");
                var quality = ScreenShareCapturePipeline.Instance.CurrentQuality;
                var codec = ScreenShareCapturePipeline.Instance.SelectedCodecName;
                await SocialManager.Instance.Realtime.SendScreenShareStartedAsync(
                    participantId,
                    callId,
                    quality.Width,
                    quality.Height,
                    codec);
                await SocialManager.Instance.Realtime.SendScreenShareMetadataAsync(
                    participantId,
                    callId,
                    quality.Width,
                    quality.Height,
                    DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    codec);
                // Media negotiation begins only after this participant chooses
                // Watch now. Until then this is an announcement-only signal.
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ScreenShare:RTP] Start notification failed for participant {participantId}: {ex.Message}");
            }
        }

        private static IReadOnlyList<string> GetLocalScreenShareCodecs()
        {
            return new[] { ScreenShareCodecNames.H264, ScreenShareCodecNames.Av1 };
        }

        private void Realtime_ScreenShareCapabilitiesReceived(object? sender, ScreenShareCapabilitiesEventArgs e)
        {
            var session = NativeCallCoordinator.Instance.CurrentSession;
            if (!IsCurrentCallSignal(session, e.CallId) || e.Codecs.Length == 0)
                return;

            var selected = SelectCompatibleScreenShareCodec(e.Codecs);
            DiagnosticLogService.WriteLine(
                $"[ScreenShare:CAPS] participant={e.FromUserId}; remoteArch={e.Architecture}; " +
                $"remoteCodecs={string.Join(",", e.Codecs)}; selected={selected}");
        }

        private static string SelectCompatibleScreenShareCodec(IReadOnlyList<string> remoteCodecs)
        {
            if (remoteCodecs.Any(ScreenShareCodecNames.IsH264))
                return ScreenShareCodecNames.H264;

            if (remoteCodecs.Any(ScreenShareCodecNames.IsAv1))
                return ScreenShareCodecNames.Av1;

            return ScreenShareCodecNames.H264;
        }

        private async Task StopLocalScreenShareAsync(bool notifyRemote, bool promptForFeedback = false)
        {
            var shouldPromptForFeedback = false;
            ScreenShareReportContext? feedbackContext = null;

            await _screenShareLifecycleLock.WaitAsync();
            try
            {
                Interlocked.Increment(ref _screenShareLifecycleGeneration);
                lock (_screenShareSendQueueSync)
                    _pendingLocalScreenShareSend = null;
                lock (_screenShareAudioQueueSync)
                    _screenShareAudioQueue.Clear();

                var wasRunning = ScreenShareCapturePipeline.Instance.IsRunning;
                Debug.WriteLine($"[ScreenShare:H264] StopLocalScreenShareAsync begin; notifyRemote={notifyRemote}; wasRunning={wasRunning}; uiSharing={_isSharingScreen}.");

                if (wasRunning)
                    feedbackContext = BuildScreenShareReportContext("pending", "");

                try
                {
                    await ScreenShareCapturePipeline.Instance.StopAsync();
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[ScreenShare:H264] Stop capture failed: {ex}");
                }

                try
                {
                    await SystemAudioShareService.Instance.StopAsync();
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[ScreenShare:Audio] Stop failed: {ex}");
                }

                try
                {
                    await CloseScreenSharePeersAsync();
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[ScreenShare:RTP] Closing peers failed: {ex}");
                }

                if (wasRunning && feedbackContext != null)
                {
                    try
                    {
                        await ScreenShareReportService.SaveLatestStateAsync(
                            WithScreenShareFeedback(
                                feedbackContext,
                                "finished",
                                $"Automatic report: screen share finished. notifyRemote={notifyRemote}."));
                    }
                    catch (Exception ex)
                    {
                        DiagnosticLogService.WriteLine("[ScreenShare:Report] Save latest state failed: " + ex);
                    }
                }

                ResetScreenShareStats();

                TryEnqueueOnUi(() =>
                {
                    if (_remoteScreenShareSenderId <= 0)
                    {
                        _isRemoteVideoVisible = false;
                        _isRemoteScreenShareLoading = false;
                        RemoteScreenImage.Source = null;
                        UpdateMediaLayerVisibility();
                    }
                });

                if (!wasRunning || !notifyRemote)
                    return;

                shouldPromptForFeedback = promptForFeedback;

                var session = NativeCallCoordinator.Instance.CurrentSession;
                if (!string.IsNullOrWhiteSpace(session.CallId))
                {
                    foreach (var participantId in GetCallParticipants(session))
                    {
                        try
                        {
                            await SocialManager.Instance.Realtime.SendScreenShareStoppedAsync(participantId, session.CallId);
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine($"[ScreenShare:H264] Stop notification failed for {participantId}: {ex.Message}");
                        }
                    }
                }
            }
            finally
            {
                Debug.WriteLine("[ScreenShare:H264] StopLocalScreenShareAsync completed.");
                DiagnosticLogService.Flush();
                _screenShareLifecycleLock.Release();
            }

            if (shouldPromptForFeedback && feedbackContext != null)
                await ShowScreenShareExperienceDialogAsync(feedbackContext);
        }

        private ScreenShareReportContext BuildScreenShareReportContext(string experience, string notes)
        {
            var session = NativeCallCoordinator.Instance.CurrentSession;
            var duration = _screenShareStartedAtUtc.HasValue
                ? DateTimeOffset.UtcNow - _screenShareStartedAtUtc.Value
                : TimeSpan.Zero;

            var remoteUserId = session.RemoteUserId > 0
                ? session.RemoteUserId
                : session.TargetUserId;

            return new ScreenShareReportContext
            {
                SessionType = "Screen share",
                Experience = experience,
                Notes = notes,
                CallId = session.CallId ?? "",
                Quality = _screenShareLastQuality,
                LocalUser = _localUserId > 0 ? GetParticipantDisplayName(_localUserId) : "You",
                RemoteUser = remoteUserId > 0 ? GetParticipantDisplayName(remoteUserId) : "Remote participant",
                Duration = duration <= TimeSpan.Zero ? "-" : duration.ToString(@"hh\:mm\:ss"),
                SenderFps = _screenShareObservedFps,
                ReceiverFps = _remotePlaybackObservedFps,
                LastWidth = _screenShareLastWidth,
                LastHeight = _screenShareLastHeight,
                LastBytes = _screenShareLastBytes,
                SentFrames = _screenShareTransmitCounter,
                ReceivedFrames = _remoteRtpFrameCount,
                RenderedFrames = _remoteRenderedFrameCount,
                DroppedSendFrames = _screenShareDroppedFrames,
                DroppedReceiveFrames = _screenShareDroppedReceiveFrames,
                DecodeFailures = _remoteDecodeFailureCount,
                DecoderResets = _remoteDecoderResetCount,
                AudioDevice = _selectedScreenShareSoundDevice,
                AudioPacketsSent = _screenShareSoundPacketsSent
            };
        }

        private async Task ShowScreenShareExperienceDialogAsync(ScreenShareReportContext snapshot)
        {
            if (_screenShareFeedbackDialogOpen || XamlRoot == null)
                return;

            _screenShareFeedbackDialogOpen = true;

            try
            {
                var dialog = new ContentDialog
                {
                    XamlRoot = XamlRoot,
                    Title = "How was your screen share?",
                    Content = new TextBlock
                    {
                        Text = "Your feedback can upload the screen-share report and logs so support can look at quality, stutter, audio, and connection issues.",
                        TextWrapping = TextWrapping.WrapWholeWords
                    },
                    PrimaryButtonText = "Good",
                    SecondaryButtonText = "Bad experience",
                    CloseButtonText = "Not now",
                    DefaultButton = ContentDialogButton.Primary
                };

                var result = await dialog.ShowAsync();

                if (result == ContentDialogResult.Primary)
                {
                    await UploadScreenShareFeedbackAsync(snapshot, "good", "User marked the screen share as good.");
                    return;
                }

                if (result == ContentDialogResult.Secondary)
                    await ShowBadScreenShareExperienceDialogAsync(snapshot);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ScreenShare:Report] Feedback dialog failed: {ex}");
                NativeCallCoordinator.Instance.SetStatus(
                    NativeCallCoordinator.Instance.CurrentSession.State,
                    $"Screen-share feedback failed: {ex.Message}");
            }
            finally
            {
                _screenShareFeedbackDialogOpen = false;
            }
        }

        private async Task ShowBadScreenShareExperienceDialogAsync(ScreenShareReportContext snapshot)
        {
            if (XamlRoot == null)
                return;

            var notesBox = new TextBox
            {
                Header = "What went wrong?",
                PlaceholderText = "Lag, freezing, audio stutter, bad colours, 1080p issue...",
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                MinHeight = 120
            };

            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = "Upload screen-share report to support?",
                Content = new StackPanel
                {
                    Spacing = 12,
                    Children =
                    {
                        new TextBlock
                        {
                            Text = "Zink will upload a focused screen-share report and recent diagnostic logs to the Zink call server. No upload happens unless you choose Upload report.",
                            TextWrapping = TextWrapping.WrapWholeWords
                        },
                        notesBox
                    }
                },
                PrimaryButtonText = "Upload report",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Primary
            };

            var result = await dialog.ShowAsync();
            if (result == ContentDialogResult.Primary)
                await UploadScreenShareFeedbackAsync(snapshot, "bad", notesBox.Text);
        }

        private async Task UploadScreenShareFeedbackAsync(ScreenShareReportContext snapshot, string experience, string notes)
        {
            try
            {
                NativeCallCoordinator.Instance.SetStatus(
                    NativeCallCoordinator.Instance.CurrentSession.State,
                    "Uploading screen-share report...");

                var context = WithScreenShareFeedback(snapshot, experience, notes);
                var bundlePath = await ScreenShareReportService.CreateBundleAsync(context);
                var result = await DiagnosticsUploadService.TryUploadSupportBundleAsync(bundlePath);

                if (!result.Success)
                {
                    NativeCallCoordinator.Instance.SetStatus(
                        NativeCallCoordinator.Instance.CurrentSession.State,
                        $"Screen-share report saved locally. Upload failed: {result.Error}");
                    await ShowSupportBundleSavedDialogAsync(
                        "Screen-share report saved locally",
                        bundlePath,
                        "Zink could not upload the report to the call server, but it saved the full support bundle on this device.");
                    return;
                }

                NativeCallCoordinator.Instance.SetStatus(
                    NativeCallCoordinator.Instance.CurrentSession.State,
                    $"Screen-share report uploaded. Report id: {result.ReportId}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ScreenShare:Report] Upload failed: {ex}");
                NativeCallCoordinator.Instance.SetStatus(
                    NativeCallCoordinator.Instance.CurrentSession.State,
                    $"Screen-share report upload failed: {ex.Message}");
            }
        }

        private static ScreenShareReportContext WithScreenShareFeedback(ScreenShareReportContext snapshot, string experience, string notes)
        {
            return new ScreenShareReportContext
            {
                SessionType = snapshot.SessionType,
                Experience = experience,
                Notes = notes,
                CallId = snapshot.CallId,
                Quality = snapshot.Quality,
                LocalUser = snapshot.LocalUser,
                RemoteUser = snapshot.RemoteUser,
                Duration = snapshot.Duration,
                SenderFps = snapshot.SenderFps,
                ReceiverFps = snapshot.ReceiverFps,
                LastWidth = snapshot.LastWidth,
                LastHeight = snapshot.LastHeight,
                LastBytes = snapshot.LastBytes,
                SentFrames = snapshot.SentFrames,
                ReceivedFrames = snapshot.ReceivedFrames,
                RenderedFrames = snapshot.RenderedFrames,
                DroppedSendFrames = snapshot.DroppedSendFrames,
                DroppedReceiveFrames = snapshot.DroppedReceiveFrames,
                DecodeFailures = snapshot.DecodeFailures,
                DecoderResets = snapshot.DecoderResets,
                AudioDevice = snapshot.AudioDevice,
                AudioPacketsSent = snapshot.AudioPacketsSent
            };
        }

        private ScreenShareReportContext BuildCallReportContext(string experience, string notes)
        {
            var session = NativeCallCoordinator.Instance.CurrentSession;
            var duration = _connectedAtUtc.HasValue
                ? DateTimeOffset.UtcNow - _connectedAtUtc.Value
                : TimeSpan.Zero;
            var remoteUserId = session.RemoteUserId > 0
                ? session.RemoteUserId
                : _callParticipantIds.FirstOrDefault(id => id > 0 && id != _localUserId);

            return new ScreenShareReportContext
            {
                SessionType = "Call",
                Experience = experience,
                Notes = notes,
                CallId = session.CallId,
                Quality = _isSharingScreen || _screenShareLastWidth > 0 ? $"Screen share {_screenShareLastQuality}" : "Voice call",
                LocalUser = _localUserId > 0 ? GetParticipantDisplayName(_localUserId) : "You",
                RemoteUser = remoteUserId > 0 ? GetParticipantDisplayName(remoteUserId) : "Remote participant",
                Duration = duration <= TimeSpan.Zero ? "-" : duration.ToString(@"hh\:mm\:ss"),
                SenderFps = _screenShareObservedFps,
                ReceiverFps = _remotePlaybackObservedFps,
                LastWidth = _screenShareLastWidth,
                LastHeight = _screenShareLastHeight,
                LastBytes = _screenShareLastBytes,
                SentFrames = _screenShareTransmitCounter,
                ReceivedFrames = _remoteRtpFrameCount,
                RenderedFrames = _remoteRenderedFrameCount,
                DroppedSendFrames = _screenShareDroppedFrames,
                DroppedReceiveFrames = _screenShareDroppedReceiveFrames,
                DecodeFailures = _remoteDecodeFailureCount,
                DecoderResets = _remoteDecoderResetCount,
                AudioDevice = _selectedScreenShareSoundDevice,
                AudioPacketsSent = _screenShareSoundPacketsSent
            };
        }

        private async Task PromptForCallExperienceOnceAsync(string reason)
        {
            var session = NativeCallCoordinator.Instance.CurrentSession;
            var key = !string.IsNullOrWhiteSpace(session.CallId)
                ? session.CallId
                : $"{session.RemoteUserId}:{session.TargetUserId}:{_connectedAtUtc:O}";

            if (string.IsNullOrWhiteSpace(key) ||
                string.Equals(_lastCallFeedbackPromptKey, key, StringComparison.Ordinal) ||
                _callFeedbackDialogOpen ||
                XamlRoot == null)
            {
                return;
            }

            _lastCallFeedbackPromptKey = key;
            await ShowCallExperienceDialogAsync(BuildCallReportContext(reason, ""));
        }

        private async Task ShowCallExperienceDialogAsync(ScreenShareReportContext snapshot)
        {
            if (_callFeedbackDialogOpen || XamlRoot == null)
                return;

            _callFeedbackDialogOpen = true;

            try
            {
                var dialog = new ContentDialog
                {
                    XamlRoot = XamlRoot,
                    Title = "How was your Zink call?",
                    Content = new TextBlock
                    {
                        Text = "Your answer can upload the call report and logs so support can check audio, screen share, lag, connection, and device issues.",
                        TextWrapping = TextWrapping.WrapWholeWords
                    },
                    PrimaryButtonText = "Good",
                    SecondaryButtonText = "Bad experience",
                    CloseButtonText = "Not now",
                    DefaultButton = ContentDialogButton.Primary
                };

                var result = await dialog.ShowAsync();

                if (result == ContentDialogResult.Primary)
                {
                    await UploadCallFeedbackAsync(snapshot, "good", "User marked the call as good.");
                    return;
                }

                if (result == ContentDialogResult.Secondary)
                    await ShowBadCallExperienceDialogAsync(snapshot);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Call:Report] Feedback dialog failed: {ex}");
                NativeCallCoordinator.Instance.SetStatus(
                    NativeCallCoordinator.Instance.CurrentSession.State,
                    $"Call feedback failed: {ex.Message}");
            }
            finally
            {
                _callFeedbackDialogOpen = false;
            }
        }

        private async Task ShowBadCallExperienceDialogAsync(ScreenShareReportContext snapshot)
        {
            if (XamlRoot == null)
                return;

            var notesBox = new TextBox
            {
                Header = "What went wrong?",
                PlaceholderText = "Audio stutter, delay, call dropped, screen share lag, no sound...",
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                MinHeight = 120
            };

            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = "Upload call report to support?",
                Content = new StackPanel
                {
                    Spacing = 12,
                    Children =
                    {
                        new TextBlock
                        {
                            Text = "Zink will upload a focused call report and recent diagnostic logs to the Zink call server. No upload happens unless you choose Upload report.",
                            TextWrapping = TextWrapping.WrapWholeWords
                        },
                        notesBox
                    }
                },
                PrimaryButtonText = "Upload report",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Primary
            };

            var result = await dialog.ShowAsync();
            if (result == ContentDialogResult.Primary)
                await UploadCallFeedbackAsync(snapshot, "bad", notesBox.Text);
        }

        private async Task UploadCallFeedbackAsync(ScreenShareReportContext snapshot, string experience, string notes)
        {
            try
            {
                NativeCallCoordinator.Instance.SetStatus(
                    NativeCallCoordinator.Instance.CurrentSession.State,
                    "Uploading call report...");

                var context = WithScreenShareFeedback(snapshot, experience, notes);
                var bundlePath = await ScreenShareReportService.CreateBundleAsync(context);
                var result = await DiagnosticsUploadService.TryUploadSupportBundleAsync(bundlePath);

                if (!result.Success)
                {
                    NativeCallCoordinator.Instance.SetStatus(
                        NativeCallCoordinator.Instance.CurrentSession.State,
                        $"Call report saved locally. Upload failed: {result.Error}");
                    await ShowSupportBundleSavedDialogAsync(
                        "Call report saved locally",
                        bundlePath,
                        "Zink could not upload the report to the call server, but it saved the full support bundle on this device.");
                    return;
                }

                NativeCallCoordinator.Instance.SetStatus(
                    NativeCallCoordinator.Instance.CurrentSession.State,
                    $"Call report uploaded. Report id: {result.ReportId}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Call:Report] Upload failed: {ex}");
                NativeCallCoordinator.Instance.SetStatus(
                    NativeCallCoordinator.Instance.CurrentSession.State,
                    $"Call report upload failed: {ex.Message}");
            }
        }

        private async Task ShowSupportBundleSavedDialogAsync(string title, string bundlePath, string message)
        {
            if (XamlRoot == null)
                return;

            var pathBox = new TextBox
            {
                Header = "Support bundle",
                Text = bundlePath,
                IsReadOnly = true,
                TextWrapping = TextWrapping.Wrap
            };

            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = title,
                Content = new StackPanel
                {
                    Spacing = 12,
                    Children =
                    {
                        new TextBlock
                        {
                            Text = message + " Copy this path from the device that had the problem and send the zip file.",
                            TextWrapping = TextWrapping.WrapWholeWords
                        },
                        pathBox
                    }
                },
                PrimaryButtonText = "Copy path",
                CloseButtonText = "OK",
                DefaultButton = ContentDialogButton.Primary
            };

            var result = await dialog.ShowAsync();
            if (result == ContentDialogResult.Primary)
            {
                var package = new DataPackage();
                package.SetText(bundlePath);
                Clipboard.SetContent(package);
                Clipboard.Flush();
            }
        }

        private async Task<bool> TryStartScreenShareSoundAsync(NativeCallSession session, bool showSuccessStatus)
        {
            try
            {
                await SystemAudioShareService.Instance.StartAsync();
                _selectedScreenShareSoundDevice = SystemAudioShareService.Instance.SelectedDeviceName;

                if (showSuccessStatus)
                {
                    NativeCallCoordinator.Instance.SetStatus(
                        session.State,
                        $"Screen share sound enabled on {_selectedScreenShareSoundDevice}.");
                }

                return true;
            }
            catch (Exception ex)
            {
                _isScreenShareSoundEnabled = false;
                _selectedScreenShareSoundDevice = SystemAudioShareService.Instance.SelectedDeviceName;
                Debug.WriteLine($"[ScreenShare:Audio] Start failed: {ex}");

                NativeCallCoordinator.Instance.SetStatus(
                    session.State,
                    $"Screen share started. Screen sound could not start: {ex.Message}");

                return false;
            }
        }

        private void NativeScreenShare_FrameReady(object? sender, NativeScreenFrameEventArgs e)
        {
            var session = NativeCallCoordinator.Instance.CurrentSession;
            if (session.State != NativeCallState.Connected ||
                string.IsNullOrWhiteSpace(session.CallId))
            {
                return;
            }

            var participants = GetCallParticipants(session).ToList();
            if (participants.Count == 0)
                return;

            QueueLocalScreenFrame(participants, session.CallId, e);

            TryEnqueueOnUi(async () =>
            {
                if (_isSharingScreen)
                {
                    var now = DateTimeOffset.UtcNow;

                    if (_remoteScreenShareSenderId <= 0 &&
                        now - _localStagePreviewLastRenderedUtc >= TimeSpan.FromMilliseconds(250))
                    {
                        _localStagePreviewLastRenderedUtc = now;
                        await RenderScreenFrameAsync(e.PreviewFrameData, RemoteScreenImage);
                        _isRemoteVideoVisible = true;
                        _remoteScreenLastRenderedAtUtc = now;
                        RemotePlaceholderTitleText.Text = "You are sharing";
                        RemotePlaceholderSubtitleText.Text = "Live local screen preview";
                        MediaOverlayText.Text = $"Your screen {e.Width} x {e.Height}";
                        UpdateMediaLayerVisibility();
                    }

                    if (now - _localPreviewLastRenderedUtc >= TimeSpan.FromSeconds(1))
                    {
                        _localPreviewLastRenderedUtc = now;
                        await RenderScreenFrameAsync(e.PreviewFrameData, LocalPreviewImage);
                        LocalPreviewPlaceholder.Visibility = Visibility.Collapsed;
                    }
                }
            });
        }

        private void QueueLocalScreenFrame(IReadOnlyList<long> participantIds, string callId, NativeScreenFrameEventArgs e)
        {
            var droppedH264Reference = false;
            lock (_screenShareSendQueueSync)
            {
                if (_pendingLocalScreenShareSend != null)
                {
                    _screenShareDroppedFrames++;
                    droppedH264Reference = ScreenShareCodecNames.IsH264(
                        _pendingLocalScreenShareSend.Frame.Codec);
                }

                _pendingLocalScreenShareSend = new PendingLocalScreenShareSend(
                    participantIds.Distinct().ToArray(),
                    callId,
                    e,
                    Volatile.Read(ref _screenShareLifecycleGeneration));
            }

            if (droppedH264Reference)
            {
                // Replacing a queued H.264 access unit breaks the reference chain
                // just like a transport drop. Suppress dependent frames until the
                // encoder produces a fresh IDR instead of showing macroblock
                // corruption on the receiver.
                Interlocked.Exchange(ref _screenShareSendRecoveryPending, 1);
                ScreenShareCapturePipeline.Instance.RequestRecoveryKeyFrame(
                    "local realtime send queue replaced an H.264 reference frame");
                ScreenShareCapturePipeline.Instance.ReportSendCongestion(
                    "local realtime send queue backlog");
            }

            if (Interlocked.CompareExchange(ref _localScreenShareSendPumpRunning, 1, 0) == 0)
            {
                _ = Task.Factory.StartNew(
                    ProcessLocalScreenShareSendQueueAsync,
                    CancellationToken.None,
                    TaskCreationOptions.DenyChildAttach | TaskCreationOptions.LongRunning,
                    TaskScheduler.Default).Unwrap();
            }
        }

        private async Task ProcessLocalScreenShareSendQueueAsync()
        {
            try
            {
                while (true)
                {
                    PendingLocalScreenShareSend? pending;
                    lock (_screenShareSendQueueSync)
                    {
                        pending = _pendingLocalScreenShareSend;
                        _pendingLocalScreenShareSend = null;
                    }

                    if (pending == null)
                        return;

                    await SendLocalScreenFrameAndTrackAsync(
                        pending.ParticipantIds,
                        pending.CallId,
                        pending.Frame,
                        pending.Generation);
                }
            }
            finally
            {
                Interlocked.Exchange(ref _localScreenShareSendPumpRunning, 0);
                lock (_screenShareSendQueueSync)
                {
                    if (_pendingLocalScreenShareSend != null &&
                        Interlocked.CompareExchange(ref _localScreenShareSendPumpRunning, 1, 0) == 0)
                    {
                        _ = Task.Factory.StartNew(
                            ProcessLocalScreenShareSendQueueAsync,
                            CancellationToken.None,
                            TaskCreationOptions.DenyChildAttach | TaskCreationOptions.LongRunning,
                            TaskScheduler.Default).Unwrap();
                    }
                }
            }
        }

        private async Task SendLocalScreenFrameAndTrackAsync(IReadOnlyList<long> participantIds, string callId, NativeScreenFrameEventArgs e, int lifecycleGeneration)
        {
            try
            {
                if (lifecycleGeneration != Volatile.Read(ref _screenShareLifecycleGeneration))
                    return;

                if (ScreenShareCodecNames.IsH264(e.Codec) || ScreenShareCodecNames.IsAv1(e.Codec))
                {
                    var sent = await TrySendScreenFrameBestAvailableAsync(participantIds, callId, e, lifecycleGeneration);
                    LogLocalScreenShareFlow(e, participantIds.Count, sent);
                    if (!sent)
                        return;
                }
                else
                {
                    await SendWithTimeoutAsync(
                        SocialManager.Instance.Realtime.SendScreenFrameAsync(
                            0,
                            callId,
                            e.FrameData,
                            e.Width,
                            e.Height,
                            e.Timestamp),
                        "legacy screen frame");
                }

                UpdateScreenShareStats(e);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ScreenShare:SEND] Failed to send local frame: {ex.Message}");
                DiagnosticLogService.WriteLine($"[ScreenShare:SEND] Failed to send local frame: {ex.Message}");
            }
        }

        private async Task<bool> TrySendScreenFrameBestAvailableAsync(IReadOnlyList<long> participantIds, string callId, NativeScreenFrameEventArgs e, int lifecycleGeneration)
        {
            if (lifecycleGeneration != Volatile.Read(ref _screenShareLifecycleGeneration))
                return false;

            lock (_screenShareViewersSync)
                participantIds = participantIds.Where(id => _screenShareViewerIds.Contains(id)).ToArray();

            // Nobody has opted in to this stream yet. Keep the local capture
            // running for its owner without sending or loading remote media.
            if (participantIds.Count == 0)
                return true;

            if (!_screenShareSendLock.Wait(0))
            {
                _screenShareDroppedFrames++;
                if (ScreenShareCodecNames.IsH264(e.Codec))
                {
                    // Dropping an H.264 access unit also invalidates dependent P
                    // frames. Do not let those frames create visible corruption;
                    // hold transmission until the encoder supplies a fresh IDR.
                    Interlocked.Exchange(ref _screenShareSendRecoveryPending, 1);
                    ScreenShareCapturePipeline.Instance.RequestRecoveryKeyFrame("legacy H.264 send overlap dropped a reference frame");
                }
                if (_screenShareDroppedFrames == 1 || _screenShareDroppedFrames % ScreenShareCapturePipeline.TargetFps == 0)
                    Debug.WriteLine($"[ScreenShare:H264] Dropped stale frame while previous send is still in flight. dropped={_screenShareDroppedFrames}");

                ScreenShareCapturePipeline.Instance.ReportSendCongestion("send backlog");
                return false;
            }

            try
            {
                if (ScreenShareCodecNames.IsH264(e.Codec) &&
                    Volatile.Read(ref _screenShareSendRecoveryPending) == 1)
                {
                    if (!e.IsKeyFrame && !ContainsH264IdrFrame(e.FrameData))
                    {
                        _screenShareDroppedFrames++;
                        return false;
                    }

                    Interlocked.Exchange(ref _screenShareSendRecoveryPending, 0);
                    Debug.WriteLine("[ScreenShare:H264] Fresh IDR restored the send dependency chain after a local overlap drop.");
                }

                var sendStartedAt = Stopwatch.StartNew();
                var sentToAnyPeer = false;
                var fallbackParticipantIds = new List<long>();
                var shouldSendRtpMetadata = ShouldSendRtpMetadataFrame(e);

                if (shouldSendRtpMetadata)
                {
                    foreach (var participantId in participantIds.Distinct())
                    {
                        await SocialManager.Instance.Realtime.SendScreenShareMetadataAsync(
                            participantId,
                            callId,
                            e.Width,
                            e.Height,
                            e.Timestamp,
                            e.Codec);

                        _screenShareRtpMetadataFrames++;
                    }
                }

                foreach (var participantId in participantIds)
                {
                    if (_screenSharePeers.TryGetValue(participantId, out var peer) &&
                        await TrySendWithTimeoutAsync(
                            peer.SendEncodedVideoFrameAsync(
                                e.FrameData,
                                e.Codec,
                                ScreenShareCapturePipeline.Instance.CurrentTargetFps),
                            $"RTP frame to participant {participantId}"))
                    {
                        sentToAnyPeer = true;
                        _screenShareRtpFrames++;
                    }
                    else
                    {
                        fallbackParticipantIds.Add(participantId);
                    }
                }

                var rtpFailedTargets = fallbackParticipantIds.Distinct().ToList();
                var shouldSendWebSocketFallback = ShouldSendFallbackFrame(e);
                var websocketFallbackTargets = new List<long>();
                if (shouldSendWebSocketFallback)
                {
                    // A WebRTC connection can remain nominally "connected" after
                    // its selected UDP path has stopped delivering packets. Keep a
                    // reliable, independently decodable keyframe path warm through
                    // signaling so the receiver can switch immediately while ICE is
                    // rebuilt. Delta frames still use fallback only when RTP reports
                    // an explicit send failure, avoiding duplicate full-rate video.
                    // Do not feed the same encoded access unit into the same
                    // decoder over both RTP and the reliable fallback path.
                    // The two transports can arrive out of order, which makes
                    // H.264 reference frames appear corrupted. Peers without
                    // a successful RTP send still receive the fallback.
                    websocketFallbackTargets = rtpFailedTargets.ToList();
                }

                var sentFallback = false;
                var sentFallbackTargets = 0;
                var sentPreview = false;

                if (websocketFallbackTargets.Count > 0)
                {
                    foreach (var participantId in websocketFallbackTargets)
                    {
                        try
                        {
                            if (ScreenShareCodecNames.IsH264(e.Codec) || ScreenShareCodecNames.IsAv1(e.Codec))
                            {
                                await SendWithTimeoutAsync(
                                    SocialManager.Instance.Realtime.SendEncodedScreenFrameBinaryAsync(
                                        participantId,
                                        callId,
                                        e.Codec,
                                        e.FrameData,
                                        e.Width,
                                        e.Height,
                                        e.Timestamp,
                                        e.IsKeyFrame),
                                    $"binary H.264 fallback to participant {participantId}");
                            }
                            else
                            {
                                await SendWithTimeoutAsync(
                                    SocialManager.Instance.Realtime.SendEncodedScreenFrameAsync(
                                        participantId,
                                        callId,
                                        e.FrameData,
                                        e.Width,
                                        e.Height,
                                        e.Timestamp,
                                        e.Codec,
                                        e.IsKeyFrame),
                                    $"encoded fallback to participant {participantId}");
                            }

                            sentFallback = true;
                            sentFallbackTargets++;
                            _screenShareFallbackFrames++;
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine($"[ScreenShare:FALLBACK] Send failed for participant {participantId}: {ex.Message}");
                        }
                    }
                }
                else if (!sentToAnyPeer && rtpFailedTargets.Count > 0)
                {
                    _screenShareDroppedFrames++;
                    ScreenShareCapturePipeline.Instance.ReportSendCongestion("fallback transport backlog");
                }

                if (!sentToAnyPeer &&
                    !sentFallback &&
                    ShouldSendPreviewFallbackFrame(e))
                {
                    foreach (var participantId in rtpFailedTargets)
                    {
                        try
                        {
                            await SendWithTimeoutAsync(
                                SocialManager.Instance.Realtime.SendScreenFrameAsync(
                                    participantId,
                                    callId,
                                    e.PreviewFrameData,
                                    e.Width,
                                    e.Height,
                                    e.Timestamp),
                                $"JPEG preview fallback to participant {participantId}");

                            _screenSharePreviewFallbackFrames++;
                            sentPreview = true;
                            if (_screenSharePreviewFallbackFrames == 1 ||
                                _screenSharePreviewFallbackFrames % (PreviewFallbackMaxFps * 2) == 0)
                            {
                                Debug.WriteLine(
                                    $"[ScreenShare:PREVIEW] Sent {_screenSharePreviewFallbackFrames} JPEG preview frames; target={participantId}; bytes={e.PreviewFrameData.Length}; {e.Width}x{e.Height}.");
                            }
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine($"[ScreenShare:PREVIEW] Send failed for participant {participantId}: {ex.Message}");
                        }
                    }
                }

                LogScreenShareSendSummary(
                    participantIds.Count,
                    sentToAnyPeer,
                    sentFallback ? sentFallbackTargets : rtpFailedTargets.Count,
                    e,
                    sendStartedAt.Elapsed);

                if (sendStartedAt.Elapsed > TimeSpan.FromMilliseconds(20))
                {
                    var slowMessage = $"[ScreenShare:SEND:SLOW] sendMs={sendStartedAt.Elapsed.TotalMilliseconds:0.0}; rtp={sentToAnyPeer}; ws={sentFallback}; preview={sentPreview}; frameBytes={e.FrameData.Length}; previewBytes={e.PreviewFrameData.Length}; participants={participantIds.Count}.";
                    Debug.WriteLine(slowMessage);
                    DiagnosticLogService.WriteLine(slowMessage);
                }

                return sentToAnyPeer || sentFallback || sentPreview;
            }
            finally
            {
                _screenShareSendLock.Release();
            }
        }

        private bool ShouldSendFallbackFrame(NativeScreenFrameEventArgs e)
        {
            if (!e.IsKeyFrame && e.FrameData.Length > WebSocketFallbackMaxDeltaBytes)
                return false;

            if (FallbackMaxFps >= ScreenShareCapturePipeline.TargetFps)
                return true;

            var now = DateTimeOffset.UtcNow;
            var fallbackInterval = TimeSpan.FromMilliseconds(1000.0 / FallbackMaxFps);
            if (_fallbackFrameLastSentUtc != DateTimeOffset.MinValue &&
                now - _fallbackFrameLastSentUtc < fallbackInterval)
            {
                return false;
            }

            _fallbackFrameLastSentUtc = now;
            return true;
        }

        private async Task SendWithTimeoutAsync(Task sendTask, string description)
        {
            if (await Task.WhenAny(sendTask, Task.Delay(ScreenShareSendTimeout)) == sendTask)
            {
                await sendTask;
                return;
            }

            _ = sendTask.ContinueWith(
                task =>
                {
                    _ = task.Exception;
                },
                TaskContinuationOptions.OnlyOnFaulted);
            DiagnosticLogService.WriteLine($"[ScreenShare:SEND] Timed out sending {description} after {ScreenShareSendTimeout.TotalMilliseconds:0}ms.");
            throw new TimeoutException($"Timed out sending {description}.");
        }

        private async Task<bool> TrySendWithTimeoutAsync(Task<bool> sendTask, string description)
        {
            if (await Task.WhenAny(sendTask, Task.Delay(ScreenShareSendTimeout)) == sendTask)
                return await sendTask;

            _ = sendTask.ContinueWith(
                task =>
                {
                    _ = task.Exception;
                },
                TaskContinuationOptions.OnlyOnFaulted);
            DiagnosticLogService.WriteLine($"[ScreenShare:SEND] Timed out sending {description} after {ScreenShareSendTimeout.TotalMilliseconds:0}ms.");
            return false;
        }

        private bool IsWebSocketFallbackWarmupActive()
        {
            return _screenShareStartedAtUtc.HasValue &&
                DateTimeOffset.UtcNow - _screenShareStartedAtUtc.Value <= WebSocketFallbackWarmupDuration;
        }

        private bool ShouldSendPreviewFallbackFrame(NativeScreenFrameEventArgs e)
        {
            if (e.PreviewFrameData.Length == 0)
                return false;

            var now = DateTimeOffset.UtcNow;
            if (now - _previewFallbackLastSentUtc < TimeSpan.FromMilliseconds(1000.0 / PreviewFallbackMaxFps))
                return false;

            _previewFallbackLastSentUtc = now;
            return true;
        }

        private bool ShouldSendRtpMetadataFrame(NativeScreenFrameEventArgs e)
        {
            var isAv1 = ScreenShareCodecNames.IsAv1(e.Codec);
            if (!isAv1 && !e.IsKeyFrame)
                return false;

            var now = DateTimeOffset.UtcNow;
            var resolutionChanged = _lastRtpMetadataWidth != e.Width || _lastRtpMetadataHeight != e.Height;
            if (!resolutionChanged && now - _lastRtpMetadataSentUtc < (isAv1 ? TimeSpan.FromSeconds(1) : RtpMetadataInterval))
                return false;

            _lastRtpMetadataSentUtc = now;
            _lastRtpMetadataWidth = e.Width;
            _lastRtpMetadataHeight = e.Height;
            return true;
        }

        private void NativeScreenShare_StreamingFailed(object? sender, string message)
        {
            TryEnqueueOnUi(async () =>
            {
                _isSharingScreen = false;
                _isScreenShare = false;
                NativeCallCoordinator.Instance.CurrentSession.IsScreenShare = false;
                await SystemAudioShareService.Instance.StopAsync();

                NativeCallCoordinator.Instance.SetStatus(
                    NativeCallCoordinator.Instance.CurrentSession.State,
                    $"H.264 screen share failed: {message}");

                LocalPreviewImage.Source = null;
                LocalPreviewPlaceholder.Visibility = Visibility.Visible;
                UpdateDockVisualStates();
                ApplySessionToUi(NativeCallCoordinator.Instance.CurrentSession);
            });
        }

        private async void SystemAudioShare_AudioCaptured(byte[] data)
        {
            if (data == null || data.Length == 0)
                return;

            lock (_screenShareAudioQueueSync)
            {
                if (_screenShareAudioQueue.Count >= MaxScreenShareAudioQueue)
                {
                    _ = _screenShareAudioQueue.Dequeue();
                    var dropped = Interlocked.Increment(ref _screenShareAudioSendDrops);
                    if (dropped == 1 || dropped % 50 == 0)
                    {
                        DiagnosticLogService.WriteLine($"[ScreenShare:Audio] Dropped oldest queued packet to preserve realtime playback; dropped={dropped}; queue={_screenShareAudioQueue.Count}.");
                    }
                }

                _screenShareAudioQueue.Enqueue((data, Volatile.Read(ref _screenShareLifecycleGeneration)));
            }

            if (Interlocked.Exchange(ref _screenShareAudioPumpRunning, 1) == 0)
            {
                _ = ProcessScreenShareAudioQueueAsync();
            }
        }

        private async Task ProcessScreenShareAudioQueueAsync()
        {
            try
            {
                while (true)
                {
                    (byte[] Data, int Generation)? queued;
                    lock (_screenShareAudioQueueSync)
                    {
                        queued = _screenShareAudioQueue.Count > 0
                            ? _screenShareAudioQueue.Dequeue()
                            : ((byte[] Data, int Generation)?)null;
                    }

                    if (queued == null)
                        break;

                    if (queued.Value.Generation == Volatile.Read(ref _screenShareLifecycleGeneration))
                        await SendScreenShareAudioPacketAsync(queued.Value.Data, queued.Value.Generation);
                }
            }
            finally
            {
                Interlocked.Exchange(ref _screenShareAudioPumpRunning, 0);

                lock (_screenShareAudioQueueSync)
                {
                    if (_screenShareAudioQueue.Count > 0 &&
                        Interlocked.Exchange(ref _screenShareAudioPumpRunning, 1) == 0)
                    {
                        _ = ProcessScreenShareAudioQueueAsync();
                    }
                }
            }
        }

        private async Task SendScreenShareAudioPacketAsync(byte[] data, int lifecycleGeneration)
        {
            await _screenShareAudioSendLock.WaitAsync();
            try
            {
                if (lifecycleGeneration != Volatile.Read(ref _screenShareLifecycleGeneration))
                    return;

                var session = NativeCallCoordinator.Instance.CurrentSession;
                if (!_isSharingScreen ||
                    !_isScreenShareSoundEnabled ||
                    session.State != NativeCallState.Connected ||
                    string.IsNullOrWhiteSpace(session.CallId))
                {
                    return;
                }

                var participants = GetCallParticipants(session).ToList();
                if (participants.Count > 1)
                {
                    await SocialManager.Instance.Realtime.SendAudioChunkAsync(0, session.CallId, data);
                }
                else
                {
                    foreach (var participantId in participants)
                    {
                        await SocialManager.Instance.Realtime.SendAudioChunkAsync(participantId, session.CallId, data);
                    }
                }

                _screenShareSoundPacketsSent++;
                _screenShareSoundLastPacketAtUtc = DateTimeOffset.UtcNow;
                if (_screenShareSoundPacketsSent == 1 || _screenShareSoundPacketsSent % 100 == 0)
                {
                    Debug.WriteLine($"[ScreenShare:Audio] Sent system-audio packet {_screenShareSoundPacketsSent}; bytes={data.Length}; targets={participants.Count}; broadcast={participants.Count > 1}.");
                }
            }
            catch (Exception ex)
            {
                DiagnosticLogService.WriteLine($"[ScreenShare:Audio] Send failed: {ex.Message}");
            }
            finally
            {
                _screenShareAudioSendLock.Release();
            }
        }

        private void ResetScreenShareStats()
        {
            _screenShareFrameCounter = 0;
            _screenShareObservedFps = 0;
            _screenShareLastWidth = 0;
            _screenShareLastHeight = 0;
            _screenShareLastBytes = 0;
            _screenShareLastQuality = ScreenShareCapturePipeline.Instance.CurrentQuality.Name;
            _screenShareFpsWindowStartedUtc = DateTimeOffset.UtcNow;
            _screenShareLastFrameAtUtc = null;
            _screenShareTransmitCounter = 0;
            _screenShareDroppedFrames = 0;
            Interlocked.Exchange(ref _screenShareSendRecoveryPending, 0);
            _screenShareDroppedReceiveFrames = 0;
            _localPreviewLastRenderedUtc = DateTimeOffset.MinValue;
            _localStagePreviewLastRenderedUtc = DateTimeOffset.MinValue;
            _fallbackFrameLastSentUtc = DateTimeOffset.MinValue;
            _previewFallbackLastSentUtc = DateTimeOffset.MinValue;
            _screenShareStartedAtUtc = null;
            _screenShareRtpFrames = 0;
            _screenShareFallbackFrames = 0;
            _screenSharePreviewFallbackFrames = 0;
            _screenShareRtpMetadataFrames = 0;
            _screenShareSoundPacketsSent = 0;
            _screenShareSoundLastPacketAtUtc = null;
            _lastRtpMetadataSentUtc = DateTimeOffset.MinValue;
            _lastRtpMetadataWidth = 0;
            _lastRtpMetadataHeight = 0;
            _lastQosSentUtc = DateTimeOffset.MinValue;
            _lastQosDroppedReceiveFrames = 0;
            _lastScreenShareSendSummaryUtc = DateTimeOffset.MinValue;
            _remoteScreenShareSenderId = 0;
            _isWatchingRemoteScreenShare = false;
            _remoteScreenLastReceivedAtUtc = null;
            _remoteScreenLastRenderedAtUtc = null;
            _lastRemoteDecoderResetAtUtc = DateTimeOffset.MinValue;
            _lastRemoteReceiveStallRecoveryUtc = DateTimeOffset.MinValue;
            Interlocked.Exchange(ref _remoteH264RecoveryInFlight, 0);
            _remoteFallbackLastReceivedAtUtc = DateTimeOffset.MinValue;
            _remoteFallbackFirstReceivedAtUtc = DateTimeOffset.MinValue;
            Interlocked.Exchange(ref _remoteRtpFrameCount, 0);
            Interlocked.Exchange(ref _remoteFallbackFrameCount, 0);
            Interlocked.Exchange(ref _remotePreviewFrameCount, 0);
            Interlocked.Exchange(ref _remoteRenderedFrameCount, 0);
            ResetRemoteRealtimeCounters();
            _remoteDecoderResetCount = 0;
            _remoteEmptyDecodeCount = 0;
            _remoteDecodeFailureCount = 0;
            _remoteH264WaitingForKeyFrame = true;
            _remoteH264FramesDroppedWaitingForKeyFrame = 0;
            _lastScreenShareMetadataTimestamp = 0;
            ResetScreenShareFlowDiagnostics();
            lock (_rtpFrameSync)
            {
                _pendingRemoteH264Frames.Clear();
                _pendingRtpFrameSequence = 0;
                _remoteDecodeGeneration++;
            }

            lock (_remoteRenderSync)
            {
                _pendingRemoteRenderFrame = null;
                _pendingRemoteRenderWidth = 0;
                _pendingRemoteRenderHeight = 0;
            }

            Interlocked.Exchange(ref _remoteRenderQueued, 0);
        }

        private void ResetScreenShareFlowDiagnostics()
        {
            _lastLocalFrameFlowLogUtc = DateTimeOffset.MinValue;
            _lastRemoteReceiveFlowLogUtc = DateTimeOffset.MinValue;
            _lastRemoteQueueFlowLogUtc = DateTimeOffset.MinValue;
            _lastRemoteDecodeFlowLogUtc = DateTimeOffset.MinValue;
            _lastRemoteRenderFlowLogUtc = DateTimeOffset.MinValue;
            _lastRemotePreviewFlowLogUtc = DateTimeOffset.MinValue;
            _lastRemoteFallbackHoldLogUtc = DateTimeOffset.MinValue;
            _lastRemoteBacklogTrimLogUtc = DateTimeOffset.MinValue;
            _lastRemoteBacklogJumpLogUtc = DateTimeOffset.MinValue;
                _lastRemoteStutterDiagnosticLogUtc = DateTimeOffset.MinValue;
                _lastRemoteQueuedSequence = 0;
                _lastRemoteQueuedAtUtc = DateTimeOffset.MinValue;
                _remoteSequenceGapCount = 0;
                _remoteDecodeSpikeCount = 0;
                _remoteRenderCoalesceCount = 0;
                _remoteRenderGapCount = 0;
                _lastRemoteRenderAtUtc = DateTimeOffset.MinValue;
            _remoteBacklogTrimmedSinceLastLog = 0;
            _remoteBacklogJumpedSinceLastLog = 0;
            _lastLocalEncodedFingerprint = 0;
            _localEncodedFingerprintRepeat = 0;
            _lastLocalPreviewFingerprint = 0;
            _localPreviewFingerprintRepeat = 0;
            _lastRemoteEncodedFingerprint = 0;
            _remoteEncodedFingerprintRepeat = 0;
            _lastRemoteDecodedFingerprint = 0;
            _remoteDecodedFingerprintRepeat = 0;
            _lastRemoteRenderedFingerprint = 0;
            _remoteRenderedFingerprintRepeat = 0;
            _lastRemotePreviewFingerprint = 0;
            _remotePreviewFingerprintRepeat = 0;
        }

        private void ResetRemoteRealtimeCounters()
        {
            _remoteReceiveFpsWindowStartedUtc = DateTimeOffset.UtcNow;
            _remotePlaybackFpsWindowStartedUtc = DateTimeOffset.UtcNow;
            _remoteReceiveFramesInWindow = 0;
            _remotePlaybackFramesInWindow = 0;
            _remoteReceiveObservedFps = 0;
            _remotePlaybackObservedFps = 0;
            _remoteGpuPlaybackQueuePeak = 0;
            _remoteGpuPlaybackQueueTotal = 0;
            _remoteGpuPlaybackQueueSamples = 0;
            _remoteGpuPlaybackAverageQueue = 0;
            _remoteGpuBacklogStartedAtUtc = DateTimeOffset.MinValue;
            _lastRemoteRealtimeStatsLogUtc = DateTimeOffset.MinValue;
        }

        private void ResetRemoteScreenShareReceiveState(bool clearImage)
        {
            StopRemoteGpuPlayback(clearSurface: clearImage);
            _remoteScreenLastReceivedAtUtc = null;
            _remoteScreenLastRenderedAtUtc = null;
            _remoteFallbackLastReceivedAtUtc = DateTimeOffset.MinValue;
            _remoteFallbackFirstReceivedAtUtc = DateTimeOffset.MinValue;
            _lastRemoteBitstreamWidth = 0;
            _lastRemoteBitstreamHeight = 0;
            _lastRemoteBitstreamResolutionAtUtc = DateTimeOffset.MinValue;
            _lastScreenShareMetadataTimestamp = 0;
            Interlocked.Exchange(ref _remoteRtpFrameCount, 0);
            Interlocked.Exchange(ref _remoteFallbackFrameCount, 0);
            Interlocked.Exchange(ref _remotePreviewFrameCount, 0);
            Interlocked.Exchange(ref _remoteRenderedFrameCount, 0);
            Interlocked.Exchange(ref _remoteRenderQueued, 0);
            ResetRemoteRealtimeCounters();
            _remoteEmptyDecodeCount = 0;
            _remoteDecodeFailureCount = 0;
            _remoteH264WaitingForKeyFrame = true;
            _remoteH264FramesDroppedWaitingForKeyFrame = 0;
            ResetScreenShareFlowDiagnostics();

            lock (_rtpFrameSync)
            {
                _pendingRemoteH264Frames.Clear();
                _pendingRtpFrameSequence = 0;
                _remoteDecodeGeneration++;
            }

            lock (_remoteRenderSync)
            {
                _pendingRemoteRenderFrame = null;
                _pendingRemoteRenderWidth = 0;
                _pendingRemoteRenderHeight = 0;
                _pendingRemoteRenderQueuedAtUtc = default;
            }

            lock (_remoteDecoderSync)
            {
                try
                {
                    _rtpH264Decoder?.Dispose();
                }
                catch
                {
                }

                _rtpH264Decoder = null;
                _rtpDecoderWidth = 0;
                _rtpDecoderHeight = 0;
                _rtpDecoderCodec = ScreenShareCodecNames.H264;
            }

            if (clearImage)
            {
                RemoteScreenImage.Source = null;
                _remoteScreenBitmap = null;
                _remoteScreenBitmapWidth = 0;
                _remoteScreenBitmapHeight = 0;
            }
        }

        private void UpdateScreenShareStats(NativeScreenFrameEventArgs e)
        {
            _screenShareLastWidth = e.Width;
            _screenShareLastHeight = e.Height;
            _screenShareLastBytes = e.FrameData.LongLength;
            _screenShareLastQuality = e.QualityName;
            _screenShareLastFrameAtUtc = DateTimeOffset.UtcNow;
            _screenShareFrameCounter++;

            var elapsed = _screenShareLastFrameAtUtc.Value - _screenShareFpsWindowStartedUtc;
            if (elapsed.TotalSeconds >= 1)
            {
                _screenShareObservedFps = _screenShareFrameCounter / elapsed.TotalSeconds;
                _screenShareFrameCounter = 0;
                _screenShareFpsWindowStartedUtc = _screenShareLastFrameAtUtc.Value;
            }
        }

        private void Realtime_ScreenShareStarted(object? sender, (string CallId, long FromUserId, int Width, int Height, string Codec) e)
        {
            var sessionLogPath = DiagnosticLogService.StartScreenShareSession("remote screen-share started");
            DiagnosticLogService.WriteLine($"[ScreenShare:SESSION] sessionLog={sessionLogPath}; role=receiver; senderId={e.FromUserId}; callId={e.CallId}; until=remote screen-share ended");
            // Cancel any delayed stop confirmation from an older lifecycle signal.
            Interlocked.Increment(ref _remoteScreenShareStopGeneration);
            TryEnqueueOnUi(async () =>
            {
                var session = NativeCallCoordinator.Instance.CurrentSession;
                if (e.CallId != session.CallId)
                    return;

                var continueWatching = _isWatchingRemoteScreenShare &&
                    _remoteScreenShareSenderId == e.FromUserId;
                if (continueWatching &&
                    _screenSharePeers.TryGetValue(e.FromUserId, out var recoveryPeer) &&
                    (recoveryPeer.ConnectionState.Equals("failed", StringComparison.OrdinalIgnoreCase) ||
                     recoveryPeer.ConnectionState.Equals("disconnected", StringComparison.OrdinalIgnoreCase) ||
                     recoveryPeer.HasRemoteDescription))
                {
                    // A repeated started signal while the viewer is already watching
                    // is the sender's recovery handshake. Retire the old negotiated
                    // peer before ICE for the replacement offer arrives; otherwise
                    // those candidates attach to the dead peer and are discarded
                    // when WebRtcOfferReceived replaces it a moment later.
                    await ResetScreenSharePeerAsync(
                        e.FromUserId,
                        $"sender restart handshake; previous state={recoveryPeer.ConnectionState}");
                }

                ResetRemoteScreenShareReceiveState(clearImage: true);
                _remoteScreenShareSenderId = e.FromUserId;
                _isWatchingRemoteScreenShare = continueWatching;
                _remoteScreenShareCodec = ScreenShareCodecNames.IsAv1(e.Codec)
                    ? ScreenShareCodecNames.Av1
                    : ScreenShareCodecNames.H264;
                if (e.Width > 0 && e.Height > 0)
                {
                    _screenShareLastWidth = e.Width;
                    _screenShareLastHeight = e.Height;
                    _screenShareLastQuality = GetResolutionTier(e.Height);

                }
                _isRemoteVideoVisible = false;
                // Do not reset signaling here. The started notification and the
                // WebRTC offer travel independently, and the offer can arrive
                // first. Closing its newly-created peer here caused the initial
                // connection to be destroyed and forced a delayed renegotiation.
                // Realtime_WebRtcOfferReceived is the sole owner of replacing a
                // peer for a new offer.
                var displayName = GetParticipantDisplayName(e.FromUserId);
                RemoteScreenWatchTitle.Text = $"{displayName} has started a stream";
                RemoteScreenWatchSubtitle.Text = "Watch when you're ready. The stream won't load automatically.";
                RemoteScreenWatchButton.IsEnabled = true;
                RemoteScreenWatchButton.Content = "Watch now";
                if (continueWatching)
                {
                    ShowRemoteScreenShareLoading(
                        $"Reconnecting to {displayName}'s stream",
                        "Waiting for the live stream...");
                }
                UpdateMediaLayerVisibility();
                NativeCallCoordinator.Instance.SetStatus(session.State, $"{displayName} has started a stream.", session.PeerText);
            });
        }

        private async void RemoteScreenWatchButton_Click(object sender, RoutedEventArgs e)
        {
            var session = NativeCallCoordinator.Instance.CurrentSession;
            var senderId = _remoteScreenShareSenderId;
            if (_isWatchingRemoteScreenShare || senderId <= 0 || string.IsNullOrWhiteSpace(session.CallId))
                return;

            _isWatchingRemoteScreenShare = true;
            RemoteScreenWatchButton.IsEnabled = false;
            RemoteScreenWatchButton.Content = "Connecting...";
            ShowRemoteScreenShareLoading(
                $"Connecting to {GetParticipantDisplayName(senderId)}'s stream",
                "Waiting for the live stream...");
            UpdateMediaLayerVisibility();
            App.MainWindow?.CollapseSidebar();

            try
            {
                await SocialManager.Instance.Realtime.SendScreenShareQosAsync(
                    senderId,
                    session.CallId,
                    0,
                    0,
                    "restart screen-share offer: viewer selected Watch now");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ScreenShare:RTP] Watch-now request failed for participant {senderId}: {ex.Message}");
                _isWatchingRemoteScreenShare = false;
                _isRemoteScreenShareLoading = false;
                RemoteScreenWatchButton.IsEnabled = true;
                RemoteScreenWatchButton.Content = "Try again";
                UpdateMediaLayerVisibility();
            }
        }

        private void Realtime_ScreenShareStopped(object? sender, (string CallId, long FromUserId) e)
        {
            var session = NativeCallCoordinator.Instance.CurrentSession;
            if (e.CallId != session.CallId)
                return;

            var stoppedAtUtc = DateTimeOffset.UtcNow;
            var stopGeneration = Interlocked.Increment(ref _remoteScreenShareStopGeneration);
            _ = ConfirmRemoteScreenShareStoppedAsync(
                e.CallId,
                e.FromUserId,
                stoppedAtUtc,
                stopGeneration);
        }

        private async Task ConfirmRemoteScreenShareStoppedAsync(
            string callId,
            long fromUserId,
            DateTimeOffset stoppedAtUtc,
            int stopGeneration)
        {
            // Started/stopped notifications and media use independent transports.
            // A delayed stop from an older share must not tear down a peer that is
            // still delivering live RTP. Give in-flight media a short confirmation
            // window; a genuine stop has no newer frames and still closes promptly.
            await Task.Delay(TimeSpan.FromMilliseconds(1250));

            if (stopGeneration != Volatile.Read(ref _remoteScreenShareStopGeneration))
                return;

            var lastReceivedAtUtc = _remoteScreenLastReceivedAtUtc;
            if (lastReceivedAtUtc.HasValue && lastReceivedAtUtc.Value > stoppedAtUtc)
            {
                Debug.WriteLine(
                    $"[ScreenShare:RTP] Ignored stale screen-share stopped signal from {fromUserId}; live media continued for {(lastReceivedAtUtc.Value - stoppedAtUtc).TotalMilliseconds:0}ms after the signal.");
                return;
            }

            DiagnosticLogService.StopScreenShareSession("remote screen-share stopped");
            TryEnqueueOnUi(() =>
            {
                var session = NativeCallCoordinator.Instance.CurrentSession;
                if (callId != session.CallId ||
                    stopGeneration != Volatile.Read(ref _remoteScreenShareStopGeneration))
                    return;

                var displayName = GetParticipantDisplayName(fromUserId);
                var stoppedText = $"{displayName} stopped screen sharing";
                _isRemoteVideoVisible = false;
                _isRemoteScreenShareLoading = false;
                _isWatchingRemoteScreenShare = false;
                _remoteScreenShareSenderId = 0;
                ResetRemoteScreenShareReceiveState(clearImage: true);
                _ = ResetScreenSharePeerAsync(fromUserId, "confirmed remote screen-share stopped");
                RemotePlaceholderTitleText.Text = stoppedText;
                RemotePlaceholderSubtitleText.Text = $"User {fromUserId} has stopped their screen share.";
                MediaOverlayText.Text = stoppedText;
                UpdateMediaLayerVisibility();
                NativeCallCoordinator.Instance.SetStatus(session.State, stoppedText, session.PeerText);
            });
        }

        private void Realtime_ScreenShareMetadataReceived(object? sender, ScreenShareMetadataEventArgs e)
        {
            var session = NativeCallCoordinator.Instance.CurrentSession;
            if (!IsCurrentCallSignal(session, e.CallId) || e.Width <= 0 || e.Height <= 0)
                return;

            TryEnqueueOnUi(() =>
            {
                if (e.Timestamp > 0 &&
                    _lastScreenShareMetadataTimestamp > 0 &&
                    e.Timestamp < _lastScreenShareMetadataTimestamp)
                {
                    Debug.WriteLine($"[ScreenShare:H264] Ignored stale metadata {e.Width}x{e.Height}; ts={e.Timestamp}; latest={_lastScreenShareMetadataTimestamp}.");
                    return;
                }

                if (e.Timestamp > 0)
                    _lastScreenShareMetadataTimestamp = e.Timestamp;

                var hadPreviousResolution = _screenShareLastWidth > 0 && _screenShareLastHeight > 0;
                var resolutionChanged = hadPreviousResolution &&
                    (_screenShareLastWidth != e.Width || _screenShareLastHeight != e.Height);
                _remoteScreenShareSenderId = e.FromUserId;
                _remoteScreenShareCodec = ScreenShareCodecNames.IsAv1(e.Codec)
                    ? ScreenShareCodecNames.Av1
                    : ScreenShareCodecNames.H264;
                _screenShareLastWidth = e.Width;
                _screenShareLastHeight = e.Height;
                _screenShareLastQuality = GetResolutionTier(e.Height);
                _screenShareLastFrameAtUtc = DateTimeOffset.UtcNow;

                if (_isWatchingRemoteScreenShare && !_remoteScreenLastRenderedAtUtc.HasValue)
                {
                    ShowRemoteScreenShareLoading(
                        $"{GetParticipantDisplayName(e.FromUserId)} is sharing their screen",
                        $"Preparing {e.Width} x {e.Height} stream...");
                    UpdateMediaLayerVisibility();
                }

                if (resolutionChanged)
                {
                    Debug.WriteLine($"[ScreenShare:H264] Metadata resolution changed to {e.Width}x{e.Height}; ts={e.Timestamp}; clearing decoder pipeline.");
                    ResetRemoteH264Decoder("sender screen-share quality changed");
                }
            });
        }

        private void Realtime_ScreenShareQosReceived(object? sender, ScreenShareQosEventArgs e)
        {
            var session = NativeCallCoordinator.Instance.CurrentSession;
            if (e.CallId != session.CallId || !_isSharingScreen)
                return;

            ScreenShareCapturePipeline.Instance.ReportSendCongestion(
                e.Reason,
                e.DroppedReceiveFrames,
                e.RenderBacklog);

            Debug.WriteLine($"[ScreenShare:RTP] Receiver QoS from {e.FromUserId}: {e.Reason}; dropped={e.DroppedReceiveFrames}; renderBacklog={e.RenderBacklog}");

            if (e.Reason.Contains("viewer selected Watch now", StringComparison.OrdinalIgnoreCase))
            {
                lock (_screenShareViewersSync)
                    _screenShareViewerIds.Add(e.FromUserId);

                _ = StartScreenShareRtpOfferAsync(e.FromUserId, session.CallId);
            }
            else if (IsReceiverRtpRestartRequest(e.Reason))
            {
                _ = RestartScreenShareRtpOfferForParticipantAsync(e.FromUserId, session.CallId, e.Reason);
            }
        }

        private void Realtime_ScreenFrameReceived(object? sender, ScreenFrameEventArgs e)
        {
            if (!_isWatchingRemoteScreenShare)
                return;

            TryEnqueueOnUi(async () =>
            {
                var session = NativeCallCoordinator.Instance.CurrentSession;
                if (e.CallId != session.CallId || e.FrameData.Length == 0)
                {
                    Debug.WriteLine($"[ScreenShare:H264] Ignored incoming frame callId={e.CallId} sessionCallId={session.CallId} bytes={e.FrameData.Length}");
                    return;
                }

                var previewFrames = Interlocked.Increment(ref _remotePreviewFrameCount);
                if (previewFrames == 1 || previewFrames % (PreviewFallbackMaxFps * 2) == 0)
                {
                    Debug.WriteLine(
                        $"[ScreenShare:PREVIEW] Received {previewFrames} JPEG preview frames from {e.FromUserId}; bytes={e.FrameData.Length}; {e.Width}x{e.Height}.");
                }

                var now = DateTimeOffset.UtcNow;
                if (_remoteScreenLastRenderedAtUtc.HasValue &&
                    now - _remoteScreenLastRenderedAtUtc.Value < TimeSpan.FromMilliseconds(350) &&
                    Volatile.Read(ref _remoteRenderedFrameCount) > 0)
                {
                    LogRemotePreviewFlow(e, rendered: false);
                    return;
                }

                _remoteScreenShareSenderId = e.FromUserId;
                _remoteScreenLastReceivedAtUtc = now;
                try
                {
                    await RenderScreenFrameAsync(e.FrameData, RemoteScreenImage);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[ScreenShare:PREVIEW] Render failed from {e.FromUserId}: {ex}");
                    return;
                }

                _isRemoteVideoVisible = true;
                _isRemoteScreenShareLoading = false;
                _remoteScreenLastRenderedAtUtc = DateTimeOffset.UtcNow;
                LogRemotePreviewFlow(e, rendered: true);
                UpdateMediaLayerVisibility();
                MediaOverlayText.Text = $"Screen preview {e.Width} x {e.Height}";
            });
        }

        private void Realtime_EncodedScreenFrameReceived(object? sender, ScreenFrameEventArgs e)
        {
            if (!_isWatchingRemoteScreenShare)
                return;

            var session = NativeCallCoordinator.Instance.CurrentSession;
            if (e.CallId != session.CallId || e.FrameData.Length == 0)
                return;

            if (Volatile.Read(ref _remoteRtpFrameCount) > 0 &&
                _remoteScreenLastRenderedAtUtc.HasValue &&
                DateTimeOffset.UtcNow - _remoteScreenLastRenderedAtUtc.Value < TimeSpan.FromMilliseconds(500))
            {
                return;
            }

            if (e.Width > 0 && e.Height > 0)
            {
                if (_screenShareLastWidth > 0 &&
                    _screenShareLastHeight > 0 &&
                    (_screenShareLastWidth != e.Width || _screenShareLastHeight != e.Height))
                {
                    ResetRemoteH264Decoder("sender screen-share resolution changed");
                }

                _screenShareLastWidth = e.Width;
                _screenShareLastHeight = e.Height;
                _screenShareLastQuality = GetResolutionTier(e.Height);
            }

            _screenShareLastBytes = e.FrameData.LongLength;
            _screenShareLastFrameAtUtc = DateTimeOffset.UtcNow;
            _remoteScreenShareSenderId = e.FromUserId;

            if ((ScreenShareCodecNames.IsH264(e.Codec) || ScreenShareCodecNames.IsAv1(e.Codec)) && e.Width > 0 && e.Height > 0)
            {
                var isAv1 = ScreenShareCodecNames.IsAv1(e.Codec);
                var hasIdr = isAv1 || ContainsH264IdrFrame(e.FrameData);
                var isKeyFrame = isAv1 ? e.IsKeyFrame : hasIdr;
                var now = DateTimeOffset.UtcNow;
                _remoteFallbackLastReceivedAtUtc = now;
                if (_remoteFallbackFirstReceivedAtUtc == DateTimeOffset.MinValue)
                    _remoteFallbackFirstReceivedAtUtc = now;

                var receivedFallbackFrames = Interlocked.Increment(ref _remoteFallbackFrameCount);
                LogRemoteEncodedReceiveFlow("websocket", e.FromUserId, e.FrameData, e.Width, e.Height, e.IsKeyFrame, hasIdr, receivedFallbackFrames);
                if (receivedFallbackFrames == 1 || receivedFallbackFrames % (ScreenShareCapturePipeline.TargetFps * 2) == 0)
                {
                    Debug.WriteLine(
                        $"[ScreenShare:FALLBACK] Received {receivedFallbackFrames} WebSocket {(isAv1 ? "AV1X" : "H.264")} frames from {e.FromUserId}; bytes={e.FrameData.Length}; {e.Width}x{e.Height}; keyFlag={e.IsKeyFrame}; idr={hasIdr}.");
                }

                var noRtpFrames = Volatile.Read(ref _remoteRtpFrameCount) == 0;
                var fallbackWarmupElapsed = now - _remoteFallbackFirstReceivedAtUtc;
                if (noRtpFrames && fallbackWarmupElapsed < WebSocketFallbackGpuStartDelay)
                {
                    if (receivedFallbackFrames == 1 ||
                        ShouldLogFlow(ref _lastRemoteFallbackHoldLogUtc, TimeSpan.FromSeconds(1)))
                    {
                        Debug.WriteLine(
                            $"[ScreenShare:FALLBACK] Holding WebSocket {(isAv1 ? "AV1X" : "H.264")} warmup frame until RTP starts; elapsedMs={fallbackWarmupElapsed.TotalMilliseconds:0}; bytes={e.FrameData.Length}; key={hasIdr}.");
                    }

                    return;
                }

                if (noRtpFrames)
                {
                    ResetRemoteH264Decoder($"receiver switched to websocket {(isAv1 ? "AV1X" : "H.264")} stream after RTP startup timeout");
                }
                else if (receivedFallbackFrames == 1)
                {
                    Debug.WriteLine($"[ScreenShare:{(isAv1 ? "AV1X" : "H264")}] WebSocket backup stream is active; keeping existing decoder to avoid resetting during RTP decode.");
                }

                QueueRemoteH264Frame(e.FrameData, e.Width, e.Height, isKeyFrame, $"receiver websocket {(isAv1 ? "AV1X" : "H.264")} backlog", e.Codec);
                return;
            }

            TryEnqueueOnUi(async () =>
            {
                try
                {
                    await RenderScreenFrameAsync(e.FrameData, RemoteScreenImage);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[ScreenShare:PREVIEW] Encoded fallback render failed from {e.FromUserId}; codec={e.Codec}; bytes={e.FrameData.Length}: {ex}");
                    return;
                }

                _isRemoteVideoVisible = true;
                _isRemoteScreenShareLoading = false;
                _remoteScreenLastRenderedAtUtc = DateTimeOffset.UtcNow;
                UpdateMediaLayerVisibility();
                MediaOverlayText.Text = $"{e.Codec.ToUpperInvariant()} stream {e.Width} x {e.Height}";
            });
        }

        private async void Realtime_WebRtcOfferReceived(object? sender, WebRtcOfferEventArgs e)
        {
            var session = NativeCallCoordinator.Instance.CurrentSession;
            if (!IsCurrentCallSignal(session, e.CallId) || !_isWatchingRemoteScreenShare)
                return;

            try
            {
                Debug.WriteLine($"[ScreenShare:RTP] Offer received from participant {e.FromUserId}: type={e.SdpType}, sdpLength={e.Sdp?.Length ?? 0}.");
                AddCallParticipant(e.FromUserId);
                // ICE can legitimately arrive before the offer. In that case the
                // candidate handler has already created a pristine peer and queued
                // the candidate on it; replacing that peer here loses the fastest
                // path and adds seconds to first-frame startup. Only replace a peer
                // that has already negotiated an older offer.
                if (_screenSharePeers.TryGetValue(e.FromUserId, out var existingPeer) &&
                    existingPeer.HasRemoteDescription)
                {
                    await ResetScreenSharePeerAsync(e.FromUserId, "new remote offer");
                }
                var peer = GetOrCreateScreenSharePeer(e.FromUserId);
                await peer.SetRemoteOfferAsync(new SessionDescriptionModel
                {
                    Type = e.SdpType,
                    Sdp = e.Sdp
                });

                var answer = await peer.CreateAnswerAsync();
                await SocialManager.Instance.Realtime.SendAnswerAsync(e.FromUserId, e.CallId, answer.Sdp, answer.Type);
                Debug.WriteLine($"[ScreenShare:RTP] Answer sent to participant {e.FromUserId}: type={answer.Type}, sdpLength={answer.Sdp.Length}.");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ScreenShare:RTP] Offer handling failed for participant {e.FromUserId}: {ex}");
            }
        }

        private async void Realtime_WebRtcAnswerReceived(object? sender, WebRtcAnswerEventArgs e)
        {
            var session = NativeCallCoordinator.Instance.CurrentSession;
            if (!IsCurrentCallSignal(session, e.CallId) || !_screenSharePeers.TryGetValue(e.FromUserId, out var peer))
                return;

            try
            {
                Debug.WriteLine($"[ScreenShare:RTP] Answer received from participant {e.FromUserId}: type={e.SdpType}, sdpLength={e.Sdp?.Length ?? 0}.");
                await peer.SetRemoteAnswerAsync(new SessionDescriptionModel
                {
                    Type = e.SdpType,
                    Sdp = e.Sdp
                });
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ScreenShare:RTP] Answer handling failed for participant {e.FromUserId}: {ex}");
            }
        }

        private async void Realtime_WebRtcIceReceived(object? sender, WebRtcIceEventArgs e)
        {
            var session = NativeCallCoordinator.Instance.CurrentSession;
            if (!IsCurrentCallSignal(session, e.CallId))
                return;

            try
            {
                var peer = GetOrCreateScreenSharePeer(e.FromUserId);
                await peer.AddIceCandidateAsync(new IceCandidateModel
                {
                    Candidate = e.Candidate,
                    Mid = e.Mid,
                    MLineIndex = e.MLineIndex
                });
                Debug.WriteLine($"[ScreenShare:RTP] ICE candidate applied from participant {e.FromUserId}: mid={e.Mid}, index={e.MLineIndex}.");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ScreenShare:RTP] ICE candidate failed from participant {e.FromUserId}: {ex}");
            }
        }

        private async Task StartScreenShareRtpOfferAsync(long participantId, string callId)
        {
            NativePeerConnectionService? peer = null;

            try
            {
                Debug.WriteLine($"[ScreenShare:RTP] Starting offer for participant {participantId}.");
                peer = GetOrCreateScreenSharePeer(participantId);
                await peer.AttachScreenShareAsync(null!);

                var offer = await peer.CreateOfferAsync();
                await SocialManager.Instance.Realtime.SendOfferAsync(participantId, callId, offer.Sdp, offer.Type);
                Debug.WriteLine($"[ScreenShare:RTP] Offer sent to participant {participantId}.");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ScreenShare:RTP] Offer failed for participant {participantId}: {ex}");

                if (peer != null)
                    await RemoveScreenSharePeerAsync(participantId, peer);
            }
        }

        private async Task SendEncodedVideoFrameToPeerAsync(long participantId, NativePeerConnectionService peer, byte[] frameData)
        {
            try
            {
                await peer.SendEncodedVideoFrameAsync(frameData, ScreenShareCapturePipeline.Instance.ActiveVideoCodec);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ScreenShare:RTP] Encoded frame send failed for participant {participantId}: {ex.Message}");
                await RemoveScreenSharePeerAsync(participantId, peer);
            }
        }

        private async Task RemoveScreenSharePeerAsync(long participantId, NativePeerConnectionService peer)
        {
            if (_screenSharePeers.TryGetValue(participantId, out var existingPeer) &&
                ReferenceEquals(existingPeer, peer))
            {
                _screenSharePeers.Remove(participantId);
            }

            try
            {
                await peer.CloseAsync();
            }
            catch
            {
            }
        }

        private async Task ResetScreenSharePeerAsync(long participantId, string reason)
        {
            if (!_screenSharePeers.TryGetValue(participantId, out var peer))
                return;

            Debug.WriteLine($"[ScreenShare:RTP] Resetting participant {participantId} peer: {reason}.");
            await RemoveScreenSharePeerAsync(participantId, peer);
        }

        private NativePeerConnectionService GetOrCreateScreenSharePeer(long participantId)
        {
            if (_screenSharePeers.TryGetValue(participantId, out var existingPeer))
                return existingPeer;

            var peer = ScreenShareServiceSelector.Active.CreatePeer($"participant {participantId}");
            peer.SetExpectedRemoteVideoCodec(_remoteScreenShareCodec);
            peer.LocalIceCandidateReady += async (_, ice) =>
            {
                var session = NativeCallCoordinator.Instance.CurrentSession;
                if (!string.IsNullOrWhiteSpace(session.CallId))
                {
                    await SocialManager.Instance.Realtime.SendIceCandidateAsync(
                        participantId,
                        session.CallId,
                        ice.Candidate,
                        ice.Mid,
                        ice.MLineIndex);
                    Debug.WriteLine($"[ScreenShare:RTP] ICE candidate sent to participant {participantId}: mid={ice.Mid}, index={ice.MLineIndex}.");
                }
            };
            peer.EncodedVideoFrameReceived += ScreenSharePeer_EncodedVideoFrameReceived;
            peer.ConnectionStateChanged += (_, state) =>
            {
                _ = HandleScreenSharePeerConnectionStateChangedAsync(participantId, peer, state);
            };

            _screenSharePeers[participantId] = peer;
            return peer;
        }

        private async Task HandleScreenSharePeerConnectionStateChangedAsync(
            long participantId,
            NativePeerConnectionService peer,
            string state)
        {
            if (state.Equals("connected", StringComparison.OrdinalIgnoreCase))
            {
                // The initial encoder IDR may have been generated while this peer
                // was still negotiating, so it could only be sent over the preview
                // fallback.  Force a new, unthrottled IDR at the exact point RTP is
                // usable instead of starting the receiver on dependent P-frames.
                if (_isSharingScreen && ScreenShareCapturePipeline.Instance.IsRunning)
                {
                    ScreenShareCapturePipeline.Instance.RequestRecoveryKeyFrame(
                        $"RTP transport connected for participant {participantId}; send first live IDR");
                    Debug.WriteLine(
                        $"[ScreenShare:RTP] Participant {participantId} connected; requested an immediate live IDR.");
                }

                return;
            }

            if (!state.Equals("failed", StringComparison.OrdinalIgnoreCase) &&
                !state.Equals("disconnected", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (state.Equals("disconnected", StringComparison.OrdinalIgnoreCase))
            {
                // WebRTC can report a short-lived disconnected state while ICE
                // checks a different route. Give that connection time to recover;
                // immediately replacing it abandons the working peer and leaves
                // the replacement offer stuck in the new state.
                await Task.Delay(TimeSpan.FromSeconds(2));

                if (!_screenSharePeers.TryGetValue(participantId, out var currentPeer) ||
                    !ReferenceEquals(currentPeer, peer) ||
                    !peer.ConnectionState.Equals("disconnected", StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }
            }

            var session = NativeCallCoordinator.Instance.CurrentSession;
            if (!_isSharingScreen ||
                !ScreenShareCapturePipeline.Instance.IsRunning ||
                session.State != NativeCallState.Connected ||
                string.IsNullOrWhiteSpace(session.CallId))
            {
                return;
            }

            var now = DateTimeOffset.UtcNow;
            lock (_screenSharePeerRestartSync)
            {
                if (_screenSharePeerRestartLastAttemptUtc.TryGetValue(participantId, out var lastAttempt) &&
                    now - lastAttempt < TimeSpan.FromSeconds(3))
                {
                    return;
                }

                _screenSharePeerRestartLastAttemptUtc[participantId] = now;
            }

            try
            {
                Debug.WriteLine($"[ScreenShare:RTP] Restarting participant {participantId} RTP offer after connectionState={state}.");
                await ResetScreenSharePeerAsync(participantId, $"RTP connection {state}");
                await StartScreenShareRtpOfferAsync(participantId, session.CallId);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ScreenShare:RTP] Restart after connectionState={state} failed for participant {participantId}: {ex.Message}");
            }
        }

        private async Task RestartScreenShareRtpOfferForParticipantAsync(long participantId, string callId, string reason)
        {
            var session = NativeCallCoordinator.Instance.CurrentSession;
            if (!_isSharingScreen ||
                !ScreenShareCapturePipeline.Instance.IsRunning ||
                session.State != NativeCallState.Connected ||
                string.IsNullOrWhiteSpace(callId))
            {
                return;
            }

            var now = DateTimeOffset.UtcNow;
            lock (_screenSharePeerRestartSync)
            {
                if (_screenSharePeerRestartLastAttemptUtc.TryGetValue(participantId, out var lastAttempt) &&
                    now - lastAttempt < TimeSpan.FromSeconds(4))
                {
                    Debug.WriteLine($"[ScreenShare:RTP] Ignored receiver media-stall restart for participant {participantId}; restart is cooling down. reason={reason}");
                    return;
                }

                _screenSharePeerRestartLastAttemptUtc[participantId] = now;
            }

            try
            {
                Debug.WriteLine($"[ScreenShare:RTP] Receiver requested a fresh RTP offer for participant {participantId}: {reason}");
                await ResetScreenSharePeerAsync(participantId, $"receiver media stall: {reason}");
                await SocialManager.Instance.Realtime.SendScreenShareStartedAsync(participantId, callId);
                await StartScreenShareRtpOfferAsync(participantId, callId);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ScreenShare:RTP] Receiver media-stall restart failed for participant {participantId}: {ex}");
            }
        }

        private static bool IsReceiverRtpRestartRequest(string reason)
        {
            return reason.Contains("restart screen-share offer", StringComparison.OrdinalIgnoreCase) ||
                reason.Contains("RTP stalled", StringComparison.OrdinalIgnoreCase);
        }

        private void ScreenSharePeer_EncodedVideoFrameReceived(object? sender, NativeRtpVideoFrameEventArgs e)
        {
            if (!_isWatchingRemoteScreenShare)
                return;

            var width = _lastRemoteBitstreamWidth > 0 ? _lastRemoteBitstreamWidth : _screenShareLastWidth;
            var height = _lastRemoteBitstreamHeight > 0 ? _lastRemoteBitstreamHeight : _screenShareLastHeight;
            var isAv1 = ScreenShareCodecNames.IsAv1(e.Codec);
            var isKeyFrame = isAv1 || ContainsH264IdrFrame(e.FrameData);
            if (!isAv1 && TryReadH264Dimensions(e.FrameData, out var bitstreamWidth, out var bitstreamHeight))
            {
                if (!IsPlausibleRemoteBitstreamResolution(bitstreamWidth, bitstreamHeight, width, height))
                {
                    Debug.WriteLine($"[ScreenShare:H264] Ignored implausible RTP SPS size {bitstreamWidth}x{bitstreamHeight}; keeping {width}x{height}.");
                }
                else
                {
                    if (bitstreamWidth != width || bitstreamHeight != height)
                    {
                        Debug.WriteLine($"[ScreenShare:H264] RTP bitstream SPS overrides assumed size {width}x{height} -> {bitstreamWidth}x{bitstreamHeight}.");
                    }

                    width = bitstreamWidth;
                    height = bitstreamHeight;
                    _lastRemoteBitstreamWidth = bitstreamWidth;
                    _lastRemoteBitstreamHeight = bitstreamHeight;
                    _lastRemoteBitstreamResolutionAtUtc = DateTimeOffset.UtcNow;
                    _screenShareLastWidth = bitstreamWidth;
                    _screenShareLastHeight = bitstreamHeight;
                    _screenShareLastQuality = GetResolutionTier(bitstreamHeight);
                }
            }

            if (width <= 0 || height <= 0)
            {
                _remoteScreenLastReceivedAtUtc = DateTimeOffset.UtcNow;
                _screenShareDroppedReceiveFrames++;
                _ = SendScreenShareQosIfNeededAsync(isAv1 ? "receiver waiting for AV1X metadata dimensions" : "receiver waiting for H.264 SPS dimensions");
                RequestRemoteVideoKeyFrame(isAv1 ? "receiver needs AV1X metadata before starting playback" : "receiver needs an IDR with SPS before starting GPU playback");
                if (ShouldLogFlow(ref _lastRemoteGpuPlaybackDropLogUtc, TimeSpan.FromSeconds(1)))
                {
                    Debug.WriteLine(
                        $"[ScreenShare:{(isAv1 ? "AV1X" : "H264")}] Dropped RTP frame before trusted bitstream dimensions were known; bytes={e.FrameData.Length}; key={isKeyFrame}.");
                }
                return;
            }

            var receivedRtpFrames = Interlocked.Increment(ref _remoteRtpFrameCount);
            if (receivedRtpFrames == 1)
                _remoteFallbackFirstReceivedAtUtc = DateTimeOffset.MinValue;
            if (!isAv1)
                Interlocked.Exchange(ref _remoteH264RecoveryInFlight, 0);

            LogRemoteEncodedReceiveFlow("rtp", _remoteScreenShareSenderId, e.FrameData, width, height, isKeyFrame, isKeyFrame, receivedRtpFrames);
            if (receivedRtpFrames == 1 || receivedRtpFrames % (ScreenShareCapturePipeline.TargetFps * 2) == 0)
            {
                Debug.WriteLine(
                    $"[ScreenShare:RTP] Received {receivedRtpFrames} remote RTP {(isAv1 ? "AV1X" : "H.264")} frames: bytes={e.FrameData.Length}, assumed={width}x{height}, key={isKeyFrame}.");
            }

            if (DateTimeOffset.UtcNow - _remoteFallbackLastReceivedAtUtc < RemoteFallbackRtpSuppressWindow)
            {
                if (receivedRtpFrames == 1 || receivedRtpFrames % (ScreenShareCapturePipeline.TargetFps * 2) == 0)
                    Debug.WriteLine("[ScreenShare:RTP] RTP frame arrived while WebSocket backup was active; preferring RTP for lower latency.");

                _remoteFallbackLastReceivedAtUtc = DateTimeOffset.MinValue;
            }

            QueueRemoteH264Frame(e.FrameData, width, height, isKeyFrame, "receiver RTP backlog", e.Codec);
        }

        private void LogScreenShareSendSummary(int participantCount, bool sentToAnyPeer, int fallbackCount, NativeScreenFrameEventArgs e, TimeSpan sendElapsed)
        {
            var now = DateTimeOffset.UtcNow;
            if (now - _lastScreenShareSendSummaryUtc < TimeSpan.FromSeconds(2))
                return;

            _lastScreenShareSendSummaryUtc = now;
            var peerSummary = _screenSharePeers.Count == 0
                ? "no peers"
                : string.Join(", ", _screenSharePeers.Select(pair => $"{pair.Key}:{pair.Value.ConnectionState}/sent={pair.Value.SentVideoFrames}/recv={pair.Value.ReceivedVideoFrames}"));

            var message = $"[ScreenShare:RTP] Send summary: participants={participantCount}; rtpSent={_screenShareRtpFrames}; fallbackSent={_screenShareFallbackFrames}; fallbackTargets={fallbackCount}; sentToAnyPeer={sentToAnyPeer}; sendMs={sendElapsed.TotalMilliseconds:0.0}; frameBytes={e.FrameData.Length}; previewBytes={e.PreviewFrameData.Length}; previewSent={_screenSharePreviewFallbackFrames}; key={e.IsKeyFrame}; peers=[{peerSummary}].";
            Debug.WriteLine(message);
            DiagnosticLogService.WriteLine(message);
        }

        private void QueueRemoteH264Frame(byte[] frameData, int width, int height, bool isKeyFrame, string backlogReason, string codec = "h264")
        {
            _remoteScreenLastReceivedAtUtc = DateTimeOffset.UtcNow;
            var isAv1 = ScreenShareCodecNames.IsAv1(codec);
            var hasIdr = isAv1 || ContainsH264IdrFrame(frameData);
            if (isKeyFrame && !hasIdr)
            {
                Debug.WriteLine(
                    $"[ScreenShare:FLOW:QUEUE] Keyframe flag ignored because compressed frame has no IDR. bytes={frameData.Length}; {width}x{height}; reason={backlogReason}.");
            }

            isKeyFrame = hasIdr;

            if (isKeyFrame)
            {
                _remoteH264WaitingForKeyFrame = false;
                _remoteH264FramesDroppedWaitingForKeyFrame = 0;
            }

            if (!isAv1 && UseRemoteGpuPlayback && QueueRemoteGpuPlaybackFrame(frameData, width, height, isKeyFrame, backlogReason))
                return;

            long sequence;
            int queueCount;
            lock (_rtpFrameSync)
            {
                if (_remoteH264WaitingForKeyFrame)
                {
                    if (!isKeyFrame)
                    {
                        _screenShareDroppedReceiveFrames++;
                        _remoteH264FramesDroppedWaitingForKeyFrame++;
                        if (_remoteH264FramesDroppedWaitingForKeyFrame == 1 ||
                            _remoteH264FramesDroppedWaitingForKeyFrame % ScreenShareCapturePipeline.TargetFps == 0)
                        {
                            Debug.WriteLine(
                                $"[ScreenShare:H264] Waiting for keyframe; dropped {_remoteH264FramesDroppedWaitingForKeyFrame} delta frames after decoder reset.");
                        }

                        _ = SendScreenShareQosIfNeededAsync("receiver waiting for keyframe");
                        return;
                    }

                    _remoteH264WaitingForKeyFrame = false;
                    _remoteH264FramesDroppedWaitingForKeyFrame = 0;
                    _pendingRemoteH264Frames.Clear();
                    Debug.WriteLine($"[ScreenShare:H264] Keyframe received; restarting remote decoder at {width}x{height}.");
                }

                PruneRemoteH264Backlog(backlogReason);

                if (_remoteH264WaitingForKeyFrame)
                {
                    if (!isKeyFrame)
                    {
                        _screenShareDroppedReceiveFrames++;
                        _remoteH264FramesDroppedWaitingForKeyFrame++;
                        if (_remoteH264FramesDroppedWaitingForKeyFrame == 1 ||
                            _remoteH264FramesDroppedWaitingForKeyFrame % ScreenShareCapturePipeline.TargetFps == 0)
                        {
                            Debug.WriteLine(
                                $"[ScreenShare:H264] Waiting for keyframe after realtime backlog prune; dropped {_remoteH264FramesDroppedWaitingForKeyFrame} delta frames.");
                        }

                        _ = SendScreenShareQosIfNeededAsync("receiver waiting for keyframe after realtime backlog prune");
                        return;
                    }

                    _remoteH264WaitingForKeyFrame = false;
                    _remoteH264FramesDroppedWaitingForKeyFrame = 0;
                    _pendingRemoteH264Frames.Clear();
                    Debug.WriteLine($"[ScreenShare:H264] Keyframe received after realtime backlog prune; restarting remote decoder at {width}x{height}.");
                }

                if (_pendingRemoteH264Frames.Count >= MaxRemoteH264HardDecodeQueue)
                {
                    var beforeDrop = _pendingRemoteH264Frames.Count;
                    var queuedFrames = _pendingRemoteH264Frames.ToList();
                    var latestQueuedKeyIndex = queuedFrames.FindLastIndex(frame => frame.IsKeyFrame);
                    _pendingRemoteH264Frames.Clear();
                    if (latestQueuedKeyIndex >= 0)
                    {
                        for (var i = latestQueuedKeyIndex; i < queuedFrames.Count; i++)
                            _pendingRemoteH264Frames.Enqueue(queuedFrames[i]);
                    }
                    var dropped = beforeDrop - _pendingRemoteH264Frames.Count;
                    _screenShareDroppedReceiveFrames += dropped;
                    _remoteBacklogTrimmedSinceLastLog += dropped;
                    _ = SendScreenShareQosIfNeededAsync(backlogReason);

                    if (!isKeyFrame && latestQueuedKeyIndex < 0)
                    {
                        // None of the retained frames is independently decodable.
                        // Continuing from this incomplete dependency chain causes
                        // macroblock corruption until a later IDR.  Pause playback
                        // and discard deltas until the requested keyframe arrives.
                        _pendingRemoteH264Frames.Clear();
                        _remoteH264WaitingForKeyFrame = true;
                        _remoteH264FramesDroppedWaitingForKeyFrame = 0;
                        _remoteDecodeGeneration++;
                        RequestRemoteVideoKeyFrame("receiver decode queue overflow");
                        Debug.WriteLine($"[ScreenShare:H264] Decode queue pressure; discarded an incomplete dependent chain after dropping {dropped} stale frames and will resume on a fresh IDR.");
                        return;
                    }

                    if (isKeyFrame || latestQueuedKeyIndex >= 0)
                    {
                        _remoteH264WaitingForKeyFrame = false;
                        _remoteH264FramesDroppedWaitingForKeyFrame = 0;
                        Debug.WriteLine($"[ScreenShare:H264] Decode queue pressure; dropped {dropped} stale frames and retained the newest keyframe chain.");
                    }
                }

                _pendingRtpFrameSequence++;
                sequence = _pendingRtpFrameSequence;
                var queuedAtUtc = DateTimeOffset.UtcNow;
                if (_lastRemoteQueuedSequence > 0 && sequence > _lastRemoteQueuedSequence + 1)
                    _remoteSequenceGapCount += sequence - _lastRemoteQueuedSequence - 1;
                _lastRemoteQueuedSequence = sequence;
                _lastRemoteQueuedAtUtc = queuedAtUtc;
                var generation = _remoteDecodeGeneration;
                var pendingFrame = new PendingRemoteH264Frame(
                    frameData,
                    width,
                    height,
                    isKeyFrame,
                    sequence,
                    generation,
                    RemoteGpuFrameDuration,
                    codec)
                {
                    QueuedAtUtc = queuedAtUtc
                };
                _pendingRemoteH264Frames.Enqueue(pendingFrame);
                queueCount = _pendingRemoteH264Frames.Count;
            }

            LogRemoteQueueFlow(sequence, queueCount, isKeyFrame, backlogReason);

            if (Interlocked.CompareExchange(ref _rtpDecodePumpRunning, 1, 0) == 0)
            {
                _ = Task.Factory.StartNew(
                    ProcessLatestRtpFramesAsync,
                    CancellationToken.None,
                    TaskCreationOptions.DenyChildAttach | TaskCreationOptions.LongRunning,
                    TaskScheduler.Default).Unwrap();
            }
        }

        private void PruneRemoteH264Backlog(string reason)
        {
            if (_pendingRemoteH264Frames.Count < MaxRemoteH264DecodeQueue)
            {
                _remoteDecodeBacklogStartedAtUtc = DateTimeOffset.MinValue;
                return;
            }

            var now = DateTimeOffset.UtcNow;
            if (_remoteDecodeBacklogStartedAtUtc == DateTimeOffset.MinValue)
            {
                _remoteDecodeBacklogStartedAtUtc = now;
                return;
            }

            // A single 80-160 ms decoder spike is common on the ARM64 receiver.
            // Give that burst time to drain instead of immediately throwing away
            // a valid H.264 dependency chain and freezing while awaiting an IDR.
            if (now - _remoteDecodeBacklogStartedAtUtc < TimeSpan.FromMilliseconds(100))
                return;

            _remoteDecodeBacklogStartedAtUtc = DateTimeOffset.MinValue;

            var frames = _pendingRemoteH264Frames.ToList();
            var latestKeyIndex = frames.FindLastIndex(frame => frame.IsKeyFrame);
            if (latestKeyIndex < 0)
            {
                RequestRemoteVideoKeyFrame("receiver realtime queue contains only dependent delta frames");
                if (ShouldLogFlow(ref _lastRemoteBacklogTrimLogUtc, TimeSpan.FromSeconds(1)))
                {
                    Debug.WriteLine($"[ScreenShare:H264] Preserving the dependent delta chain while requesting a fresh IDR; queue={_pendingRemoteH264Frames.Count}; reason={reason}.");
                }

                return;
            }

            if (latestKeyIndex == 0)
            {
                if (_pendingRemoteH264Frames.Count >= MaxRemoteH264DecodeQueue)
                    DropRemoteBacklogAndRequestKeyFrame(reason, "receiver realtime queue exceeded low-latency budget after current IDR", waitForKeyFrame: false);

                return;
            }

            _pendingRemoteH264Frames.Clear();
            _remoteDecodeBacklogStartedAtUtc = DateTimeOffset.MinValue;
            for (var i = latestKeyIndex; i < frames.Count; i++)
                _pendingRemoteH264Frames.Enqueue(frames[i]);

            _screenShareDroppedReceiveFrames += latestKeyIndex;
            _remoteBacklogJumpedSinceLastLog += latestKeyIndex;
            _ = SendScreenShareQosIfNeededAsync(reason);
            if (ShouldLogFlow(ref _lastRemoteBacklogJumpLogUtc, TimeSpan.FromSeconds(2)))
            {
                Debug.WriteLine($"[ScreenShare:H264] Realtime backlog jumped to latest keyframe; dropped {_remoteBacklogJumpedSinceLastLog} queued frames; queue={_pendingRemoteH264Frames.Count}; reason={reason}.");
                _remoteBacklogJumpedSinceLastLog = 0;
            }
        }

        private void DropRemoteBacklogAndRequestKeyFrame(string reason, string keyFrameReason, bool waitForKeyFrame = true)
        {
            var dropped = _pendingRemoteH264Frames.Count;
            if (dropped <= 0)
                return;

            _pendingRemoteH264Frames.Clear();
            _remoteDecodeBacklogStartedAtUtc = DateTimeOffset.MinValue;
            _screenShareDroppedReceiveFrames += dropped;
            _remoteBacklogTrimmedSinceLastLog += dropped;
            _remoteH264WaitingForKeyFrame = waitForKeyFrame;
            _remoteH264FramesDroppedWaitingForKeyFrame = 0;
            _remoteDecodeGeneration++;
            _ = SendScreenShareQosIfNeededAsync(reason);
            RequestRemoteVideoKeyFrame(keyFrameReason);
            if (ShouldLogFlow(ref _lastRemoteBacklogTrimLogUtc, TimeSpan.FromSeconds(1)))
            {
                Debug.WriteLine($"[ScreenShare:H264] Receiver recovery dropped {_remoteBacklogTrimmedSinceLastLog} queued frames and requested a fresh IDR; queue={_pendingRemoteH264Frames.Count}; reason={reason}; keyRequest={keyFrameReason}.");
                _remoteBacklogTrimmedSinceLastLog = 0;
            }
        }

        private bool QueueRemoteGpuPlaybackFrame(byte[] frameData, int width, int height, bool isKeyFrame, string backlogReason)
        {
            if (width <= 0 || height <= 0 || frameData.Length == 0)
                return false;

            var shouldStartPlayback = false;
            lock (_remoteGpuPlaybackSync)
            {
                if (_remoteGpuPlaybackStopping)
                    return true;

                var dimensionsMatch = (_remoteGpuPlaybackEnabled || _remoteGpuPlaybackStarting) &&
                    _remoteGpuPlaybackWidth == width &&
                    _remoteGpuPlaybackHeight == height;

                if (!dimensionsMatch)
                {
                    if (!isKeyFrame)
                    {
                        _screenShareDroppedReceiveFrames++;
                        _ = SendScreenShareQosIfNeededAsync("receiver GPU playback waiting for IDR");
                        RequestRemoteVideoKeyFrame("receiver GPU playback needs an IDR to start");
                        return true;
                    }

                    var dropped = _pendingRemoteGpuPlaybackFrames.Count;
                    _pendingRemoteGpuPlaybackFrames.Clear();
                    _screenShareDroppedReceiveFrames += dropped;
                    _remoteGpuPlaybackStarting = true;
                    _remoteGpuPlaybackWidth = width;
                    _remoteGpuPlaybackHeight = height;
                    _remoteGpuWaitingForKeyFrame = false;
                    _remoteGpuBacklogKeyFrameRequested = false;
                    _remoteGpuFirstSampleSubmitted = false;
                    _remoteGpuEstimatedFrameDuration = RemoteGpuFrameDuration;
                    _remoteGpuLastQueuedAtUtc = null;
                    _remoteGpuBacklogStartedAtUtc = DateTimeOffset.MinValue;
                    shouldStartPlayback = true;
                    if (dropped > 0)
                        Debug.WriteLine($"[ScreenShare:GPU:RECEIVER] Restarting hardware playback from IDR at {width}x{height}; dropped {dropped} stale compressed frames.");
                }
                else if (_remoteGpuPlaybackStarting && !_remoteGpuPlaybackEnabled && !isKeyFrame)
                {
                    _screenShareDroppedReceiveFrames++;
                    return true;
                }

                if (_remoteGpuWaitingForKeyFrame)
                {
                    if (!isKeyFrame)
                    {
                        _screenShareDroppedReceiveFrames++;
                        return true;
                    }

                    _pendingRemoteGpuPlaybackFrames.Clear();
                    _remoteGpuWaitingForKeyFrame = false;
                    _remoteGpuBacklogKeyFrameRequested = false;
                    _remoteGpuEstimatedFrameDuration = RemoteGpuFrameDuration;
                    _remoteGpuLastQueuedAtUtc = null;
                    _remoteGpuBacklogStartedAtUtc = DateTimeOffset.MinValue;
                    Debug.WriteLine($"[ScreenShare:GPU:RECEIVER] Fresh IDR received; restarted compressed GPU playback queue at {width}x{height}.");
                }

                if (isKeyFrame && _remoteGpuBacklogKeyFrameRequested && _pendingRemoteGpuPlaybackFrames.Count > 0)
                {
                    var dropped = _pendingRemoteGpuPlaybackFrames.Count;
                    _pendingRemoteGpuPlaybackFrames.Clear();
                    _screenShareDroppedReceiveFrames += dropped;
                    _remoteBacklogTrimmedSinceLastLog += dropped;
                    _remoteGpuBacklogKeyFrameRequested = false;
                    _remoteGpuEstimatedFrameDuration = RemoteGpuFrameDuration;
                    _remoteGpuLastQueuedAtUtc = null;
                    _remoteGpuBacklogStartedAtUtc = DateTimeOffset.MinValue;
                    Debug.WriteLine($"[ScreenShare:GPU:RECEIVER] Fresh IDR caught up realtime playback; dropped {dropped} old dependent compressed frames before decode.");
                }

                var queuedBeforeCurrentFrame = _pendingRemoteGpuPlaybackFrames.Count;
                if (queuedBeforeCurrentFrame < RemoteGpuBacklogKeyFrameRequestQueue)
                {
                    _remoteGpuBacklogStartedAtUtc = DateTimeOffset.MinValue;
                }
                else if (_remoteGpuBacklogStartedAtUtc == DateTimeOffset.MinValue)
                {
                    _remoteGpuBacklogStartedAtUtc = DateTimeOffset.UtcNow;
                }

                var backlogHasPersisted = _remoteGpuBacklogStartedAtUtc != DateTimeOffset.MinValue &&
                    DateTimeOffset.UtcNow - _remoteGpuBacklogStartedAtUtc >= RemoteGpuSustainedBacklogThreshold;
                if (!isKeyFrame &&
                    !_remoteGpuBacklogKeyFrameRequested &&
                    queuedBeforeCurrentFrame >= RemoteGpuBacklogKeyFrameRequestQueue &&
                    backlogHasPersisted)
                {
                    _remoteGpuBacklogKeyFrameRequested = true;
                    var keyFrameReason = "receiver GPU playback keyframe requested after realtime queue pressure";
                    _ = SendScreenShareQosIfNeededAsync(keyFrameReason);
                    RequestRemoteVideoKeyFrame(keyFrameReason);
                    if (ShouldLogFlow(ref _lastRemoteGpuPlaybackDropLogUtc, TimeSpan.FromSeconds(1)))
                    {
                        Debug.WriteLine($"[ScreenShare:GPU:RECEIVER] GPU playback queue reached {RemoteGpuBacklogKeyFrameRequestQueue}; preserving H.264 dependency chain while requesting a fresh IDR. queue={_pendingRemoteGpuPlaybackFrames.Count}; reason={backlogReason}.");
                        _remoteBacklogTrimmedSinceLastLog = 0;
                    }
                }

                if (_pendingRemoteGpuPlaybackFrames.Count >= MaxRemoteGpuPlaybackQueue)
                {
                    if (isKeyFrame)
                    {
                        var dropped = _pendingRemoteGpuPlaybackFrames.Count;
                        _pendingRemoteGpuPlaybackFrames.Clear();
                        _screenShareDroppedReceiveFrames += dropped;
                        _remoteBacklogTrimmedSinceLastLog += dropped;
                        _remoteGpuBacklogKeyFrameRequested = false;
                        Debug.WriteLine($"[ScreenShare:GPU:RECEIVER] GPU playback queue caught up from a fresh IDR; dropped {dropped} old compressed frames before decode.");
                    }
                    else
                    {
                        if (ShouldLogFlow(ref _lastRemoteGpuPlaybackDropLogUtc, TimeSpan.FromSeconds(1)))
                        {
                            Debug.WriteLine($"[ScreenShare:GPU:RECEIVER] GPU playback queue reached {MaxRemoteGpuPlaybackQueue}; dropping incoming delta until a fresh IDR arrives to avoid corrupting the H.264 chain. reason={backlogReason}.");
                            _remoteBacklogTrimmedSinceLastLog = 0;
                        }

                        _screenShareDroppedReceiveFrames++;
                        return true;
                    }
                }

                _remoteGpuPlaybackSequence++;
                var queuedAtUtc = DateTimeOffset.UtcNow;
                var sampleDuration = EstimateRemoteGpuSampleDuration(queuedAtUtc);
                _pendingRemoteGpuPlaybackFrames.Enqueue(new PendingRemoteH264Frame(
                    frameData,
                    width,
                    height,
                    isKeyFrame,
                    _remoteGpuPlaybackSequence,
                    Volatile.Read(ref _remoteDecodeGeneration),
                    sampleDuration));
                var queuedCount = _pendingRemoteGpuPlaybackFrames.Count;
                _remoteGpuPlaybackQueuePeak = Math.Max(_remoteGpuPlaybackQueuePeak, queuedCount);
                if (queuedCount >= RemoteGpuBacklogKeyFrameRequestQueue / 2 &&
                    ShouldLogFlow(ref _lastRemoteGpuPlaybackDropLogUtc, TimeSpan.FromSeconds(2)))
                {
                    Debug.WriteLine($"[ScreenShare:GPU:RECEIVER] 60fps queue watch: queue={queuedCount}; requestThreshold={RemoteGpuBacklogKeyFrameRequestQueue}; hardLimit={MaxRemoteGpuPlaybackQueue}; receiveFps={_remoteReceiveObservedFps:0.0}; playbackFps={_remotePlaybackObservedFps:0.0}; reason={backlogReason}.");
                }
                Monitor.PulseAll(_remoteGpuPlaybackSync);
            }

            if (shouldStartPlayback)
                TryEnqueueOnUi(() => StartRemoteGpuPlayback(width, height));

            _remoteScreenLastRenderedAtUtc = DateTimeOffset.UtcNow;
            _isRemoteVideoVisible = true;
            _isRemoteScreenShareLoading = false;

            return true;
        }

        private TimeSpan EstimateRemoteGpuSampleDuration(DateTimeOffset queuedAtUtc)
        {
            if (_remoteGpuLastQueuedAtUtc.HasValue)
            {
                var observed = queuedAtUtc - _remoteGpuLastQueuedAtUtc.Value;
                if (observed >= RemoteGpuMinFrameDuration && observed <= RemoteGpuMaxFrameDuration)
                {
                    var smoothedTicks = (long)((_remoteGpuEstimatedFrameDuration.Ticks * 0.82) + (observed.Ticks * 0.18));
                    _remoteGpuEstimatedFrameDuration = TimeSpan.FromTicks(smoothedTicks);
                }
            }

            _remoteGpuLastQueuedAtUtc = queuedAtUtc;
            return _remoteGpuEstimatedFrameDuration;
        }

        private void StartRemoteGpuPlayback(int width, int height)
        {
            if (_remoteGpuPlaybackEnabled &&
                _remoteGpuPlaybackWidth == width &&
                _remoteGpuPlaybackHeight == height)
            {
                return;
            }

            StopRemoteGpuPlayback(clearSurface: false, preservePendingFrames: true);

            var videoProperties = VideoEncodingProperties.CreateH264();
            videoProperties.Width = (uint)width;
            videoProperties.Height = (uint)height;
            videoProperties.FrameRate.Numerator = (uint)RemoteGpuCompatibilityFps;
            videoProperties.FrameRate.Denominator = 1;
            videoProperties.PixelAspectRatio.Numerator = 1;
            videoProperties.PixelAspectRatio.Denominator = 1;

            var descriptor = new VideoStreamDescriptor(videoProperties);
            var source = new MediaStreamSource(descriptor)
            {
                BufferTime = TimeSpan.Zero,
                CanSeek = false,
                Duration = TimeSpan.Zero
            };

            source.SampleRequested += RemoteGpuMediaStreamSource_SampleRequested;
            source.Starting += RemoteGpuMediaStreamSource_Starting;

            var player = new MediaPlayer
            {
                AutoPlay = true,
                RealTimePlayback = true
            };

            player.Source = MediaSource.CreateFromMediaStreamSource(source);
            RemoteScreenPlayer.SetMediaPlayer(player);
            RemoteScreenPlayer.Visibility = Visibility.Visible;
            RemoteScreenImage.Visibility = Visibility.Collapsed;
            MediaOverlayText.Text = $"GPU H.264 stream {width} x {height}";
            UpdateMediaLayerVisibility();
            player.Play();

            lock (_remoteGpuPlaybackSync)
            {
                _remoteGpuPlaybackEnabled = true;
                _remoteGpuPlaybackStarting = false;
                _remoteGpuPlaybackStopping = false;
                _remoteGpuPlaybackWidth = width;
                _remoteGpuPlaybackHeight = height;
                _remoteGpuPlaybackSampleTime = TimeSpan.Zero;
                _remoteGpuEstimatedFrameDuration = RemoteGpuFrameDuration;
                _remoteGpuLastQueuedAtUtc = null;
                _remoteGpuWaitingForKeyFrame = false;
                _remoteGpuBacklogKeyFrameRequested = false;
                _remoteGpuFirstSampleSubmitted = false;
                Monitor.PulseAll(_remoteGpuPlaybackSync);
            }

            _remoteGpuMediaStreamSource = source;
            _remoteGpuMediaPlayer = player;
            Debug.WriteLine($"[ScreenShare:GPU:RECEIVER] Started hardware MediaStreamSource playback surface {width}x{height} @ {RemoteGpuCompatibilityFps}fps. Decoded frames stay out of managed BGRA/WriteableBitmap path.");
        }

        private void RemoteGpuMediaStreamSource_Starting(MediaStreamSource sender, MediaStreamSourceStartingEventArgs args)
        {
            args.Request.SetActualStartPosition(TimeSpan.Zero);
            Debug.WriteLine("[ScreenShare:GPU:RECEIVER] MediaStreamSource starting at live position 0.");
        }

        private void RemoteGpuMediaStreamSource_SampleRequested(MediaStreamSource sender, MediaStreamSourceSampleRequestedEventArgs args)
        {
            var deferral = args.Request.GetDeferral();
            try
            {
                PendingRemoteH264Frame? frame = null;
                lock (_remoteGpuPlaybackSync)
                {
                    var waitUntil = DateTimeOffset.UtcNow + RemoteGpuSampleWaitTimeout;
                    var requestedRecoveryForCurrentUnderrun = false;
                    while (!_remoteGpuPlaybackStopping)
                    {
                        if (_pendingRemoteGpuPlaybackFrames.Count > 0)
                        {
                            frame = _pendingRemoteGpuPlaybackFrames.Dequeue();
                            if (_remoteGpuFirstSampleSubmitted || frame.IsKeyFrame)
                                break;

                            frame = null;
                            _screenShareDroppedReceiveFrames++;
                            _remoteGpuWaitingForKeyFrame = true;
                            RequestRemoteVideoKeyFrame("receiver GPU playback first sample was not an IDR");
                        }

                        var now = DateTimeOffset.UtcNow;
                        var lastReceiveAge = _remoteScreenLastReceivedAtUtc.HasValue
                            ? now - _remoteScreenLastReceivedAtUtc.Value
                            : TimeSpan.MaxValue;
                        if (!requestedRecoveryForCurrentUnderrun &&
                            _remoteGpuFirstSampleSubmitted &&
                            lastReceiveAge >= TimeSpan.FromMilliseconds(1500) &&
                            _remoteGpuConsecutiveSampleStarvations >= 2)
                        {
                            requestedRecoveryForCurrentUnderrun = true;
                            _screenShareDroppedReceiveFrames++;
                            _ = SendScreenShareQosIfNeededAsync("receiver GPU playback pacing underrun");
                            RequestRemoteVideoKeyFrame("receiver GPU playback pacing underrun");
                            if (ShouldLogFlow(ref _lastRemoteGpuPlaybackDropLogUtc, TimeSpan.FromSeconds(1)))
                            {
                                Debug.WriteLine(
                                    $"[ScreenShare:GPU:RECEIVER] Hardware playback queue empty for {lastReceiveAge.TotalMilliseconds:0}ms; holding MediaStreamSource sample request open instead of returning an empty sample.");
                            }
                        }

                        var remaining = waitUntil - DateTimeOffset.UtcNow;
                        if (remaining <= TimeSpan.Zero)
                            break;
                        Monitor.Wait(_remoteGpuPlaybackSync, remaining > TimeSpan.FromMilliseconds(100)
                            ? TimeSpan.FromMilliseconds(100)
                            : remaining);
                    }
                }

                if (frame == null)
                {
                    _remoteGpuConsecutiveSampleStarvations++;
                    var lastReceiveAge = _remoteScreenLastReceivedAtUtc.HasValue
                        ? DateTimeOffset.UtcNow - _remoteScreenLastReceivedAtUtc.Value
                        : TimeSpan.MaxValue;
                    bool playbackActive;
                    var hardStarved = lastReceiveAge > RemoteFrameReceiveStallThreshold ||
                        _remoteGpuConsecutiveSampleStarvations >= 3;
                    lock (_remoteGpuPlaybackSync)
                    {
                        playbackActive = _remoteGpuPlaybackEnabled || _remoteGpuPlaybackStarting;
                        if (playbackActive && hardStarved)
                        {
                            _remoteGpuBacklogKeyFrameRequested = true;
                        }
                    }

                    if (playbackActive)
                    {
                        _screenShareDroppedReceiveFrames++;
                        var reason = hardStarved
                            ? "receiver GPU playback hard sample starvation"
                            : "receiver GPU playback pacing underrun";
                        _ = SendScreenShareQosIfNeededAsync(reason);
                        RequestRemoteVideoKeyFrame(reason);
                    }

                    if (ShouldLogFlow(ref _lastRemoteGpuPlaybackDropLogUtc, TimeSpan.FromSeconds(1)))
                    {
                        Debug.WriteLine(
                            $"[ScreenShare:GPU:RECEIVER] Hardware player requested a sample but the realtime queue was empty; playbackActive={playbackActive}; hardStarved={hardStarved}; consecutive={_remoteGpuConsecutiveSampleStarvations}; lastReceiveAgeMs={lastReceiveAge.TotalMilliseconds:0}. Requested a keyframe while preserving the current H.264 dependency chain.");
                    }
                    return;
                }

                var sample = MediaStreamSample.CreateFromBuffer(frame.FrameData.AsBuffer(), _remoteGpuPlaybackSampleTime);
                sample.Duration = frame.SampleDuration;
                sample.KeyFrame = frame.IsKeyFrame;
                args.Request.Sample = sample;
                _remoteGpuPlaybackSampleTime += frame.SampleDuration;
                _remoteGpuFirstSampleSubmitted = true;
                _remoteGpuConsecutiveSampleStarvations = 0;
                _remoteScreenLastRenderedAtUtc = DateTimeOffset.UtcNow;

                var renderedFrames = Interlocked.Increment(ref _remoteRenderedFrameCount);
                var queueDepth = GetPendingRemoteGpuPlaybackFrameCount();
                UpdateRemotePlaybackFps(queueDepth);
                if (renderedFrames == 1 || renderedFrames % (ScreenShareCapturePipeline.TargetFps * 2) == 0)
                {
                    Debug.WriteLine($"[ScreenShare:GPU:RECEIVER] Submitted {renderedFrames} compressed H.264 samples to hardware playback; frame={frame.Width}x{frame.Height}; key={frame.IsKeyFrame}; playbackFps={_remotePlaybackObservedFps:0.0}; receiveFps={_remoteReceiveObservedFps:0.0}; queued={queueDepth}; avgQueue={_remoteGpuPlaybackAverageQueue:0.0}; peakQueue={_remoteGpuPlaybackQueuePeak}.");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ScreenShare:GPU:RECEIVER] Sample request failed: {ex}");
            }
            finally
            {
                deferral.Complete();
            }
        }

        private int GetPendingRemoteGpuPlaybackFrameCount()
        {
            lock (_remoteGpuPlaybackSync)
                return _pendingRemoteGpuPlaybackFrames.Count;
        }

        private void StopRemoteGpuPlayback(bool clearSurface, bool preservePendingFrames = false)
        {
            MediaPlayer? player;
            MediaStreamSource? source;
            lock (_remoteGpuPlaybackSync)
            {
                _remoteGpuPlaybackStopping = true;
                _remoteGpuPlaybackEnabled = false;
                _remoteGpuPlaybackStarting = false;
                if (!preservePendingFrames)
                    _pendingRemoteGpuPlaybackFrames.Clear();
                _remoteGpuPlaybackWidth = 0;
                _remoteGpuPlaybackHeight = 0;
                _remoteGpuPlaybackSampleTime = TimeSpan.Zero;
                _remoteGpuEstimatedFrameDuration = RemoteGpuFrameDuration;
                _remoteGpuLastQueuedAtUtc = null;
                _remoteGpuWaitingForKeyFrame = false;
                _remoteGpuBacklogKeyFrameRequested = false;
                _remoteGpuFirstSampleSubmitted = false;
                _remoteGpuConsecutiveSampleStarvations = 0;
                Monitor.PulseAll(_remoteGpuPlaybackSync);
            }

            player = _remoteGpuMediaPlayer;
            source = _remoteGpuMediaStreamSource;
            _remoteGpuMediaPlayer = null;
            _remoteGpuMediaStreamSource = null;

            if (source != null)
            {
                source.SampleRequested -= RemoteGpuMediaStreamSource_SampleRequested;
                source.Starting -= RemoteGpuMediaStreamSource_Starting;
            }

            try
            {
                player?.Pause();
                player?.Dispose();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ScreenShare:GPU:RECEIVER] Stop playback failed: {ex.Message}");
            }

            if (clearSurface)
            {
                RemoteScreenPlayer.SetMediaPlayer(null);
                RemoteScreenPlayer.Visibility = Visibility.Collapsed;
                RemoteScreenImage.Visibility = Visibility.Visible;
            }

            lock (_remoteGpuPlaybackSync)
            {
                _remoteGpuPlaybackStopping = false;
                Monitor.PulseAll(_remoteGpuPlaybackSync);
            }
        }

        private async Task ProcessLatestRtpFramesAsync()
        {
            try
            {
                Thread.CurrentThread.Priority = ThreadPriority.AboveNormal;
            }
            catch
            {
            }

            try
            {
                while (true)
                {
                    PendingRemoteH264Frame? pendingFrame;

                    lock (_rtpFrameSync)
                    {
                        pendingFrame = _pendingRemoteH264Frames.Count > 0
                            ? _pendingRemoteH264Frames.Dequeue()
                            : null;
                    }

                    if (pendingFrame == null ||
                        pendingFrame.FrameData.Length == 0 ||
                        pendingFrame.Width <= 0 ||
                        pendingFrame.Height <= 0)
                    {
                        break;
                    }

                    if (pendingFrame.Generation != Volatile.Read(ref _remoteDecodeGeneration))
                    {
                        Debug.WriteLine($"[ScreenShare:H264] Dropped stale compressed frame from old decoder generation. seq={pendingFrame.Sequence}; frame={pendingFrame.Width}x{pendingFrame.Height}; currentGeneration={Volatile.Read(ref _remoteDecodeGeneration)}; frameGeneration={pendingFrame.Generation}.");
                        continue;
                    }

                    try
                    {
                        byte[]? bgraFrame;
                        var decodeTimer = Stopwatch.StartNew();
                        lock (_remoteDecoderSync)
                        {
                            if (_rtpH264Decoder == null ||
                                _rtpDecoderWidth != pendingFrame.Width ||
                                _rtpDecoderHeight != pendingFrame.Height ||
                                !string.Equals(_rtpDecoderCodec, pendingFrame.Codec, StringComparison.OrdinalIgnoreCase))
                            {
                                _rtpH264Decoder?.Dispose();
                                _rtpH264Decoder = new MediaFoundationH264Decoder(pendingFrame.Width, pendingFrame.Height, pendingFrame.Codec);
                                _rtpDecoderWidth = pendingFrame.Width;
                                _rtpDecoderHeight = pendingFrame.Height;
                                _rtpDecoderCodec = pendingFrame.Codec;
                            }

                            var materializeOutput = GetPendingRemoteH264FrameCount() < RemoteDecodeRenderBacklogThreshold;
                            bgraFrame = _rtpH264Decoder.DecodeToBgra(
                                pendingFrame.FrameData,
                                pendingFrame.Width,
                                pendingFrame.Height,
                                materializeOutput);

                            if (!materializeOutput)
                            {
                                // This is an intentional presentation drop, not an
                                // empty decoder output. The sample was decoded and
                                // advanced the reference chain normally.
                                _screenShareDroppedReceiveFrames++;
                                _remoteEmptyDecodeCount = 0;
                                continue;
                            }
                        }
                        decodeTimer.Stop();
                        var queueRemaining = GetPendingRemoteH264FrameCount();

                        if (bgraFrame != null)
                        {
                            _remoteEmptyDecodeCount = 0;
                            _remoteDecodeFailureCount = 0;
                            LogRemoteDecodeFlow(pendingFrame, bgraFrame, queueRemaining, decodeTimer.Elapsed.TotalMilliseconds);
                            QueueRemoteRender(bgraFrame, pendingFrame.Width, pendingFrame.Height);
                        }
                        else
                        {
                            _remoteEmptyDecodeCount++;
                            LogRemoteDecodeFlow(pendingFrame, null, queueRemaining, decodeTimer.Elapsed.TotalMilliseconds);
                            if (!ScreenShareCodecNames.IsAv1(pendingFrame.Codec) &&
                                _remoteEmptyDecodeCount == ScreenShareCapturePipeline.TargetFps)
                            {
                                var switchedInputMode = false;
                                lock (_remoteDecoderSync)
                                {
                                    switchedInputMode = _rtpH264Decoder?.TrySwitchToLengthPrefixedInput("receiver decoder accepted input but produced no frames") == true;
                                }

                                if (switchedInputMode)
                                {
                                    lock (_rtpFrameSync)
                                    {
                                        _pendingRemoteH264Frames.Clear();
                                        _remoteH264WaitingForKeyFrame = true;
                                        _remoteH264FramesDroppedWaitingForKeyFrame = 0;
                                        _remoteDecodeGeneration++;
                                    }

                                    RequestRemoteVideoKeyFrame("receiver decoder switched H.264 input format");
                                    Debug.WriteLine("[ScreenShare:H264] Receiver decoder input format changed; waiting for a fresh IDR keyframe before resuming decode.");
                                    continue;
                                }
                            }

                            if (_remoteEmptyDecodeCount >= ScreenShareCapturePipeline.TargetFps * 5 ||
                                IsRemoteRenderStalled(DateTimeOffset.UtcNow))
                            {
                                ResetRemoteH264Decoder("receiver decoder produced no frame");
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[ScreenShare:RTP] Decode failed: {ex.Message}");
                        _remoteDecodeFailureCount++;
                        if (_remoteDecodeFailureCount >= 3)
                            ResetRemoteH264Decoder("receiver decoder failure");
                    }
                }

                await Task.CompletedTask;
            }
            finally
            {
                Interlocked.Exchange(ref _rtpDecodePumpRunning, 0);

                lock (_rtpFrameSync)
                {
                    if (_pendingRemoteH264Frames.Count > 0 &&
                        Interlocked.CompareExchange(ref _rtpDecodePumpRunning, 1, 0) == 0)
                    {
                        _ = Task.Run(ProcessLatestRtpFramesAsync);
                    }
                }
            }
        }

        private void QueueRemoteRender(byte[] bgraFrame, int width, int height)
        {
            bool replacedPendingFrame;
            lock (_remoteRenderSync)
            {
                replacedPendingFrame = _pendingRemoteRenderFrame != null;
                _pendingRemoteRenderFrame = bgraFrame;
                _pendingRemoteRenderWidth = width;
                _pendingRemoteRenderHeight = height;
                _pendingRemoteRenderQueuedAtUtc = DateTimeOffset.UtcNow;
            }

            if (replacedPendingFrame)
            {
                _remoteRenderCoalesceCount++;
                if (ShouldLogFlow(ref _lastRemoteRenderCoalescedLogUtc, TimeSpan.FromSeconds(2)))
                    Debug.WriteLine($"[ScreenShare:H264:STUTTER] Render coalesced decoded frame; count={_remoteRenderCoalesceCount}; receiveFps={_remoteReceiveObservedFps:0.0}; playbackFps={_remotePlaybackObservedFps:0.0}; pendingQueue={GetPendingRemoteH264FrameCount()}; lastQueuedAge={FormatAge(_lastRemoteQueuedAtUtc, DateTimeOffset.UtcNow)}.");
            }

            if (Interlocked.CompareExchange(ref _remoteRenderQueued, 1, 0) == 0)
                EnqueueLatestRemoteRender();
        }

        private void EnqueueLatestRemoteRender()
        {
            if (!TryEnqueueOnUi(RenderLatestRemoteFrame, Microsoft.UI.Dispatching.DispatcherQueuePriority.High))
                Interlocked.Exchange(ref _remoteRenderQueued, 0);
        }

        private void RenderLatestRemoteFrame()
        {
            byte[]? bgraFrame;
            int width;
            int height;
            DateTimeOffset queuedAtUtc;

            lock (_remoteRenderSync)
            {
                bgraFrame = _pendingRemoteRenderFrame;
                width = _pendingRemoteRenderWidth;
                height = _pendingRemoteRenderHeight;
                queuedAtUtc = _pendingRemoteRenderQueuedAtUtc;
                _pendingRemoteRenderFrame = null;
            }

            try
            {
                if (bgraFrame != null && width > 0 && height > 0)
                {
                    _screenShareLastWidth = width;
                    _screenShareLastHeight = height;
                    _screenShareLastBytes = bgraFrame.LongLength;
                    _screenShareLastQuality = GetResolutionTier(height);
                    _screenShareLastFrameAtUtc = DateTimeOffset.UtcNow;
                    _remoteScreenLastRenderedAtUtc = _screenShareLastFrameAtUtc;
                    _remoteEmptyDecodeCount = 0;
                    _remoteDecodeFailureCount = 0;
                    var renderTimer = Stopwatch.StartNew();
                    RenderBgraFrame(bgraFrame, width, height, RemoteScreenImage);
                    renderTimer.Stop();
                    var renderedFrames = Interlocked.Increment(ref _remoteRenderedFrameCount);
                    UpdateRemotePlaybackFps(0);
                    var queuedMs = queuedAtUtc == default || !_screenShareLastFrameAtUtc.HasValue
                        ? 0
                        : Math.Max(0, (_screenShareLastFrameAtUtc.Value - queuedAtUtc).TotalMilliseconds);
                    LogRemoteRenderFlow(bgraFrame, width, height, renderedFrames, renderTimer.Elapsed.TotalMilliseconds, queuedMs);
                    if (renderedFrames == 1 || renderedFrames % (ScreenShareCapturePipeline.TargetFps * 2) == 0)
                    {
                        Debug.WriteLine(
                            $"[ScreenShare:H264] Rendered {renderedFrames} remote frames: {width}x{height}; bgraBytes={bgraFrame.Length}.");
                    }

                    _isRemoteVideoVisible = true;
                    _isRemoteScreenShareLoading = false;
                    UpdateMediaLayerVisibility();
                    MediaOverlayText.Text = $"H.264 stream {width} x {height}";
                }
            }
            finally
            {
                var hasPendingFrame = false;
                lock (_remoteRenderSync)
                {
                    hasPendingFrame = _pendingRemoteRenderFrame != null;
                }

                if (hasPendingFrame)
                {
                    EnqueueLatestRemoteRender();
                }
                else
                {
                    Interlocked.Exchange(ref _remoteRenderQueued, 0);

                    lock (_remoteRenderSync)
                    {
                        hasPendingFrame = _pendingRemoteRenderFrame != null;
                    }

                    if (hasPendingFrame &&
                        Interlocked.CompareExchange(ref _remoteRenderQueued, 1, 0) == 0)
                    {
                        EnqueueLatestRemoteRender();
                    }
                }
            }
        }

        private async Task SendScreenShareQosIfNeededAsync(string reason)
        {
            var session = NativeCallCoordinator.Instance.CurrentSession;
            if (_remoteScreenShareSenderId <= 0 ||
                string.IsNullOrWhiteSpace(session.CallId) ||
                session.State != NativeCallState.Connected)
            {
                return;
            }

            var now = DateTimeOffset.UtcNow;
            // A transport restart is a recovery command, not routine receiver
            // pressure telemetry. A pacing-underrun message can be sent just before
            // RTP goes silent; throttling the following restart command leaves a
            // peer that still reports "connected" permanently stuck with no media.
            bool isRtpRestartRequest = IsReceiverRtpRestartRequest(reason);
            if (!isRtpRestartRequest && now - _lastQosSentUtc < QosFeedbackInterval)
            {
                return;
            }

            _lastQosSentUtc = now;
            var isLegacyH264 = !ScreenShareCodecNames.IsAv1(_remoteScreenShareCodec);
            var droppedForQos = isLegacyH264
                ? Math.Max(0, _screenShareDroppedReceiveFrames - _lastQosDroppedReceiveFrames)
                : _screenShareDroppedReceiveFrames;
            _lastQosDroppedReceiveFrames = _screenShareDroppedReceiveFrames;

            try
            {
                Debug.WriteLine($"[ScreenShare:QOS] Sending receiver pressure to {_remoteScreenShareSenderId}: dropped={droppedForQos}; renderQueued={Volatile.Read(ref _remoteRenderQueued)}; reason={reason}.");
                await SocialManager.Instance.Realtime.SendScreenShareQosAsync(
                    _remoteScreenShareSenderId,
                    session.CallId,
                    droppedForQos,
                    Volatile.Read(ref _remoteRenderQueued),
                    reason);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ScreenShare:QOS] Failed to send receiver pressure: {ex.Message}");
            }
        }

        private void ResetRemoteH264Decoder(string reason)
        {
            var now = DateTimeOffset.UtcNow;
            lock (_rtpFrameSync)
            {
                _remoteH264WaitingForKeyFrame = true;
                _remoteH264FramesDroppedWaitingForKeyFrame = 0;
            }

            if (now - _lastRemoteDecoderResetAtUtc < RemoteDecoderResetCooldown)
            {
                RequestRemoteVideoKeyFrame(reason);
                return;
            }

            _lastRemoteDecoderResetAtUtc = now;
            _remoteDecoderResetCount++;
            _remoteEmptyDecodeCount = 0;
            _remoteDecodeFailureCount = 0;

            Debug.WriteLine($"[ScreenShare:H264] Resetting remote decoder: {reason}");
            RequestRemoteVideoKeyFrame(reason);

            lock (_remoteDecoderSync)
            {
                try
                {
                    _rtpH264Decoder?.Dispose();
                }
                catch
                {
                }

                _rtpH264Decoder = null;
                _rtpDecoderWidth = 0;
                _rtpDecoderHeight = 0;
                _rtpDecoderCodec = ScreenShareCodecNames.H264;
            }
            lock (_rtpFrameSync)
            {
                _pendingRemoteH264Frames.Clear();
                _remoteH264WaitingForKeyFrame = true;
                _remoteH264FramesDroppedWaitingForKeyFrame = 0;
                _remoteDecodeGeneration++;
            }

            try
            {
                _h264Decoder?.Dispose();
            }
            catch
            {
            }

            _h264Decoder = null;
            _screenShareDroppedReceiveFrames++;
            _ = SendScreenShareQosIfNeededAsync(reason);
        }

        private void RequestRemoteVideoKeyFrame(string reason)
        {
            if (_remoteScreenShareSenderId <= 0)
                return;

            var now = DateTimeOffset.UtcNow;
            if (now - _lastRemoteKeyFrameRequestAtUtc < TimeSpan.FromMilliseconds(750))
                return;

            _lastRemoteKeyFrameRequestAtUtc = now;

            if (_screenSharePeers.TryGetValue(_remoteScreenShareSenderId, out var peer))
            {
                Debug.WriteLine($"[ScreenShare:FLOW:KEYFRAME] Requesting remote RTP keyframe from {_remoteScreenShareSenderId}: {reason}");
                peer.RequestVideoKeyFrame(reason);
            }
            else
            {
                Debug.WriteLine($"[ScreenShare:FLOW:KEYFRAME] Cannot request RTP keyframe from {_remoteScreenShareSenderId}: no peer. reason={reason}");
            }
        }

        private void LogLocalScreenShareFlow(NativeScreenFrameEventArgs e, int participantCount, bool sent)
        {
            var encodedFingerprint = SampleBytesFingerprint(e.FrameData);
            var previewFingerprint = SampleBytesFingerprint(e.PreviewFrameData);
            var encodedRepeat = UpdateFingerprintRepeat(
                ref _lastLocalEncodedFingerprint,
                ref _localEncodedFingerprintRepeat,
                encodedFingerprint);
            var previewRepeat = UpdateFingerprintRepeat(
                ref _lastLocalPreviewFingerprint,
                ref _localPreviewFingerprintRepeat,
                previewFingerprint);

            if (!ShouldLogFlow(ref _lastLocalFrameFlowLogUtc, TimeSpan.FromSeconds(2)) &&
                encodedRepeat != ScreenShareCapturePipeline.TargetFps * 3 &&
                previewRepeat != PreviewFallbackMaxFps * 3)
            {
                return;
            }

            var hasIdr = ContainsH264IdrFrame(e.FrameData);
            var message = $"[ScreenShare:FLOW:SEND] participants={participantCount}; sent={sent}; {e.Width}x{e.Height}; codec={e.Codec}; bytes={e.FrameData.Length}; keyFlag={e.IsKeyFrame}; idr={hasIdr}; encodedHash=0x{encodedFingerprint:X8}; sameEncoded={encodedRepeat}; previewBytes={e.PreviewFrameData.Length}; previewHash=0x{previewFingerprint:X8}; samePreview={previewRepeat}; rtpSent={_screenShareRtpFrames}; wsSent={_screenShareFallbackFrames}; previewSent={_screenSharePreviewFallbackFrames}; droppedSend={_screenShareDroppedFrames}.";
            Debug.WriteLine(message);
            DiagnosticLogService.WriteLine(message);
        }

        private void LogRemoteEncodedReceiveFlow(string transport, long fromUserId, byte[] frameData, int width, int height, bool keyFlag, bool hasIdr, long receivedFrames)
        {
            UpdateRemoteReceiveFps();

            if (keyFlag && !hasIdr)
            {
                Debug.WriteLine(
                    $"[ScreenShare:FLOW:RECV] {transport} frame from {fromUserId} was flagged as keyframe but has no IDR; it will be treated as delta. bytes={frameData.Length}; {width}x{height}.");
            }

            var fingerprint = SampleBytesFingerprint(frameData);
            var repeat = UpdateFingerprintRepeat(
                ref _lastRemoteEncodedFingerprint,
                ref _remoteEncodedFingerprintRepeat,
                fingerprint);

            if (!ShouldLogFlow(ref _lastRemoteReceiveFlowLogUtc, TimeSpan.FromSeconds(2)) &&
                repeat != ScreenShareCapturePipeline.TargetFps * 3 &&
                receivedFrames % (ScreenShareCapturePipeline.TargetFps * 5) != 0)
            {
                return;
            }

            var now = DateTimeOffset.UtcNow;
            Debug.WriteLine(
                $"[ScreenShare:FLOW:RECV] transport={transport}; from={fromUserId}; frames={receivedFrames}; {width}x{height}; receiveFps={_remoteReceiveObservedFps:0.0}; playbackFps={_remotePlaybackObservedFps:0.0}; bytes={frameData.Length}; keyFlag={keyFlag}; idr={hasIdr}; encodedHash=0x{fingerprint:X8}; sameEncoded={repeat}; queue={GetPendingRemoteH264FrameCount()}; gpuQueue={GetPendingRemoteGpuPlaybackFrameCount()}; gpuAvgQueue={_remoteGpuPlaybackAverageQueue:0.0}; gpuPeakQueue={_remoteGpuPlaybackQueuePeak}; waitingKey={_remoteH264WaitingForKeyFrame}; rendered={Volatile.Read(ref _remoteRenderedFrameCount)}; lastRenderAge={FormatAge(_remoteScreenLastRenderedAtUtc, now)}; droppedRecv={_screenShareDroppedReceiveFrames}.");
        }

        private void UpdateRemoteReceiveFps()
        {
            _remoteReceiveFramesInWindow++;
            var now = DateTimeOffset.UtcNow;
            var elapsed = now - _remoteReceiveFpsWindowStartedUtc;
            if (elapsed.TotalSeconds < 1)
                return;

            _remoteReceiveObservedFps = _remoteReceiveFramesInWindow / Math.Max(0.001, elapsed.TotalSeconds);
            _remoteReceiveFramesInWindow = 0;
            _remoteReceiveFpsWindowStartedUtc = now;
            LogRemoteRealtimeStatsIfNeeded("receive-fps-window");
        }

        private void UpdateRemotePlaybackFps(int queueDepth)
        {
            _remotePlaybackFramesInWindow++;
            _remoteGpuPlaybackQueuePeak = Math.Max(_remoteGpuPlaybackQueuePeak, queueDepth);
            _remoteGpuPlaybackQueueTotal += queueDepth;
            _remoteGpuPlaybackQueueSamples++;
            _remoteGpuPlaybackAverageQueue = _remoteGpuPlaybackQueueTotal / Math.Max(1d, _remoteGpuPlaybackQueueSamples);

            var now = DateTimeOffset.UtcNow;
            var elapsed = now - _remotePlaybackFpsWindowStartedUtc;
            if (elapsed.TotalSeconds < 1)
                return;

            _remotePlaybackObservedFps = _remotePlaybackFramesInWindow / Math.Max(0.001, elapsed.TotalSeconds);
            _remotePlaybackFramesInWindow = 0;
            _remotePlaybackFpsWindowStartedUtc = now;
            LogRemoteRealtimeStatsIfNeeded("playback-fps-window");
        }

        private void LogRemoteRealtimeStatsIfNeeded(string reason)
        {
            if (!ShouldLogFlow(ref _lastRemoteRealtimeStatsLogUtc, TimeSpan.FromSeconds(2)))
                return;

            var target = ScreenShareCapturePipeline.TargetFps;
            var receiveLocked = _remoteReceiveObservedFps >= target - 2;
            var playbackLocked = _remotePlaybackObservedFps >= target - 2;
            Debug.WriteLine(
                $"[ScreenShare:60FPS] reason={reason}; target={target}; receive={_remoteReceiveObservedFps:0.0}fps; playback={_remotePlaybackObservedFps:0.0}fps; locked={(receiveLocked && playbackLocked ? "yes" : "no")}; gpuQueue={GetPendingRemoteGpuPlaybackFrameCount()}; avgQueue={_remoteGpuPlaybackAverageQueue:0.0}; peakQueue={_remoteGpuPlaybackQueuePeak}; droppedRecv={_screenShareDroppedReceiveFrames}; lastReceive={FormatAge(_remoteScreenLastReceivedAtUtc, DateTimeOffset.UtcNow)}; lastRender={FormatAge(_remoteScreenLastRenderedAtUtc, DateTimeOffset.UtcNow)}.");
        }

        private void LogRemotePreviewFlow(ScreenFrameEventArgs e, bool rendered)
        {
            var fingerprint = SampleBytesFingerprint(e.FrameData);
            var repeat = UpdateFingerprintRepeat(
                ref _lastRemotePreviewFingerprint,
                ref _remotePreviewFingerprintRepeat,
                fingerprint);

            if (!ShouldLogFlow(ref _lastRemotePreviewFlowLogUtc, TimeSpan.FromSeconds(2)) &&
                repeat != PreviewFallbackMaxFps * 3)
            {
                return;
            }

            Debug.WriteLine(
                $"[ScreenShare:FLOW:PREVIEW] from={e.FromUserId}; rendered={rendered}; {e.Width}x{e.Height}; bytes={e.FrameData.Length}; jpegHash=0x{fingerprint:X8}; samePreview={repeat}; h264Rendered={Volatile.Read(ref _remoteRenderedFrameCount)}.");
        }

        private void LogRemoteQueueFlow(long sequence, int queueCount, bool isKeyFrame, string backlogReason)
        {
            if (!ShouldLogFlow(ref _lastRemoteQueueFlowLogUtc, TimeSpan.FromSeconds(2)))
                return;

            Debug.WriteLine(
                $"[ScreenShare:FLOW:QUEUE] seq={sequence}; queue={queueCount}; key={isKeyFrame}; waitingKey={_remoteH264WaitingForKeyFrame}; decodePump={Volatile.Read(ref _rtpDecodePumpRunning)}; reason={backlogReason}; droppedRecv={_screenShareDroppedReceiveFrames}.");
        }

        private void LogRemoteDecodeFlow(PendingRemoteH264Frame frame, byte[]? bgraFrame, int queueRemaining, double decodeMilliseconds)
        {
            var decoded = bgraFrame != null;
            var decodedFingerprint = decoded ? SampleBytesFingerprint(bgraFrame!) : 0;
            var decodedRepeat = decoded
                ? UpdateFingerprintRepeat(
                    ref _lastRemoteDecodedFingerprint,
                    ref _remoteDecodedFingerprintRepeat,
                    decodedFingerprint)
                : _remoteDecodedFingerprintRepeat;

            if (!ShouldLogFlow(ref _lastRemoteDecodeFlowLogUtc, TimeSpan.FromSeconds(2)) &&
                decoded &&
                decodedRepeat != ScreenShareCapturePipeline.TargetFps * 3)
            {
                return;
            }

            Debug.WriteLine(
                $"[ScreenShare:FLOW:DECODE] seq={frame.Sequence}; key={frame.IsKeyFrame}; {frame.Width}x{frame.Height}; inputBytes={frame.FrameData.Length}; inputHash=0x{SampleBytesFingerprint(frame.FrameData):X8}; decoded={decoded}; decodedBytes={(bgraFrame?.Length ?? 0)}; decodedHash=0x{decodedFingerprint:X8}; sameDecoded={decodedRepeat}; decodeMs={decodeMilliseconds:0.0}; empty={_remoteEmptyDecodeCount}; failures={_remoteDecodeFailureCount}; queueRemaining={queueRemaining}; renderQueued={Volatile.Read(ref _remoteRenderQueued)}.");
        }

        private void LogRemoteRenderFlow(byte[] bgraFrame, int width, int height, long renderedFrames, double renderMilliseconds, double queuedMilliseconds)
        {
            var fingerprint = SampleBytesFingerprint(bgraFrame);
            var repeat = UpdateFingerprintRepeat(
                ref _lastRemoteRenderedFingerprint,
                ref _remoteRenderedFingerprintRepeat,
                fingerprint);

            if (!ShouldLogFlow(ref _lastRemoteRenderFlowLogUtc, TimeSpan.FromSeconds(2)) &&
                renderedFrames != 1 &&
                repeat != ScreenShareCapturePipeline.TargetFps * 3)
            {
                return;
            }

            var now = DateTimeOffset.UtcNow;
            Debug.WriteLine(
                $"[ScreenShare:FLOW:RENDER] rendered={renderedFrames}; {width}x{height}; bgraBytes={bgraFrame.Length}; renderHash=0x{fingerprint:X8}; sameRendered={repeat}; renderMs={renderMilliseconds:0.0}; queuedMs={queuedMilliseconds:0.0}; receiveAge={FormatAge(_remoteScreenLastReceivedAtUtc, now)}; droppedRecv={_screenShareDroppedReceiveFrames}.");
        }

        private int GetPendingRemoteH264FrameCount()
        {
            lock (_rtpFrameSync)
            {
                return _pendingRemoteH264Frames.Count;
            }
        }

        private static bool ShouldLogFlow(ref DateTimeOffset lastLoggedUtc, TimeSpan interval)
        {
            var now = DateTimeOffset.UtcNow;
            if (now - lastLoggedUtc < interval)
                return false;

            lastLoggedUtc = now;
            return true;
        }

        private static int UpdateFingerprintRepeat(ref uint lastFingerprint, ref int repeatCount, uint fingerprint)
        {
            if (lastFingerprint == fingerprint)
            {
                repeatCount++;
            }
            else
            {
                lastFingerprint = fingerprint;
                repeatCount = 1;
            }

            return repeatCount;
        }

        private static string FormatAge(DateTimeOffset? timestampUtc, DateTimeOffset nowUtc)
        {
            if (!timestampUtc.HasValue)
                return "never";

            return $"{Math.Max(0, (nowUtc - timestampUtc.Value).TotalMilliseconds):0}ms";
        }

        private static uint SampleBytesFingerprint(byte[] data)
        {
            unchecked
            {
                const uint offsetBasis = 2166136261;
                const uint prime = 16777619;
                var hash = offsetBasis;
                hash = (hash ^ (uint)data.Length) * prime;

                if (data.Length == 0)
                    return hash;

                var step = Math.Max(1, data.Length / 1024);
                for (var i = 0; i < data.Length; i += step)
                    hash = (hash ^ data[i]) * prime;

                hash = (hash ^ data[^1]) * prime;
                return hash;
            }
        }

        private static bool ContainsH264IdrFrame(byte[] frameData)
        {
            if (frameData.Length == 0)
                return false;

            if ((frameData[0] & 0x1F) == 5)
                return true;

            if (frameData.Length < 5)
                return false;

            var offset = 0;
            while (offset + 4 <= frameData.Length)
            {
                var startCodeLength = GetAnnexBStartCodeLength(frameData, offset);
                if (startCodeLength > 0)
                {
                    var nalStart = offset + startCodeLength;
                    if (nalStart < frameData.Length && (frameData[nalStart] & 0x1F) == 5)
                        return true;

                    offset = nalStart + 1;
                    continue;
                }

                offset++;
            }

            offset = 0;
            while (offset + 4 <= frameData.Length)
            {
                var nalLength =
                    (frameData[offset] << 24) |
                    (frameData[offset + 1] << 16) |
                    (frameData[offset + 2] << 8) |
                    frameData[offset + 3];

                offset += 4;
                if (nalLength <= 0 || nalLength > frameData.Length - offset)
                    return false;

                if ((frameData[offset] & 0x1F) == 5)
                    return true;

                offset += nalLength;
            }

            return false;
        }

        private static bool TryReadH264Dimensions(byte[] frameData, out int width, out int height)
        {
            width = 0;
            height = 0;

            foreach (var nal in EnumerateH264NalUnits(frameData))
            {
                if (nal.Count < 2 || (nal.Array![nal.Offset] & 0x1F) != 7)
                    continue;

                return TryReadSpsDimensions(nal.Array, nal.Offset + 1, nal.Count - 1, out width, out height);
            }

            return false;
        }

        private static bool IsPlausibleRemoteBitstreamResolution(int width, int height, int expectedWidth, int expectedHeight)
        {
            if (width < 320 || height < 180 || width > 8192 || height > 8192)
                return false;

            if ((width & 1) != 0 || (height & 1) != 0)
                return false;

            var aspect = (double)width / height;
            if (aspect < 1.2 || aspect > 2.4)
                return false;

            if (expectedWidth <= 0 || expectedHeight <= 0)
                return true;

            var expectedAspect = (double)expectedWidth / expectedHeight;
            return Math.Abs(aspect - expectedAspect) <= 0.35;
        }

        private static IEnumerable<ArraySegment<byte>> EnumerateH264NalUnits(byte[] frameData)
        {
            var offset = 0;
            var foundStartCode = false;

            while (offset + 3 < frameData.Length)
            {
                var startCodeLength = GetAnnexBStartCodeLength(frameData, offset);
                if (startCodeLength == 0)
                {
                    offset++;
                    continue;
                }

                foundStartCode = true;
                var nalStart = offset + startCodeLength;
                var nextStart = nalStart;
                while (nextStart + 3 < frameData.Length && GetAnnexBStartCodeLength(frameData, nextStart) == 0)
                    nextStart++;

                var nalLength = nextStart - nalStart;
                if (nalLength > 0)
                    yield return new ArraySegment<byte>(frameData, nalStart, nalLength);

                offset = nextStart;
            }

            if (foundStartCode)
                yield break;

            offset = 0;
            while (offset + 4 <= frameData.Length)
            {
                var nalLength =
                    (frameData[offset] << 24) |
                    (frameData[offset + 1] << 16) |
                    (frameData[offset + 2] << 8) |
                    frameData[offset + 3];

                offset += 4;
                if (nalLength <= 0 || nalLength > frameData.Length - offset)
                    yield break;

                yield return new ArraySegment<byte>(frameData, offset, nalLength);
                offset += nalLength;
            }
        }

        private static bool TryReadSpsDimensions(byte[] data, int offset, int length, out int width, out int height)
        {
            width = 0;
            height = 0;

            try
            {
                var rbsp = RemoveH264EmulationPreventionBytes(data, offset, length);
                var reader = new BitReader(rbsp);

                var profileIdc = reader.ReadBits(8);
                reader.ReadBits(8);
                reader.ReadBits(8);
                reader.ReadUnsignedExpGolomb();

                var chromaFormatIdc = 1u;
                if (profileIdc is 100 or 110 or 122 or 244 or 44 or 83 or 86 or 118 or 128 or 138 or 139 or 134 or 135)
                {
                    chromaFormatIdc = reader.ReadUnsignedExpGolomb();
                    if (chromaFormatIdc == 3)
                        reader.ReadBit();

                    reader.ReadUnsignedExpGolomb();
                    reader.ReadUnsignedExpGolomb();
                    reader.ReadBit();
                    if (reader.ReadBit())
                    {
                        var scalingListCount = chromaFormatIdc != 3 ? 8 : 12;
                        for (var i = 0; i < scalingListCount; i++)
                        {
                            if (reader.ReadBit())
                                SkipH264ScalingList(reader, i < 6 ? 16 : 64);
                        }
                    }
                }

                reader.ReadUnsignedExpGolomb();
                var picOrderCntType = reader.ReadUnsignedExpGolomb();
                if (picOrderCntType == 0)
                {
                    reader.ReadUnsignedExpGolomb();
                }
                else if (picOrderCntType == 1)
                {
                    reader.ReadBit();
                    reader.ReadSignedExpGolomb();
                    reader.ReadSignedExpGolomb();
                    var cycleCount = reader.ReadUnsignedExpGolomb();
                    for (var i = 0; i < cycleCount; i++)
                        reader.ReadSignedExpGolomb();
                }

                reader.ReadUnsignedExpGolomb();
                reader.ReadBit();
                var picWidthInMbsMinus1 = reader.ReadUnsignedExpGolomb();
                var picHeightInMapUnitsMinus1 = reader.ReadUnsignedExpGolomb();
                var frameMbsOnlyFlag = reader.ReadBit();
                if (!frameMbsOnlyFlag)
                    reader.ReadBit();

                reader.ReadBit();

                var cropLeft = 0u;
                var cropRight = 0u;
                var cropTop = 0u;
                var cropBottom = 0u;
                if (reader.ReadBit())
                {
                    cropLeft = reader.ReadUnsignedExpGolomb();
                    cropRight = reader.ReadUnsignedExpGolomb();
                    cropTop = reader.ReadUnsignedExpGolomb();
                    cropBottom = reader.ReadUnsignedExpGolomb();
                }

                var cropUnitX = chromaFormatIdc == 0 ? 1u : chromaFormatIdc == 3 ? 1u : 2u;
                var cropUnitY = chromaFormatIdc == 0
                    ? (frameMbsOnlyFlag ? 1u : 2u)
                    : (chromaFormatIdc == 1 ? 2u : 1u) * (frameMbsOnlyFlag ? 1u : 2u);

                var computedWidth = ((picWidthInMbsMinus1 + 1) * 16) - ((cropLeft + cropRight) * cropUnitX);
                var computedHeight = ((2 - (frameMbsOnlyFlag ? 1u : 0u)) * (picHeightInMapUnitsMinus1 + 1) * 16) -
                                     ((cropTop + cropBottom) * cropUnitY);

                if (computedWidth <= 0 || computedHeight <= 0 || computedWidth > 8192 || computedHeight > 8192)
                    return false;

                width = (int)computedWidth;
                height = (int)computedHeight;
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static byte[] RemoveH264EmulationPreventionBytes(byte[] data, int offset, int length)
        {
            var output = new List<byte>(length);
            var zeros = 0;
            for (var i = offset; i < offset + length; i++)
            {
                var value = data[i];
                if (zeros >= 2 && value == 0x03)
                {
                    zeros = 0;
                    continue;
                }

                output.Add(value);
                zeros = value == 0 ? zeros + 1 : 0;
            }

            return output.ToArray();
        }

        private static void SkipH264ScalingList(BitReader reader, int size)
        {
            var lastScale = 8;
            var nextScale = 8;
            for (var j = 0; j < size; j++)
            {
                if (nextScale != 0)
                {
                    var deltaScale = reader.ReadSignedExpGolomb();
                    nextScale = (lastScale + deltaScale + 256) % 256;
                }

                lastScale = nextScale == 0 ? lastScale : nextScale;
            }
        }

        private static int GetAnnexBStartCodeLength(byte[] frameData, int offset)
        {
            if (offset + 3 <= frameData.Length &&
                frameData[offset] == 0 &&
                frameData[offset + 1] == 0 &&
                frameData[offset + 2] == 1)
            {
                return 3;
            }

            if (offset + 4 <= frameData.Length &&
                frameData[offset] == 0 &&
                frameData[offset + 1] == 0 &&
                frameData[offset + 2] == 0 &&
                frameData[offset + 3] == 1)
            {
                return 4;
            }

            return 0;
        }

        private void WatchRemoteScreenShareHealth()
        {
            var session = NativeCallCoordinator.Instance.CurrentSession;
            if (_remoteScreenShareSenderId <= 0 ||
                session.State != NativeCallState.Connected)
            {
                return;
            }

            var now = DateTimeOffset.UtcNow;
            // WebSocket fallback frames prove that the sender is alive, but they
            // must not hide a failed/missed WebRTC negotiation. They update the
            // normal receive/render timestamps, so the generic stall checks below
            // never fire when a peer is stuck in "new" after the receiver rejoins.
            // Explicitly request a fresh offer when fallback has been active for a
            // short grace period and RTP has still delivered no video frames.
            if (Volatile.Read(ref _remoteRtpFrameCount) == 0 &&
                _remoteFallbackFirstReceivedAtUtc != DateTimeOffset.MinValue &&
                now - _remoteFallbackFirstReceivedAtUtc > TimeSpan.FromSeconds(3) &&
                now - _lastRemoteReceiveStallRecoveryUtc > TimeSpan.FromSeconds(4))
            {
                const string reason = "receiver RTP stalled; restart screen-share offer";
                _lastRemoteReceiveStallRecoveryUtc = now;
                Debug.WriteLine(
                    $"[ScreenShare:HEALTH] WebSocket fallback is active but RTP has delivered no frames for {(now - _remoteFallbackFirstReceivedAtUtc).TotalMilliseconds:0}ms; requesting a fresh offer.");
                _ = SendScreenShareQosIfNeededAsync(reason);
            }

            if (!_remoteScreenLastRenderedAtUtc.HasValue)
            {
                ShowRemoteScreenShareLoading("Screen share is loading", "Waiting for the first live frame...");
                UpdateMediaLayerVisibility();
                _ = SendScreenShareQosIfNeededAsync("receiver waiting for keyframe before first visible frame");
                RequestRemoteVideoKeyFrame("receiver waiting for first visible frame");
                return;
            }

            if (_remoteGpuPlaybackEnabled || _remoteGpuPlaybackStarting)
            {
                if (_remoteScreenLastReceivedAtUtc.HasValue &&
                    now - _remoteScreenLastReceivedAtUtc.Value > RemoteFrameReceiveStallThreshold &&
                now - _lastRemoteReceiveStallRecoveryUtc > TimeSpan.FromSeconds(2) &&
                Interlocked.CompareExchange(ref _remoteH264RecoveryInFlight, 1, 0) == 0)
                {
                    var receiveStallAge = now - _remoteScreenLastReceivedAtUtc.Value;
                    // The hardware player cannot repair a complete RTP input stall
                    // with a PLI. The peer may remain nominally "connected", so ask
                    // the sender to rebuild its offer on the first confirmed stall.
                    const string reason = "receiver RTP stalled; restart screen-share offer";

                    _lastRemoteReceiveStallRecoveryUtc = now;
                    _screenShareDroppedReceiveFrames++;
                    Debug.WriteLine($"[ScreenShare:HEALTH] Receiver has not received RTP video for {receiveStallAge.TotalMilliseconds:0}ms; reason={reason}.");
                    _ = SendScreenShareQosIfNeededAsync(reason);
                    RequestRemoteVideoKeyFrame(reason);
                    ShowRemoteScreenShareLoading("Screen share reconnecting", "The stream paused while waiting for new frames...");
                    UpdateMediaLayerVisibility();
                    MediaOverlayText.Text = "Screen share reconnecting...";
                }

                return;
            }

            if (_remoteScreenLastReceivedAtUtc.HasValue &&
                now - _remoteScreenLastReceivedAtUtc.Value > RemoteFrameReceiveStallThreshold &&
                now - _lastRemoteReceiveStallRecoveryUtc > TimeSpan.FromSeconds(2) &&
                Interlocked.CompareExchange(ref _remoteH264RecoveryInFlight, 1, 0) == 0)
            {
                var receiveStallAge = now - _remoteScreenLastReceivedAtUtc.Value;
                var reason = receiveStallAge > TimeSpan.FromSeconds(4)
                    ? "receiver RTP stalled; restart screen-share offer"
                    : "receiver no RTP frames";

                _lastRemoteReceiveStallRecoveryUtc = now;
                _screenShareDroppedReceiveFrames++;
                ResetRemoteH264Decoder(reason);
                ShowRemoteScreenShareLoading("Screen share reconnecting", "The stream paused while waiting for new frames...");
                UpdateMediaLayerVisibility();
                MediaOverlayText.Text = "Screen share reconnecting...";
                return;
            }

            if (IsRemoteRenderStalled(now))
            {
                _screenShareDroppedReceiveFrames++;
                ResetRemoteH264Decoder("receiver render stalled");
                ShowRemoteScreenShareLoading("Screen share recovering", "The stream is catching up...");
                UpdateMediaLayerVisibility();
                MediaOverlayText.Text = "Screen share recovering...";
            }
        }

        private bool IsRemoteRenderStalled(DateTimeOffset now)
        {
            return _remoteScreenLastReceivedAtUtc.HasValue &&
                _remoteScreenLastRenderedAtUtc.HasValue &&
                _remoteScreenLastReceivedAtUtc.Value > _remoteScreenLastRenderedAtUtc.Value &&
                now - _remoteScreenLastRenderedAtUtc.Value > RemoteRenderStallThreshold;
        }

        private async Task CloseScreenSharePeersAsync()
        {
            var peers = _screenSharePeers.Values.ToList();
            _screenSharePeers.Clear();

            foreach (var peer in peers)
                await peer.CloseAsync();
        }

        private static bool IsCurrentCallSignal(NativeCallSession session, string callId)
        {
            return !string.IsNullOrWhiteSpace(callId) &&
                string.Equals(session.CallId, callId, StringComparison.Ordinal);
        }

        private async Task RenderScreenFrameAsync(byte[] frameData, Image target)
        {
            using var stream = new InMemoryRandomAccessStream();
            var writer = new DataWriter(stream.GetOutputStreamAt(0));
            try
            {
                writer.WriteBytes(frameData);
                await writer.StoreAsync();
                await writer.FlushAsync();
                writer.DetachStream();
            }
            finally
            {
                writer.Dispose();
            }

            stream.Seek(0);

            var bitmap = new BitmapImage();
            await bitmap.SetSourceAsync(stream);
            target.Source = bitmap;
        }

        private void RenderBgraFrame(byte[] frameData, int width, int height, Image target)
        {
            if (frameData.Length < width * height * 4)
            {
                Debug.WriteLine($"[ScreenShare:FLOW:RENDER] Skipped BGRA render: bytes={frameData.Length}; expected={width * height * 4}; {width}x{height}.");
                return;
            }

            if (_remoteScreenBitmap == null ||
                _remoteScreenBitmapWidth != width ||
                _remoteScreenBitmapHeight != height)
            {
                _remoteScreenBitmap = new WriteableBitmap(width, height);
                _remoteScreenBitmapWidth = width;
                _remoteScreenBitmapHeight = height;
                Debug.WriteLine($"[ScreenShare:FLOW:RENDER] Created remote WriteableBitmap {width}x{height}.");
            }

            // A late JPEG preview can finish decoding after RTP rendering starts
            // and replace Image.Source. Reclaim the visible surface before every
            // decoded frame so updates are never written to an off-screen bitmap.
            if (!ReferenceEquals(target.Source, _remoteScreenBitmap))
                target.Source = _remoteScreenBitmap;

            using var pixelStream = _remoteScreenBitmap.PixelBuffer.AsStream();
            pixelStream.Seek(0, System.IO.SeekOrigin.Begin);
            pixelStream.Write(frameData, 0, width * height * 4);
            _remoteScreenBitmap.Invalidate();
        }

        private void AudioActivityService_ActivityChanged(object? sender, AudioActivityState e)
        {
            TryEnqueueOnUi(() =>
            {
                UpdateSpeakingIndicators(NativeCallCoordinator.Instance.CurrentSession, e);
            });
        }

        private void EnsureTimerCreated()
        {
            if (_callTimer != null)
                return;

            _callTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(1)
            };
            _callTimer.Tick += CallTimer_Tick;
        }

        private void CallTimer_Tick(object? sender, object e)
        {
            UpdateCallTimerText();
            WatchRemoteScreenShareHealth();
        }

        private void StartCallTimerIfNeeded()
        {
            EnsureTimerCreated();

            if (_connectedAtUtc == null)
                _connectedAtUtc = DateTimeOffset.UtcNow;

            if (_callTimer != null && !_callTimer.IsEnabled)
                _callTimer.Start();

            UpdateCallTimerText();
        }

        private void StopCallTimer()
        {
            if (_callTimer != null && _callTimer.IsEnabled)
                _callTimer.Stop();
        }

        private void ResetCallTimer()
        {
            StopCallTimer();
            _connectedAtUtc = null;
            UpdateCallTimerText();
        }

        private void UpdateCallTimerText()
        {
            if (_connectedAtUtc == null)
            {
                CallTimerText.Text = "Duration: 00:00";
                return;
            }

            var elapsed = DateTimeOffset.UtcNow - _connectedAtUtc.Value;
            var totalHours = (int)elapsed.TotalHours;

            CallTimerText.Text = totalHours > 0
                ? $"Duration: {totalHours:00}:{elapsed.Minutes:00}:{elapsed.Seconds:00}"
                : $"Duration: {elapsed.Minutes:00}:{elapsed.Seconds:00}";
        }

        private void NativeCallCoordinator_SessionChanged(object? sender, NativeCallSession e)
        {
            TryEnqueueOnUi(() =>
            {
                SyncPageStateFromSession(e);
                ApplySessionToUi(e);
            });
        }

        private void NativeSignalingBridge_IncomingCallReceived(object? sender, IncomingCallEventArgs e)
        {
            IncomingCallRingtoneService.TryStart();
            TryEnqueueOnUi(() =>
            {
                if (NativeCallCoordinator.Instance.CurrentSession.State == NativeCallState.Incoming ||
                    string.IsNullOrWhiteSpace(NativeCallCoordinator.Instance.CurrentSession.CallId))
                {
                    NativeCallCoordinator.Instance.SetIncoming(
                        e.CallId,
                        e.FromUserId,
                        string.IsNullOrWhiteSpace(e.FromDisplayName) ? e.FromUsername : e.FromDisplayName,
                        false);
                }

                AddCallParticipant(e.FromUserId);
                RememberParticipantName(e.FromUserId, e.FromDisplayName, e.FromUsername);

                SyncPageStateFromSession(NativeCallCoordinator.Instance.CurrentSession);
                ApplySessionToUi(NativeCallCoordinator.Instance.CurrentSession);
            });
        }

        private void NativeSignalingBridge_CallAnsweredReceived(object? sender, CallSignalEventArgs e)
        {
            IncomingCallRingtoneService.TryStop();
            TryEnqueueOnUi(async () =>
            {
                var session = NativeCallCoordinator.Instance.CurrentSession;
                RememberParticipantName(e.FromUserId, e.FromDisplayName, e.FromUsername);

                if (!string.IsNullOrWhiteSpace(session.CallId) && session.CallId == e.CallId)
                {
                    session.RemoteUserId = e.FromUserId;
                    if (session.TargetUserId <= 0)
                        session.TargetUserId = e.FromUserId;
                }

                AddCallParticipant(e.FromUserId);

                SyncPageStateFromSession(session);
                ApplySessionToUi(session);

                if (_isSharingScreen && session.State == NativeCallState.Connected)
                {
                    try
                    {
                        await StartLocalScreenShareAsync();
                    }
                    catch (Exception ex)
                    {
                        _isSharingScreen = false;
                        _isScreenShare = false;
                        session.IsScreenShare = false;
                        NativeCallCoordinator.Instance.SetStatus(session.State, $"Screen share failed: {ex.Message}", session.PeerText);
                        UpdateDockVisualStates();
                        ApplySessionToUi(session);
                    }
                }
            });
        }

        private void NativeSignalingBridge_CallRejectedReceived(object? sender, CallSignalEventArgs e)
        {
            IncomingCallRingtoneService.TryStop();
            TryEnqueueOnUi(() =>
            {
                RememberParticipantName(e.FromUserId, e.FromDisplayName, e.FromUsername);
                SyncPageStateFromSession(NativeCallCoordinator.Instance.CurrentSession);
                ApplySessionToUi(NativeCallCoordinator.Instance.CurrentSession);
            });
        }

        private void NativeSignalingBridge_CallEndedReceived(object? sender, CallSignalEventArgs e)
        {
            IncomingCallRingtoneService.TryStop();
            TryEnqueueOnUi(() =>
            {
                RememberParticipantName(e.FromUserId, e.FromDisplayName, e.FromUsername);
                _leftParticipantIds.Add(e.FromUserId);
                _callParticipantIds.Remove(e.FromUserId);
                var leaveText = GetLeaveStatusText(e.FromUserId, e.Reason);

                var session = NativeCallCoordinator.Instance.CurrentSession;
                if (string.Equals(session.CallId, e.CallId, StringComparison.Ordinal))
                {
                    var wasRemoteScreenSharing = _remoteScreenShareSenderId == e.FromUserId || _isRemoteVideoVisible || _isRemoteScreenShareLoading;
                    var screenShareEndedText = wasRemoteScreenSharing
                        ? GetScreenShareEndedStatusText(e.FromUserId, e.Reason)
                        : leaveText;
                    _isRemoteVideoVisible = false;
                    _isRemoteScreenShareLoading = false;
                    _isWatchingRemoteScreenShare = false;
                    _remoteScreenShareSenderId = 0;
                    lock (_screenShareViewersSync)
                        _screenShareViewerIds.Remove(e.FromUserId);
                    ResetRemoteScreenShareReceiveState(clearImage: true);
                    RemotePlaceholderTitleText.Text = screenShareEndedText;
                    RemotePlaceholderSubtitleText.Text = leaveText;
                    MediaOverlayText.Text = screenShareEndedText;
                    NativeCallCoordinator.Instance.SetStatus(
                        NativeCallState.Ended,
                        leaveText,
                        leaveText);
                }

                SyncPageStateFromSession(session);
                ApplySessionToUi(session);
                _ = PromptForCallExperienceOnceAsync("remote-ended");
            });
        }

        private async void EndButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var session = NativeCallCoordinator.Instance.CurrentSession;
                if (!string.IsNullOrWhiteSpace(session.CallId))
                {
                    foreach (var participantId in GetCallParticipants(session))
                    {
                        if (participantId != session.RemoteUserId)
                            await SocialManager.Instance.Realtime.EndCallAsync(participantId, session.CallId, "left-call");
                    }
                }

                await NativeCallCoordinator.Instance.EndAsync("left-call");
                await StopLocalScreenShareAsync(false);
                AudioActivityService.Instance.Reset();
                LocalPreviewImage.Source = null;
                LocalPreviewPlaceholder.Visibility = Visibility.Visible;
                _isLocalPreviewHidden = false;
                ApplyLocalPreviewVisibility();
                await PromptForCallExperienceOnceAsync("local-ended");
            }
            catch (Exception ex)
            {
                NativeCallCoordinator.Instance.SetStatus(NativeCallState.Failed, ex.Message);
            }
        }

        private void SoundDeviceButton_Click(object sender, RoutedEventArgs e)
        {
            var flyout = new MenuFlyout();

            var outputHeader = new MenuFlyoutSubItem
            {
                Text = $"Speaker: {_selectedAudioOutput}"
            };

            foreach (var device in AudioPlaybackService.Instance.GetOutputDevices())
            {
                AddAudioOutputMenuItem(outputHeader, device);
            }

            flyout.Items.Add(outputHeader);

            var microphoneHeader = new MenuFlyoutSubItem
            {
                Text = $"Microphone: {_selectedMicrophone}"
            };

            foreach (var device in MicCaptureService.Instance.GetInputDevices())
            {
                AddMicrophoneMenuItem(microphoneHeader, device);
            }

            flyout.Items.Add(microphoneHeader);
            flyout.ShowAt(SoundDeviceButton);
        }

        private void AddAudioOutputMenuItem(MenuFlyoutSubItem parent, AudioPlaybackService.OutputDeviceInfo device)
        {
            var item = new ToggleMenuFlyoutItem
            {
                Text = device.Name,
                IsChecked = device.DeviceNumber == AudioPlaybackService.Instance.SelectedOutputDeviceNumber
            };

            item.Click += (_, __) =>
            {
                if (AudioPlaybackService.Instance.SetOutputDevice(device.DeviceNumber))
                {
                    _selectedAudioOutput = device.Name;

                    NativeCallCoordinator.Instance.SetStatus(
                        NativeCallCoordinator.Instance.CurrentSession.State,
                        $"Audio output set to {_selectedAudioOutput}.");
                }
            };

            parent.Items.Add(item);
        }

        private bool TryEnqueueOnUi(Action action)
        {
            return TryEnqueueOnUi(action, Microsoft.UI.Dispatching.DispatcherQueuePriority.Normal);
        }

        private bool TryEnqueueOnUi(Action action, Microsoft.UI.Dispatching.DispatcherQueuePriority priority)
        {
            var queue = DispatcherQueue;
            if (queue == null)
            {
                Debug.WriteLine("[CallPage] DispatcherQueue is unavailable; skipping queued UI work.");
                return false;
            }

            return queue.TryEnqueue(priority, () => action());
        }

        private bool TryEnqueueOnUi(Func<Task> action)
        {
            var queue = DispatcherQueue;
            if (queue == null)
            {
                Debug.WriteLine("[CallPage] DispatcherQueue is unavailable; skipping queued UI work.");
                return false;
            }

            return queue.TryEnqueue(async () =>
            {
                try
                {
                    await action();
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[CallPage] Queued UI work failed: {ex}");
                }
            });
        }

        private void DeafenMenuButton_Click(object sender, RoutedEventArgs e)
        {
            var flyout = new MenuFlyout();
            var muteItem = new ToggleMenuFlyoutItem
            {
                Text = "Mute microphone",
                IsChecked = _isMuted
            };
            muteItem.Click += (_, __) => SetMicrophoneMuted(!_isMuted);
            flyout.Items.Add(muteItem);

            var deafenItem = new ToggleMenuFlyoutItem
            {
                Text = "Deafen sound",
                IsChecked = _isDeafened
            };
            deafenItem.Click += (_, __) => SetDeafened(!_isDeafened);
            flyout.Items.Add(deafenItem);
            flyout.ShowAt(DeafenToggleButton);
        }

        private void SetMicrophoneMuted(bool muted)
        {
            _isMuted = muted;
            _isMicEnabled = !muted;

            if (_isMuted)
                AudioActivityService.Instance.UpdateLocalLevel(0);

            NativeCallCoordinator.Instance.SetLocalAudioMuted(_isMuted || !_isMicEnabled);
            NativeCallCoordinator.Instance.SetStatus(
                NativeCallCoordinator.Instance.CurrentSession.State,
                _isMuted ? "Microphone muted." : "Microphone unmuted.");

            UpdateDockVisualStates();
            UpdateSpeakingIndicators(NativeCallCoordinator.Instance.CurrentSession, AudioActivityService.Instance.Current);
        }

        private void SetDeafened(bool deafened)
        {
            _isDeafened = deafened;

            if (_isDeafened)
                AudioActivityService.Instance.UpdateRemoteLevel(0);

            NativeCallCoordinator.Instance.SetRemoteAudioDeafened(_isDeafened);

            NativeCallCoordinator.Instance.SetStatus(
                NativeCallCoordinator.Instance.CurrentSession.State,
                _isDeafened ? "Incoming audio deafened." : "Incoming audio restored.");

            UpdateDockVisualStates();
            UpdateSpeakingIndicators(NativeCallCoordinator.Instance.CurrentSession, AudioActivityService.Instance.Current);
        }

        private async void ScreenShareToggleButton_Click(object sender, RoutedEventArgs e)
        {
            DiagnosticLogService.WriteLine(
                $"[ScreenShare:UI] Toggle clicked; state={NativeCallCoordinator.Instance.CurrentSession.State}; currentSharing={_isSharingScreen}; nativeRunning={ScreenShareCapturePipeline.Instance.IsRunning}");
            DiagnosticLogService.Flush();

            if (NativeCallCoordinator.Instance.CurrentSession.State != NativeCallState.Connected)
            {
                NativeCallCoordinator.Instance.SetStatus(
                    NativeCallCoordinator.Instance.CurrentSession.State,
                    "Start or accept the call before sharing your screen.");
                return;
            }

            _isSharingScreen = !_isSharingScreen;
            _isScreenShare = _isSharingScreen;
            NativeCallCoordinator.Instance.CurrentSession.IsScreenShare = _isSharingScreen;

            ModeText.Text = _isSharingScreen ? "Mode: 4K Screen Share + Voice" : "Mode: Voice Call";

            NativeCallCoordinator.Instance.SetStatus(
                NativeCallCoordinator.Instance.CurrentSession.State,
                _isSharingScreen ? "Screen share enabled." : "Screen share disabled.");

            UpdateDockVisualStates();

            if (NativeCallCoordinator.Instance.CurrentSession.State == NativeCallState.Connected)
            {
                try
                {
                    if (_isSharingScreen)
                    {
                        await StartLocalScreenShareAsync();
                    }
                    else
                    {
                        await StopLocalScreenShareAsync(true, promptForFeedback: true);
                        LocalPreviewImage.Source = null;
                        LocalPreviewPlaceholder.Visibility = Visibility.Visible;
                    }
                }
                catch (Exception ex)
                {
                    DiagnosticLogService.WriteLine("[ScreenShare:UI] Toggle failed: " + ex);
                    DiagnosticLogService.Flush();

                    _isSharingScreen = !_isSharingScreen;
                    _isScreenShare = _isSharingScreen;
                    NativeCallCoordinator.Instance.CurrentSession.IsScreenShare = _isSharingScreen;

                    NativeCallCoordinator.Instance.SetStatus(
                        NativeCallCoordinator.Instance.CurrentSession.State,
                        $"Screen share failed: {ex.Message}");

                    UpdateDockVisualStates();
                    ApplySessionToUi(NativeCallCoordinator.Instance.CurrentSession);
                }
            }
        }

        private void FullscreenButton_Click(object sender, RoutedEventArgs e)
        {
            SetScreenShareFullscreen(!_isFullscreen);
        }

        private void CallPage_KeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (_isFullscreen && e.Key == VirtualKey.Escape)
            {
                e.Handled = true;
                ExitScreenShareFullscreenNow("escape");
            }
        }

        private void ExitScreenShareFullscreenNow(string reason)
        {
            Debug.WriteLine($"[ScreenShare:UI] Exiting fullscreen via {reason}.");
            DiagnosticLogService.WriteLine($"[ScreenShare:UI] Exiting fullscreen via {reason}.");
            SetScreenShareFullscreen(false);
            App.MainWindow?.ExitFullscreenMode();
        }

        public void ExitScreenShareFullscreenFromWindowHook()
        {
            if (!_isFullscreen)
                return;

            Debug.WriteLine("[ScreenShare:UI] Exiting fullscreen via window mouse hook.");
            DiagnosticLogService.WriteLine("[ScreenShare:UI] Exiting fullscreen via window mouse hook.");
            SetScreenShareFullscreen(false, updateWindow: false);
        }

        private void SetScreenShareFullscreen(bool fullscreen)
        {
            SetScreenShareFullscreen(fullscreen, updateWindow: true);
        }

        private void SetScreenShareFullscreen(bool fullscreen, bool updateWindow)
        {
            _fullscreenChromeTimer?.Stop();
            _isFullscreen = fullscreen;
            ApplyScreenShareFocusMode(updateWindow);
            UpdateDockVisualStates();
        }

        private void CallMediaStage_PointerMoved(object sender, PointerRoutedEventArgs e)
        {
            if (_isFullscreen)
                ShowFullscreenChrome();
        }

        private void EnsureFullscreenChromeTimer()
        {
            if (_fullscreenChromeTimer != null)
                return;

            _fullscreenChromeTimer = new DispatcherTimer
            {
                Interval = FullscreenChromeHideDelay
            };

            _fullscreenChromeTimer.Tick += FullscreenChromeTimer_Tick;
        }

        private void FullscreenChromeTimer_Tick(object? sender, object e)
        {
            _fullscreenChromeTimer?.Stop();

            if (_isFullscreen)
                FullscreenExitHotspot.Visibility = Visibility.Collapsed;
        }

        private void ShowFullscreenChrome()
        {
            if (_isFullscreen)
            {
                FullscreenPointerSurface.Visibility = Visibility.Visible;
                FullscreenButton.Visibility = Visibility.Collapsed;
                FullscreenExitHotspot.Visibility = Visibility.Visible;
                FullscreenExitHotspot.Opacity = 1;
            }
            else
            {
                FullscreenButton.Visibility = Visibility.Visible;
                FullscreenExitHotspot.Visibility = Visibility.Collapsed;
                FullscreenButton.Opacity = 1;
            }

            if (!_isFullscreen)
                return;

            EnsureFullscreenChromeTimer();
            _fullscreenChromeTimer?.Stop();
            _fullscreenChromeTimer?.Start();
        }

        private void StopFullscreenChromeTimer()
        {
            if (_fullscreenChromeTimer == null)
                return;

            _fullscreenChromeTimer.Stop();
            _fullscreenChromeTimer.Tick -= FullscreenChromeTimer_Tick;
            _fullscreenChromeTimer = null;
        }

        private void LocalPreviewHideButton_Click(object sender, RoutedEventArgs e)
        {
            _isLocalPreviewHidden = true;
            ApplyLocalPreviewVisibility();
            UpdateDockVisualStates();
        }

        private void LocalPreviewRestoreButton_Click(object sender, RoutedEventArgs e)
        {
            _isLocalPreviewHidden = false;
            ApplyLocalPreviewVisibility();
            UpdateDockVisualStates();
        }

        private void StreamInformationButton_Click(object sender, RoutedEventArgs e)
        {
            var flyout = new Flyout
            {
                Content = BuildStreamInformationPanel()
            };

            flyout.ShowAt((FrameworkElement)sender);
        }

        private void ScreenShareDetailsButton_Click(object sender, RoutedEventArgs e)
        {
            var flyout = new Flyout
            {
                Content = BuildScreenShareSummaryPanel()
            };

            flyout.ShowAt((FrameworkElement)sender);
        }

        private Border BuildScreenShareSummaryPanel()
        {
            var isSharing = ScreenShareCapturePipeline.Instance.IsRunning && _isSharingScreen;
            var resolutionText = _screenShareLastWidth > 0 && _screenShareLastHeight > 0
                ? $"{_screenShareLastWidth} x {_screenShareLastHeight}"
                : "Waiting for frames";

            var panel = new StackPanel
            {
                Width = 240,
                Spacing = 8
            };

            panel.Children.Add(new TextBlock
            {
                Text = "Call details",
                FontSize = 16,
                Foreground = new SolidColorBrush(Colors.White)
            });

            panel.Children.Add(CreateDetailsText($"Status: {(isSharing ? "Sharing" : "Not sharing")}"));
            panel.Children.Add(CreateDetailsText($"Quality: {ScreenShareCapturePipeline.Instance.CurrentQuality.Name}"));
            panel.Children.Add(CreateDetailsText($"FPS: {_screenShareObservedFps:0.0} / {ScreenShareCapturePipeline.TargetFps}"));
            panel.Children.Add(CreateDetailsText($"Receiver FPS: {_remoteReceiveObservedFps:0.0} in / {_remotePlaybackObservedFps:0.0} view"));
            panel.Children.Add(CreateDetailsText($"Bitrate: {ScreenShareCapturePipeline.Instance.CurrentBitrate / 1_000_000d:0.0} Mbps"));
            panel.Children.Add(CreateDetailsText($"Resolution: {resolutionText}"));
            panel.Children.Add(CreateDetailsText($"Last frame: {FormatBytes(_screenShareLastBytes)}"));

            return new Border
            {
                Padding = new Thickness(14),
                CornerRadius = new CornerRadius(16),
                Background = new SolidColorBrush(ColorHelper.FromArgb(255, 17, 19, 24)),
                BorderBrush = new SolidColorBrush(ColorHelper.FromArgb(48, 255, 255, 255)),
                BorderThickness = new Thickness(1),
                Child = panel
            };
        }

        private Border BuildStreamInformationPanel()
        {
            var isSharing = ScreenShareCapturePipeline.Instance.IsRunning && _isSharingScreen;
            var resolutionText = _screenShareLastWidth > 0 && _screenShareLastHeight > 0
                ? $"{_screenShareLastWidth} x {_screenShareLastHeight} ({GetResolutionTier(_screenShareLastHeight)})"
                : "Waiting for frames";

            var panel = new StackPanel
            {
                Width = 340,
                Spacing = 8
            };

            panel.Children.Add(new TextBlock
            {
                Text = "Stream information",
                FontSize = 16,
                Foreground = new SolidColorBrush(Colors.White)
            });

            panel.Children.Add(CreateDetailsText($"Status: {(isSharing ? "Sharing" : "Not sharing")}"));
            panel.Children.Add(CreateDetailsText($"Transport codec: H.264"));
            panel.Children.Add(CreateDetailsText($"Transport path: {ScreenShareCapturePipeline.Instance.TransportPipeline}"));
            panel.Children.Add(CreateDetailsText($"Capture policy: {(ScreenShareCapturePipeline.Instance.RequireDirectX12CapturePath ? "DirectX 12 WGC required" : "Best available")}"));
            panel.Children.Add(CreateDetailsText($"Encoder policy: {(ScreenShareCapturePipeline.Instance.RequireHardwareEncoder ? "DirectX 12 GPU hardware required" : "GPU preferred with software fallback")}"));
            panel.Children.Add(CreateDetailsText($"Encoder mode: {ScreenShareCapturePipeline.Instance.EncoderMode}"));
            panel.Children.Add(CreateDetailsText($"GPU path: {ScreenShareCapturePipeline.Instance.EncoderGpuDeviceMode}"));
            panel.Children.Add(CreateDetailsText($"Encoder input: {ScreenShareCapturePipeline.Instance.EncoderInputFormat}"));
            panel.Children.Add(CreateDetailsText($"Low latency flags: {(ScreenShareCapturePipeline.Instance.EncoderRealtimeModeEnabled ? "encoder" : "encoder pending")} / {(ScreenShareCapturePipeline.Instance.EncoderLowLatencyOutputEnabled ? "output" : "output pending")}"));
            panel.Children.Add(CreateDetailsText($"Recovery keyframe: every {ScreenShareCapturePipeline.Instance.RecoveryKeyFrameInterval} frames"));
            panel.Children.Add(CreateDetailsText($"Recovery requests: {ScreenShareCapturePipeline.Instance.RecoveryKeyFrameRequests}"));
            panel.Children.Add(CreateDetailsText($"Mode: locked realtime"));
            panel.Children.Add(CreateDetailsText($"Quality state: {ScreenShareCapturePipeline.Instance.AdaptiveState}"));
            panel.Children.Add(CreateDetailsText($"Target FPS: {ScreenShareCapturePipeline.TargetFps} locked"));
            panel.Children.Add(CreateDetailsText($"Sender throughput: {_screenShareObservedFps:0.0} FPS"));
            panel.Children.Add(CreateDetailsText($"Capture loop: {ScreenShareCapturePipeline.Instance.CaptureFps:0.0} FPS"));
            panel.Children.Add(CreateDetailsText($"Encoder output: {ScreenShareCapturePipeline.Instance.EncodedFps:0.0} FPS"));
            panel.Children.Add(CreateDetailsText($"Receiver input: {_remoteReceiveObservedFps:0.0} FPS"));
            panel.Children.Add(CreateDetailsText($"Receiver playback: {_remotePlaybackObservedFps:0.0} FPS"));
            panel.Children.Add(CreateDetailsText($"Receiver GPU queue: avg {_remoteGpuPlaybackAverageQueue:0.0}, peak {_remoteGpuPlaybackQueuePeak}, now {GetPendingRemoteGpuPlaybackFrameCount()}"));
            panel.Children.Add(CreateDetailsText($"Gaming readiness: {GetGamingReadinessText()}"));
            panel.Children.Add(CreateDetailsText($"Capture/encode: {ScreenShareCapturePipeline.Instance.LastCaptureMilliseconds:0}ms / {ScreenShareCapturePipeline.Instance.LastEncodeMilliseconds:0}ms"));
            panel.Children.Add(CreateDetailsText($"Bitrate: {ScreenShareCapturePipeline.Instance.CurrentBitrate / 1_000_000d:0.0} Mbps"));
            panel.Children.Add(CreateDetailsText($"Resolution: {resolutionText}"));
            panel.Children.Add(CreateDetailsText($"Preference: {ScreenShareCapturePipeline.Instance.RequestedQuality.Name}"));
            panel.Children.Add(CreateDetailsText($"Active quality: {ScreenShareCapturePipeline.Instance.CurrentQuality.Name}"));
            panel.Children.Add(CreateDetailsText($"Auto quality changes: Off"));
            panel.Children.Add(CreateDetailsText($"Congestion signals: {ScreenShareCapturePipeline.Instance.CongestionSignals}"));
            panel.Children.Add(CreateDetailsText($"Encoder fallbacks: {ScreenShareCapturePipeline.Instance.HardwareEncoderFallbackCount}"));
            panel.Children.Add(CreateDetailsText($"Local preview: throttled snapshot"));
            panel.Children.Add(CreateDetailsText($"Remote render: hardware H.264 playback"));
            panel.Children.Add(CreateDetailsText($"Render target: {ScreenShareCapturePipeline.Instance.CurrentQuality.Width} x {ScreenShareCapturePipeline.Instance.CurrentQuality.Height}"));
            panel.Children.Add(CreateDetailsText($"RTP frames: {_screenShareRtpFrames}"));
            panel.Children.Add(CreateDetailsText($"RTP metadata frames: {_screenShareRtpMetadataFrames}"));
            panel.Children.Add(CreateDetailsText($"RTCP recovery: PLI {_screenSharePeers.Values.Sum(peer => peer.SentPliRequests)}, NACK {_screenSharePeers.Values.Sum(peer => peer.SentNackRequests)}"));
            panel.Children.Add(CreateDetailsText($"Fallback frames: {_screenShareFallbackFrames}"));
            panel.Children.Add(CreateDetailsText($"Dropped send frames: {_screenShareDroppedFrames}"));
            panel.Children.Add(CreateDetailsText($"Dropped receive frames: {_screenShareDroppedReceiveFrames}"));
            panel.Children.Add(CreateDetailsText($"Receiver last packet: {FormatRelativeTimestamp(_remoteScreenLastReceivedAtUtc)}"));
            panel.Children.Add(CreateDetailsText($"Receiver last render: {FormatRelativeTimestamp(_remoteScreenLastRenderedAtUtc)}"));
            panel.Children.Add(CreateDetailsText($"Receiver decoder resets: {_remoteDecoderResetCount}"));
            panel.Children.Add(CreateDetailsText($"Screen sound: {(_isScreenShareSoundEnabled ? (SystemAudioShareService.Instance.IsRunning ? "On, capturing" : "On, waiting") : "Off")}"));
            panel.Children.Add(CreateDetailsText($"Sound device: {_selectedScreenShareSoundDevice}"));
            panel.Children.Add(CreateDetailsText($"Sound packets sent: {_screenShareSoundPacketsSent}"));
            panel.Children.Add(CreateDetailsText($"Last sound packet: {FormatRelativeTimestamp(_screenShareSoundLastPacketAtUtc)}"));
            panel.Children.Add(CreateDetailsText($"Last frame: {FormatBytes(_screenShareLastBytes)}"));

            return new Border
            {
                Padding = new Thickness(14),
                CornerRadius = new CornerRadius(16),
                Background = new SolidColorBrush(ColorHelper.FromArgb(255, 17, 19, 24)),
                BorderBrush = new SolidColorBrush(ColorHelper.FromArgb(48, 255, 255, 255)),
                BorderThickness = new Thickness(1),
                Child = new ScrollViewer
                {
                    MaxHeight = 560,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    Content = panel
                }
            };
        }

        private static TextBlock CreateDetailsText(string text)
        {
            return new TextBlock
            {
                Text = text,
                FontSize = 13,
                Foreground = new SolidColorBrush(ColorHelper.FromArgb(255, 218, 222, 230)),
                TextWrapping = TextWrapping.Wrap
            };
        }

        private static string GetResolutionTier(int height)
        {
            if (height >= 2160)
                return "4K";
            if (height >= 1440)
                return "1440p";
            if (height >= 1080)
                return "1080p";
            if (height >= 720)
                return "720p";

            return $"{height}p";
        }

        private string GetGamingReadinessText()
        {
            var target = ScreenShareCapturePipeline.TargetFps;
            var senderLocked =
                ScreenShareCapturePipeline.Instance.CaptureFps >= target - 2 &&
                ScreenShareCapturePipeline.Instance.EncodedFps >= target - 2;
            var receiverLocked =
                _remoteReceiveObservedFps >= target - 2 &&
                _remotePlaybackObservedFps >= target - 2;
            var queueHealthy = _remoteGpuPlaybackAverageQueue <= 12 &&
                _remoteGpuPlaybackQueuePeak < RemoteGpuBacklogKeyFrameRequestQueue;

            if (senderLocked && receiverLocked && queueHealthy)
                return "Ready: sender and receiver near 60fps";

            if (!senderLocked)
                return "Sender below 60fps";

            if (!receiverLocked)
                return "Receiver below 60fps";

            return "Queue pressure";
        }

        private static string FormatBytes(long bytes)
        {
            if (bytes <= 0)
                return "-";

            return bytes >= 1024 * 1024
                ? $"{bytes / 1024d / 1024d:0.0} MB"
                : $"{bytes / 1024d:0.0} KB";
        }

        private static string FormatRelativeTimestamp(DateTimeOffset? timestamp)
        {
            if (!timestamp.HasValue)
                return "-";

            var elapsed = DateTimeOffset.UtcNow - timestamp.Value;
            return elapsed.TotalSeconds < 1
                ? "now"
                : $"{elapsed.TotalSeconds:0}s ago";
        }

        private void ScreenShareQualityButton_Click(object sender, RoutedEventArgs e)
        {
            var flyout = new MenuFlyout();
            AddScreenShareQualityItem(flyout, ScreenShareQualityPreset.Hd720p);
            AddScreenShareQualityItem(flyout, ScreenShareQualityPreset.FullHd1080p);
            AddScreenShareQualityItem(flyout, ScreenShareQualityPreset.QuadHd2K);
            flyout.ShowAt((FrameworkElement)sender);
        }

        private void ScreenShareCodecButton_Click(object sender, RoutedEventArgs e)
        {
            var flyout = new MenuFlyout();
            AddScreenShareCodecItem(flyout, ScreenShareVideoCodec.Auto, "Auto compatibility (recommended)");
            AddScreenShareCodecItem(flyout, ScreenShareVideoCodec.AV1X, "AV1X hardware/modern codec");
            AddScreenShareCodecItem(flyout, ScreenShareVideoCodec.H264, "H.264 compatibility codec");
            flyout.ShowAt((FrameworkElement)sender);
        }

        private void AddScreenShareCodecItem(MenuFlyout flyout, ScreenShareVideoCodec codec, string text)
        {
            var item = new ToggleMenuFlyoutItem
            {
                Text = text,
                IsChecked = ScreenShareCapturePipeline.Instance.PreferredVideoCodec == codec
            };

            item.Click += async (_, __) =>
            {
                var wasSharing = _isSharingScreen && ScreenShareCapturePipeline.Instance.IsRunning;
                ScreenShareCapturePipeline.Instance.PreferredVideoCodec = codec;
                ScreenShareCodecLabel.Text = codec == ScreenShareVideoCodec.Auto
                    ? "Auto"
                    : codec == ScreenShareVideoCodec.AV1X ? "AV1X" : "H.264";
                DiagnosticLogService.WriteLine($"[ScreenShare:UI] Preferred video codec changed to {codec}; wasSharing={wasSharing}.");
                ScreenShareTestLogService.WriteLine($"codec-selection codec={codec}; wasSharing={wasSharing}");

                if (wasSharing)
                {
                    await StopLocalScreenShareAsync(false);
                    await StartLocalScreenShareAsync();
                }

                NativeCallCoordinator.Instance.SetStatus(
                    NativeCallCoordinator.Instance.CurrentSession.State,
                    wasSharing ? $"Restarted screen share using {ScreenShareCodecLabel.Text}." : $"Screen share codec set to {ScreenShareCodecLabel.Text}.");
            };

            flyout.Items.Add(item);
        }

        private void AddScreenShareQualityItem(MenuFlyout flyout, ScreenShareQualityPreset preset)
        {
            var profile = ScreenShareQualityProfile.FromPreset(preset);
            var item = new ToggleMenuFlyoutItem
            {
                Text = $"{profile.Name} {profile.TargetFps} FPS",
                IsChecked = ScreenShareCapturePipeline.Instance.QualityPreset == preset
            };

            item.Click += async (_, __) =>
            {
                var wasSharing = _isSharingScreen && ScreenShareCapturePipeline.Instance.IsRunning;
                if (wasSharing)
                {
                    var session = NativeCallCoordinator.Instance.CurrentSession;
                    if (!string.IsNullOrWhiteSpace(session.CallId))
                    {
                        var transitionTimestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                        var codec = ScreenShareCapturePipeline.Instance.ActiveVideoCodec;
                        foreach (var participantId in GetCallParticipants(session))
                        {
                            try
                            {
                                await SocialManager.Instance.Realtime.SendScreenShareMetadataAsync(
                                    participantId,
                                    session.CallId,
                                    profile.Width,
                                    profile.Height,
                                    transitionTimestamp,
                                    codec);
                            }
                            catch (Exception ex)
                            {
                                Debug.WriteLine($"[ScreenShare:H264] Quality transition metadata failed for participant {participantId}: {ex.Message}");
                            }
                        }
                    }
                }

                await ApplyScreenShareQualityAsync(preset, wasSharing);

                NativeCallCoordinator.Instance.SetStatus(
                    NativeCallCoordinator.Instance.CurrentSession.State,
                    wasSharing
                        ? $"Switching live screen share quality to {profile.Name}."
                        : $"Screen share quality set to {profile.Name}.");

                if (wasSharing)
                {
                    _fallbackFrameLastSentUtc = DateTimeOffset.MinValue;
                }

                UpdateDockVisualStates();
            };

            flyout.Items.Add(item);
        }

        private async Task ApplyScreenShareQualityAsync(
            ScreenShareQualityPreset preset,
            bool restartActiveShare)
        {
            var profile = ScreenShareQualityProfile.FromPreset(preset);

            var previousStack = ScreenShareServiceSelector.Active;
            var previousPipeline = previousStack.CapturePipeline;
            if (previousStack.Preset != preset)
            {
                previousPipeline.FrameReady -= NativeScreenShare_FrameReady;
                previousPipeline.StreamingFailed -= NativeScreenShare_StreamingFailed;

                if (previousPipeline.IsRunning)
                    await previousPipeline.StopAsync();

                previousStack.AudioPlayback.Stop();
            }

            ScreenShareServiceSelector.Select(preset);
            ScreenShareCapturePipeline.Instance.SetQuality(preset);
            DiagnosticLogService.WriteLine(
                $"[ScreenShare:STACK] selected={preset}; policyFps={ScreenSharePolicy.Controls.CompatibilityFps}; " +
                $"decodeQueue={ScreenSharePolicy.Controls.MaxDecodeQueue}; sendTimeoutMs={ScreenSharePolicy.Signaling.SendTimeout.TotalMilliseconds:0}; " +
                $"receiveStallMs={ScreenSharePolicy.Errors.ReceiveStallThreshold.TotalMilliseconds:0}.");

            if (previousStack.Preset != preset)
            {
                ScreenShareCapturePipeline.Instance.FrameReady += NativeScreenShare_FrameReady;
                ScreenShareCapturePipeline.Instance.StreamingFailed += NativeScreenShare_StreamingFailed;
                AudioPlaybackService.Instance.Start();

                if (restartActiveShare)
                    await ScreenShareCapturePipeline.Instance.StartAsync();
            }

            _screenShareLastQuality = profile.Name;
            _screenShareLastWidth = profile.Width;
            _screenShareLastHeight = profile.Height;
            ScreenShareQualityLabel.Text = $"{profile.Name} {profile.TargetFps} FPS";
            _screenShareFpsWindowStartedUtc = DateTimeOffset.UtcNow;
            _screenShareFrameCounter = 0;
            _screenShareObservedFps = 0;
            _lastRtpMetadataSentUtc = DateTimeOffset.MinValue;
            _lastRtpMetadataWidth = 0;
            _lastRtpMetadataHeight = 0;
            _h264Decoder?.Dispose();
            _h264Decoder = null;
            _rtpH264Decoder?.Dispose();
            _rtpH264Decoder = null;
            _rtpDecoderWidth = 0;
            _rtpDecoderHeight = 0;
            _rtpDecoderCodec = ScreenShareCodecNames.H264;
            _remoteScreenBitmap = null;
        }

        private async void ScreenShareSoundButton_Click(object sender, RoutedEventArgs e)
        {
            var flyout = new MenuFlyout();

            var shareSoundItem = new ToggleMenuFlyoutItem
            {
                Text = "Share screen sound",
                IsChecked = _isScreenShareSoundEnabled
            };
            shareSoundItem.Click += async (_, __) =>
            {
                _isScreenShareSoundEnabled = !_isScreenShareSoundEnabled;
                var wantedEnabled = _isScreenShareSoundEnabled;

                if (_isSharingScreen && ScreenShareCapturePipeline.Instance.IsRunning)
                {
                    if (wantedEnabled)
                        await TryStartScreenShareSoundAsync(NativeCallCoordinator.Instance.CurrentSession, showSuccessStatus: true);
                    else
                        await SystemAudioShareService.Instance.StopAsync();
                }

                if (!wantedEnabled)
                {
                    NativeCallCoordinator.Instance.SetStatus(
                        NativeCallCoordinator.Instance.CurrentSession.State,
                        "Screen share sound disabled.");
                }

                UpdateDockVisualStates();
            };

            flyout.Items.Add(shareSoundItem);
            flyout.Items.Add(new MenuFlyoutSeparator());

            var deviceMenu = new MenuFlyoutSubItem
            {
                Text = $"Sound device: {_selectedScreenShareSoundDevice}"
            };

            foreach (var device in await SystemAudioShareService.Instance.GetOutputDevicesAsync())
            {
                AddScreenShareSoundDeviceItem(deviceMenu, device);
            }

            flyout.Items.Add(deviceMenu);
            flyout.ShowAt((FrameworkElement)sender);
        }

        private void AddScreenShareSoundDeviceItem(MenuFlyoutSubItem parent, Zink.Models.RecorderDeviceItem device)
        {
            var selectedId = SystemAudioShareService.Instance.SelectedDeviceId ?? "";
            var item = new ToggleMenuFlyoutItem
            {
                Text = device.Name,
                IsChecked = string.Equals(device.Id ?? "", selectedId, StringComparison.OrdinalIgnoreCase)
            };

            item.Click += async (_, __) =>
            {
                await SystemAudioShareService.Instance.SetOutputDeviceAsync(device.Id, device.Name);
                _selectedScreenShareSoundDevice = SystemAudioShareService.Instance.SelectedDeviceName;

                NativeCallCoordinator.Instance.SetStatus(
                    NativeCallCoordinator.Instance.CurrentSession.State,
                    $"Screen share sound device set to {_selectedScreenShareSoundDevice}.");

                UpdateDockVisualStates();
            };

            parent.Items.Add(item);
        }

        private async void AddFriendToCallButton_Click(object sender, RoutedEventArgs e)
        {
            var session = NativeCallCoordinator.Instance.CurrentSession;
            if (session.State != NativeCallState.Connected && session.State != NativeCallState.Calling)
            {
                NativeCallCoordinator.Instance.SetStatus(session.State, "Start the call before adding friends.");
                return;
            }

            if (_callParticipantIds.Count >= MaxCallParticipants - 1)
            {
                NativeCallCoordinator.Instance.SetStatus(session.State, "This call already has the maximum of 10 people.");
                return;
            }

            var flyout = new MenuFlyout();

            try
            {
                var friends = await SocialManager.Instance.Api.GetFriendsAsync();
                var availableFriends = friends
                    .Where(friend => friend.UserId > 0 && !_callParticipantIds.Contains(friend.UserId))
                    .OrderByDescending(friend => friend.IsOnline)
                    .ThenBy(friend => string.IsNullOrWhiteSpace(friend.DisplayName) ? friend.Username : friend.DisplayName)
                    .ToList();

                if (availableFriends.Count == 0)
                {
                    flyout.Items.Add(new MenuFlyoutItem
                    {
                        Text = "No friends available"
                    });
                }
                else
                {
                    foreach (var friend in availableFriends)
                    {
                        var item = new MenuFlyoutItem
                        {
                            Text = $"{GetFriendDisplayName(friend)}{(friend.IsOnline ? " - online" : " - offline")}"
                        };

                        item.Click += async (_, __) => await InviteFriendToCurrentCallAsync(friend);
                        flyout.Items.Add(item);
                    }
                }
            }
            catch (Exception ex)
            {
                flyout.Items.Add(new MenuFlyoutItem
                {
                    Text = $"Could not load friends: {ex.Message}"
                });
            }

            flyout.ShowAt((FrameworkElement)sender);
        }

        private async Task InviteFriendToCurrentCallAsync(FriendDto friend)
        {
            var session = NativeCallCoordinator.Instance.CurrentSession;
            if (string.IsNullOrWhiteSpace(session.CallId))
            {
                NativeCallCoordinator.Instance.SetStatus(session.State, "No active call to invite friends into.");
                return;
            }

            if (_callParticipantIds.Count >= MaxCallParticipants - 1)
            {
                NativeCallCoordinator.Instance.SetStatus(session.State, "This call already has the maximum of 10 people.");
                return;
            }

            AddCallParticipant(friend.UserId);
            RememberParticipantName(friend.UserId, friend.DisplayName, friend.Username);
            await SocialManager.Instance.Realtime.CallUserAsync(friend.UserId, session.CallId);
            if (ScreenShareCapturePipeline.Instance.IsRunning)
                _ = StartScreenShareRtpOfferAsync(friend.UserId, session.CallId);

            NativeCallCoordinator.Instance.SetStatus(
                session.State,
                $"Invited {GetFriendDisplayName(friend)} to the call.");

            UpdateMembersPanel(session);
        }

        private static string GetFriendDisplayName(FriendDto friend)
        {
            if (!string.IsNullOrWhiteSpace(friend.DisplayName))
                return friend.DisplayName;

            if (!string.IsNullOrWhiteSpace(friend.Username))
                return friend.Username;

            return $"User {friend.UserId}";
        }

        private void RememberParticipantName(long userId, string? displayName, string? username = null)
        {
            if (userId <= 0)
                return;

            var name = !string.IsNullOrWhiteSpace(displayName)
                ? displayName.Trim()
                : (!string.IsNullOrWhiteSpace(username) ? username.Trim() : "");

            if (!string.IsNullOrWhiteSpace(name))
                _participantDisplayNames[userId] = name;
        }

        private string GetParticipantDisplayName(long userId)
        {
            if (userId > 0 &&
                _participantDisplayNames.TryGetValue(userId, out var name) &&
                !string.IsNullOrWhiteSpace(name))
            {
                return name;
            }

            return userId > 0 ? $"User {userId}" : "Remote user";
        }

        private string GetLeaveStatusText(long userId, string? reason)
        {
            var name = GetParticipantDisplayName(userId);
            return string.Equals(reason, "closed-app", StringComparison.OrdinalIgnoreCase)
                ? $"{name} closed the app and left the call"
                : $"{name} left the call";
        }

        private string GetScreenShareEndedStatusText(long userId, string? reason)
        {
            var name = GetParticipantDisplayName(userId);
            if (string.Equals(reason, "closed-app", StringComparison.OrdinalIgnoreCase))
                return $"{name}'s screen share ended because they closed Zink";

            return $"{name} ended their screen share";
        }

        private static string GetAvatarInitial(string displayName)
        {
            if (string.IsNullOrWhiteSpace(displayName))
                return "U";

            return char.ToUpperInvariant(displayName.Trim()[0]).ToString();
        }

        private IEnumerable<long> GetCallParticipants(NativeCallSession session)
        {
            if (session.RemoteUserId > 0)
                _callParticipantIds.Add(session.RemoteUserId);

            if (session.TargetUserId > 0)
                _callParticipantIds.Add(session.TargetUserId);

            return _callParticipantIds
                .Where(id => id > 0 && (_localUserId <= 0 || id != _localUserId) && !_leftParticipantIds.Contains(id))
                .Distinct()
                .Take(MaxCallParticipants - 1)
                .ToList();
        }

        private void AddCallParticipant(long userId)
        {
            if (userId <= 0 ||
                (_localUserId > 0 && userId == _localUserId) ||
                _callParticipantIds.Count >= MaxCallParticipants - 1)
                return;

            _leftParticipantIds.Remove(userId);
            _callParticipantIds.Add(userId);
            RenderAdditionalMembers();
        }

        private async Task LoadLocalUserIdAsync()
        {
            try
            {
                var userInfo = await Zink.Services.Calling.TokenStore.Instance.GetUserInfoAsync();
                if (userInfo == null)
                    return;

                _localUserId = userInfo.Value.userId;
                _callParticipantIds.Remove(_localUserId);
                Debug.WriteLine($"[Call] Local user id loaded: {_localUserId}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Call] Failed to load local user id: {ex.Message}");
            }
        }

        private void RenderAdditionalMembers()
        {
            AdditionalMembersPanel.Children.Clear();

            var session = NativeCallCoordinator.Instance.CurrentSession;
            var primaryRemote = session.RemoteUserId > 0 ? session.RemoteUserId : session.TargetUserId;

            foreach (var participantId in _callParticipantIds.Where(id => id > 0 && id != primaryRemote).Take(MaxCallParticipants - 2))
            {
                AdditionalMembersPanel.Children.Add(CreateAdditionalMemberCard(participantId));
            }
        }

        private Border CreateAdditionalMemberCard(long participantId)
        {
            return new Border
            {
                CornerRadius = new CornerRadius(18),
                Background = new SolidColorBrush(ColorHelper.FromArgb(255, 18, 21, 26)),
                Padding = new Thickness(14),
                Child = new Grid
                {
                    ColumnSpacing = 12,
                    ColumnDefinitions =
                    {
                        new ColumnDefinition { Width = new GridLength(44) },
                        new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
                        new ColumnDefinition { Width = GridLength.Auto }
                    },
                    Children =
                    {
                        CreateMemberAvatar(GetParticipantDisplayName(participantId)),
                        CreateAdditionalMemberText(participantId),
                        CreateAdditionalMemberBadge()
                    }
                }
            };
        }

        private static Grid CreateMemberAvatar(string displayName)
        {
            var avatar = new Grid
            {
                Width = 44,
                Height = 44
            };

            avatar.Children.Add(new Microsoft.UI.Xaml.Shapes.Ellipse
            {
                Fill = new SolidColorBrush(ColorHelper.FromArgb(255, 35, 38, 43))
            });
            avatar.Children.Add(new TextBlock
            {
                Text = GetAvatarInitial(displayName),
                Foreground = new SolidColorBrush(Colors.White),
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            });

            return avatar;
        }

        private StackPanel CreateAdditionalMemberText(long participantId)
        {
            var panel = new StackPanel
            {
                Spacing = 2,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(panel, 1);

            panel.Children.Add(new TextBlock
            {
                Text = GetParticipantDisplayName(participantId),
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Colors.White)
            });
            panel.Children.Add(new TextBlock
            {
                Text = "Invited",
                Foreground = new SolidColorBrush(ColorHelper.FromArgb(255, 168, 168, 168)),
                FontSize = 13
            });

            return panel;
        }

        private static Border CreateAdditionalMemberBadge()
        {
            var badge = new Border
            {
                Padding = new Thickness(10, 4, 10, 4),
                CornerRadius = new CornerRadius(999),
                Background = new SolidColorBrush(ColorHelper.FromArgb(255, 45, 49, 56)),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock
                {
                    Text = "Joined",
                    Foreground = new SolidColorBrush(Colors.White),
                    FontSize = 12,
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
                }
            };
            Grid.SetColumn(badge, 2);
            return badge;
        }

        private void MoreButton_Click(object sender, RoutedEventArgs e)
        {
            var flyout = BuildMoreFlyout();
            flyout.ShowAt((FrameworkElement)sender);
        }

        private MenuFlyout BuildMoreFlyout()
        {
            var flyout = new MenuFlyout();

            var micHeader = new MenuFlyoutSubItem
            {
                Text = $"Microphone: {_selectedMicrophone}"
            };

            foreach (var device in MicCaptureService.Instance.GetInputDevices())
            {
                AddMicrophoneMenuItem(micHeader, device);
            }

            flyout.Items.Add(micHeader);
            flyout.Items.Add(new MenuFlyoutSeparator());

            var reconnectItem = new MenuFlyoutItem
            {
                Text = "Reconnect"
            };
            reconnectItem.Click += MoreReconnect_Click;
            flyout.Items.Add(reconnectItem);

            var resetItem = new MenuFlyoutItem
            {
                Text = "Reset Call"
            };
            resetItem.Click += MoreReset_Click;
            flyout.Items.Add(resetItem);

            if (Frame.CanGoBack)
            {
                var backItem = new MenuFlyoutItem
                {
                    Text = "Back"
                };
                backItem.Click += MoreBack_Click;
                flyout.Items.Add(backItem);
            }

            return flyout;
        }

        private void AddMicrophoneMenuItem(MenuFlyoutSubItem parent, MicCaptureService.InputDeviceInfo device)
        {
            var item = new ToggleMenuFlyoutItem
            {
                Text = device.Name,
                IsChecked = device.DeviceNumber == MicCaptureService.Instance.SelectedInputDeviceNumber
            };

            item.Click += (_, __) =>
            {
                if (MicCaptureService.Instance.SetInputDevice(device.DeviceNumber))
                {
                    _selectedMicrophone = device.Name;

                    NativeCallCoordinator.Instance.SetStatus(
                        NativeCallCoordinator.Instance.CurrentSession.State,
                        $"Microphone set to {_selectedMicrophone}.");
                }
            };

            parent.Items.Add(item);
        }

        private string GetCurrentMicDisplayName()
        {
            var devices = MicCaptureService.Instance.GetInputDevices();
            var current = devices.FirstOrDefault(x => x.DeviceNumber == MicCaptureService.Instance.SelectedInputDeviceNumber);
            return current?.Name ?? "Default microphone";
        }

        private void MoreBack_Click(object? sender, RoutedEventArgs e)
        {
            if (Frame.CanGoBack)
                Frame.GoBack();
        }

        private void MoreReconnect_Click(object? sender, RoutedEventArgs e)
        {
            NativeCallCoordinator.Instance.SetStatus(
                NativeCallCoordinator.Instance.CurrentSession.State,
                "Reconnect requested.");
        }

        private void MoreReset_Click(object? sender, RoutedEventArgs e)
        {
            NativeCallCoordinator.Instance.Reset();
            _callParticipantIds.Clear();
            _leftParticipantIds.Clear();
            AdditionalMembersPanel.Children.Clear();
            _isMicEnabled = true;
            _isMuted = false;
            _isDeafened = false;
            NativeCallCoordinator.Instance.SetLocalAudioMuted(false);
            NativeCallCoordinator.Instance.SetRemoteAudioDeafened(false);
            _isSharingScreen = false;
            _isRemoteVideoVisible = false;
            _isRemoteScreenShareLoading = false;
            _isWatchingRemoteScreenShare = false;
            _remoteScreenShareSenderId = 0;
            ResetRemoteScreenShareReceiveState(clearImage: true);
            ResetCallTimer();
            AudioActivityService.Instance.Reset();
            _ = StopLocalScreenShareAsync(true);
            LocalPreviewImage.Source = null;
            LocalPreviewPlaceholder.Visibility = Visibility.Visible;
            _isLocalPreviewHidden = false;
            ApplyLocalPreviewVisibility();
            UpdateDockVisualStates();
            SyncPageStateFromSession(NativeCallCoordinator.Instance.CurrentSession);
            ApplySessionToUi(NativeCallCoordinator.Instance.CurrentSession);
        }

        private void SyncPageStateFromSession(NativeCallSession session)
        {
            if (session.TargetUserId > 0)
                _targetUserId = session.TargetUserId;
            else if (session.RemoteUserId > 0)
                _targetUserId = session.RemoteUserId;

            if (session.TargetUserId > 0)
                AddCallParticipant(session.TargetUserId);

            if (session.RemoteUserId > 0)
                AddCallParticipant(session.RemoteUserId);

            if (session.IsScreenShare)
                _isScreenShare = true;

            _isSharingScreen = _isScreenShare;
        }

        private void ApplySessionToUi(NativeCallSession session)
        {
            SyncPageStateFromSession(session);

            AvatarInitialText.Text = _isScreenShare ? "S" : "V";
            ModeText.Text = _isScreenShare ? "Mode: 4K Screen Share + Voice" : "Mode: Voice Call";

            CallIdText.Text = $"Call ID: {(string.IsNullOrWhiteSpace(session.CallId) ? "-" : session.CallId)}";
            TargetUserText.Text = $"Target user: {(session.TargetUserId > 0 ? session.TargetUserId.ToString() : "-")}";
            RemoteUserText.Text = $"Remote user: {(session.RemoteUserId > 0 ? session.RemoteUserId.ToString() : "-")}";
            PeerText.Text = string.IsNullOrWhiteSpace(session.PeerText) ? "Preparing call..." : session.PeerText;
            StatusText.Text = string.IsNullOrWhiteSpace(session.StatusText) ? "Waiting..." : session.StatusText;
            MediaOverlayText.Text = GetCallStateDisplayText(session);
            RemotePlaceholderTitleText.Text = session.State == NativeCallState.Ended ? "Call ended" : "Remote participant";
            RemotePlaceholderSubtitleText.Text = session.State == NativeCallState.Ended
                ? (!string.IsNullOrWhiteSpace(session.PeerText)
                    ? session.PeerText
                    : (session.RemoteUserId > 0 ? $"{GetParticipantDisplayName(session.RemoteUserId)} left the call" : "The remote participant left"))
                : "Remote media surface";

            if (session.State == NativeCallState.Connected)
            {
                StartCallTimerIfNeeded();
            }
            else if (session.State == NativeCallState.Ended ||
                     session.State == NativeCallState.Rejected ||
                     session.State == NativeCallState.Failed ||
                     session.State == NativeCallState.Idle)
            {
                StopCallTimer();
                if (session.State == NativeCallState.Idle)
                    ResetCallTimer();
                _ = StopLocalScreenShareAsync(false);
            }

            UpdateMembersPanel(session);
            UpdateMediaLayerVisibility();
            UpdateSpeakingIndicators(session, AudioActivityService.Instance.Current);
            SetConnectionState(session.State);
            UpdateDiscordCallPresence(session);
        }

        private void UpdateDiscordCallPresence(NativeCallSession session)
        {
            try
            {
                var isActiveCall =
                    session.State == NativeCallState.Calling ||
                    session.State == NativeCallState.Incoming ||
                    session.State == NativeCallState.Accepted ||
                    session.State == NativeCallState.Negotiating ||
                    session.State == NativeCallState.Connected;

                if (!isActiveCall)
                {
                    if (session.State == NativeCallState.Ended ||
                        session.State == NativeCallState.Rejected ||
                        session.State == NativeCallState.Failed)
                    {
                        DiscordPresenceService.Instance.SetAppPresence("Call ended");
                    }
                    else if (session.State == NativeCallState.Idle ||
                             session.State == NativeCallState.Ready)
                    {
                        DiscordPresenceService.Instance.SetAppPresence("Calls ready");
                    }

                    return;
                }

                var isScreenSharing =
                    _isSharingScreen ||
                    session.IsScreenShare ||
                    ScreenShareCapturePipeline.Instance.IsRunning;

                var participantCount = Math.Min(
                    MaxCallParticipants,
                    Math.Max(1, 1 + GetCallParticipants(session).Count()));

                TimeSpan? connectedFor = null;
                if (session.State == NativeCallState.Connected && _connectedAtUtc.HasValue)
                    connectedFor = DateTimeOffset.UtcNow - _connectedAtUtc.Value;

                var status = session.State switch
                {
                    NativeCallState.Calling => isScreenSharing ? "Starting a screen share in Zink" : "Calling on Zink",
                    NativeCallState.Incoming => isScreenSharing ? "Incoming screen share on Zink" : "Incoming Zink call",
                    NativeCallState.Accepted => "Joining a Zink call",
                    NativeCallState.Negotiating => "Connecting a Zink call",
                    NativeCallState.Connected => isScreenSharing ? "Screen sharing in Zink" : "In a Zink call",
                    _ => "Using Zink Calls"
                };

                DiscordPresenceService.Instance.SetCallPresence(
                    status,
                    participantCount,
                    isScreenSharing,
                    _isMuted,
                    _isDeafened,
                    connectedFor);
            }
            catch { }
        }

        private void UpdateMembersPanel(NativeCallSession session)
        {
            var remoteName = session.RemoteUserId > 0 ? GetParticipantDisplayName(session.RemoteUserId) : "Remote user";
            RemoteMemberTitleText.Text = remoteName;
            RemoteAvatarText.Text = session.RemoteUserId > 0 ? GetAvatarInitial(remoteName) : "R";

            var remoteLeft = session.State == NativeCallState.Ended ||
                (session.RemoteUserId > 0 && _leftParticipantIds.Contains(session.RemoteUserId));
            var participantCount = Math.Min(MaxCallParticipants, 1 + GetCallParticipants(session).Count());
            MembersSummaryText.Text = session.State == NativeCallState.Ended
                ? "Call ended"
                : $"The current number of people in the call: {participantCount}";
            AddFriendToCallButton.IsEnabled = participantCount < MaxCallParticipants &&
                (session.State == NativeCallState.Connected || session.State == NativeCallState.Calling);
            RenderAdditionalMembers();
            RemoteMemberCard.Opacity = remoteLeft ? 0.62 : 1.0;

            YouMemberStateText.Text = session.State switch
            {
                NativeCallState.Calling => "Calling",
                NativeCallState.Incoming => "Ringing",
                NativeCallState.Accepted => "Accepted",
                NativeCallState.Connected => "Connected",
                NativeCallState.Ended => "Ended",
                NativeCallState.Rejected => "Rejected",
                NativeCallState.Failed => "Failed",
                _ => "Ready"
            };

            RemoteMemberStateText.Text = remoteLeft
                ? "Left call"
                : (session.State switch
                {
                    NativeCallState.Calling => "Ringing",
                    NativeCallState.Incoming => "Calling you",
                    NativeCallState.Accepted => "Joining",
                    NativeCallState.Connected => "Connected",
                    NativeCallState.Ended => "Left call",
                    NativeCallState.Rejected => "Declined",
                    NativeCallState.Failed => "Unavailable",
                    _ => session.RemoteUserId > 0 ? "Waiting" : "Not connected"
                });
        }

        private void UpdateMediaLayerVisibility()
        {
            var showWatchPrompt = _remoteScreenShareSenderId > 0 && !_isWatchingRemoteScreenShare;
            var showLoadingPanel = _isWatchingRemoteScreenShare && _isRemoteScreenShareLoading;
            RemotePlaceholderPanel.Visibility = (_isRemoteVideoVisible || showLoadingPanel || showWatchPrompt)
                ? Visibility.Collapsed
                : Visibility.Visible;
            RemoteScreenWatchPanel.Visibility = showWatchPrompt ? Visibility.Visible : Visibility.Collapsed;
            RemoteScreenLoadingPanel.Visibility = showLoadingPanel ? Visibility.Visible : Visibility.Collapsed;
            RemoteScreenLoadingRing.IsActive = showLoadingPanel;
            ApplyLocalPreviewVisibility();
        }

        private void ShowRemoteScreenShareLoading(string title, string subtitle)
        {
            _isRemoteScreenShareLoading = true;
            RemoteScreenLoadingTitle.Text = title;
            RemoteScreenLoadingSubtitle.Text = subtitle;
        }

        private void ApplyScreenShareFocusMode()
        {
            ApplyScreenShareFocusMode(updateWindow: true);
        }

        private void ApplyScreenShareFocusMode(bool updateWindow)
        {
            CallHeaderPanel.Visibility = Visibility.Collapsed;
            CallSidePanel.Visibility = _isFullscreen ? Visibility.Collapsed : Visibility.Visible;
            CallStatusPanel.Visibility = Visibility.Collapsed;
            CallControlDock.Visibility = _isFullscreen ? Visibility.Collapsed : Visibility.Visible;
            FullscreenButton.Visibility = _isFullscreen ? Visibility.Collapsed : Visibility.Visible;
            FullscreenExitHotspot.Visibility = _isFullscreen ? Visibility.Visible : Visibility.Collapsed;
            FullscreenPointerSurface.Visibility = _isFullscreen ? Visibility.Visible : Visibility.Collapsed;
            StreamInformationButton.Visibility = _isFullscreen ? Visibility.Collapsed : Visibility.Visible;
            MediaOverlayBadge.Visibility = Visibility.Collapsed;

            if (updateWindow)
            {
                if (_isFullscreen)
                    App.MainWindow?.EnterFullscreenMode();
                else
                    App.MainWindow?.ExitFullscreenMode();
            }

            CallSideColumn.Width = _isFullscreen ? new GridLength(0) : new GridLength(330);
            CallLayoutRoot.Padding = _isFullscreen ? new Thickness(0) : new Thickness(24);
            CallMainContentGrid.ColumnSpacing = _isFullscreen ? 0 : 18;
            CallMediaStage.Margin = _isFullscreen ? new Thickness(0) : new Thickness(18, 18, 18, 120);
            RemoteScreenImage.Margin = _isFullscreen ? new Thickness(0) : new Thickness(12);
            RemoteScreenPlayer.Margin = _isFullscreen ? new Thickness(0) : new Thickness(12);
            CallStageBorder.CornerRadius = _isFullscreen ? new CornerRadius(0) : new CornerRadius(30);
            CallStageBorder.BorderThickness = _isFullscreen ? new Thickness(0) : new Thickness(1);
            FullscreenIcon.Glyph = _isFullscreen ? "\uE73F" : "\uE740";
            FullscreenDockIcon.Glyph = _isFullscreen ? "\uE73F" : "\uE740";
            ToolTipService.SetToolTip(FullscreenButton, _isFullscreen ? "Exit fullscreen" : "Fullscreen screen share");
            ToolTipService.SetToolTip(FullscreenDockButton, _isFullscreen ? "Exit fullscreen" : "Fullscreen screen share");

            if (_isFullscreen)
            {
                ShowFullscreenChrome();
            }
            else
            {
                _fullscreenChromeTimer?.Stop();
                FullscreenPointerSurface.Visibility = Visibility.Collapsed;
                FullscreenButton.Visibility = Visibility.Visible;
                FullscreenExitHotspot.Visibility = Visibility.Collapsed;
            }

            ApplyLocalPreviewVisibility();
        }

        private void ApplyLocalPreviewVisibility()
        {
            bool showPreview = _isSharingScreen &&
                ScreenShareCapturePipeline.Instance.IsRunning &&
                !_isLocalPreviewHidden &&
                !_isFullscreen;
            LocalPreviewBorder.Visibility = showPreview ? Visibility.Visible : Visibility.Collapsed;
            LocalPreviewRestorePanel.Visibility = _isSharingScreen &&
                ScreenShareCapturePipeline.Instance.IsRunning &&
                _isLocalPreviewHidden &&
                !_isFullscreen
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        private void UpdateSpeakingIndicators(NativeCallSession session, AudioActivityState activity)
        {
            var youSpeaking = activity.LocalSpeaking && _isMicEnabled && !_isMuted && session.State == NativeCallState.Connected;
            var remoteLeft = session.State == NativeCallState.Ended ||
                (session.RemoteUserId > 0 && _leftParticipantIds.Contains(session.RemoteUserId));
            var remoteSpeaking = activity.RemoteSpeaking && !_isDeafened && session.State == NativeCallState.Connected && !remoteLeft;

            YouSpeakingBadgeText.Text = youSpeaking ? $"Speaking {activity.LocalLevel:P0}" : (_isMuted ? "Muted" : "Idle");
            RemoteSpeakingBadgeText.Text = remoteLeft ? "Left" : (remoteSpeaking ? $"Speaking {activity.RemoteLevel:P0}" : "Idle");

            YouSpeakingBadge.Background = new SolidColorBrush(
                youSpeaking
                    ? ColorHelper.FromArgb(255, 36, 88, 62)
                    : (_isMuted
                        ? ColorHelper.FromArgb(255, 95, 40, 40)
                        : ColorHelper.FromArgb(255, 45, 49, 56)));

            RemoteSpeakingBadge.Background = new SolidColorBrush(
                remoteSpeaking
                    ? ColorHelper.FromArgb(255, 36, 88, 62)
                    : ColorHelper.FromArgb(255, 45, 49, 56));

            YouMemberCard.BorderThickness = new Thickness(youSpeaking ? 2 : 0);
            RemoteMemberCard.BorderThickness = new Thickness(remoteSpeaking ? 2 : 0);

            YouMemberCard.BorderBrush = new SolidColorBrush(
                youSpeaking
                    ? ColorHelper.FromArgb(255, 60, 140, 90)
                    : Colors.Transparent);

            RemoteMemberCard.BorderBrush = new SolidColorBrush(
                remoteSpeaking
                    ? ColorHelper.FromArgb(255, 60, 140, 90)
                    : Colors.Transparent);

            if (session.State == NativeCallState.Connected)
            {
                YouMemberStateText.Text = youSpeaking ? "Speaking" : (_isMuted ? "Muted" : "Connected");
                RemoteMemberStateText.Text = remoteLeft ? "Left call" : (remoteSpeaking ? "Speaking" : "Connected");

                if (youSpeaking && remoteSpeaking)
                {
                    PeerText.Text = $"Connected with user {session.RemoteUserId} - both speaking";
                }
                else if (youSpeaking)
                {
                    PeerText.Text = $"Connected with user {session.RemoteUserId} - you are speaking";
                }
                else if (remoteSpeaking)
                {
                    PeerText.Text = $"Connected with user {session.RemoteUserId} - remote is speaking";
                }
                else
                {
                    PeerText.Text = string.IsNullOrWhiteSpace(session.PeerText)
                        ? $"Connected with user {session.RemoteUserId}"
                        : session.PeerText;
                }
            }
            else if (session.State == NativeCallState.Ended)
            {
                YouMemberStateText.Text = "Call ended";
                RemoteMemberStateText.Text = "Left call";
                PeerText.Text = string.IsNullOrWhiteSpace(session.PeerText) ? "Call ended" : session.PeerText;
                StatusText.Text = string.IsNullOrWhiteSpace(session.StatusText) ? "Call ended." : session.StatusText;
            }
        }

        private static string GetCallStateDisplayText(NativeCallSession session)
        {
            return session.State switch
            {
                NativeCallState.Connected => "Connected",
                NativeCallState.Ended => "Call ended",
                NativeCallState.Rejected => "Call rejected",
                NativeCallState.Failed => "Call failed",
                NativeCallState.Calling => "Calling",
                NativeCallState.Incoming => "Incoming call",
                NativeCallState.Accepted => "Accepted",
                NativeCallState.Negotiating => "Connecting",
                NativeCallState.Ready => "Ready",
                _ => string.IsNullOrWhiteSpace(session.StatusText) ? session.State.ToString() : session.StatusText
            };
        }

        private void SetConnectionState(NativeCallState state)
        {
            ConnectionBadgeText.Text = state.ToString();

            switch (state)
            {
                case NativeCallState.Ready:
                case NativeCallState.Connected:
                    ConnectionBadge.Background = new SolidColorBrush(ColorHelper.FromArgb(255, 36, 64, 36));
                    break;

                case NativeCallState.Calling:
                case NativeCallState.Incoming:
                case NativeCallState.Accepted:
                case NativeCallState.Negotiating:
                    ConnectionBadge.Background = new SolidColorBrush(ColorHelper.FromArgb(255, 72, 56, 24));
                    break;

                case NativeCallState.Rejected:
                case NativeCallState.Ended:
                case NativeCallState.Failed:
                    ConnectionBadge.Background = new SolidColorBrush(ColorHelper.FromArgb(255, 80, 28, 28));
                    break;

                default:
                    ConnectionBadge.Background = new SolidColorBrush(ColorHelper.FromArgb(255, 46, 46, 46));
                    break;
            }
        }

        private void UpdateDockVisualStates()
        {
            DeafenToggleButton.Background = _isDeafened || _isMuted
                ? new SolidColorBrush(ColorHelper.FromArgb(255, 95, 40, 40))
                : new SolidColorBrush(ColorHelper.FromArgb(255, 23, 26, 32));

            ScreenShareToggleButton.Background = _isSharingScreen
                ? new SolidColorBrush(ColorHelper.FromArgb(255, 36, 88, 62))
                : new SolidColorBrush(ColorHelper.FromArgb(255, 23, 26, 32));

            SoundDeviceButton.Background = new SolidColorBrush(ColorHelper.FromArgb(255, 23, 26, 32));
            MoreButton.Background = new SolidColorBrush(ColorHelper.FromArgb(255, 23, 26, 32));
            ScreenShareSoundButton.Background = _isScreenShareSoundEnabled
                ? new SolidColorBrush(ColorHelper.FromArgb(255, 36, 88, 62))
                : new SolidColorBrush(ColorHelper.FromArgb(255, 23, 26, 32));
            LocalPreviewRestoreButton.Background = _isLocalPreviewHidden
                ? new SolidColorBrush(ColorHelper.FromArgb(255, 36, 64, 96))
                : new SolidColorBrush(ColorHelper.FromArgb(255, 23, 26, 32));
            ScreenShareQualityButton.Background = ScreenShareCapturePipeline.Instance.QualityPreset == ScreenShareQualityPreset.UltraHd4K
                ? new SolidColorBrush(ColorHelper.FromArgb(255, 36, 88, 62))
                : new SolidColorBrush(ColorHelper.FromArgb(255, 23, 26, 32));
            var screenShareQuality = ScreenShareCapturePipeline.Instance.CurrentQuality;
            ScreenShareQualityLabel.Text = $"{screenShareQuality.Name} {screenShareQuality.TargetFps} FPS";
            ScreenShareDetailsButton.Background = _isSharingScreen
                ? new SolidColorBrush(ColorHelper.FromArgb(255, 36, 64, 96))
                : new SolidColorBrush(ColorHelper.FromArgb(255, 23, 26, 32));
            FullscreenButton.Background = _isFullscreen
                ? new SolidColorBrush(ColorHelper.FromArgb(220, 36, 64, 96))
                : new SolidColorBrush(ColorHelper.FromArgb(196, 17, 19, 24));
            FullscreenDockButton.Background = _isFullscreen
                ? new SolidColorBrush(ColorHelper.FromArgb(255, 36, 64, 96))
                : new SolidColorBrush(ColorHelper.FromArgb(255, 23, 26, 32));

        }

        private sealed class PendingRemoteH264Frame
        {
            public PendingRemoteH264Frame(byte[] frameData, int width, int height, bool isKeyFrame, long sequence)
                : this(frameData, width, height, isKeyFrame, sequence, 0)
            {
            }

            public PendingRemoteH264Frame(byte[] frameData, int width, int height, bool isKeyFrame, long sequence, long generation)
                : this(frameData, width, height, isKeyFrame, sequence, generation, RemoteGpuFrameDuration)
            {
            }

            public PendingRemoteH264Frame(byte[] frameData, int width, int height, bool isKeyFrame, long sequence, long generation, TimeSpan sampleDuration)
                : this(frameData, width, height, isKeyFrame, sequence, generation, sampleDuration, ScreenShareCodecNames.H264)
            {
            }

            public PendingRemoteH264Frame(byte[] frameData, int width, int height, bool isKeyFrame, long sequence, long generation, TimeSpan sampleDuration, string codec)
            {
                FrameData = frameData;
                Width = width;
                Height = height;
                IsKeyFrame = isKeyFrame;
                Sequence = sequence;
                Generation = generation;
                SampleDuration = sampleDuration;
                Codec = string.IsNullOrWhiteSpace(codec) ? ScreenShareCodecNames.H264 : codec;
            }

            public byte[] FrameData { get; }
            public int Width { get; }
            public int Height { get; }
            public bool IsKeyFrame { get; }
            public long Sequence { get; }
            public long Generation { get; }
            public TimeSpan SampleDuration { get; }
            public string Codec { get; }
            public DateTimeOffset QueuedAtUtc { get; init; }
        }

        private sealed class PendingLocalScreenShareSend
        {
            public PendingLocalScreenShareSend(
                IReadOnlyList<long> participantIds,
                string callId,
                NativeScreenFrameEventArgs frame,
                int generation)
            {
                ParticipantIds = participantIds;
                CallId = callId;
                Frame = frame;
                Generation = generation;
            }

            public IReadOnlyList<long> ParticipantIds { get; }
            public string CallId { get; }
            public NativeScreenFrameEventArgs Frame { get; }
            public int Generation { get; }
        }

        private sealed class BitReader
        {
            private readonly byte[] _data;
            private int _bitOffset;

            public BitReader(byte[] data)
            {
                _data = data;
            }

            public uint ReadBits(int count)
            {
                uint value = 0;
                for (var i = 0; i < count; i++)
                {
                    value = (value << 1) | (ReadBit() ? 1u : 0u);
                }

                return value;
            }

            public bool ReadBit()
            {
                if (_bitOffset >= _data.Length * 8)
                    throw new InvalidOperationException("Unexpected end of H.264 bitstream.");

                var byteIndex = _bitOffset / 8;
                var bitIndex = 7 - (_bitOffset % 8);
                _bitOffset++;
                return ((_data[byteIndex] >> bitIndex) & 1) != 0;
            }

            public uint ReadUnsignedExpGolomb()
            {
                var leadingZeroBits = 0;
                while (!ReadBit())
                    leadingZeroBits++;

                if (leadingZeroBits == 0)
                    return 0;

                return ((1u << leadingZeroBits) - 1u) + ReadBits(leadingZeroBits);
            }

            public int ReadSignedExpGolomb()
            {
                var value = ReadUnsignedExpGolomb();
                var signed = (int)((value + 1) / 2);
                return (value & 1) == 0 ? -signed : signed;
            }
        }
    }
}

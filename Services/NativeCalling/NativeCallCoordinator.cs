using System;
using System.Threading.Tasks;
using Zink.Services.Social;

namespace Zink.Services.NativeCalling
{
    public sealed class NativeCallCoordinator
    {
        public static NativeCallCoordinator Instance { get; } = new NativeCallCoordinator();

        public NativeCallSession CurrentSession { get; } = new NativeCallSession();

        public event EventHandler<NativeCallSession>? SessionChanged;

        private bool _audioHooked;
        private bool _realtimeHooked;

        private int _sentAudioChunks;
        private int _receivedAudioChunks;
        private int _rawIncomingAudioMessages;
        private bool _audioStarted;
        private bool _localAudioMuted;
        private bool _remoteAudioDeafened;
        private int _localUserId;
        private bool _localUserIdLoadInProgress;

        public DateTimeOffset LastRemoteVoiceAudioReceivedUtc { get; private set; } = DateTimeOffset.MinValue;

        private NativeCallCoordinator()
        {
            EnsureRealtimeHooks();
            _ = EnsureLocalUserIdAsync();
        }

        public void Reset()
        {
            UnhookAudioCapture();
            AudioActivityService.Instance.Reset();
            AudioPlaybackService.Instance.Stop();
            _ = MicCaptureService.Instance.StopAsync();

            _sentAudioChunks = 0;
            _receivedAudioChunks = 0;
            _rawIncomingAudioMessages = 0;
            _audioStarted = false;
            _localAudioMuted = false;
            _remoteAudioDeafened = false;

            CurrentSession.CallId = "";
            CurrentSession.TargetUserId = 0;
            CurrentSession.RemoteUserId = 0;
            CurrentSession.IsScreenShare = false;
            CurrentSession.State = NativeCallState.Idle;
            CurrentSession.StatusText = "Waiting...";
            CurrentSession.PeerText = "Preparing call...";
            CurrentSession.CreatedAtUtc = DateTimeOffset.UtcNow;

            RaiseChanged();
        }

        public void SetStatus(NativeCallState state, string status, string? peerText = null)
        {
            CurrentSession.State = state;
            CurrentSession.StatusText = status;

            if (!string.IsNullOrWhiteSpace(peerText))
                CurrentSession.PeerText = peerText!;

            RaiseChanged();
        }

        public void SetOutgoing(string callId, long targetUserId, bool isScreenShare)
        {
            _sentAudioChunks = 0;
            _receivedAudioChunks = 0;
            _rawIncomingAudioMessages = 0;
            _audioStarted = false;
            _localAudioMuted = false;
            _remoteAudioDeafened = false;

            CurrentSession.CallId = callId;
            CurrentSession.TargetUserId = targetUserId;
            CurrentSession.RemoteUserId = targetUserId;
            CurrentSession.IsScreenShare = isScreenShare;
            CurrentSession.State = NativeCallState.Calling;
            CurrentSession.StatusText = "Calling...";
            CurrentSession.PeerText = $"Calling user {targetUserId}";
            CurrentSession.CreatedAtUtc = DateTimeOffset.UtcNow;

            RaiseChanged();
        }

        public void SetIncoming(string callId, long fromUserId, string displayName, bool isScreenShare)
        {
            _sentAudioChunks = 0;
            _receivedAudioChunks = 0;
            _rawIncomingAudioMessages = 0;
            _audioStarted = false;
            _localAudioMuted = false;
            _remoteAudioDeafened = false;

            CurrentSession.CallId = callId;
            CurrentSession.TargetUserId = fromUserId;
            CurrentSession.RemoteUserId = fromUserId;
            CurrentSession.IsScreenShare = isScreenShare;
            CurrentSession.State = NativeCallState.Incoming;
            CurrentSession.StatusText = $"Incoming call from {displayName}.";
            CurrentSession.PeerText = $"Incoming call from {displayName}";
            CurrentSession.CreatedAtUtc = DateTimeOffset.UtcNow;

            RaiseChanged();
        }

        public async Task StartOutgoingAsync(long targetUserId, bool isScreenShare)
        {
            EnsureRealtimeHooks();

            var callId = Guid.NewGuid().ToString();

            await SocialManager.Instance.Realtime.CallUserAsync(targetUserId, callId);
            Zink.Services.IncomingCallRingtoneService.TryStartOutgoing();

            CallLaunchState.SetOutgoing(callId, targetUserId, isScreenShare);
            SetOutgoing(callId, targetUserId, isScreenShare);
        }

        public async Task AcceptIncomingAsync(long remoteUserId, string callId)
        {
            EnsureRealtimeHooks();

            Zink.Services.IncomingCallRingtoneService.TryStop();
            await SocialManager.Instance.Realtime.AcceptCallAsync(remoteUserId, callId);

            CurrentSession.CallId = callId;
            CurrentSession.RemoteUserId = remoteUserId;
            CurrentSession.TargetUserId = remoteUserId;
            CurrentSession.State = NativeCallState.Connected;
            CurrentSession.StatusText = "Connected.";
            CurrentSession.PeerText = $"Connected with user {remoteUserId}";
            CurrentSession.CreatedAtUtc = DateTimeOffset.UtcNow;

            RaiseChanged();

            await TryStartLocalAudioAsync();
        }

        public async Task RejectIncomingAsync(long remoteUserId, string callId)
        {
            Zink.Services.IncomingCallRingtoneService.TryStop();
            await SocialManager.Instance.Realtime.RejectCallAsync(remoteUserId, callId);

            CurrentSession.CallId = callId;
            CurrentSession.RemoteUserId = remoteUserId;
            CurrentSession.TargetUserId = remoteUserId;
            CurrentSession.State = NativeCallState.Rejected;
            CurrentSession.StatusText = "Rejected call.";

            RaiseChanged();
        }

        public async Task OnCallAnsweredAsync(string callId, long fromUserId, string? displayName = null)
        {
            EnsureRealtimeHooks();
            Zink.Services.IncomingCallRingtoneService.TryStop();

            var peerName = FormatPeerDisplayName(fromUserId, displayName);
            CurrentSession.CallId = callId;
            CurrentSession.RemoteUserId = fromUserId;
            CurrentSession.TargetUserId = fromUserId;
            CurrentSession.State = NativeCallState.Connected;
            CurrentSession.StatusText = "Connected.";
            CurrentSession.PeerText = $"Connected with {peerName}";

            RaiseChanged();

            await TryStartLocalAudioAsync();
        }

        public async Task EndAsync(string reason = "left-call")
        {
            Zink.Services.IncomingCallRingtoneService.TryStop();

            if (CurrentSession.RemoteUserId > 0 && !string.IsNullOrWhiteSpace(CurrentSession.CallId))
            {
                await SocialManager.Instance.Realtime.EndCallAsync(CurrentSession.RemoteUserId, CurrentSession.CallId, reason);
            }

            UnhookAudioCapture();
            AudioActivityService.Instance.Reset();
            AudioPlaybackService.Instance.Stop();
            await MicCaptureService.Instance.StopAsync();

            _sentAudioChunks = 0;
            _receivedAudioChunks = 0;
            _rawIncomingAudioMessages = 0;
            _audioStarted = false;
            _localAudioMuted = false;
            _remoteAudioDeafened = false;

            CurrentSession.State = NativeCallState.Ended;
            CurrentSession.StatusText = "Call ended.";
            CurrentSession.PeerText = "You left the call.";
            CurrentSession.IsScreenShare = false;

            RaiseChanged();
        }

        public void SetLocalAudioMuted(bool muted)
        {
            _localAudioMuted = muted;

            if (muted)
                AudioActivityService.Instance.UpdateLocalLevel(0);
        }

        public void SetRemoteAudioDeafened(bool deafened)
        {
            _remoteAudioDeafened = deafened;

            if (deafened)
            {
                AudioPlaybackService.Instance.Stop();
                AudioActivityService.Instance.UpdateRemoteLevel(0);
            }
            else if (CurrentSession.State == NativeCallState.Connected)
            {
                AudioPlaybackService.Instance.Start();
            }
        }

        private void EnsureRealtimeHooks()
        {
            if (_realtimeHooked)
                return;

            _realtimeHooked = true;

            SocialManager.Instance.Realtime.AudioChunkReceived += Realtime_AudioChunkReceived;
            SocialManager.Instance.Realtime.RawIncomingAudioMessageCountChanged += Realtime_RawIncomingAudioMessageCountChanged;
            SocialManager.Instance.Realtime.CallAnswered += Realtime_CallAnswered;
            SocialManager.Instance.Realtime.CallEnded += Realtime_CallEnded;
            SocialManager.Instance.Realtime.CallRejected += Realtime_CallRejected;
        }

        private void Realtime_RawIncomingAudioMessageCountChanged(object? sender, int e)
        {
            _rawIncomingAudioMessages = e;
            UpdateAudioFlowStatus();
        }

        private async void Realtime_CallAnswered(object? sender, CallSignalEventArgs e)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(CurrentSession.CallId) && e.CallId != CurrentSession.CallId)
                    return;

                await OnCallAnsweredAsync(e.CallId, e.FromUserId, GetSignalDisplayName(e));
            }
            catch
            {
            }
        }

        private async void Realtime_CallEnded(object? sender, CallSignalEventArgs e)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(CurrentSession.CallId) && e.CallId != CurrentSession.CallId)
                    return;

                Zink.Services.IncomingCallRingtoneService.TryStop();
                UnhookAudioCapture();
                AudioActivityService.Instance.Reset();
                AudioPlaybackService.Instance.Stop();
                await MicCaptureService.Instance.StopAsync();

                _sentAudioChunks = 0;
                _receivedAudioChunks = 0;
                _rawIncomingAudioMessages = 0;
                _audioStarted = false;
                _localAudioMuted = false;
                _remoteAudioDeafened = false;

                var peerName = FormatPeerDisplayName(e.FromUserId, GetSignalDisplayName(e));
                var closedApp = IsClosedAppReason(e.Reason);
                CurrentSession.State = NativeCallState.Ended;
                CurrentSession.StatusText = closedApp
                    ? $"{peerName} closed the app."
                    : $"{peerName} left the call.";
                CurrentSession.PeerText = closedApp
                    ? $"{peerName} closed the app and left the call"
                    : $"{peerName} left the call";

                RaiseChanged();
            }
            catch
            {
            }
        }

        private async void Realtime_CallRejected(object? sender, CallSignalEventArgs e)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(CurrentSession.CallId) && e.CallId != CurrentSession.CallId)
                    return;

                Zink.Services.IncomingCallRingtoneService.TryStop();
                UnhookAudioCapture();
                AudioActivityService.Instance.Reset();
                AudioPlaybackService.Instance.Stop();
                await MicCaptureService.Instance.StopAsync();

                _sentAudioChunks = 0;
                _receivedAudioChunks = 0;
                _rawIncomingAudioMessages = 0;
                _audioStarted = false;
                _localAudioMuted = false;
                _remoteAudioDeafened = false;

                var peerName = FormatPeerDisplayName(e.FromUserId, GetSignalDisplayName(e));
                CurrentSession.State = NativeCallState.Rejected;
                CurrentSession.StatusText = $"{peerName} rejected the call.";
                CurrentSession.PeerText = $"{peerName} rejected the call";

                RaiseChanged();
            }
            catch
            {
            }
        }

        private static string GetSignalDisplayName(CallSignalEventArgs e)
        {
            if (!string.IsNullOrWhiteSpace(e.FromDisplayName))
                return e.FromDisplayName;

            return e.FromUsername;
        }

        private static string FormatPeerDisplayName(long userId, string? displayName)
        {
            if (!string.IsNullOrWhiteSpace(displayName))
                return displayName.Trim();

            return userId > 0 ? $"User {userId}" : "Remote user";
        }

        private static bool IsClosedAppReason(string? reason)
        {
            return string.Equals(reason, "closed-app", StringComparison.OrdinalIgnoreCase);
        }

        private async Task EnsureLocalUserIdAsync()
        {
            if (_localUserId > 0 || _localUserIdLoadInProgress)
                return;

            _localUserIdLoadInProgress = true;
            try
            {
                var userInfo = await Zink.Services.Calling.TokenStore.Instance.GetUserInfoAsync();
                _localUserId = userInfo?.userId ?? 0;
            }
            catch
            {
                _localUserId = 0;
            }
            finally
            {
                _localUserIdLoadInProgress = false;
            }
        }

        private void Realtime_AudioChunkReceived(object? sender, AudioChunkEventArgs e)
        {
            try
            {
                if (_remoteAudioDeafened)
                {
                    AudioActivityService.Instance.UpdateRemoteLevel(0);
                    return;
                }

                if (string.IsNullOrWhiteSpace(CurrentSession.CallId))
                    return;

                if (e.CallId != CurrentSession.CallId)
                    return;

                if (_localUserId <= 0)
                    _ = EnsureLocalUserIdAsync();

                if (_localUserId > 0 && e.FromUserId == _localUserId)
                    return;

                if (!AudioPlaybackService.IsScreenShareAudioPacket(e.AudioData))
                    LastRemoteVoiceAudioReceivedUtc = DateTimeOffset.UtcNow;

                if (CurrentSession.RemoteUserId <= 0 || CurrentSession.RemoteUserId != e.FromUserId)
                {
                    CurrentSession.RemoteUserId = e.FromUserId;

                    if (CurrentSession.TargetUserId <= 0)
                        CurrentSession.TargetUserId = e.FromUserId;
                }

                AudioPlaybackService.Instance.QueuePlayback(e.AudioData);

                _receivedAudioChunks++;
                UpdateAudioFlowStatus();
            }
            catch (Exception ex)
            {
                SetStatus(
                    CurrentSession.State,
                    $"Connected, but remote audio playback failed: {ex.Message}",
                    CurrentSession.PeerText);
            }
        }

        private async Task TryStartLocalAudioAsync()
        {
            try
            {
                await StartLocalAudioAsync();
                _audioStarted = true;
                UpdateAudioFlowStatus();
            }
            catch (Exception ex)
            {
                UnhookAudioCapture();
                AudioActivityService.Instance.Reset();
                AudioPlaybackService.Instance.Stop();
                await MicCaptureService.Instance.StopAsync();

                SetStatus(
                    NativeCallState.Connected,
                    $"Connected, but audio failed to start: {ex.Message}",
                    CurrentSession.PeerText);
            }
        }

        private async Task StartLocalAudioAsync()
        {
            AudioPlaybackService.Instance.Start();

            if (!_audioHooked)
            {
                MicCaptureService.Instance.AudioCaptured += OnAudioCaptured;
                _audioHooked = true;
            }

            await MicCaptureService.Instance.StartAsync();
        }

        private void UnhookAudioCapture()
        {
            if (_audioHooked)
            {
                MicCaptureService.Instance.AudioCaptured -= OnAudioCaptured;
                _audioHooked = false;
            }
        }

        private async void OnAudioCaptured(byte[] data)
        {
            try
            {
                if (_localAudioMuted)
                    return;

                if (CurrentSession.State != NativeCallState.Connected)
                    return;

                if (CurrentSession.RemoteUserId <= 0 || string.IsNullOrWhiteSpace(CurrentSession.CallId))
                    return;

                if (data == null || data.Length == 0)
                    return;

                await SocialManager.Instance.Realtime.SendAudioChunkAsync(
                    CurrentSession.RemoteUserId,
                    CurrentSession.CallId,
                    data);

                _sentAudioChunks++;
                UpdateAudioFlowStatus();
            }
            catch (Exception ex)
            {
                SetStatus(
                    CurrentSession.State,
                    $"Connected, but sending microphone audio failed: {ex.Message}",
                    CurrentSession.PeerText);
            }
        }

        private void UpdateAudioFlowStatus()
        {
            if (CurrentSession.State != NativeCallState.Connected)
                return;

            var peerText = CurrentSession.RemoteUserId > 0
                ? $"Connected with user {CurrentSession.RemoteUserId}"
                : CurrentSession.PeerText;

            var audioState = _audioStarted ? "started" : "starting";
            var status = $"Connected. Audio {audioState}. sent={_sentAudioChunks}, raw-in={_rawIncomingAudioMessages}, received={_receivedAudioChunks}";

            SetStatus(CurrentSession.State, status, peerText);
        }

        private void RaiseChanged()
        {
            SessionChanged?.Invoke(this, CurrentSession);
        }
    }
}

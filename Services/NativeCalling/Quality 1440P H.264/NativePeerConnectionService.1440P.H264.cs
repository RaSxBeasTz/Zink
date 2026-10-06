using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;
using Zink.Services.NativeCalling.Models;

namespace Zink.Services.NativeCalling.Quality1440p
{
    public sealed class NativePeerConnectionService
    {
        private readonly ScreenSharePeerConnectionSettings _settings;

        private readonly object _syncRoot = new();
        private readonly List<IceCandidateModel> _pendingRemoteIceCandidates = new();
        private RTCPeerConnection? _peerConnection;
        private bool _hasVideoTrack;
        private bool _isClosed;
        private bool _hasRemoteDescription;
        private long _sentVideoFrames;
        private long _receivedVideoFrames;
        private uint _lastRemoteVideoSsrc;
        private ushort? _lastRemoteVideoSequence;
        private string _lastRemoteVideoCodec = ScreenShareCodecNames.H264;
        private string _expectedRemoteVideoCodec = ScreenShareCodecNames.H264;
        private DateTimeOffset _lastPliSentUtc = DateTimeOffset.MinValue;
        private DateTimeOffset _lastNackSentUtc = DateTimeOffset.MinValue;
        private DateTimeOffset _lastSendDiagnosticUtc = DateTimeOffset.MinValue;
        private long _sentPliRequests;
        private long _sentNackRequests;
        private readonly string _diagnosticName;
        public ScreenShareQualityPreset QualityPreset { get; }

        public NativePeerConnectionService(
            string? diagnosticName = null,
            ScreenShareQualityPreset? qualityPreset = null,
            ScreenSharePeerConnectionSettings? settings = null)
        {
            QualityPreset = qualityPreset ?? ScreenShareServiceSelector.Active.Preset;
            _settings = settings ?? new ScreenSharePeerConnectionSettings(
                102, 103, 90_000, 30,
                "packetization-mode=1;profile-level-id=42e034;level-asymmetry-allowed=1");
            _diagnosticName = string.IsNullOrWhiteSpace(diagnosticName)
                ? "peer"
                : diagnosticName.Trim();
        }

        public event EventHandler<IceCandidateModel>? LocalIceCandidateReady;
        public event EventHandler<NativeRtpVideoFrameEventArgs>? EncodedVideoFrameReceived;
        public event EventHandler<string>? ConnectionStateChanged;

        public bool HasVideoTrack => _hasVideoTrack;
        public long SentVideoFrames => _sentVideoFrames;
        public long ReceivedVideoFrames => Interlocked.Read(ref _receivedVideoFrames);
        public long SentPliRequests => Interlocked.Read(ref _sentPliRequests);
        public long SentNackRequests => Interlocked.Read(ref _sentNackRequests);
        public string ConnectionState => _peerConnection?.connectionState.ToString() ?? "not-created";
        public bool HasRemoteDescription
        {
            get
            {
                lock (_syncRoot)
                    return _hasRemoteDescription;
            }
        }
        public bool IsVideoTransportReady
        {
            get
            {
                lock (_syncRoot)
                {
                    return _peerConnection != null &&
                        !_isClosed &&
                        _peerConnection.connectionState == RTCPeerConnectionState.connected;
                }
            }
        }

        public string TransportDescription => "WebRTC RTP AV1X/H.264 media track";
        public string CodecParameters => $"AV1X payload={_settings.Av1PayloadType}; H.264 {_settings.H264FormatParameters}";

        public void SetExpectedRemoteVideoCodec(string codec)
        {
            var normalizedCodec = ScreenShareCodecNames.IsAv1(codec)
                ? ScreenShareCodecNames.Av1
                : ScreenShareCodecNames.H264;

            Volatile.Write(ref _expectedRemoteVideoCodec, normalizedCodec);
            Volatile.Write(ref _lastRemoteVideoCodec, normalizedCodec);
        }

        public Task AttachMicrophoneAsync(MicCaptureService mic)
        {
            return Task.CompletedTask;
        }

        public Task AttachScreenShareAsync(ScreenShareCaptureService screen)
        {
            EnsureVideoTrack();
            return Task.CompletedTask;
        }

        public Task<SessionDescriptionModel> CreateOfferAsync()
        {
            EnsureVideoTrack();

            var peerConnection = EnsurePeerConnection();
            var offer = peerConnection.createOffer(null);
            peerConnection.setLocalDescription(offer);

            return Task.FromResult(new SessionDescriptionModel
            {
                Type = offer.type.ToString().ToLowerInvariant(),
                Sdp = offer.sdp ?? ""
            });
        }

        public Task<SessionDescriptionModel> CreateAnswerAsync()
        {
            EnsureVideoTrack();

            var peerConnection = EnsurePeerConnection();
            var answer = peerConnection.createAnswer(null);
            peerConnection.setLocalDescription(answer);

            return Task.FromResult(new SessionDescriptionModel
            {
                Type = answer.type.ToString().ToLowerInvariant(),
                Sdp = answer.sdp ?? ""
            });
        }

        public Task SetRemoteOfferAsync(SessionDescriptionModel offer)
        {
            EnsureVideoTrack();
            var peerConnection = EnsurePeerConnection();
            peerConnection.setRemoteDescription(ToRtcDescription(offer));
            ApplyPendingRemoteIceCandidates(peerConnection);
            return Task.CompletedTask;
        }

        public Task SetRemoteAnswerAsync(SessionDescriptionModel answer)
        {
            var peerConnection = EnsurePeerConnection();
            peerConnection.setRemoteDescription(ToRtcDescription(answer));
            ApplyPendingRemoteIceCandidates(peerConnection);
            return Task.CompletedTask;
        }

        public Task AddIceCandidateAsync(IceCandidateModel ice)
        {
            if (string.IsNullOrWhiteSpace(ice.Candidate))
                return Task.CompletedTask;

            lock (_syncRoot)
            {
                if (!_hasRemoteDescription)
                {
                    _pendingRemoteIceCandidates.Add(ice);
                    return Task.CompletedTask;
                }
            }

            EnsurePeerConnection().addIceCandidate(ToRtcIceCandidate(ice));

            return Task.CompletedTask;
        }

        private void ApplyPendingRemoteIceCandidates(RTCPeerConnection peerConnection)
        {
            List<IceCandidateModel> pending;
            lock (_syncRoot)
            {
                _hasRemoteDescription = true;
                pending = new List<IceCandidateModel>(_pendingRemoteIceCandidates);
                _pendingRemoteIceCandidates.Clear();
            }

            foreach (var candidate in pending)
                peerConnection.addIceCandidate(ToRtcIceCandidate(candidate));
        }

        private static RTCIceCandidateInit ToRtcIceCandidate(IceCandidateModel ice)
        {
            return new RTCIceCandidateInit
            {
                candidate = ice.Candidate,
                sdpMid = ice.Mid,
                sdpMLineIndex = ice.MLineIndex.HasValue ? (ushort)ice.MLineIndex.Value : (ushort)0
            };
        }

        public Task<bool> SendEncodedVideoFrameAsync(byte[] h264Frame)
        {
            return SendEncodedVideoFrameAsync(h264Frame, ScreenShareCodecNames.H264, _settings.RealtimeVideoFps);
        }

        public Task<bool> SendEncodedVideoFrameAsync(byte[] encodedFrame, string codec)
        {
            return SendEncodedVideoFrameAsync(encodedFrame, codec, _settings.RealtimeVideoFps);
        }

        public Task<bool> SendEncodedVideoFrameAsync(byte[] encodedFrame, string codec, int frameRate)
        {
            if (encodedFrame.Length == 0)
                return Task.FromResult(false);

            RTCPeerConnection? peerConnection;
            lock (_syncRoot)
            {
                peerConnection = _peerConnection;
            }

            if (peerConnection == null || _isClosed)
            {
                LogSendDiagnostic("not sent: peer connection is closed or not created", encodedFrame.Length);
                return Task.FromResult(false);
            }

            if (peerConnection.connectionState == RTCPeerConnectionState.closed ||
                peerConnection.connectionState == RTCPeerConnectionState.failed ||
                peerConnection.connectionState == RTCPeerConnectionState.disconnected)
            {
                LogSendDiagnostic($"not sent: connectionState={peerConnection.connectionState}", encodedFrame.Length);
                return Task.FromResult(false);
            }

            if (peerConnection.connectionState != RTCPeerConnectionState.connected)
            {
                LogSendDiagnostic($"not sent: video transport not ready; connectionState={peerConnection.connectionState}", encodedFrame.Length);
                return Task.FromResult(false);
            }

            try
            {
                // SIPSorcery advances the RTP timestamp by this duration for every
                // encoded access unit. The legacy path used a fixed 30 FPS duration
                // even when the H.264 encoder was producing 60 FPS, so the receiver
                // clock ran at half speed and immediately accumulated a backlog.
                var videoFrameDuration = Math.Max(1u, _settings.VideoClockRate / (uint)Math.Clamp(frameRate, 1, 120));
                if (ScreenShareCodecNames.IsAv1(codec))
                {
                    peerConnection.VideoStream.SendAv1Frame(videoFrameDuration, _settings.Av1PayloadType, encodedFrame);
                }
                else
                {
                    peerConnection.VideoStream.SendH264Frame(videoFrameDuration, _settings.H264PayloadType, encodedFrame);
                }

                var sentFrames = Interlocked.Increment(ref _sentVideoFrames);
                if (sentFrames == 1 || sentFrames % (ScreenShareCapturePipeline.TargetFps * 2) == 0)
                    Debug.WriteLine($"[ScreenShare:RTP] {_diagnosticName}: sent {sentFrames} {GetLogCodecName(codec)} frames; bytes={encodedFrame.Length}; connectionState={peerConnection.connectionState}.");
                return Task.FromResult(true);
            }
            catch (Exception ex)
            {
                LogSendDiagnostic($"send failed: {ex.Message}", encodedFrame.Length);
                return Task.FromResult(false);
            }
        }

        public Task CloseAsync()
        {
            RTCPeerConnection? peerConnection;
            lock (_syncRoot)
            {
                peerConnection = _peerConnection;
                _peerConnection = null;
                _hasVideoTrack = false;
                _isClosed = true;
                _hasRemoteDescription = false;
                _pendingRemoteIceCandidates.Clear();
            }

            peerConnection?.close();
            peerConnection?.Dispose();
            return Task.CompletedTask;
        }

        public void RequestVideoKeyFrame(string reason)
        {
            RTCPeerConnection? peerConnection;
            uint mediaSsrc;

            lock (_syncRoot)
            {
                peerConnection = _peerConnection;
                mediaSsrc = _lastRemoteVideoSsrc;
            }

            if (peerConnection == null || _isClosed || mediaSsrc == 0)
            {
                Debug.WriteLine($"[ScreenShare:RTP] {_diagnosticName}: PLI not sent; peer={(peerConnection == null ? "null" : peerConnection.connectionState.ToString())}; closed={_isClosed}; ssrc={mediaSsrc}; reason={reason}.");
                return;
            }

            SendPictureLossIndication(peerConnection, mediaSsrc, reason);
        }

        private RTCPeerConnection EnsurePeerConnection()
        {
            lock (_syncRoot)
            {
                if (_peerConnection != null)
                    return _peerConnection;

                _isClosed = false;
                _peerConnection = new RTCPeerConnection(new RTCConfiguration
                {
                    iceServers = new List<RTCIceServer>
                    {
                        new RTCIceServer
                        {
                            urls = "stun:stun.l.google.com:19302"
                        }
                    }
                });
                var peerConnection = _peerConnection;
                peerConnection.onicecandidate += candidate =>
                {
                    if (candidate == null || string.IsNullOrWhiteSpace(candidate.candidate))
                        return;

                    Debug.WriteLine($"[ScreenShare:RTP] {_diagnosticName}: local ICE candidate ready mid={candidate.sdpMid} index={candidate.sdpMLineIndex}.");
                    LocalIceCandidateReady?.Invoke(this, new IceCandidateModel
                    {
                        Candidate = candidate.candidate,
                        Mid = candidate.sdpMid,
                        MLineIndex = candidate.sdpMLineIndex
                    });
                };
                peerConnection.onsignalingstatechange += () =>
                    Debug.WriteLine($"[ScreenShare:RTP] {_diagnosticName}: signalingState={peerConnection.signalingState}.");
                peerConnection.oniceconnectionstatechange += state =>
                    Debug.WriteLine($"[ScreenShare:RTP] {_diagnosticName}: iceConnectionState={state}.");
                peerConnection.onicegatheringstatechange += state =>
                    Debug.WriteLine($"[ScreenShare:RTP] {_diagnosticName}: iceGatheringState={state}.");
                peerConnection.onconnectionstatechange += state =>
                {
                    Debug.WriteLine($"[ScreenShare:RTP] {_diagnosticName}: connectionState={state}.");
                    ConnectionStateChanged?.Invoke(this, state.ToString());
                };
                peerConnection.OnVideoFrameReceived += (_, _, frame, _) =>
                {
                    var receivedFrames = Interlocked.Increment(ref _receivedVideoFrames);
                    if (receivedFrames == 1 || receivedFrames % (ScreenShareCapturePipeline.TargetFps * 2) == 0)
                    Debug.WriteLine($"[ScreenShare:RTP] {_diagnosticName}: received {receivedFrames} encoded video frames; bytes={frame.Length}.");

                    var codec = Volatile.Read(ref _expectedRemoteVideoCodec) ??
                        Volatile.Read(ref _lastRemoteVideoCodec) ??
                        ScreenShareCodecNames.H264;
                    EncodedVideoFrameReceived?.Invoke(this, new NativeRtpVideoFrameEventArgs(frame, codec));
                };
                peerConnection.OnRtpPacketReceived += (_, mediaType, packet) =>
                {
                    if (mediaType == SDPMediaTypesEnum.video)
                        HandleIncomingVideoRtpPacket(peerConnection, packet);
                };

                return peerConnection;
            }
        }

        private void HandleIncomingVideoRtpPacket(RTCPeerConnection peerConnection, RTPPacket packet)
        {
            var header = packet.Header;
            var mediaSsrc = header.SyncSource;
            var sequenceNumber = header.SequenceNumber;
            ushort? nackPid = null;
            ushort nackBlp = 0;
            var sendPli = false;

            lock (_syncRoot)
            {
                _lastRemoteVideoSsrc = mediaSsrc;
                _lastRemoteVideoCodec = header.PayloadType == _settings.Av1PayloadType
                    ? ScreenShareCodecNames.Av1
                    : ScreenShareCodecNames.H264;

                if (_lastRemoteVideoSequence.HasValue)
                {
                    var expected = unchecked((ushort)(_lastRemoteVideoSequence.Value + 1));
                    var gap = GetForwardSequenceDistance(expected, sequenceNumber);
                    if (gap > 0 && gap <= 32)
                    {
                        nackPid = expected;
                        nackBlp = BuildNackBitmask(gap);
                    }
                    else if (gap > 32 && gap < 0x8000)
                    {
                        sendPli = true;
                    }
                }

                _lastRemoteVideoSequence = sequenceNumber;
            }

            if (nackPid.HasValue)
                SendNack(peerConnection, mediaSsrc, nackPid.Value, nackBlp);

            if (sendPli)
                SendPictureLossIndication(peerConnection, mediaSsrc, "large RTP sequence gap");
        }

        private void SendNack(RTCPeerConnection peerConnection, uint mediaSsrc, ushort packetId, ushort bitmask)
        {
            var now = DateTimeOffset.UtcNow;
            if (now - _lastNackSentUtc < TimeSpan.FromMilliseconds(60))
                return;

            _lastNackSentUtc = now;

            try
            {
                var feedback = new RTCPFeedback(
                    0,
                    mediaSsrc,
                    RTCPFeedbackTypesEnum.NACK,
                    packetId,
                    bitmask);
                peerConnection.SendRtcpFeedback(SDPMediaTypesEnum.video, feedback);
                Interlocked.Increment(ref _sentNackRequests);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ScreenShare:RTP] NACK feedback skipped: {ex.Message}");
            }
        }

        private void SendPictureLossIndication(RTCPeerConnection peerConnection, uint mediaSsrc, string reason)
        {
            var now = DateTimeOffset.UtcNow;
            if (now - _lastPliSentUtc < TimeSpan.FromMilliseconds(500))
                return;

            _lastPliSentUtc = now;

            try
            {
                var feedback = new RTCPFeedback(0, mediaSsrc, PSFBFeedbackTypesEnum.PLI);
                peerConnection.SendRtcpFeedback(SDPMediaTypesEnum.video, feedback);
                Interlocked.Increment(ref _sentPliRequests);
                Debug.WriteLine($"[ScreenShare:RTP] PLI feedback sent: {reason}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ScreenShare:RTP] PLI feedback skipped: {ex.Message}");
            }
        }

        private static int GetForwardSequenceDistance(ushort expected, ushort actual)
        {
            return (actual - expected) & 0xFFFF;
        }

        private static ushort BuildNackBitmask(int gap)
        {
            var bitmask = 0;
            var missingAfterPid = Math.Min(16, gap - 1);
            for (var offset = 0; offset < missingAfterPid; offset++)
                bitmask |= 1 << offset;

            return (ushort)bitmask;
        }

        private void EnsureVideoTrack()
        {
            lock (_syncRoot)
            {
                if (_hasVideoTrack)
                    return;

                var videoFormats = new List<VideoFormat>
                {
                    new VideoFormat(_settings.Av1PayloadType, "AV1", (int)_settings.VideoClockRate, null),
                    new VideoFormat(
                        VideoCodecsEnum.H264,
                        _settings.H264PayloadType,
                        (int)_settings.VideoClockRate,
                        _settings.H264FormatParameters)
                };
                var videoTrack = new MediaStreamTrack(videoFormats, MediaStreamStatusEnum.SendRecv);

                EnsurePeerConnection().addTrack(videoTrack);
                _hasVideoTrack = true;
                Debug.WriteLine($"[ScreenShare:RTP] {_diagnosticName}: added low-latency AV1X/H.264 video track: AV1/{_settings.VideoClockRate}, H.264 {_settings.H264FormatParameters}");
            }
        }

        private static string GetLogCodecName(string codec)
        {
            return ScreenShareCodecNames.IsAv1(codec) ? ScreenShareCodecNames.AV1XDisplayName : "H.264";
        }

        private void LogSendDiagnostic(string message, int byteCount)
        {
            var now = DateTimeOffset.UtcNow;
            if (now - _lastSendDiagnosticUtc < TimeSpan.FromSeconds(1))
                return;

            _lastSendDiagnosticUtc = now;
            Debug.WriteLine($"[ScreenShare:RTP] {_diagnosticName}: {message}; bytes={byteCount}.");
        }

        private static RTCSessionDescriptionInit ToRtcDescription(SessionDescriptionModel description)
        {
            var type = description.Type.Equals("answer", StringComparison.OrdinalIgnoreCase)
                ? RTCSdpType.answer
                : RTCSdpType.offer;

            return new RTCSessionDescriptionInit
            {
                type = type,
                sdp = description.Sdp
            };
        }
    }

    public sealed class NativeRtpVideoFrameEventArgs : EventArgs
    {
        public NativeRtpVideoFrameEventArgs(byte[] frameData)
            : this(frameData, ScreenShareCodecNames.H264)
        {
        }

        public NativeRtpVideoFrameEventArgs(byte[] frameData, string codec)
        {
            FrameData = frameData;
            Codec = codec;
        }

        public byte[] FrameData { get; }
        public string Codec { get; }
    }
}

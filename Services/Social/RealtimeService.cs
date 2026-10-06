using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net.WebSockets;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Zink.Services.Social
{
    public sealed class IncomingCallEventArgs : EventArgs
    {
        public string CallId { get; set; } = "";
        public long FromUserId { get; set; }
        public string FromUsername { get; set; } = "";
        public string FromDisplayName { get; set; } = "";
    }

    public sealed class CallSignalEventArgs : EventArgs
    {
        public string CallId { get; set; } = "";
        public long FromUserId { get; set; }
        public string FromUsername { get; set; } = "";
        public string FromDisplayName { get; set; } = "";
        public string Reason { get; set; } = "";
    }

    public sealed class WebRtcOfferEventArgs : EventArgs
    {
        public string CallId { get; set; } = "";
        public long FromUserId { get; set; }
        public string Sdp { get; set; } = "";
        public string SdpType { get; set; } = "";
    }

    public sealed class WebRtcAnswerEventArgs : EventArgs
    {
        public string CallId { get; set; } = "";
        public long FromUserId { get; set; }
        public string Sdp { get; set; } = "";
        public string SdpType { get; set; } = "";
    }

    public sealed class WebRtcIceEventArgs : EventArgs
    {
        public string CallId { get; set; } = "";
        public long FromUserId { get; set; }
        public string Candidate { get; set; } = "";
        public string? Mid { get; set; }
        public int? MLineIndex { get; set; }
    }

    public sealed class AudioChunkEventArgs : EventArgs
    {
        public string CallId { get; set; } = "";
        public long FromUserId { get; set; }
        public byte[] AudioData { get; set; } = Array.Empty<byte>();
    }

    public sealed class ScreenFrameEventArgs : EventArgs
    {
        public string CallId { get; set; } = "";
        public long FromUserId { get; set; }
        public byte[] FrameData { get; set; } = Array.Empty<byte>();
        public int Width { get; set; }
        public int Height { get; set; }
        public long Timestamp { get; set; }
        public string Codec { get; set; } = "jpeg";
        public bool IsKeyFrame { get; set; }
    }

    public sealed class ScreenShareQosEventArgs : EventArgs
    {
        public string CallId { get; set; } = "";
        public long FromUserId { get; set; }
        public int DroppedReceiveFrames { get; set; }
        public int RenderBacklog { get; set; }
        public string Reason { get; set; } = "";
    }

    public sealed class IncomingMessageEventArgs : EventArgs
    {
        public long FromUserId { get; set; }
        public string FromUsername { get; set; } = "";
        public string FromDisplayName { get; set; } = "";
        public string Message { get; set; } = "";
    }

    public sealed class ScreenShareMetadataEventArgs : EventArgs
    {
        public string CallId { get; set; } = "";
        public long FromUserId { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public long Timestamp { get; set; }
        public string Codec { get; set; } = "h264";
    }

    public sealed class ScreenShareCapabilitiesEventArgs : EventArgs
    {
        public string CallId { get; set; } = "";
        public long FromUserId { get; set; }
        public string[] Codecs { get; set; } = Array.Empty<string>();
        public string Architecture { get; set; } = "unknown";
    }

    public sealed class RealtimeService
    {
        private ClientWebSocket? _ws;
        private CancellationTokenSource? _cts;
        private bool _connected;
        private readonly SemaphoreSlim _connectionLock = new(1, 1);
        private readonly SemaphoreSlim _sendLock = new(1, 1);
        private long _outgoingTextH264Frames;
        private long _outgoingBinaryH264Frames;
        private long _incomingBinaryH264Frames;
        private long _incomingBinaryH264ParseFailures;

        public bool IsConnected => _connected && _ws != null && _ws.State == WebSocketState.Open;

        public int RawIncomingAudioMessages { get; private set; }

        public event EventHandler<IncomingCallEventArgs>? IncomingCall;
        public event EventHandler<CallSignalEventArgs>? CallAnswered;
        public event EventHandler<CallSignalEventArgs>? CallRejected;
        public event EventHandler<CallSignalEventArgs>? CallEnded;

        public event EventHandler<WebRtcOfferEventArgs>? WebRtcOfferReceived;
        public event EventHandler<WebRtcAnswerEventArgs>? WebRtcAnswerReceived;
        public event EventHandler<WebRtcIceEventArgs>? WebRtcIceReceived;

        public event EventHandler<AudioChunkEventArgs>? AudioChunkReceived;
        public event EventHandler<ScreenFrameEventArgs>? ScreenFrameReceived;
        public event EventHandler<ScreenFrameEventArgs>? EncodedScreenFrameReceived;
        public event EventHandler<(string CallId, long FromUserId, int Width, int Height, string Codec)>? ScreenShareStarted;
        public event EventHandler<(string CallId, long FromUserId)>? ScreenShareStopped;
        public event EventHandler<ScreenShareMetadataEventArgs>? ScreenShareMetadataReceived;
        public event EventHandler<ScreenShareCapabilitiesEventArgs>? ScreenShareCapabilitiesReceived;
        public event EventHandler<ScreenShareQosEventArgs>? ScreenShareQosReceived;
        public event EventHandler<int>? RawIncomingAudioMessageCountChanged;
        public event EventHandler<IncomingMessageEventArgs>? IncomingMessage;

        public async Task ConnectAsync()
        {
            await _connectionLock.WaitAsync();
            try
            {
                if (IsConnected)
                    return;

                var token = await Calling.TokenStore.Instance.GetTokenAsync();
                if (string.IsNullOrWhiteSpace(token))
                    throw new Exception("No auth token found.");

                await DisconnectAsync();

                RawIncomingAudioMessages = 0;

                _cts = new CancellationTokenSource();
                _ws = new ClientWebSocket();

                var uri = new Uri(Calling.CallServerConfig.GetWebSocketUrl(token));

                try
                {
                    await _ws.ConnectAsync(uri, CancellationToken.None);
                    _connected = true;
                    _ = ReceiveLoopAsync(_ws, _cts.Token);
                }
                catch (WebSocketException ex)
                {
                    _connected = false;
                    throw new Exception($"Realtime websocket failed: {ex.Message}", ex);
                }
            }
            finally
            {
                _connectionLock.Release();
            }
        }

        public async Task DisconnectAsync()
        {
            _connected = false;
            RawIncomingAudioMessages = 0;

            try
            {
                _cts?.Cancel();
            }
            catch
            {
            }

            if (_ws != null)
            {
                try
                {
                    if (_ws.State == WebSocketState.Open || _ws.State == WebSocketState.CloseReceived)
                    {
                        await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", CancellationToken.None);
                    }
                }
                catch
                {
                }

                _ws.Dispose();
                _ws = null;
            }

            _cts?.Dispose();
            _cts = null;
        }

        private async Task ReceiveLoopAsync(ClientWebSocket socket, CancellationToken cancellationToken)
        {
            var buffer = new byte[64 * 1024];

            try
            {
                while (!cancellationToken.IsCancellationRequested && socket.State == WebSocketState.Open)
                {
                    using var ms = new System.IO.MemoryStream();

                    WebSocketReceiveResult result;
                    do
                    {
                        result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken);

                        if (result.MessageType == WebSocketMessageType.Close)
                        {
                            if (ReferenceEquals(_ws, socket))
                                _connected = false;
                            return;
                        }

                        ms.Write(buffer, 0, result.Count);
                    }
                    while (!result.EndOfMessage);

                    var messageBytes = ms.ToArray();

                    if (result.MessageType == WebSocketMessageType.Binary)
                    {
                        HandleBinaryMessage(messageBytes);
                        continue;
                    }

                    var json = Encoding.UTF8.GetString(messageBytes);

                    SignalEnvelope? msg;
                    try
                    {
                        msg = JsonSerializer.Deserialize<SignalEnvelope>(json);
                    }
                    catch
                    {
                        continue;
                    }

                    if (msg == null || string.IsNullOrWhiteSpace(msg.Type))
                        continue;

                    switch (msg.Type)
                    {
                        case "incoming-call":
                            IncomingCall?.Invoke(this, new IncomingCallEventArgs
                            {
                                CallId = msg.CallId ?? "",
                                FromUserId = msg.FromUserId,
                                FromUsername = msg.FromUsername ?? "",
                                FromDisplayName = msg.FromDisplayName ?? ""
                            });
                            break;

                        case "screen-share-capabilities":
                            ScreenShareCapabilitiesReceived?.Invoke(this, new ScreenShareCapabilitiesEventArgs
                            {
                                CallId = msg.CallId ?? "",
                                FromUserId = msg.FromUserId,
                                Codecs = msg.ScreenCodecs ?? Array.Empty<string>(),
                                Architecture = msg.ScreenArchitecture ?? "unknown"
                            });
                            break;

                        case "call-accepted":
                            CallAnswered?.Invoke(this, CreateCallSignalEventArgs(msg));
                            break;

                        case "call-declined":
                            CallRejected?.Invoke(this, CreateCallSignalEventArgs(msg));
                            break;

                        case "call-ended":
                            CallEnded?.Invoke(this, CreateCallSignalEventArgs(msg));
                            break;

                        case "message":
                        case "chat-message":
                        case "incoming-message":
                            IncomingMessage?.Invoke(this, new IncomingMessageEventArgs
                            {
                                FromUserId = msg.FromUserId,
                                FromUsername = msg.FromUsername ?? "",
                                FromDisplayName = msg.FromDisplayName ?? "",
                                Message = msg.Message ?? msg.DebugMessage ?? ""
                            });
                            break;

                        case "webrtc-offer":
                            WebRtcOfferReceived?.Invoke(this, new WebRtcOfferEventArgs
                            {
                                CallId = msg.CallId ?? "",
                                FromUserId = msg.FromUserId,
                                Sdp = msg.Sdp ?? "",
                                SdpType = msg.SdpType ?? "offer"
                            });
                            break;

                        case "webrtc-answer":
                            WebRtcAnswerReceived?.Invoke(this, new WebRtcAnswerEventArgs
                            {
                                CallId = msg.CallId ?? "",
                                FromUserId = msg.FromUserId,
                                Sdp = msg.Sdp ?? "",
                                SdpType = msg.SdpType ?? "answer"
                            });
                            break;

                        case "webrtc-ice":
                            WebRtcIceReceived?.Invoke(this, new WebRtcIceEventArgs
                            {
                                CallId = msg.CallId ?? "",
                                FromUserId = msg.FromUserId,
                                Candidate = msg.Candidate ?? "",
                                Mid = msg.Mid,
                                MLineIndex = msg.MLineIndex
                            });
                            break;

                        case "audio-chunk":
                            RawIncomingAudioMessages++;
                            RawIncomingAudioMessageCountChanged?.Invoke(this, RawIncomingAudioMessages);

                            if (!string.IsNullOrWhiteSpace(msg.AudioBase64))
                            {
                                try
                                {
                                    var bytes = Convert.FromBase64String(msg.AudioBase64);
                                    AudioChunkReceived?.Invoke(this, new AudioChunkEventArgs
                                    {
                                        CallId = msg.CallId ?? "",
                                        FromUserId = msg.FromUserId,
                                        AudioData = bytes
                                    });
                                }

                                catch
                                {
                                }
                            }
                            break;

                        case "screen-share-started":
                            ScreenShareStarted?.Invoke(this, (
                                msg.CallId ?? "",
                                msg.FromUserId,
                                msg.ScreenFrameWidth ?? msg.ScreenWidth ?? 0,
                                msg.ScreenFrameHeight ?? msg.ScreenHeight ?? 0,
                                msg.ScreenCodec ?? "h264"));
                            break;

                        case "screen-share-stopped":
                            ScreenShareStopped?.Invoke(this, (msg.CallId ?? "", msg.FromUserId));
                            break;

                        case "screen-frame":
                            if (!string.IsNullOrWhiteSpace(msg.ScreenFrameBase64))
                            {
                                try
                                {
                                    var bytes = Convert.FromBase64String(msg.ScreenFrameBase64);
                                    Debug.WriteLine($"[ScreenShare:PREVIEW:IN] from={msg.FromUserId} {msg.ScreenFrameWidth ?? msg.ScreenWidth ?? 0}x{msg.ScreenFrameHeight ?? msg.ScreenHeight ?? 0} bytes={bytes.Length}");
                                    ScreenFrameReceived?.Invoke(this, new ScreenFrameEventArgs
                                    {
                                        CallId = msg.CallId ?? "",
                                        FromUserId = msg.FromUserId,
                                        FrameData = bytes,
                                        Width = msg.ScreenFrameWidth ?? msg.ScreenWidth ?? 0,
                                        Height = msg.ScreenFrameHeight ?? msg.ScreenHeight ?? 0,
                                        Timestamp = msg.ScreenFrameTimestamp ?? msg.TimestampUtcMs ?? 0
                                    });
                                }
                                catch (Exception ex)
                                {
                                    Debug.WriteLine($"[ScreenShare:PREVIEW:IN] Failed to parse incoming preview frame from {msg.FromUserId}: {ex}");
                                }
                            }
                            break;

                        case "screen-frame-h264":
                            if (!string.IsNullOrWhiteSpace(msg.ScreenFrameBase64))
                            {
                                try
                                {
                                    var bytes = Convert.FromBase64String(msg.ScreenFrameBase64);
                                    Debug.WriteLine($"[ScreenShare:H264:TEXT:IN] from={msg.FromUserId} {msg.ScreenFrameWidth ?? msg.ScreenWidth ?? 0}x{msg.ScreenFrameHeight ?? msg.ScreenHeight ?? 0} bytes={bytes.Length}");
                                    EncodedScreenFrameReceived?.Invoke(this, new ScreenFrameEventArgs
                                    {
                                        CallId = msg.CallId ?? "",
                                        FromUserId = msg.FromUserId,
                                        FrameData = bytes,
                                        Width = msg.ScreenFrameWidth ?? msg.ScreenWidth ?? 0,
                                        Height = msg.ScreenFrameHeight ?? msg.ScreenHeight ?? 0,
                                        Timestamp = msg.ScreenFrameTimestamp ?? msg.TimestampUtcMs ?? 0,
                                        Codec = msg.ScreenCodec ?? "h264",
                                        IsKeyFrame = msg.ScreenFrameIsKeyFrame ?? false
                                    });
                                }
                                catch (Exception ex)
                                {
                                    Debug.WriteLine($"[ScreenShare:H264:TEXT:IN] Failed to parse incoming H.264 frame from {msg.FromUserId}: {ex}");
                                }
                            }
                            break;

                        case "screen-frame-h264-ack":
                            Debug.WriteLine($"[ScreenShare:H264:ACK] {msg.DebugMessage}");
                            break;

                        case "screen-share-metadata":
                            ScreenShareMetadataReceived?.Invoke(this, new ScreenShareMetadataEventArgs
                            {
                                CallId = msg.CallId ?? "",
                                FromUserId = msg.FromUserId,
                                Width = msg.ScreenFrameWidth ?? msg.ScreenWidth ?? 0,
                                Height = msg.ScreenFrameHeight ?? msg.ScreenHeight ?? 0,
                                Timestamp = msg.ScreenFrameTimestamp ?? msg.TimestampUtcMs ?? 0,
                                Codec = msg.ScreenCodec ?? "h264"
                            });
                            break;

                        case "screen-share-qos":
                            ScreenShareQosReceived?.Invoke(this, new ScreenShareQosEventArgs
                            {
                                CallId = msg.CallId ?? "",
                                FromUserId = msg.FromUserId,
                                DroppedReceiveFrames = msg.QosDroppedReceiveFrames ?? 0,
                                RenderBacklog = msg.QosRenderBacklog ?? 0,
                                Reason = msg.QosReason ?? "receiver feedback"
                            });
                            break;
                    }
                }
            }
            catch
            {
                if (ReferenceEquals(_ws, socket))
                    _connected = false;
            }
        }

        private void HandleBinaryMessage(byte[] data)
        {
            if (!BinaryScreenFrameProtocol.TryReadH264Frame(data, out var frame))
            {
                var failures = Interlocked.Increment(ref _incomingBinaryH264ParseFailures);
                if (failures == 1 || failures % 30 == 0)
                    Debug.WriteLine($"[ScreenShare:H264:BINARY:IN] Failed to parse binary WebSocket frame; bytes={data.Length}; failures={failures}.");
                return;
            }

            var received = Interlocked.Increment(ref _incomingBinaryH264Frames);
            if (received == 1 || received % 120 == 0)
                Debug.WriteLine($"[ScreenShare:{frame.Codec.ToUpperInvariant()}:BINARY:IN] from={frame.UserId} {frame.Width}x{frame.Height} bytes={frame.Payload.Length} key={frame.IsKeyFrame}; count={received}.");

            EncodedScreenFrameReceived?.Invoke(this, new ScreenFrameEventArgs
            {
                CallId = frame.CallId,
                FromUserId = frame.UserId,
                FrameData = frame.Payload,
                Width = frame.Width,
                Height = frame.Height,
                Timestamp = frame.Timestamp,
                Codec = frame.Codec,
                IsKeyFrame = frame.IsKeyFrame
            });
        }

        public async Task CallUserAsync(long userId, string callId)
        {
            if (!IsConnected)
                await ConnectAsync();

            await SendAsync(new SignalEnvelope
            {
                Type = "call-invite",
                TargetUserId = userId,
                CallId = callId
            });
        }

        public async Task AcceptCallAsync(long userId, string callId)
        {
            if (!IsConnected)
                await ConnectAsync();

            await SendAsync(new SignalEnvelope
            {
                Type = "call-accept",
                TargetUserId = userId,
                CallId = callId
            });
        }

        public async Task RejectCallAsync(long userId, string callId)
        {
            if (!IsConnected)
                await ConnectAsync();

            await SendAsync(new SignalEnvelope
            {
                Type = "call-decline",
                TargetUserId = userId,
                CallId = callId
            });
        }

        public async Task EndCallAsync(long userId, string callId, string? reason = null)
        {
            if (!IsConnected)
                return;

            await SendAsync(new SignalEnvelope
            {
                Type = "call-ended",
                TargetUserId = userId,
                CallId = callId,
                DebugMessage = reason
            });
        }

        public async Task SendOfferAsync(long userId, string callId, string sdp, string sdpType)
        {
            if (!IsConnected)
                await ConnectAsync();

            await SendAsync(new SignalEnvelope
            {
                Type = "webrtc-offer",
                TargetUserId = userId,
                CallId = callId,
                Sdp = sdp,
                SdpType = sdpType
            });
        }

        public async Task SendAnswerAsync(long userId, string callId, string sdp, string sdpType)
        {
            if (!IsConnected)
                await ConnectAsync();

            await SendAsync(new SignalEnvelope
            {
                Type = "webrtc-answer",
                TargetUserId = userId,
                CallId = callId,
                Sdp = sdp,
                SdpType = sdpType
            });
        }

        public async Task SendIceCandidateAsync(long userId, string callId, string candidate, string? mid, int? mLineIndex)
        {
            if (!IsConnected)
                await ConnectAsync();

            await SendAsync(new SignalEnvelope
            {
                Type = "webrtc-ice",
                TargetUserId = userId,
                CallId = callId,
                Candidate = candidate,
                Mid = mid,
                MLineIndex = mLineIndex
            });
        }

        public async Task SendAudioChunkAsync(long userId, string callId, byte[] audioData)
        {
            if (!IsConnected)
                await ConnectAsync();

            if (audioData == null || audioData.Length == 0)
                return;

            await SendAsync(new SignalEnvelope
            {
                Type = "audio-chunk",
                TargetUserId = userId,
                CallId = callId,
                AudioBase64 = Convert.ToBase64String(audioData)
            });
        }

        public async Task SendScreenShareStartedAsync(long userId, string callId, int width = 0, int height = 0, string codec = "h264")
        {
            if (!IsConnected)
                await ConnectAsync();

            await SendAsync(new SignalEnvelope
            {
                Type = "screen-share-started",
                TargetUserId = userId,
                CallId = callId,
                ScreenFrameWidth = width,
                ScreenFrameHeight = height,
                ScreenWidth = width,
                ScreenHeight = height,
                ScreenCodec = codec
            });
        }

        public async Task SendScreenShareCapabilitiesAsync(
            long userId,
            string callId,
            IReadOnlyList<string> codecs,
            string architecture)
        {
            if (!IsConnected)
                await ConnectAsync();

            await SendAsync(new SignalEnvelope
            {
                Type = "screen-share-capabilities",
                TargetUserId = userId,
                CallId = callId,
                ScreenCodecs = codecs.ToArray(),
                ScreenArchitecture = architecture
            });
        }

        public async Task SendScreenShareStoppedAsync(long userId, string callId)
        {
            if (!IsConnected)
                await ConnectAsync();

            await SendAsync(new SignalEnvelope
            {
                Type = "screen-share-stopped",
                TargetUserId = userId,
                CallId = callId
            });
        }

        public async Task SendScreenShareQosAsync(
            long userId,
            string callId,
            int droppedReceiveFrames,
            int renderBacklog,
            string reason)
        {
            if (!IsConnected)
                await ConnectAsync();

            await SendAsync(new SignalEnvelope
            {
                Type = "screen-share-qos",
                TargetUserId = userId,
                CallId = callId,
                QosDroppedReceiveFrames = droppedReceiveFrames,
                QosRenderBacklog = renderBacklog,
                QosReason = reason
            });
        }

        public async Task SendScreenShareMetadataAsync(
            long userId,
            string callId,
            int width,
            int height,
            long timestamp,
            string codec)
        {
            if (!IsConnected)
                await ConnectAsync();

            await SendAsync(new SignalEnvelope
            {
                Type = "screen-share-metadata",
                TargetUserId = userId,
                CallId = callId,
                ScreenFrameWidth = width,
                ScreenFrameHeight = height,
                ScreenFrameTimestamp = timestamp,
                ScreenWidth = width,
                ScreenHeight = height,
                TimestampUtcMs = timestamp,
                ScreenCodec = codec
            });
        }

        public async Task SendScreenFrameAsync(long userId, string callId, byte[] frameData, int width, int height, long timestamp)
        {
            if (!IsConnected)
                await ConnectAsync();

            if (frameData == null || frameData.Length == 0)
                return;

            await SendAsync(new SignalEnvelope
            {
                Type = "screen-frame",
                TargetUserId = userId,
                CallId = callId,
                ScreenFrameBase64 = Convert.ToBase64String(frameData),
                ScreenFrameWidth = width,
                ScreenFrameHeight = height,
                ScreenFrameTimestamp = timestamp,
                ScreenWidth = width,
                ScreenHeight = height,
                TimestampUtcMs = timestamp
            });
        }

        public async Task SendEncodedScreenFrameAsync(
            long userId,
            string callId,
            byte[] frameData,
            int width,
            int height,
            long timestamp,
            string codec,
            bool isKeyFrame)
        {
            if (!IsConnected)
                await ConnectAsync();

            if (frameData == null || frameData.Length == 0)
                return;

            var isH264 = codec.Equals("h264", StringComparison.OrdinalIgnoreCase);
            if (isH264)
            {
                var sent = Interlocked.Increment(ref _outgoingTextH264Frames);
                if (sent == 1 || sent % 120 == 0)
                    Debug.WriteLine($"[ScreenShare:H264:TEXT:OUT] target={userId} {width}x{height} bytes={frameData.Length} key={isKeyFrame}; count={sent}.");
            }

            await SendAsync(new SignalEnvelope
            {
                Type = isH264 ? "screen-frame-h264" : "screen-frame-encoded",
                TargetUserId = userId,
                CallId = callId,
                ScreenFrameBase64 = Convert.ToBase64String(frameData),
                ScreenFrameWidth = width,
                ScreenFrameHeight = height,
                ScreenFrameTimestamp = timestamp,
                ScreenWidth = width,
                ScreenHeight = height,
                TimestampUtcMs = timestamp,
                ScreenCodec = codec,
                ScreenFrameIsKeyFrame = isKeyFrame
            });
        }

        public async Task SendEncodedScreenFrameBinaryAsync(
            long userId,
            string callId,
            byte[] frameData,
            int width,
            int height,
            long timestamp,
            bool isKeyFrame)
        {
            if (!IsConnected)
                await ConnectAsync();

            if (frameData == null || frameData.Length == 0)
                return;

            var data = BinaryScreenFrameProtocol.CreateH264Frame(
                userId,
                callId,
                width,
                height,
                timestamp,
                isKeyFrame,
                frameData);

            var sent = Interlocked.Increment(ref _outgoingBinaryH264Frames);
            if (sent == 1 || sent % 120 == 0)
            {
                Debug.WriteLine(
                    $"[ScreenShare:H264:BINARY:OUT] target={userId} {width}x{height} bytes={frameData.Length} key={isKeyFrame}; websocketBytes={data.Length}; count={sent}.");
            }

            await SendBinaryAsync(data);
        }

        public async Task SendEncodedScreenFrameBinaryAsync(
            long userId,
            string callId,
            string codec,
            byte[] frameData,
            int width,
            int height,
            long timestamp,
            bool isKeyFrame)
        {
            if (!IsConnected)
                await ConnectAsync();

            if (frameData == null || frameData.Length == 0)
                return;

            var safeCodec = string.IsNullOrWhiteSpace(codec) ? "h264" : codec.Trim().ToLowerInvariant();
            var data = BinaryScreenFrameProtocol.CreateEncodedFrame(
                userId,
                callId,
                safeCodec,
                width,
                height,
                timestamp,
                isKeyFrame,
                frameData);

            var sent = Interlocked.Increment(ref _outgoingBinaryH264Frames);
            if (sent == 1 || sent % 120 == 0)
            {
                Debug.WriteLine(
                    $"[ScreenShare:{safeCodec.ToUpperInvariant()}:BINARY:OUT] target={userId} {width}x{height} bytes={frameData.Length} key={isKeyFrame}; websocketBytes={data.Length}; count={sent}.");
            }

            await SendBinaryAsync(data);
        }

        private async Task SendAsync(SignalEnvelope obj)
        {
            await _sendLock.WaitAsync();
            try
            {
                if (_ws == null || _ws.State != WebSocketState.Open)
                    throw new Exception("Realtime connection is not open.");

                var json = JsonSerializer.Serialize(obj);
                var bytes = Encoding.UTF8.GetBytes(json);

                await _ws.SendAsync(
                    new ArraySegment<byte>(bytes),
                    WebSocketMessageType.Text,
                    true,
                    CancellationToken.None);
            }
            finally
            {
                _sendLock.Release();
            }
        }

        private async Task SendBinaryAsync(byte[] data)
        {
            await _sendLock.WaitAsync();
            try
            {
                if (_ws == null || _ws.State != WebSocketState.Open)
                    throw new Exception("Realtime connection is not open.");

                await _ws.SendAsync(
                    new ArraySegment<byte>(data),
                    WebSocketMessageType.Binary,
                    true,
                    CancellationToken.None);
            }
            finally
            {
                _sendLock.Release();
            }
        }

        private static CallSignalEventArgs CreateCallSignalEventArgs(SignalEnvelope msg)
        {
            return new CallSignalEventArgs
            {
                CallId = msg.CallId ?? "",
                FromUserId = msg.FromUserId,
                FromUsername = msg.FromUsername ?? "",
                FromDisplayName = msg.FromDisplayName ?? "",
                Reason = msg.DebugMessage ?? ""
            };
        }
    }

    public sealed class SignalEnvelope
    {
        public string Type { get; set; } = "";
        public string? CallId { get; set; }
        public long FromUserId { get; set; }
        public string? FromUsername { get; set; }
        public string? FromDisplayName { get; set; }
        public long TargetUserId { get; set; }

        public string? Sdp { get; set; }
        public string? SdpType { get; set; }
        public string? Candidate { get; set; }
        public string? Mid { get; set; }
        public int? MLineIndex { get; set; }

        public string? AudioBase64 { get; set; }
        public string? ScreenFrameBase64 { get; set; }
        public int? ScreenFrameWidth { get; set; }
        public int? ScreenFrameHeight { get; set; }
        public long? ScreenFrameTimestamp { get; set; }
        public int? ScreenWidth { get; set; }
        public int? ScreenHeight { get; set; }
        public long? TimestampUtcMs { get; set; }
        public string? ScreenCodec { get; set; }
        public string[]? ScreenCodecs { get; set; }
        public string? ScreenArchitecture { get; set; }
        public bool? ScreenFrameIsKeyFrame { get; set; }
        public string? DebugMessage { get; set; }
        public string? Message { get; set; }
        public int? QosDroppedReceiveFrames { get; set; }
        public int? QosRenderBacklog { get; set; }
        public string? QosReason { get; set; }
    }
}

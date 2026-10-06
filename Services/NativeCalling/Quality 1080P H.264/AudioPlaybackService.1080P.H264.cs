using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using ConcentusDecoder = Concentus.Structs.OpusDecoder;
using NAudio.Wave;

namespace Zink.Services.NativeCalling.Quality1080p
{
    public sealed class AudioPlaybackService
    {
        public sealed class OutputDeviceInfo
        {
            public int DeviceNumber { get; init; }
            public string Name { get; init; } = string.Empty;

            public override string ToString() => Name;
        }

        public static AudioPlaybackService Instance => ScreenShareServiceSelector.Active.AudioPlayback;

        private readonly object _gate = new();
        private readonly Queue<byte[]> _packetQueue = new();
        private readonly Queue<byte[]> _mediaPacketQueue = new();
        private readonly ConcurrentQueue<QueuedAudioPacket> _queuedPlaybackPackets = new();

        private BufferedWaveProvider? _buffer;
        private BufferedWaveProvider? _mediaBuffer;
        private WaveOut? _output;
        private WaveOut? _mediaOutput;
        private readonly ConcentusDecoder _decoder;
        private readonly ConcentusDecoder _mediaDecoder;

        private const int SampleRate = 16000;
        private const int Channels = 1;
        private const int FrameSize = 320;
        private const int MediaSampleRate = 48000;
        private const int MediaChannels = 2;
        private const int MediaFrameSize = 960;
        private const int MaxBufferedPackets = 8;
        // A 20 ms Opus packet can arrive in short scheduler/network bursts. Keep
        // enough compressed reserve to absorb a 400 ms burst instead of dropping
        // audio while WaveOut is between refill callbacks.
        private const int StartPlaybackPacketThreshold = 2;
        private const int VoiceTargetBufferedMs = 120;
        private const int VoiceHardTrimBufferedMs = 360;
        // Keep enough decoded PCM ready for normal network jitter without also
        // accumulating a second, much larger queue of compressed Opus packets.
        // Keep the decoded buffer close to the actual output latency.  The old
        // 140 ms target stayed full, so incoming Opus accumulated in the
        // compressed queue and was periodically discarded during small receive
        // bursts.  Those drops became audible after a few minutes of sharing.
        private readonly ScreenShareAudioPlaybackSettings _screenShareSettings;
        private const float VoicePlaybackGain = 1.8f;
        // Screen-share audio is already full-scale media. Extra gain clipped
        // transients and made packet-boundary disturbances sound like crackles.
        private const float MediaPlaybackGain = 1.0f;
        private static readonly byte[] ScreenShareAudioHeader = { (byte)'Z', (byte)'S', (byte)'A', (byte)'1' };

        private int _selectedOutputDeviceNumber = -1;
        private bool _isPrimed;
        private bool _isMediaPrimed;
        private long _mediaPacketsReceived;
        private long _mediaPacketsDropped;
        private long _mediaPacketsDecoded;
        private bool _mediaDiscontinuityPending;
        private bool _hasLastMediaSamples;
        private short _lastMediaSampleLeft;
        private short _lastMediaSampleRight;
        private DateTimeOffset _lastMediaPlaybackLogUtc = DateTimeOffset.MinValue;
        private long _queuedPlaybackGeneration;
        private int _queuedPlaybackPumpRunning;

        public int SelectedOutputDeviceNumber
        {
            get
            {
                lock (_gate)
                {
                    return _selectedOutputDeviceNumber;
                }
            }
        }

        internal AudioPlaybackService(ScreenShareAudioPlaybackSettings screenShareSettings)
        {
            _screenShareSettings = screenShareSettings;
            _decoder = new ConcentusDecoder(SampleRate, Channels);
            _mediaDecoder = new ConcentusDecoder(MediaSampleRate, MediaChannels);
        }

        public IReadOnlyList<OutputDeviceInfo> GetOutputDevices()
        {
            var devices = new List<OutputDeviceInfo>
            {
                new OutputDeviceInfo
                {
                    DeviceNumber = -1,
                    Name = "Default"
                }
            };

            for (int i = 0; i < WaveOut.DeviceCount; i++)
            {
                var capabilities = WaveOut.GetCapabilities(i);

                devices.Add(new OutputDeviceInfo
                {
                    DeviceNumber = i,
                    Name = capabilities.ProductName
                });
            }

            return devices;
        }

        public bool SetOutputDevice(int deviceNumber)
        {
            lock (_gate)
            {
                if (deviceNumber < -1 || deviceNumber >= WaveOut.DeviceCount)
                    return false;

                bool wasRunning = _output != null || _mediaOutput != null;

                if (wasRunning)
                    StopPlaybackLocked();

                _selectedOutputDeviceNumber = deviceNumber;

                if (wasRunning)
                    StartPlaybackLocked();

                return true;
            }
        }

        public string GetCurrentOutputDisplayName()
        {
            var devices = GetOutputDevices();
            var current = devices.FirstOrDefault(x => x.DeviceNumber == SelectedOutputDeviceNumber);
            return current?.Name ?? "Default";
        }

        public void Start()
        {
            lock (_gate)
            {
                if (_output != null)
                    return;

                StartPlaybackLocked();
            }
        }

        public void Stop()
        {
            Interlocked.Increment(ref _queuedPlaybackGeneration);
            while (_queuedPlaybackPackets.TryDequeue(out _))
            {
            }

            lock (_gate)
            {
                StopPlaybackLocked();
            }
        }

        /// <summary>
        /// Preserves packet order while keeping device startup and Opus decoding off
        /// the realtime receive callback (which may also be presenting video).
        /// </summary>
        public void QueuePlayback(byte[] data)
        {
            if (data == null || data.Length == 0)
                return;

            _queuedPlaybackPackets.Enqueue(new QueuedAudioPacket(
                Volatile.Read(ref _queuedPlaybackGeneration),
                data));
            StartQueuedPlaybackPump();
        }

        private void StartQueuedPlaybackPump()
        {
            if (Interlocked.CompareExchange(ref _queuedPlaybackPumpRunning, 1, 0) != 0)
                return;

            _ = Task.Run(ProcessQueuedPlayback);
        }

        private void ProcessQueuedPlayback()
        {
            try
            {
                while (_queuedPlaybackPackets.TryDequeue(out var queuedPacket))
                {
                    try
                    {
                        lock (_gate)
                        {
                            if (queuedPacket.Generation != Volatile.Read(ref _queuedPlaybackGeneration))
                                continue;

                            if (_output == null)
                                StartPlaybackLocked();

                            // Monitor locks are re-entrant; keeping the generation
                            // check and playback under this lock prevents Stop() from
                            // racing a stale packet into restarting the audio device.
                            Play(queuedPacket.Data);
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[AudioPlayback] Queued playback failed: {ex.Message}");
                    }
                }
            }
            finally
            {
                Interlocked.Exchange(ref _queuedPlaybackPumpRunning, 0);
                if (!_queuedPlaybackPackets.IsEmpty)
                    StartQueuedPlaybackPump();
            }
        }

        private void StartPlaybackLocked()
        {
            var format = new WaveFormat(SampleRate, 16, Channels);

            _buffer = new BufferedWaveProvider(format, TimeSpan.FromMilliseconds(450))
            {
                DiscardOnBufferOverflow = true,
                ReadFully = true
            };

            _output = new WaveOut
            {
                BufferMilliseconds = 40,
                DeviceNumber = _selectedOutputDeviceNumber
            };

            _output.Init(_buffer);
            _output.Play();

            _mediaBuffer = new BufferedWaveProvider(
                new WaveFormat(MediaSampleRate, 16, MediaChannels),
                TimeSpan.FromMilliseconds(420))
            {
                DiscardOnBufferOverflow = true,
                ReadFully = true
            };

            _mediaOutput = new WaveOut
            {
                BufferMilliseconds = _screenShareSettings.DesiredLatencyMs,
                DeviceNumber = _selectedOutputDeviceNumber
            };

            _mediaOutput.Init(_mediaBuffer);
            _mediaOutput.Play();

            _packetQueue.Clear();
            _mediaPacketQueue.Clear();
            _isPrimed = false;
            _isMediaPrimed = false;
            _mediaPacketsReceived = 0;
            _mediaPacketsDropped = 0;
            _mediaPacketsDecoded = 0;
            _mediaDiscontinuityPending = false;
            _hasLastMediaSamples = false;
            _lastMediaPlaybackLogUtc = DateTimeOffset.MinValue;
        }

        private void StopPlaybackLocked()
        {
            try
            {
                _output?.Stop();
                _output?.Dispose();
                _output = null;
                _buffer = null;
                _mediaOutput?.Stop();
                _mediaOutput?.Dispose();
                _mediaOutput = null;
                _mediaBuffer = null;

                _packetQueue.Clear();
                _mediaPacketQueue.Clear();
                _isPrimed = false;
                _isMediaPrimed = false;
                _mediaDiscontinuityPending = false;
                _hasLastMediaSamples = false;
                _lastMediaPlaybackLogUtc = DateTimeOffset.MinValue;
            }
            finally
            {
                AudioActivityService.Instance.UpdateRemoteLevel(0);
            }
        }

        public void Play(byte[] data)
        {
            if (data == null || data.Length == 0)
            {
                AudioActivityService.Instance.UpdateRemoteLevel(0);
                return;
            }

            lock (_gate)
            {
                if (TryUnwrapScreenShareAudioPacket(data, out var mediaPacket))
                {
                    PlayMediaLocked(mediaPacket);
                    return;
                }

                if (_buffer == null)
                {
                    AudioActivityService.Instance.UpdateRemoteLevel(0);
                    return;
                }

                _packetQueue.Enqueue(data);

                while (_packetQueue.Count > MaxBufferedPackets)
                    _packetQueue.Dequeue();

                if (!_isPrimed)
                {
                    if (_packetQueue.Count < StartPlaybackPacketThreshold)
                    {
                        AudioActivityService.Instance.UpdateRemoteLevel(0);
                        return;
                    }

                    _isPrimed = true;
                }

                if (_buffer.BufferedDuration.TotalMilliseconds > VoiceHardTrimBufferedMs)
                {
                    _buffer.ClearBuffer();

                    while (_packetQueue.Count > StartPlaybackPacketThreshold)
                        _packetQueue.Dequeue();
                }

                while (_packetQueue.Count > 0 && _buffer.BufferedDuration.TotalMilliseconds < VoiceTargetBufferedMs)
                {
                    var packet = _packetQueue.Dequeue();
                    DecodeAndQueuePacket(packet);
                }
            }
        }

        public static bool IsScreenShareAudioPacket(byte[] data)
        {
            if (data == null || data.Length <= ScreenShareAudioHeader.Length)
                return false;

            for (var i = 0; i < ScreenShareAudioHeader.Length; i++)
            {
                if (data[i] != ScreenShareAudioHeader[i])
                    return false;
            }

            return true;
        }

        private void PlayMediaLocked(byte[] data)
        {
            if (_mediaBuffer == null)
            {
                AudioActivityService.Instance.UpdateRemoteLevel(0);
                return;
            }

            _mediaPacketsReceived++;
            _mediaPacketQueue.Enqueue(data);
            while (_mediaPacketQueue.Count > _screenShareSettings.MaxBufferedMediaPackets)
            {
                _mediaPacketQueue.Dequeue();
                _mediaPacketsDropped++;
                _mediaDiscontinuityPending = true;
            }

            if (!_isMediaPrimed)
            {
                if (_mediaPacketQueue.Count < _screenShareSettings.StartPlaybackPacketThreshold)
                    return;

                _isMediaPrimed = true;
            }

            if (_mediaBuffer.BufferedDuration.TotalMilliseconds > _screenShareSettings.HardTrimBufferedMs)
            {
                _mediaBuffer.ClearBuffer();
                while (_mediaPacketQueue.Count > _screenShareSettings.StartPlaybackPacketThreshold)
                {
                    _mediaPacketQueue.Dequeue();
                    _mediaPacketsDropped++;
                    _mediaDiscontinuityPending = true;
                }

                _isMediaPrimed = _mediaPacketQueue.Count >= _screenShareSettings.StartPlaybackPacketThreshold;
            }

            while (_mediaPacketQueue.Count > 0 && _mediaBuffer.BufferedDuration.TotalMilliseconds < _screenShareSettings.TargetBufferedMs)
            {
                DecodeAndQueueMediaPacket(_mediaPacketQueue.Dequeue());
            }

            var now = DateTimeOffset.UtcNow;
            if (now - _lastMediaPlaybackLogUtc >= TimeSpan.FromSeconds(2))
            {
                _lastMediaPlaybackLogUtc = now;
                Debug.WriteLine(
                    $"[ScreenShare:AudioPlayback] media received={_mediaPacketsReceived}; decoded={_mediaPacketsDecoded}; dropped={_mediaPacketsDropped}; packetQueue={_mediaPacketQueue.Count}; bufferedMs={_mediaBuffer.BufferedDuration.TotalMilliseconds:0}; desiredLatencyMs={_mediaOutput?.BufferMilliseconds ?? 0}.");
            }
        }

        private void DecodeAndQueuePacket(byte[] packet)
        {
            if (_buffer == null || packet.Length == 0)
                return;

            short[] pcm = new short[FrameSize * 6];
            int decodedSamples;

            try
            {
                decodedSamples = _decoder.Decode(
                    packet,
                    0,
                    packet.Length,
                    pcm,
                    0,
                    pcm.Length,
                    false);
            }
            catch
            {
                AudioActivityService.Instance.UpdateRemoteLevel(0);
                return;
            }

            if (decodedSamples <= 0)
            {
                AudioActivityService.Instance.UpdateRemoteLevel(0);
                return;
            }

            ApplyPcmGain(pcm, decodedSamples, VoicePlaybackGain);

            byte[] pcmBytes = new byte[decodedSamples * sizeof(short)];
            Buffer.BlockCopy(pcm, 0, pcmBytes, 0, pcmBytes.Length);
            _buffer.AddSamples(pcmBytes, 0, pcmBytes.Length);

            double level = 0;
            for (int i = 0; i < decodedSamples; i++)
            {
                level += Math.Abs((int)pcm[i]);
            }

            level /= decodedSamples;
            level /= short.MaxValue;

            AudioActivityService.Instance.UpdateRemoteLevel(level);
        }

        private void DecodeAndQueueMediaPacket(byte[] packet)
        {
            if (_mediaBuffer == null || packet.Length == 0)
                return;

            short[] pcm = new short[MediaFrameSize * MediaChannels * 6];
            int decodedSamples;

            try
            {
                decodedSamples = _mediaDecoder.Decode(
                    packet,
                    0,
                    packet.Length,
                    pcm,
                    0,
                    pcm.Length / MediaChannels,
                    false);
            }
            catch
            {
                AudioActivityService.Instance.UpdateRemoteLevel(0);
                return;
            }

            if (decodedSamples <= 0)
            {
                AudioActivityService.Instance.UpdateRemoteLevel(0);
                return;
            }

            var sampleCount = decodedSamples * MediaChannels;
            ApplyPcmGain(pcm, sampleCount, MediaPlaybackGain);
            SmoothMediaDiscontinuity(pcm, decodedSamples);

            byte[] pcmBytes = new byte[sampleCount * sizeof(short)];
            Buffer.BlockCopy(pcm, 0, pcmBytes, 0, pcmBytes.Length);
            _mediaBuffer.AddSamples(pcmBytes, 0, pcmBytes.Length);
            _mediaPacketsDecoded++;

            double level = 0;
            for (int i = 0; i < sampleCount; i++)
                level += Math.Abs((int)pcm[i]);

            level /= sampleCount;
            level /= short.MaxValue;
            AudioActivityService.Instance.UpdateRemoteLevel(level);
        }

        private void SmoothMediaDiscontinuity(short[] pcm, int decodedFrames)
        {
            if (decodedFrames <= 0 || pcm.Length < MediaChannels)
                return;

            if (_mediaDiscontinuityPending && _hasLastMediaSamples)
            {
                // Dropping compressed media frames is sometimes required to keep
                // realtime latency bounded. Interpolate across the first 5 ms of
                // the next decoded packet so the skipped boundary cannot become
                // an audible full-scale click/pop.
                var fadeFrames = Math.Min(decodedFrames, MediaSampleRate / 200);
                for (var frame = 0; frame < fadeFrames; frame++)
                {
                    var amount = (frame + 1f) / fadeFrames;
                    var index = frame * MediaChannels;
                    pcm[index] = (short)Math.Clamp(
                        (int)MathF.Round(_lastMediaSampleLeft + ((pcm[index] - _lastMediaSampleLeft) * amount)),
                        short.MinValue,
                        short.MaxValue);
                    pcm[index + 1] = (short)Math.Clamp(
                        (int)MathF.Round(_lastMediaSampleRight + ((pcm[index + 1] - _lastMediaSampleRight) * amount)),
                        short.MinValue,
                        short.MaxValue);
                }
            }

            var lastIndex = (decodedFrames - 1) * MediaChannels;
            _lastMediaSampleLeft = pcm[lastIndex];
            _lastMediaSampleRight = pcm[lastIndex + 1];
            _hasLastMediaSamples = true;
            _mediaDiscontinuityPending = false;
        }

        private static void ApplyPcmGain(short[] samples, int sampleCount, float gain)
        {
            if (gain <= 1f)
                return;

            var count = Math.Min(sampleCount, samples.Length);
            for (var i = 0; i < count; i++)
            {
                var amplified = (int)MathF.Round(samples[i] * gain);
                samples[i] = (short)Math.Clamp(amplified, short.MinValue, short.MaxValue);
            }
        }

        private static bool TryUnwrapScreenShareAudioPacket(byte[] data, out byte[] opusPacket)
        {
            opusPacket = Array.Empty<byte>();
            if (!IsScreenShareAudioPacket(data))
                return false;

            opusPacket = new byte[data.Length - ScreenShareAudioHeader.Length];
            Buffer.BlockCopy(data, ScreenShareAudioHeader.Length, opusPacket, 0, opusPacket.Length);
            return true;
        }

        private sealed class QueuedAudioPacket
        {
            public QueuedAudioPacket(long generation, byte[] data)
            {
                Generation = generation;
                Data = data;
            }

            public long Generation { get; }
            public byte[] Data { get; }
        }
    }
}

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using NAudio.Wave;
using Zink.Services.NativeCalling;

namespace Zink.Services
{
    public sealed class IncomingCallRingtoneService : IDisposable
    {
        private static readonly object InstanceSync = new();
        private static IncomingCallRingtoneService? _instance;

        public static IncomingCallRingtoneService Instance
        {
            get
            {
                lock (InstanceSync)
                {
                    return _instance ??= new IncomingCallRingtoneService();
                }
            }
        }

        public static void TryStart()
        {
            TryStartIncoming();
        }

        public static void TryStartIncoming()
        {
            try
            {
                Instance.Start(CallSoundKind.Incoming);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[IncomingCallSound] Start skipped because ringtone service is unavailable: {ex}");
            }
        }

        public static void TryStartOutgoing()
        {
            try
            {
                Instance.Start(CallSoundKind.Outgoing);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[OutgoingCallSound] Start skipped because call sound service is unavailable: {ex}");
            }
        }

        public static void TryStop()
        {
            try
            {
                Instance.Stop();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[IncomingCallSound] Stop skipped because ringtone service is unavailable: {ex}");
            }
        }

        private readonly object _syncRoot = new();
        private readonly List<WaveOut> _outputs = new();
        private readonly List<IDisposable> _sources = new();
        private DateTimeOffset _startedAt;
        private int _stopVersion;

        private IncomingCallRingtoneService()
        {
        }

        public void Start()
        {
            Start(CallSoundKind.Incoming);
        }

        private void Start(CallSoundKind soundKind)
        {
            lock (_syncRoot)
            {
                try
                {
                    Debug.WriteLine("[IncomingCallSound] Start requested.");
                    StopCore();

                    foreach (var deviceNumber in GetPlaybackDeviceNumbers())
                    {
                        try
                        {
                            IDisposable? disposableSource = null;
                            var output = new WaveOut
                            {
                                DeviceNumber = deviceNumber,
                                BufferMilliseconds = 80
                            };

                            var source = CreateCallSoundProvider(soundKind);
                            disposableSource = source as IDisposable;
                            output.Init(source);
                            output.Play();
                            _outputs.Add(output);
                            if (disposableSource != null)
                                _sources.Add(disposableSource);

                            Debug.WriteLine($"[IncomingCallSound] Ringtone started on device {deviceNumber} ({GetDeviceName(deviceNumber)}).");
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine($"[IncomingCallSound] Failed to start device {deviceNumber} ({GetDeviceName(deviceNumber)}): {ex.Message}");
                        }
                    }

                    if (_outputs.Count == 0)
                    {
                        Debug.WriteLine("[IncomingCallSound] No playback devices accepted the ringtone.");
                        return;
                    }

                    _startedAt = DateTimeOffset.UtcNow;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[IncomingCallSound] Failed to play ringtone: {ex}");
                }
            }
        }

        public void Stop()
        {
            int version;
            TimeSpan remaining;

            lock (_syncRoot)
            {
                if (_outputs.Count == 0)
                    return;

                version = ++_stopVersion;
                var elapsed = DateTimeOffset.UtcNow - _startedAt;
                remaining = TimeSpan.FromMilliseconds(1500) - elapsed;
                if (remaining <= TimeSpan.Zero)
                {
                    StopCore();
                    return;
                }
            }

            _ = StopAfterMinimumDurationAsync(version, remaining);
        }

        private async Task StopAfterMinimumDurationAsync(int version, TimeSpan delay)
        {
            try
            {
                await Task.Delay(delay);
            }
            catch
            {
            }

            lock (_syncRoot)
            {
                if (version == _stopVersion)
                    StopCore();
            }
        }

        private void StopCore()
        {
            try
            {
                if (_outputs.Count > 0)
                    Debug.WriteLine("[IncomingCallSound] Stop requested.");

                foreach (var output in _outputs)
                {
                    try
                    {
                        output.Stop();
                        output.Dispose();
                    }
                    catch
                    {
                    }
                }

                foreach (var source in _sources)
                {
                    try
                    {
                        source.Dispose();
                    }
                    catch
                    {
                    }
                }
            }
            catch
            {
            }
            finally
            {
                _outputs.Clear();
                _sources.Clear();
            }
        }

        private static IWaveProvider CreateCallSoundProvider(CallSoundKind soundKind)
        {
            var ringtonePath = Path.Combine(
                AppContext.BaseDirectory,
                "Assets",
                "Sounds",
                "zink-call-ring.wav");

            if (File.Exists(ringtonePath))
            {
                try
                {
                    return new LoopingWaveFileProvider(ringtonePath);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[CallSound] Failed to load packaged ringtone '{ringtonePath}': {ex.Message}");
                }
            }

            Debug.WriteLine($"[CallSound] Packaged ringtone was unavailable; using synthesized fallback.");
            return new PopCallWaveProvider(soundKind);
        }

        public void Dispose()
        {
            Stop();
        }

        private static IEnumerable<int> GetPlaybackDeviceNumbers()
        {
            var selectedDevice = AudioPlaybackService.Instance.SelectedOutputDeviceNumber;
            var seen = new HashSet<int>();

            if (selectedDevice >= -1 && seen.Add(selectedDevice))
                yield return selectedDevice;

            if (seen.Add(-1))
                yield return -1;

            for (var deviceNumber = 0; deviceNumber < WaveOut.DeviceCount; deviceNumber++)
            {
                if (seen.Add(deviceNumber))
                    yield return deviceNumber;
            }
        }

        private static string GetDeviceName(int deviceNumber)
        {
            if (deviceNumber < 0)
                return "Windows default";

            try
            {
                var capabilities = WaveOut.GetCapabilities(deviceNumber);
                return capabilities.ProductName;
            }
            catch
            {
                return "Unknown";
            }
        }

        private enum CallSoundKind
        {
            Incoming,
            Outgoing
        }

        private sealed class LoopingWaveFileProvider : IWaveProvider, IDisposable
        {
            private readonly WaveFileReader _reader;

            public LoopingWaveFileProvider(string path)
            {
                _reader = new WaveFileReader(path);
            }

            public WaveFormat WaveFormat => _reader.WaveFormat;

            public int Read(byte[] buffer, int offset, int count)
            {
                return Read(buffer.AsSpan(offset, count));
            }

            public int Read(Span<byte> buffer)
            {
                var totalRead = 0;
                while (totalRead < buffer.Length)
                {
                    var read = _reader.Read(buffer[totalRead..]);
                    if (read == 0)
                    {
                        _reader.Position = 0;
                        continue;
                    }

                    totalRead += read;
                }

                return totalRead;
            }

            public void Dispose()
            {
                _reader.Dispose();
            }
        }

        private sealed class PopCallWaveProvider : IWaveProvider
        {
            private const int SampleRate = 44100;
            private const int Channels = 2;

            private readonly CallSoundKind _soundKind;
            private long _sampleIndex;

            public PopCallWaveProvider(CallSoundKind soundKind)
            {
                _soundKind = soundKind;
            }

            public WaveFormat WaveFormat { get; } = new WaveFormat(SampleRate, 16, Channels);

            public int Read(byte[] buffer, int offset, int count)
            {
                return Read(buffer.AsSpan(offset, count));
            }

            public int Read(Span<byte> buffer)
            {
                var frameSize = Channels * 2;
                var frames = buffer.Length / frameSize;

                for (var frame = 0; frame < frames; frame++)
                {
                    var time = _sampleIndex / (double)SampleRate;
                    var amplitude = _soundKind == CallSoundKind.Incoming
                        ? RenderPop(time % 1.65, 0.0, 1.0) + RenderPop(time % 1.65, 0.19, 0.78)
                        : RenderPop(time % 1.30, 0.0, 0.58);

                    amplitude = Math.Clamp(amplitude, -0.82, 0.82);

                    var sample = (short)(amplitude * short.MaxValue);
                    var writeOffset = frame * frameSize;

                    buffer[writeOffset] = (byte)(sample & 0xff);
                    buffer[writeOffset + 1] = (byte)((sample >> 8) & 0xff);
                    buffer[writeOffset + 2] = buffer[writeOffset];
                    buffer[writeOffset + 3] = buffer[writeOffset + 1];

                    _sampleIndex++;
                }

                return frames * frameSize;
            }

            private static double RenderPop(double cycleTime, double startTime, double volume)
            {
                var popTime = cycleTime - startTime;
                if (popTime < 0.0 || popTime >= 0.22)
                    return 0.0;

                // A short pitch drop gives the cue its rounded "bubble pop" shape.
                var pitchPhase = 2.0 * Math.PI * (980.0 * popTime - 1550.0 * popTime * popTime);
                var bodyEnvelope = (1.0 - Math.Exp(-popTime * 240.0)) * Math.Exp(-popTime * 25.0);
                var body = Math.Sin(pitchPhase) * bodyEnvelope;

                // A tiny high-frequency transient adds definition without sounding harsh.
                var clickEnvelope = Math.Exp(-popTime * 115.0);
                var click = Math.Sin(2.0 * Math.PI * 3100.0 * popTime) * clickEnvelope * 0.16;

                // A quiet octave layer makes the pop feel fuller on laptop speakers.
                var warmth = Math.Sin(pitchPhase * 0.5) * bodyEnvelope * 0.22;
                return (body + click + warmth) * volume * 0.48;
            }
        }
    }
}

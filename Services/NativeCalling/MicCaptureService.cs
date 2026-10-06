using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using Concentus.Enums;
using ConcentusEncoder = Concentus.Structs.OpusEncoder;
using NAudio.Wave;

namespace Zink.Services.NativeCalling
{
    public sealed class MicCaptureService
    {
        public sealed class InputDeviceInfo
        {
            public int DeviceNumber { get; init; }
            public string Name { get; init; } = string.Empty;

            public override string ToString() => Name;
        }

        public static MicCaptureService Instance { get; } = new MicCaptureService();

        private readonly object _gate = new();
        private WaveInEvent? _capture;
        private ConcentusEncoder? _encoder;
        private int _selectedInputDeviceNumber = 0;
        private long _encodeFailures;

        public bool IsRunning { get; private set; }

        public int SelectedInputDeviceNumber
        {
            get
            {
                lock (_gate)
                {
                    return _selectedInputDeviceNumber;
                }
            }
        }

        public event Action<byte[]>? AudioCaptured;

        private MicCaptureService()
        {
        }

        public IReadOnlyList<InputDeviceInfo> GetInputDevices()
        {
            var devices = new List<InputDeviceInfo>();

            for (int i = 0; i < WaveInEvent.DeviceCount; i++)
            {
                var capabilities = WaveInEvent.GetCapabilities(i);

                devices.Add(new InputDeviceInfo
                {
                    DeviceNumber = i,
                    Name = capabilities.ProductName
                });
            }

            return devices;
        }

        public bool SetInputDevice(int deviceNumber)
        {
            lock (_gate)
            {
                if (deviceNumber < 0 || deviceNumber >= WaveInEvent.DeviceCount)
                    return false;

                bool wasRunning = IsRunning;

                if (wasRunning)
                    StopCaptureLocked();

                _selectedInputDeviceNumber = deviceNumber;

                if (wasRunning)
                    StartCaptureLocked();

                return true;
            }
        }

        public Task StartAsync()
        {
            lock (_gate)
            {
                if (IsRunning)
                    return Task.CompletedTask;

                StartCaptureLocked();
            }

            return Task.CompletedTask;
        }

        public Task StopAsync()
        {
            lock (_gate)
            {
                if (!IsRunning)
                    return Task.CompletedTask;

                StopCaptureLocked();
            }

            return Task.CompletedTask;
        }

        private void StartCaptureLocked()
        {
            _encoder = new ConcentusEncoder(16000, 1, OpusApplication.OPUS_APPLICATION_VOIP)
            {
                Bitrate = 16000,
                Complexity = 10,
                SignalType = OpusSignal.OPUS_SIGNAL_VOICE,
                // Keep sending 20 ms packets even during quiet moments so calls
                // remain continuously open instead of sounding voice-activated.
                UseDTX = false,
                UseInbandFEC = true,
                PacketLossPercent = 10
            };
            _encodeFailures = 0;

            _capture = new WaveInEvent
            {
                DeviceNumber = _selectedInputDeviceNumber,
                WaveFormat = new WaveFormat(16000, 16, 1),
                BufferMilliseconds = 20,
                NumberOfBuffers = 2
            };

            _capture.DataAvailable += OnDataAvailable;
            _capture.RecordingStopped += OnRecordingStopped;
            _capture.StartRecording();

            IsRunning = true;
        }

        private void StopCaptureLocked()
        {
            try
            {
                if (_capture != null)
                {
                    _capture.DataAvailable -= OnDataAvailable;
                    _capture.RecordingStopped -= OnRecordingStopped;
                    _capture.StopRecording();
                    _capture.Dispose();
                    _capture = null;
                }

                _encoder = null;
            }
            finally
            {
                IsRunning = false;
                AudioActivityService.Instance.UpdateLocalLevel(0);
            }
        }

        private void OnRecordingStopped(object? sender, StoppedEventArgs e)
        {
            lock (_gate)
            {
                IsRunning = false;
            }

            AudioActivityService.Instance.UpdateLocalLevel(0);
        }

        private void OnDataAvailable(object? sender, WaveInEventArgs e)
        {
            var encoder = _encoder;
            if (e.BytesRecorded <= 0 || encoder == null || !IsRunning)
            {
                AudioActivityService.Instance.UpdateLocalLevel(0);
                return;
            }

            var pcmBytes = new byte[e.BytesRecorded];
            Array.Copy(e.Buffer, pcmBytes, e.BytesRecorded);

            float max = 0f;
            for (int i = 0; i < pcmBytes.Length - 1; i += 2)
            {
                short sample = (short)(pcmBytes[i] | (pcmBytes[i + 1] << 8));
                float abs = Math.Abs((int)sample);
                if (abs > max)
                    max = abs;
            }

            float gain = 1.0f;
            if (max > 0f)
            {
                gain = 18000f / max;

                if (gain > 4.0f) gain = 4.0f;
                if (gain < 1.0f) gain = 1.0f;
            }

            double level = 0;
            for (int i = 0; i < pcmBytes.Length - 1; i += 2)
            {
                short sample = (short)(pcmBytes[i] | (pcmBytes[i + 1] << 8));

                float amplified = sample * gain;

                if (amplified > short.MaxValue) amplified = short.MaxValue;
                if (amplified < short.MinValue) amplified = short.MinValue;

                short newSample = (short)amplified;
                pcmBytes[i] = (byte)newSample;
                pcmBytes[i + 1] = (byte)(newSample >> 8);
                level += Math.Abs((int)newSample);
            }

            int sampleCount = pcmBytes.Length / 2;
            if (sampleCount <= 0)
            {
                AudioActivityService.Instance.UpdateLocalLevel(0);
                return;
            }

            short[] pcmSamples = new short[sampleCount];
            Buffer.BlockCopy(pcmBytes, 0, pcmSamples, 0, pcmBytes.Length);

            byte[] opusBuffer = new byte[4000];
            int encodedLength;
            try
            {
                encodedLength = encoder.Encode(
                    pcmSamples,
                    0,
                    pcmSamples.Length,
                    opusBuffer,
                    0,
                    opusBuffer.Length);
            }
            catch (Exception ex)
            {
                _encodeFailures++;
                if (_encodeFailures == 1 || _encodeFailures % 50 == 0)
                {
                    Debug.WriteLine($"[Call:Mic] Opus encode failed #{_encodeFailures}: samples={pcmSamples.Length}; bytes={pcmBytes.Length}; running={IsRunning}; {ex.GetType().Name}: {ex.Message}");
                }

                AudioActivityService.Instance.UpdateLocalLevel(0);
                return;
            }

            if (encodedLength > 0)
            {
                byte[] finalPacket = new byte[encodedLength];
                Array.Copy(opusBuffer, finalPacket, encodedLength);
                AudioCaptured?.Invoke(finalPacket);
            }

            level /= sampleCount;
            level /= short.MaxValue;

            AudioActivityService.Instance.UpdateLocalLevel(level);
        }
    }
}

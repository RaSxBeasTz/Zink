using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Zink.Interop;
using Zink.Services.NativeCalling;
using CaptureNativeMethods = Zink.Services.NativeCalling.NativeMethods;

namespace Zink.Services.Recording
{
    public abstract class WasapiCaptureBase : IAudioCaptureService
    {
        private readonly object _gate = new();

        private CancellationTokenSource? _cts;
        private Task? _workerTask;

        private IMMDeviceEnumerator? _enumerator;
        private IMMDevice? _device;
        private IAudioClient? _audioClient;
        private IAudioCaptureClient? _captureClient;
        private EventWaitHandle? _captureEvent;

        private WAVEFORMATEX _waveFormat;
        private bool _isFloatFormat;
        private bool _initialized;
        private DateTime _captureStartUtc;
        private long _packetCount;
        private long _totalBytes;
        private int _nonZeroAudioPackets;
        private float _largestObservedSample;

        // Stable audio-clock tracking.
        private long _capturedFrames;
        private int _bytesPerFrame;
        private long _firstQpcPosition100ns;
        private TimeSpan _nextPacketTimestamp;
        private bool _hasNextPacketTimestamp;

        public event EventHandler<AudioPacket>? AudioPacketArrived;

        public bool IsRunning { get; private set; }
        public string? DeviceId { get; private set; }

        protected abstract bool UseLoopback { get; }
        protected abstract EDataFlow DataFlow { get; }
        private protected virtual bool UsesFixedLoopbackFormat => false;

        public async Task StartAsync(string? deviceId = null)
        {
            lock (_gate)
            {
                if (IsRunning)
                    return;
            }

            try
            {
                await RecorderLog.InfoAsync(GetType().Name,
                    $"Starting audio capture. DeviceId='{deviceId ?? "(default)"}', Loopback={UseLoopback}, DataFlow={DataFlow}");

                await InitializeAudioClientAsync(deviceId);

                var cts = new CancellationTokenSource();
                _cts = cts;
                _captureStartUtc = DateTime.UtcNow;
                _packetCount = 0;
                _totalBytes = 0;
                _nonZeroAudioPackets = 0;
                _largestObservedSample = 0;
                _capturedFrames = 0;
                _bytesPerFrame = _waveFormat.nBlockAlign;
                _firstQpcPosition100ns = -1;
                _nextPacketTimestamp = TimeSpan.Zero;
                _hasNextPacketTimestamp = false;

                _workerTask = Task.Factory.StartNew(
                    () => CaptureLoop(cts.Token),
                    cts.Token,
                    TaskCreationOptions.LongRunning,
                    TaskScheduler.Default);

                lock (_gate)
                {
                    IsRunning = true;
                    DeviceId = deviceId;
                }

                await RecorderLog.InfoAsync(GetType().Name,
                    $"Audio capture started. Format: {_waveFormat.nSamplesPerSec} Hz, {_waveFormat.nChannels} ch, {_waveFormat.wBitsPerSample} bit, blockAlign={_waveFormat.nBlockAlign}, formatTag=0x{_waveFormat.wFormatTag:X4}, float={_isFloatFormat}, avgBytes={_waveFormat.nAvgBytesPerSec}");
            }
            catch (Exception ex)
            {
                await RecorderLog.ErrorAsync(GetType().Name, ex, "StartAsync failed");
                await CleanupFailedStartAsync();
                throw;
            }
        }

        public async Task StopAsync()
        {
            CancellationTokenSource? cts;
            Task? workerTask;
            IAudioClient? audioClient;

            lock (_gate)
            {
                if (!IsRunning && !_initialized)
                    return;

                IsRunning = false;

                cts = _cts;
                workerTask = _workerTask;
                audioClient = _audioClient;

                _cts = null;
                _workerTask = null;
            }

            try
            {
                cts?.Cancel();
            }
            catch
            {
            }

            if (workerTask is not null)
            {
                try
                {
                    await workerTask;
                }
                catch
                {
                }
            }

            try
            {
                audioClient?.Stop();
            }
            catch
            {
            }

            ReleaseComObjects();

            cts?.Dispose();
            DeviceId = null;

            await RecorderLog.InfoAsync(GetType().Name,
                $"Audio capture stopped. Packets={_packetCount}, Bytes={_totalBytes}, CapturedFrames={_capturedFrames}, NonZeroPackets={_nonZeroAudioPackets}, Peak={_largestObservedSample:F6}");
        }

        private protected virtual Task<IAudioClient?> ActivateCustomAudioClientAsync()
        {
            return Task.FromResult<IAudioClient?>(null);
        }

        private async Task InitializeAudioClientAsync(string? deviceId)
        {
            _audioClient = await ActivateCustomAudioClientAsync();
            if (_audioClient is null)
            {
                _enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
                _device = ResolveDevice(deviceId);
                Guid audioClientGuid = CoreAudioGuids.IID_IAudioClient;
                HResult.Check(
                    _device!.Activate(ref audioClientGuid, CLSCTX.ALL, IntPtr.Zero, out var audioClientObj),
                    "IMMDevice.Activate(IAudioClient)");
                _audioClient = (IAudioClient)audioClientObj;
            }

            IntPtr mixFormatPtr = IntPtr.Zero;

            try
            {
                if (UsesFixedLoopbackFormat)
                {
                    _waveFormat = new WAVEFORMATEX
                    {
                        wFormatTag = 3,
                        nChannels = 2,
                        nSamplesPerSec = 44100,
                        wBitsPerSample = 32,
                        nBlockAlign = 8,
                        nAvgBytesPerSec = 44100 * 8,
                        cbSize = 0
                    };
                    mixFormatPtr = Marshal.AllocCoTaskMem(Marshal.SizeOf<WAVEFORMATEX>());
                    Marshal.StructureToPtr(_waveFormat, mixFormatPtr, false);
                }
                else
                {
                    HResult.Check(
                        _audioClient.GetMixFormat(out mixFormatPtr),
                        "IAudioClient.GetMixFormat");
                }

                _waveFormat = Marshal.PtrToStructure<WAVEFORMATEX>(mixFormatPtr);
                _isFloatFormat = IsFloatWaveFormat(mixFormatPtr, _waveFormat);

                var flags =
                    AUDCLNT_STREAMFLAGS.AUTOCONVERTPCM |
                    AUDCLNT_STREAMFLAGS.SRC_DEFAULT_QUALITY |
                    AUDCLNT_STREAMFLAGS.EVENTCALLBACK;

                if (UseLoopback)
                {
                    flags |= AUDCLNT_STREAMFLAGS.LOOPBACK;
                }

                long bufferDuration100ns = 2_000_000;

                HResult.Check(
                    _audioClient.Initialize(
                        AUDCLNT_SHAREMODE.SHARED,
                        flags,
                        bufferDuration100ns,
                        0,
                        mixFormatPtr,
                        IntPtr.Zero),
                    "IAudioClient.Initialize");

                object captureClientObj;
                Guid captureClientGuid = CoreAudioGuids.IID_IAudioCaptureClient;
                HResult.Check(
                    _audioClient.GetService(ref captureClientGuid, out captureClientObj),
                    "IAudioClient.GetService(IAudioCaptureClient)");

                _captureClient = (IAudioCaptureClient)captureClientObj;
                _captureEvent?.Dispose();
                _captureEvent = new EventWaitHandle(false, EventResetMode.AutoReset);
                HResult.Check(
                    _audioClient.SetEventHandle(_captureEvent.SafeWaitHandle.DangerousGetHandle()),
                    "IAudioClient.SetEventHandle");

                HResult.Check(
                    _audioClient.Start(),
                    "IAudioClient.Start");

                _initialized = true;
            }
            finally
            {
                if (mixFormatPtr != IntPtr.Zero)
                {
                    Marshal.FreeCoTaskMem(mixFormatPtr);
                }
            }
        }

        private IMMDevice ResolveDevice(string? deviceId)
        {
            if (_enumerator is null)
                throw new InvalidOperationException("IMMDeviceEnumerator was not initialized.");

            if (!string.IsNullOrWhiteSpace(deviceId))
            {
                try
                {
                    int hr = _enumerator.GetDevice(deviceId, out IMMDevice selectedDevice);
                    HResult.Check(hr, "IMMDeviceEnumerator.GetDevice");

                    RecorderLog.InfoAsync(GetType().Name,
                        $"Using requested Core Audio device id: {deviceId}")
                        .ConfigureAwait(false);

                    return selectedDevice;
                }
                catch (Exception ex)
                {
                    RecorderLog.ErrorAsync(GetType().Name, ex,
                        $"GetDevice failed for supplied id '{deviceId}'. Falling back to default endpoint.")
                        .ConfigureAwait(false);
                }
            }

            // Games, browsers and the Windows volume flyout follow the console
            // render endpoint. eMultimedia can point at an idle device while
            // WASAPI still returns non-SILENT zero-filled packets.
            ERole role = UseLoopback ? ERole.eConsole : ERole.eCommunications;
            HResult.Check(
                _enumerator.GetDefaultAudioEndpoint(DataFlow, role, out IMMDevice defaultDevice),
                "IMMDeviceEnumerator.GetDefaultAudioEndpoint");

            RecorderLog.InfoAsync(GetType().Name,
                $"Using default endpoint for {DataFlow}, role={role}.")
                .ConfigureAwait(false);

            return defaultDevice;
        }

        private void CaptureLoop(CancellationToken token)
        {
            var mmcssHandle = IntPtr.Zero;
            try
            {
                try
                {
                    Thread.CurrentThread.Priority = ThreadPriority.Highest;
                    mmcssHandle = CaptureNativeMethods.AvSetMmThreadCharacteristics("Audio", out var taskIndex);
                    if (mmcssHandle != IntPtr.Zero)
                        CaptureNativeMethods.AvSetMmThreadPriority(mmcssHandle, AvrtPriority.Critical);

                    _ = RecorderLog.InfoAsync(GetType().Name,
                        $"Audio capture MMCSS enabled. taskIndex={taskIndex}, eventCallback={_captureEvent is not null}");
                }
                catch (Exception ex)
                {
                    _ = RecorderLog.ErrorAsync(GetType().Name, ex, "Audio MMCSS setup failed");
                }

                while (!token.IsCancellationRequested)
                {
                    uint nextPacketFrames = 0;

                    try
                    {
                        HResult.Check(
                            _captureClient!.GetNextPacketSize(out nextPacketFrames),
                            "IAudioCaptureClient.GetNextPacketSize");
                    }
                    catch (Exception ex)
                    {
                        _ = RecorderLog.ErrorAsync(GetType().Name, ex, "GetNextPacketSize failed");
                        _captureEvent?.WaitOne(5);
                        continue;
                    }

                    if (nextPacketFrames == 0)
                    {
                        _captureEvent?.WaitOne(20);
                        continue;
                    }

                    while (nextPacketFrames > 0 && !token.IsCancellationRequested)
                    {
                        IntPtr dataPtr = IntPtr.Zero;
                        uint framesRead = 0;
                        AUDCLNT_BUFFERFLAGS flags = AUDCLNT_BUFFERFLAGS.NONE;
                        long devicePosition = 0;
                        long qpcPosition = 0;

                        HResult.Check(
                            _captureClient!.GetBuffer(
                                out dataPtr,
                                out framesRead,
                                out flags,
                                out devicePosition,
                                out qpcPosition),
                            "IAudioCaptureClient.GetBuffer");

                        try
                        {
                            int bytesPerFrame = _bytesPerFrame;
                            if (bytesPerFrame <= 0)
                                throw new InvalidOperationException("Invalid audio block alignment.");

                            int byteCount = checked((int)framesRead * bytesPerFrame);
                            byte[] pcm = new byte[byteCount];

                            bool isSilent = (flags & AUDCLNT_BUFFERFLAGS.SILENT) == AUDCLNT_BUFFERFLAGS.SILENT;

                            if (!isSilent && byteCount > 0 && dataPtr != IntPtr.Zero)
                            {
                                Marshal.Copy(dataPtr, pcm, 0, byteCount);
                                float packetPeak = MeasurePeak(pcm, _isFloatFormat, _waveFormat.wBitsPerSample);
                                if (packetPeak > 0.000001f)
                                {
                                    _nonZeroAudioPackets++;
                                    _largestObservedSample = Math.Max(_largestObservedSample, packetPeak);
                                }
                            }

                            // WASAPI supplies the QPC position of the first audio sample in
                            // 100-nanosecond units. Unlike a packet counter, this clock keeps
                            // advancing across a capture discontinuity, so the mixer can insert
                            // silence for a lost interval instead of shifting all later audio.
                            TimeSpan clockTimestamp;
                            if (qpcPosition > 0)
                            {
                                if (_firstQpcPosition100ns < 0)
                                    _firstQpcPosition100ns = qpcPosition;

                                clockTimestamp = TimeSpan.FromTicks(
                                    Math.Max(0, qpcPosition - _firstQpcPosition100ns));
                            }
                            else
                            {
                                clockTimestamp = TimeSpan.FromSeconds(
                                    _capturedFrames / (double)_waveFormat.nSamplesPerSec);
                            }

                            // QPC contains small packet-to-packet timing jitter. Placing every
                            // packet at that raw position can create tiny gaps or overlaps that
                            // are heard as crackles. Keep normal packets sample-contiguous, but
                            // retain genuine capture gaps so later audio remains synchronized.
                            TimeSpan packetTimestamp;
                            if (!_hasNextPacketTimestamp)
                            {
                                packetTimestamp = clockTimestamp;
                                _hasNextPacketTimestamp = true;
                            }
                            else
                            {
                                TimeSpan clockDifference = clockTimestamp - _nextPacketTimestamp;
                                packetTimestamp = Math.Abs(clockDifference.TotalMilliseconds) >= 20
                                    ? clockTimestamp
                                    : _nextPacketTimestamp;
                            }

                            _nextPacketTimestamp = packetTimestamp + TimeSpan.FromSeconds(
                                framesRead / (double)_waveFormat.nSamplesPerSec);

                            _capturedFrames += framesRead;
                            _packetCount++;
                            _totalBytes += byteCount;

                            if (_packetCount % 200 == 0)
                            {
                                _ = RecorderLog.InfoAsync(GetType().Name,
                                    $"Packets={_packetCount}, Bytes={_totalBytes}, FramesRead={framesRead}, Silent={isSilent}, NonZeroPackets={_nonZeroAudioPackets}, Peak={_largestObservedSample:F6}, Timestamp={packetTimestamp}, DevicePosition={devicePosition}, Qpc={qpcPosition}");
                            }

                            AudioPacketArrived?.Invoke(this, new AudioPacket
                            {
                                Timestamp = packetTimestamp,
                                PcmData = pcm,
                                SampleRate = (int)_waveFormat.nSamplesPerSec,
                                Channels = _waveFormat.nChannels,
                                BitsPerSample = _waveFormat.wBitsPerSample,
                                FormatTag = _waveFormat.wFormatTag,
                                IsFloatFormat = _isFloatFormat
                            });
                        }
                        catch (Exception ex)
                        {
                            _ = RecorderLog.ErrorAsync(GetType().Name, ex, "Audio packet processing failed");
                        }
                        finally
                        {
                            try
                            {
                                _captureClient.ReleaseBuffer(framesRead);
                            }
                            catch (Exception ex)
                            {
                                _ = RecorderLog.ErrorAsync(GetType().Name, ex, "ReleaseBuffer failed");
                            }
                        }

                        HResult.Check(
                            _captureClient.GetNextPacketSize(out nextPacketFrames),
                            "IAudioCaptureClient.GetNextPacketSize");
                    }
                }
            }
            finally
            {
                if (mmcssHandle != IntPtr.Zero)
                    CaptureNativeMethods.AvRevertMmThreadCharacteristics(mmcssHandle);
            }
        }

        private async Task CleanupFailedStartAsync()
        {
            try
            {
                await StopAsync();
            }
            catch
            {
            }
        }

        private static float MeasurePeak(byte[] data, bool isFloat, ushort bitsPerSample)
        {
            float peak = 0;
            if (isFloat && bitsPerSample == 32)
            {
                for (int i = 0; i + 3 < data.Length; i += 4)
                {
                    float value = Math.Abs(BitConverter.ToSingle(data, i));
                    if (!float.IsNaN(value) && !float.IsInfinity(value))
                        peak = Math.Max(peak, value);
                }
            }
            else if (bitsPerSample == 16)
            {
                for (int i = 0; i + 1 < data.Length; i += 2)
                    peak = Math.Max(peak, Math.Abs(BitConverter.ToInt16(data, i)) / 32768f);
            }
            return peak;
        }

        private static bool IsFloatWaveFormat(IntPtr formatPtr, WAVEFORMATEX format)
        {
            const ushort waveFormatIeeeFloat = 3;
            const ushort waveFormatExtensible = 0xFFFE;
            var pcmSubFormat = new Guid("00000001-0000-0010-8000-00AA00389B71");
            var floatSubFormat = new Guid("00000003-0000-0010-8000-00AA00389B71");

            if (format.wFormatTag == waveFormatIeeeFloat)
                return true;

            if (format.wFormatTag != waveFormatExtensible || format.cbSize < 22)
                return false;

            try
            {
                var subFormatOffset = Marshal.SizeOf<WAVEFORMATEX>() + 6;
                var subFormat = Marshal.PtrToStructure<Guid>(IntPtr.Add(formatPtr, subFormatOffset));
                if (subFormat == floatSubFormat)
                    return true;

                if (subFormat == pcmSubFormat)
                    return false;

                return format.wBitsPerSample == 32;
            }
            catch
            {
                return format.wBitsPerSample == 32;
            }
        }

        private void ReleaseComObjects()
        {
            if (_captureClient is not null)
            {
                try { Marshal.ReleaseComObject(_captureClient); } catch { }
                _captureClient = null;
            }

            if (_audioClient is not null)
            {
                try
                {
                    if (_audioClient is IDisposable disposable)
                        disposable.Dispose();
                    else
                        Marshal.ReleaseComObject(_audioClient);
                }
                catch { }
                _audioClient = null;
            }

            if (_device is not null)
            {
                try { Marshal.ReleaseComObject(_device); } catch { }
                _device = null;
            }

            if (_enumerator is not null)
            {
                try { Marshal.ReleaseComObject(_enumerator); } catch { }
                _enumerator = null;
            }

            _captureEvent?.Dispose();
            _captureEvent = null;

            _initialized = false;
        }

        public async ValueTask DisposeAsync()
        {
            await StopAsync();
        }
    }
}

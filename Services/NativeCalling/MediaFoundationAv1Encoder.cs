using SharpDX;
using SharpDX.MediaFoundation;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;

namespace Zink.Services.NativeCalling
{
    public sealed class MediaFoundationAv1Encoder : IDisposable
    {
        public static readonly Guid Av1Subtype = new(0x31305641, 0x0000, 0x0010, 0x80, 0x00, 0x00, 0xAA, 0x00, 0x38, 0x9B, 0x71);
        private static readonly Guid CodecApiAvEncVideoForceKeyFrame = new("398C1B98-8353-475A-9EF2-8F265D260345");
        private static readonly Guid CodecApiAvEncCommonLowLatency = new("9D3ECD55-89E8-490A-970A-0C9548D5A56E");
        private static readonly Guid CodecApiAvEncCommonRealTime = new("143A0FF6-A131-43DA-B81E-98FBB8EC378E");
        private const int MfENeedMoreInput = unchecked((int)0xC00D6D72);
        private const int MfENotAccepting = unchecked((int)0xC00D36B5);
        private const int MftOutputStreamProvidesSamples = 0x00000100;
        private const int MftOutputStreamCanProvideSamples = 0x00000200;
        private const int ImfTransformProcessOutputVtableSlot = 25;

        private readonly Transform _encoder;
        private readonly int _width;
        private readonly int _height;
        private readonly int _frameRate;
        private readonly long _frameDuration100Ns;
        private readonly byte[] _nv12Buffer;
        private long _sampleTime;
        private int _frameIndex;
        private int _forceNextKeyFrame = 1;
        private bool _loggedFirstOutputFrame;
        private bool _loggedOutputStreamInfo;
        private MediaEventGenerator? _eventGenerator;
        private readonly object _eventSync = new();
        private Thread? _eventThread;
        private bool _stopEventThread;
        private int _inputRequests;
        private int _outputRequests;
        private bool _asyncMft;
        private ProcessOutputNativeDelegate? _processOutputNative;

        static MediaFoundationAv1Encoder()
        {
            try { MediaManager.Startup(); } catch { }
        }

        public MediaFoundationAv1Encoder(int width, int height, int bitrate, int frameRate)
        {
            _width = width;
            _height = height;
            _frameRate = Math.Clamp(frameRate, 1, ScreenShareCapturePipeline.TargetFps);
            _frameDuration100Ns = 10_000_000L / _frameRate;
            _nv12Buffer = new byte[_width * _height * 3 / 2];

            _encoder = CreateEncoderTransform();
            ConfigureEncoder(bitrate);
            InitializeAsyncEventPump();
            DiagnosticLogService.WriteLine(
                $"[ScreenShare:AV1X] Configured encoder: mode='{EncoderMode}'; hardware={IsHardwareAccelerated}; input={InputFormat}; size={_width}x{_height}; fps={_frameRate}; bitrate={bitrate}; synchronous=true.");
            ScreenShareTestLogService.WriteLine(
                $"av1-configured mode='{EncoderMode}'; hardware={IsHardwareAccelerated}; input={InputFormat}; size={_width}x{_height}; fps={_frameRate}; bitrate={bitrate}; synchronous=true");
        }

        public string EncoderMode { get; private set; } = "Windows Media Foundation AV1X";
        public string InputFormat => "NV12 system memory";
        public bool IsHardwareAccelerated { get; private set; }

        public void ForceNextKeyFrame()
        {
            Interlocked.Exchange(ref _forceNextKeyFrame, 1);
        }

        public IReadOnlyList<EncodedVideoFrame> Encode(Bitmap bitmap)
        {
            if (_asyncMft && _frameIndex > 0 && !WaitForInputRequest())
                return Array.Empty<EncodedVideoFrame>();

            using var inputBuffer = MediaFactory.CreateMemoryBuffer(_nv12Buffer.Length);
            int maxLength;
            int currentLength;
            var inputPtr = inputBuffer.Lock(out maxLength, out currentLength);
            try
            {
                ConvertBitmapToNv12(bitmap, _width, _height, _nv12Buffer);
                Marshal.Copy(_nv12Buffer, 0, inputPtr, _nv12Buffer.Length);
            }
            finally
            {
                inputBuffer.Unlock();
            }

            inputBuffer.CurrentLength = _nv12Buffer.Length;

            using var sample = MediaFactory.CreateSample();
            sample.AddBuffer(inputBuffer);
            sample.SampleTime = _sampleTime;
            sample.SampleDuration = _frameDuration100Ns;

            var forceKeyFrame = Interlocked.Exchange(ref _forceNextKeyFrame, 0) == 1 || _frameIndex == 0 || _frameIndex % (_frameRate * 2) == 0;
            if (forceKeyFrame)
            {
                try { _encoder.Attributes.Set(CodecApiAvEncVideoForceKeyFrame, 1); } catch { }
                try { sample.Set(SampleAttributeKeys.CleanPoint, true); } catch { }
            }

            try
            {
                var submitted = false;
                for (var attempt = 0; attempt < 3 && !submitted; attempt++)
                {
                    try
                    {
                        _encoder.ProcessInput(0, sample, 0);
                        submitted = true;
                    }
                    catch (SharpDXException ex) when (ex.ResultCode.Code == MfENotAccepting && attempt < 2)
                    {
                        Thread.Sleep(1);
                        WaitForInputRequest();
                    }
                }

                if (!submitted)
                    return Array.Empty<EncodedVideoFrame>();
            }
            catch (Exception ex)
            {
                DiagnosticLogService.WriteLine(
                    $"[ScreenShare:AV1X] ProcessInput failed: mode='{EncoderMode}'; size={_width}x{_height}; fps={_frameRate}; sampleBytes={_nv12Buffer.Length}; error={ex}");
                ScreenShareTestLogService.WriteLine($"av1-process-input-failed mode='{EncoderMode}'; error={ex}");
                throw;
            }
            _sampleTime += _frameDuration100Ns;
            _frameIndex++;

            if (_asyncMft)
            {
                WaitForOutputRequest();
                return DrainOutput(forceKeyFrame);
            }

            return DrainOutput(forceKeyFrame);
        }

        private void ConfigureEncoder(int bitrate)
        {
            TryEnableRealtimeAttributes();

            using var outputType = new MediaType();
            outputType.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
            outputType.Set(MediaTypeAttributeKeys.Subtype, Av1Subtype);
            outputType.Set(MediaTypeAttributeKeys.AvgBitrate, bitrate);
            outputType.Set(MediaTypeAttributeKeys.FrameSize, PackRatio(_width, _height));
            outputType.Set(MediaTypeAttributeKeys.FrameRate, PackRatio(_frameRate, 1));
            outputType.Set(MediaTypeAttributeKeys.PixelAspectRatio, PackRatio(1, 1));
            outputType.Set(MediaTypeAttributeKeys.InterlaceMode, (int)VideoInterlaceMode.Progressive);
            _encoder.SetOutputType(0, outputType, 0);

            using var inputType = new MediaType();
            inputType.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
            inputType.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.NV12);
            inputType.Set(MediaTypeAttributeKeys.FrameSize, PackRatio(_width, _height));
            inputType.Set(MediaTypeAttributeKeys.FrameRate, PackRatio(_frameRate, 1));
            inputType.Set(MediaTypeAttributeKeys.PixelAspectRatio, PackRatio(1, 1));
            inputType.Set(MediaTypeAttributeKeys.InterlaceMode, (int)VideoInterlaceMode.Progressive);
            inputType.Set(MediaTypeAttributeKeys.FixedSizeSamples, 1);
            inputType.Set(MediaTypeAttributeKeys.AllSamplesIndependent, 1);
            inputType.Set(MediaTypeAttributeKeys.SampleSize, _nv12Buffer.Length);
            inputType.Set(MediaTypeAttributeKeys.DefaultStride, _width);
            _encoder.SetInputType(0, inputType, 0);

            _encoder.ProcessMessage(TMessageType.NotifyBeginStreaming, IntPtr.Zero);
            _encoder.ProcessMessage(TMessageType.NotifyStartOfStream, IntPtr.Zero);
        }

        private IReadOnlyList<EncodedVideoFrame> DrainOutput(bool keyFrameHint)
        {
            var frames = new List<EncodedVideoFrame>();
            _encoder.GetOutputStreamInfo(0, out var info);
            var outputBufferSize = Math.Max(Math.Max(info.CbSize, _width * _height), _width * _height * 4);
            var useEncoderAllocatedOutput =
                (info.DwFlags & MftOutputStreamProvidesSamples) != 0 ||
                (info.DwFlags & MftOutputStreamCanProvideSamples) != 0;

            if (_asyncMft && !TryConsumeOutputRequest())
                return frames;

            if (!_loggedOutputStreamInfo)
            {
                _loggedOutputStreamInfo = true;
                DiagnosticLogService.WriteLine(
                    $"[ScreenShare:AV1X] Output stream info: flags=0x{info.DwFlags:X}; bufferSize={info.CbSize}; callerBufferSize={outputBufferSize}; encoderAllocatedOutput={useEncoderAllocatedOutput}.");
            }

            while (true)
            {
                Sample? callerOutputSample = null;
                MediaBuffer? callerOutputBuffer = null;
                NativeProcessOutputResult? nativeOutput = null;
                try
                {
                    if (!useEncoderAllocatedOutput)
                    {
                        callerOutputSample = MediaFactory.CreateSample();
                        callerOutputBuffer = MediaFactory.CreateMemoryBuffer(outputBufferSize);
                        callerOutputSample.AddBuffer(callerOutputBuffer);
                    }

                    try
                    {
                        nativeOutput = ProcessOutputNative(callerOutputSample, useEncoderAllocatedOutput);
                    }
                    catch (SharpDXException ex) when (ex.ResultCode.Code == MfENeedMoreInput)
                    {
                        if (_loggedFirstOutputFrame)
                            DiagnosticLogService.WriteLine("[ScreenShare:AV1X] ProcessOutput requested more input after a HaveOutput event.");
                        break;
                    }
                    catch (Exception ex)
                    {
                        DiagnosticLogService.WriteLine(
                            $"[ScreenShare:AV1X] ProcessOutput failed: mode='{EncoderMode}'; flags=0x{info.DwFlags:X}; error={ex}");
                        ScreenShareTestLogService.WriteLine($"av1-process-output-failed mode='{EncoderMode}'; flags=0x{info.DwFlags:X}; error={ex}");
                        throw;
                    }

                    var sampleToRead = nativeOutput.Sample ?? callerOutputSample;
                    if (sampleToRead == null)
                    {
                        DiagnosticLogService.WriteLine($"[ScreenShare:AV1X] ProcessOutput returned no output sample; status=0x{nativeOutput.OutputBufferStatus:X}; processStatus={nativeOutput.ProcessStatus}.");
                        ScreenShareTestLogService.WriteLine($"av1-output-empty reason=null-sample status=0x{nativeOutput.OutputBufferStatus:X}");
                        break;
                    }

                    if (!TryReadSampleBytes(sampleToRead, out var data))
                    {
                        DiagnosticLogService.WriteLine("[ScreenShare:AV1X] ProcessOutput returned a sample that could not be read.");
                        ScreenShareTestLogService.WriteLine("av1-output-empty reason=unreadable-sample");
                        break;
                    }

                    if (!_loggedFirstOutputFrame)
                    {
                        _loggedFirstOutputFrame = true;
                        Debug.WriteLine($"[ScreenShare:AV1X] First encoded AV1 temporal unit: bytes={data.Length}; keyHint={keyFrameHint}; mode={EncoderMode}.");
                    }

                    frames.Add(new EncodedVideoFrame(data, keyFrameHint, GetSampleTimestampMilliseconds(sampleToRead), ScreenShareCodecNames.Av1));

                    if (_asyncMft)
                        break;
                }
                finally
                {
                    nativeOutput?.Dispose();
                    callerOutputBuffer?.Dispose();
                    callerOutputSample?.Dispose();
                }
            }

            return frames;
        }

        private NativeProcessOutputResult ProcessOutputNative(
            Sample? callerOutputSample,
            bool useEncoderAllocatedOutput)
        {
            var nativeBuffer = new NativeMftOutputDataBuffer
            {
                DwStreamID = 0,
                PSample = callerOutputSample?.NativePointer ?? IntPtr.Zero,
                DwStatus = 0,
                PEvents = IntPtr.Zero
            };
            var nativeBufferPtr = Marshal.AllocHGlobal(Marshal.SizeOf<NativeMftOutputDataBuffer>());

            try
            {
                Marshal.StructureToPtr(nativeBuffer, nativeBufferPtr, false);
                var hr = GetNativeProcessOutput()(
                    _encoder.NativePointer,
                    (int)TransformProcessOutputFlags.None,
                    1,
                    nativeBufferPtr,
                    out var processStatus);
                new Result(hr).CheckError();

                nativeBuffer = Marshal.PtrToStructure<NativeMftOutputDataBuffer>(nativeBufferPtr);
                Sample? outputSample = null;
                Collection? outputEvents = null;

                if (useEncoderAllocatedOutput && nativeBuffer.PSample != IntPtr.Zero)
                    outputSample = new Sample(nativeBuffer.PSample);
                if (nativeBuffer.PEvents != IntPtr.Zero)
                    outputEvents = new Collection(nativeBuffer.PEvents);

                return new NativeProcessOutputResult(
                    outputSample,
                    outputEvents,
                    nativeBuffer.DwStatus,
                    (TransformProcessOutputStatus)processStatus);
            }
            finally
            {
                Marshal.FreeHGlobal(nativeBufferPtr);
            }
        }

        private ProcessOutputNativeDelegate GetNativeProcessOutput()
        {
            if (_processOutputNative != null)
                return _processOutputNative;

            var vtable = Marshal.ReadIntPtr(_encoder.NativePointer);
            var processOutputPtr = Marshal.ReadIntPtr(vtable, IntPtr.Size * ImfTransformProcessOutputVtableSlot);
            _processOutputNative = Marshal.GetDelegateForFunctionPointer<ProcessOutputNativeDelegate>(processOutputPtr);
            return _processOutputNative;
        }

        private Transform CreateEncoderTransform()
        {
            var outputInfo = new TRegisterTypeInformation
            {
                GuidMajorType = MediaTypeGuids.Video,
                GuidSubtype = Av1Subtype
            };
            var inputInfo = new TRegisterTypeInformation
            {
                GuidMajorType = MediaTypeGuids.Video,
                GuidSubtype = VideoFormatGuids.NV12
            };

            // AV1 hardware MFTs commonly require the asynchronous unlock contract.
            // Enumerating them without Asyncmft produces MF_E_ASYNC_MFT_CANCELED
            // (0xC00D6D77) during SetInputType/stream startup.
            var activations = EnumerateEncoderActivations(
                inputInfo,
                outputInfo,
                TransformEnumFlag.Hardware | TransformEnumFlag.Asyncmft | TransformEnumFlag.SortAndFilter).ToList();
            if (activations.Count == 0)
                activations = EnumerateEncoderActivations(inputInfo, outputInfo, TransformEnumFlag.SortAndFilter).ToList();
            foreach (var activation in activations)
            {
                try
                {
                    var name = GetActivationString(activation, TransformAttributeKeys.MftFriendlyNameAttribute, "Windows AV1 encoder");
                    var hardware = GetActivationString(activation, TransformAttributeKeys.MftEnumHardwareVendorIdAttribute, "");
                    IsHardwareAccelerated = !string.IsNullOrWhiteSpace(hardware);
                    _asyncMft = IsHardwareAccelerated;
                    EncoderMode = IsHardwareAccelerated
                        ? $"{ScreenShareCodecNames.AV1XDisplayName} hardware MFT: {name}"
                        : $"{ScreenShareCodecNames.AV1XDisplayName} software MFT: {name}";
                    Debug.WriteLine($"[ScreenShare:AV1X] Using {EncoderMode}.");
                    return activation.ActivateObject<Transform>();
                }
                finally
                {
                    activation.Dispose();
                }
            }

            throw new InvalidOperationException("Windows Media Foundation did not expose an AV1 encoder MFT.");
        }

        private void TryEnableRealtimeAttributes()
        {
            try
            {
                using var attributes = _encoder.Attributes;
                if (_asyncMft)
                    attributes.Set(TransformAttributeKeys.TransformAsyncUnlock, 1);
                attributes.Set(SinkWriterAttributeKeys.LowLatency.Guid, 1);
                attributes.Set(CodecApiAvEncCommonLowLatency, 1);
                attributes.Set(CodecApiAvEncCommonRealTime, 1);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ScreenShare:AV1X] Realtime AV1X attributes skipped: {ex.Message}");
            }
        }

        private void InitializeAsyncEventPump()
        {
            if (!_asyncMft)
                return;

            try
            {
                _eventGenerator = _encoder.QueryInterfaceOrNull<MediaEventGenerator>();
                if (_eventGenerator == null)
                {
                    _asyncMft = false;
                    DiagnosticLogService.WriteLine("[ScreenShare:AV1X] Async AV1 MFT did not expose a media event generator.");
                    return;
                }

                lock (_eventSync)
                {
                    _stopEventThread = false;
                    _inputRequests = 0;
                    _outputRequests = 0;
                }

                _eventThread = new Thread(AsyncEventLoop)
                {
                    IsBackground = true,
                    Name = "Zink AV1 MFT Event Pump",
                    Priority = ThreadPriority.AboveNormal
                };
                _eventThread.Start();
                DiagnosticLogService.WriteLine("[ScreenShare:AV1X] Asynchronous AV1 MFT event pump enabled.");
                ScreenShareTestLogService.WriteLine("av1-event-pump enabled=true");
            }
            catch (Exception ex)
            {
                _asyncMft = false;
                _eventGenerator = null;
                DiagnosticLogService.WriteLine($"[ScreenShare:AV1X] Failed to initialize async event pump: {ex}");
                ScreenShareTestLogService.WriteLine($"av1-event-pump-failed error={ex}");
            }
        }

        private void AsyncEventLoop()
        {
            while (true)
            {
                MediaEventGenerator? generator;
                lock (_eventSync)
                {
                    if (_stopEventThread)
                        return;

                    generator = _eventGenerator;
                }

                if (generator == null)
                    return;

                try
                {
                    using var mediaEvent = generator.GetEvent(isBlocking: true);
                    mediaEvent.Status.CheckError();
                    lock (_eventSync)
                    {
                        switch (mediaEvent.TypeInfo)
                        {
                            case MediaEventTypes.TransformNeedInput:
                                _inputRequests++;
                                break;
                            case MediaEventTypes.TransformHaveOutput:
                                _outputRequests++;
                                break;
                        }

                        if ((_inputRequests + _outputRequests) <= 24)
                        {
                            DiagnosticLogService.WriteLine(
                                $"[ScreenShare:AV1X] Async MFT event={mediaEvent.TypeInfo}; needInput={_inputRequests}; haveOutput={_outputRequests}.");
                            ScreenShareTestLogService.WriteLine(
                                $"av1-event type={mediaEvent.TypeInfo}; needInput={_inputRequests}; haveOutput={_outputRequests}");
                        }
                    }
                }
                catch (ObjectDisposedException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    lock (_eventSync)
                    {
                        if (_stopEventThread)
                            return;
                    }

                    DiagnosticLogService.WriteLine($"[ScreenShare:AV1X] Async MFT event pump error: {ex.Message}");
                    Thread.Sleep(5);
                }
            }
        }

        private bool TryConsumeInputRequest()
        {
            lock (_eventSync)
            {
                if (_inputRequests <= 0)
                    return false;

                _inputRequests--;
                return true;
            }
        }

        private bool TryConsumeOutputRequest()
        {
            lock (_eventSync)
            {
                if (_outputRequests <= 0)
                    return false;

                _outputRequests--;
                return true;
            }
        }

        private bool WaitForInputRequest()
        {
            var deadline = Stopwatch.GetTimestamp() + Stopwatch.Frequency / 60;
            while (Stopwatch.GetTimestamp() < deadline)
            {
                if (TryConsumeInputRequest())
                    return true;

                Thread.Yield();
            }

            return false;
        }

        private void WaitForOutputRequest()
        {
            var deadline = Stopwatch.GetTimestamp() + Stopwatch.Frequency / 60;
            while (Stopwatch.GetTimestamp() < deadline)
            {
                lock (_eventSync)
                {
                    if (_outputRequests > 0)
                        return;
                }

                Thread.Yield();
            }
        }

        private static IEnumerable<Activate> EnumerateEncoderActivations(TRegisterTypeInformation inputInfo, TRegisterTypeInformation outputInfo, TransformEnumFlag flags)
        {
            var category = TransformCategoryGuids.VideoEncoder;
            var activationArrayPtr = IntPtr.Zero;
            var inputInfoPtr = AllocateNativeTypeInfo(inputInfo);
            var outputInfoPtr = AllocateNativeTypeInfo(outputInfo);

            try
            {
                var hr = MFTEnumEx(
                    ref category,
                    (int)flags,
                    inputInfoPtr,
                    outputInfoPtr,
                    out activationArrayPtr,
                    out var activationCount);

                if (hr < 0)
                    Marshal.ThrowExceptionForHR(hr);

                for (var i = 0; i < activationCount; i++)
                {
                    var activationPtr = Marshal.ReadIntPtr(activationArrayPtr, i * IntPtr.Size);
                    if (activationPtr != IntPtr.Zero)
                        yield return new Activate(activationPtr);
                }
            }
            finally
            {
                if (activationArrayPtr != IntPtr.Zero)
                    CoTaskMemFree(activationArrayPtr);
                Marshal.FreeHGlobal(inputInfoPtr);
                Marshal.FreeHGlobal(outputInfoPtr);
            }
        }

        private static IntPtr AllocateNativeTypeInfo(TRegisterTypeInformation typeInfo)
        {
            var ptr = Marshal.AllocHGlobal(Marshal.SizeOf<NativeMftRegisterTypeInfo>());
            Marshal.StructureToPtr(
                new NativeMftRegisterTypeInfo { GuidMajorType = typeInfo.GuidMajorType, GuidSubtype = typeInfo.GuidSubtype },
                ptr,
                false);
            return ptr;
        }

        private static string GetActivationString(Activate activation, MediaAttributeKey<string> key, string fallback)
        {
            try
            {
                var value = activation.Get<string>(key);
                return string.IsNullOrWhiteSpace(value) ? fallback : value;
            }
            catch
            {
                return fallback;
            }
        }

        private static bool TryReadSampleBytes(Sample sample, out byte[] data)
        {
            data = Array.Empty<byte>();
            try
            {
                using var contiguous = sample.ConvertToContiguousBuffer();
                int length = contiguous.CurrentLength;
                if (length <= 0)
                    return false;

                int maxLength;
                int currentLength;
                var ptr = contiguous.Lock(out maxLength, out currentLength);
                try
                {
                    data = new byte[length];
                    Marshal.Copy(ptr, data, 0, data.Length);
                    return true;
                }
                finally
                {
                    contiguous.Unlock();
                }
            }
            catch (Exception ex)
            {
                DiagnosticLogService.WriteLine($"[ScreenShare:AV1X] Output sample read failed: {ex}");
                return false;
            }
        }

        private static long GetSampleTimestampMilliseconds(Sample sample)
        {
            try { return Math.Max(0, sample.SampleTime / 10_000L); }
            catch { return 0; }
        }

        private static long PackRatio(int high, int low)
        {
            return ((long)high << 32) | (uint)low;
        }

        private static unsafe void ConvertBitmapToNv12(Bitmap bitmap, int width, int height, byte[] nv12)
        {
            using var source = bitmap.Width == width && bitmap.Height == height
                ? new Bitmap(bitmap)
                : new Bitmap(bitmap, width, height);

            var rect = new Rectangle(0, 0, width, height);
            var data = source.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                int uvStart = width * height;
                fixed (byte* nv12Base = nv12)
                {
                    for (var y = 0; y < height; y++)
                    {
                        var srcRow = (byte*)data.Scan0 + y * data.Stride;
                        var yPlane = nv12Base + y * width;
                        for (var x = 0; x < width; x++)
                        {
                            var p = srcRow + x * 4;
                            yPlane[x] = ClampToByte(((47 * p[2]) + (157 * p[1]) + (16 * p[0]) + (16 << 8)) >> 8);
                        }
                    }

                    for (var y = 0; y < height; y += 2)
                    {
                        var uvRow = nv12Base + uvStart + (y / 2) * width;
                        var y1 = Math.Min(y + 1, height - 1);
                        var row0 = (byte*)data.Scan0 + y * data.Stride;
                        var row1 = (byte*)data.Scan0 + y1 * data.Stride;
                        for (var x = 0; x < width; x += 2)
                        {
                            var x1 = Math.Min(x + 1, width - 1);
                            AverageUv(row0 + x * 4, row0 + x1 * 4, row1 + x * 4, row1 + x1 * 4, out var u, out var v);
                            uvRow[x] = u;
                            uvRow[x + 1] = v;
                        }
                    }
                }
            }
            finally
            {
                source.UnlockBits(data);
            }
        }

        private static unsafe void AverageUv(byte* p00, byte* p01, byte* p10, byte* p11, out byte u, out byte v)
        {
            var uu = (GetU(p00) + GetU(p01) + GetU(p10) + GetU(p11)) >> 2;
            var vv = (GetV(p00) + GetV(p01) + GetV(p10) + GetV(p11)) >> 2;
            u = (byte)uu;
            v = (byte)vv;
        }

        private static unsafe int GetU(byte* p) => ClampToByte(((-26 * p[2]) - (87 * p[1]) + (112 * p[0]) + (128 << 8)) >> 8);
        private static unsafe int GetV(byte* p) => ClampToByte(((112 * p[2]) - (102 * p[1]) - (10 * p[0]) + (128 << 8)) >> 8);
        private static byte ClampToByte(int value) => value < 0 ? (byte)0 : value > 255 ? (byte)255 : (byte)value;

        public void Dispose()
        {
            lock (_eventSync)
            {
                _stopEventThread = true;
            }

            try { _eventGenerator?.Dispose(); } catch { }
            try
            {
                if (_eventThread != null && _eventThread != Thread.CurrentThread)
                    _eventThread.Join(200);
            }
            catch { }

            _eventGenerator = null;
            _eventThread = null;
            try { _encoder.ProcessMessage(TMessageType.NotifyEndOfStream, IntPtr.Zero); } catch { }
            try { _encoder.ProcessMessage(TMessageType.NotifyEndStreaming, IntPtr.Zero); } catch { }
            _encoder.Dispose();
        }

        [DllImport("mfplat.dll", ExactSpelling = true)]
        private static extern int MFTEnumEx(
            ref Guid guidCategory,
            int flags,
            IntPtr inputTypeRef,
            IntPtr outputTypeRef,
            out IntPtr activateArrayOut,
            out int activateCountRef);

        [DllImport("ole32.dll", ExactSpelling = true)]
        private static extern void CoTaskMemFree(IntPtr ptr);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int ProcessOutputNativeDelegate(
            IntPtr transform,
            int flags,
            int outputBufferCount,
            IntPtr outputSamples,
            out int status);

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeMftOutputDataBuffer
        {
            public int DwStreamID;
            public IntPtr PSample;
            public int DwStatus;
            public IntPtr PEvents;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeMftRegisterTypeInfo
        {
            public Guid GuidMajorType;
            public Guid GuidSubtype;
        }

        private sealed class NativeProcessOutputResult : IDisposable
        {
            public NativeProcessOutputResult(
                Sample? sample,
                Collection? events,
                int outputBufferStatus,
                TransformProcessOutputStatus processStatus)
            {
                Sample = sample;
                Events = events;
                OutputBufferStatus = outputBufferStatus;
                ProcessStatus = processStatus;
            }

            public Sample? Sample { get; }
            public Collection? Events { get; }
            public int OutputBufferStatus { get; }
            public TransformProcessOutputStatus ProcessStatus { get; }

            public void Dispose()
            {
                try { Sample?.Dispose(); } catch { }
                try { Events?.Dispose(); } catch { }
            }
        }
    }

    public sealed class EncodedVideoFrame
    {
        public EncodedVideoFrame(byte[] data, bool isKeyFrame, long timestampMilliseconds, string codec)
        {
            Data = data;
            IsKeyFrame = isKeyFrame;
            TimestampMilliseconds = timestampMilliseconds;
            Codec = codec;
        }

        public byte[] Data { get; }
        public bool IsKeyFrame { get; }
        public long TimestampMilliseconds { get; }
        public string Codec { get; }
    }
}

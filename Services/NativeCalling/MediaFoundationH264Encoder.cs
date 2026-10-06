using SharpDX;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using SharpDX.MediaFoundation;
using SharpDX.Mathematics.Interop;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Zink.Services.Recording;

namespace Zink.Services.NativeCalling
{
    public sealed class MediaFoundationH264Encoder : IH264VideoEncoder
    {
        private static readonly Guid CmsH264EncoderMft = new("6CA50344-051A-4DED-9779-A43305165E35");
        private static readonly Guid CodecApiAvLowLatencyMode = new("9C27891A-ED7A-40E1-88E8-B22727A024EE");
        private static readonly Guid CodecApiAvEncCommonLowLatency = new("9D3ECD55-89E8-490A-970A-0C9548D5A56E");
        private static readonly Guid CodecApiAvEncCommonRealTime = new("143A0FF6-A131-43DA-B81E-98FBB8EC378E");
        private static readonly Guid CodecApiAvEncCommonQualityVsSpeed = new("98332DF8-03CD-476B-89FA-3F9E442DEC9F");
        private static readonly Guid CodecApiAvEncVideoMaxKeyframeDistance = new("2987123A-BA93-4704-B489-EC1E5F25292C");
        private static readonly Guid CodecApiAvEncVideoForceKeyFrame = new("398C1B98-8353-475A-9EF2-8F265D260345");
        private static readonly Guid CodecApiAvEncVideoNumGopsPerIdr = new("83BC5BDB-5B89-4521-8F66-33151C373176");
        private static readonly Guid CodecApiAvEncMpvGopSize = new("95F31B26-95A4-41AA-9303-246A7FC6EEF1");
        private static readonly Guid CodecApiAvEncMpvDefaultBPictureCount = new("8D390AAC-DC5C-4200-B57F-814D04BABAB2");
        private static readonly Guid CodecApiAvEncCommonRateControlMode = new("1C0608E9-370C-4710-8A58-CB6181C42423");
        private const int MfENeedMoreInput = unchecked((int)0xC00D6D72);
        private const int MfEStreamChange = unchecked((int)0xC00D6D61);
        private const int MfEUnsupportedD3DType = unchecked((int)0xC00D6D76);
        private const int MfENoEventsAvailable = unchecked((int)0xC00D3E80);
        private const int MfENotAccepting = unchecked((int)0xC00D36B5);
        private const int EUnexpected = unchecked((int)0x8000FFFF);
        private const int MftOutputStreamProvidesSamples = 0x00000100;
        private const int MftOutputStreamCanProvideSamples = 0x00000200;
        private const int DxgiInputTexturePoolSize = 24;
        private const int ImfTransformProcessOutputVtableSlot = 25;
        private static readonly int[] YFromR = BuildContributionTable(47, 16 << 8);
        private static readonly int[] YFromG = BuildContributionTable(157, 0);
        private static readonly int[] YFromB = BuildContributionTable(16, 0);
        private static readonly int[] UFromR = BuildContributionTable(-26, 128 << 8);
        private static readonly int[] UFromG = BuildContributionTable(-87, 0);
        private static readonly int[] UFromB = BuildContributionTable(112, 0);
        private static readonly int[] VFromR = BuildContributionTable(112, 128 << 8);
        private static readonly int[] VFromG = BuildContributionTable(-102, 0);
        private static readonly int[] VFromB = BuildContributionTable(-10, 0);
        private static readonly Guid CodecApiInterfaceId = new("901DB4C7-31CE-41A2-85DC-8FA0BF41B8DA");


        private Transform _encoder = null!;
        private readonly int _width;
        private readonly int _height;
        private readonly int _frameRate;
        private readonly long _frameDuration100Ns;
        private int _recoveryKeyFrameIntervalFrames;
        private int _bitrate;
        private bool _useRgb32Input;
        private bool _useDxgiSurfaceInput;
        private bool _useDxgiSurfaceInputForHardwareEncoder = true;
        private byte[]? _nv12Buffer;
        private int _inputBufferLength;
        private string _encoderMode = "Not started";
        private bool _isHardwareAccelerated;
        private bool _dxgiDeviceManagerAttached;
        private long _sampleTime;
        private long _lastSubmittedSampleTime = -1;
        private int _frameIndex;
        private int _forceNextKeyFrame;
        private int _recoveryKeyFrameRequestPending;
        private bool _loggedFirstOutputFrame;
        private bool _loggedOutputStreamMode;
        private bool _loggedForceKeyFrameUnavailable;
        private byte[]? _cachedParameterSetsAnnexB;
        private DirectX12VideoDeviceManager? _directX12VideoDeviceManager;
        private SharpDX.Direct3D11.Device? _nativeMediaFoundationDevice;
        private readonly SharpDX.Direct3D11.Device? _preferredMediaFoundationDevice;
        private readonly ScreenShareH264EncoderFamily _preferredEncoderFamily;
        private readonly bool _enableHdr10;
        private Guid OutputSubtype => _enableHdr10 ? VideoFormatGuids.Hevc : VideoFormatGuids.H264;
        private Guid InputSubtype => _enableHdr10 ? VideoFormatGuids.P010 : VideoFormatGuids.NV12;
        private Format InputTextureFormat => _enableHdr10 ? Format.P010 : Format.NV12;
        private string CodecName => _enableHdr10 ? "HEVC Main10" : "H.264";
        private SharpDX.Direct3D11.Device? _encoderD3D11Device;
        private Texture2D[]? _dxgiInputTextures;
        private Texture2D? _videoProcessorOutputTexture;
        private Texture2D? _hdrReadbackTexture;
        private bool _hasConvertedGpuFrame;
        private Texture2D? _crossAdapterSourceTexture;
        private KeyedMutex? _crossAdapterSourceMutex;
        private IntPtr _crossAdapterSourceIdentity;
        private Texture2D? _localEncoderSourceTexture;
        private VideoProcessorEnumerator? _videoProcessorEnumerator;
        private VideoProcessor? _videoProcessor;
        private VideoProcessorOutputView? _videoProcessorOutputView;
        private ZinkHdrGpuConverter? _hdrGpuConverter;
        private ZinkHdrToSdrGpuConverter? _streamHdrConverter;
        private Texture2D? _streamSdrTexture;
        private bool _loggedStreamHdrConversion;
        private int _videoProcessorSourceWidth;
        private int _videoProcessorSourceHeight;
        private int _dxgiInputTextureIndex;
        private DXGIDeviceManager? _dxgiDeviceManager;
        private MediaEventGenerator? _hardwareEventGenerator;
        private readonly Queue<PendingHardwareInputSample> _pendingHardwareInputs = new();
        private readonly object _hardwareEventSync = new();
        private Thread? _hardwareEventThread;
        private ProcessOutputNativeDelegate? _processOutputNative;
        private bool _stopHardwareEventThread;
        private int _hardwareInputRequests;
        private int _hardwareOutputRequests;
        private int _loggedHardwareEvents;
        private bool _useHardwareEventPump;
        private bool _gateHardwareInputOnNeedInput;
        private bool _waitBrieflyForHardwareInputRequest;
        private bool _allowRepeatedForceKeyFrameRequests;
        private bool _loggedWaitingForHardwareInput;
        private DateTimeOffset _lastHardwareInputWaitLogUtc = DateTimeOffset.MinValue;
        private string _gpuDeviceManagerMode = "Not attached";
        private bool _loggedHardwareInputBackPressure;
        private bool _loggedUnreadableHardwareOutput;
        private bool _gpuTextureInputDisabled;
        private bool _loggedFirstGpuConversion;
        private bool _loggedFirstInputSubmission;
        private bool _loggedFirstAcceptedInput;
        private DateTimeOffset _lastOutputStreamChangeLogUtc = DateTimeOffset.MinValue;
        private long _lastNonNvidiaFallbackOutputPollTicks;
        private long _lastNonNvidiaOpportunisticInputTicks;
        private DateTimeOffset _lastNonNvidiaFallbackOutputPollLogUtc = DateTimeOffset.MinValue;

        static MediaFoundationH264Encoder()
        {
            try
            {
                MediaManager.Startup();
            }
            catch
            {
            }
        }

        public MediaFoundationH264Encoder(
            int width,
            int height,
            int bitrate,
            bool preferHardware = true,
            bool requireHardware = false,
            SharpDX.Direct3D11.Device? preferredMediaFoundationDevice = null,
            int frameRate = ScreenShareCapturePipeline.TargetFps,
            ScreenShareH264EncoderFamily preferredEncoderFamily = ScreenShareH264EncoderFamily.Auto,
            bool enableHdr10 = false)
        {
            _width = width;
            _height = height;
            _frameRate = Math.Clamp(frameRate, 1, 120);
            _frameDuration100Ns = 10_000_000L / _frameRate;
            _recoveryKeyFrameIntervalFrames = _frameRate * 2;
            _bitrate = bitrate;
            _preferredMediaFoundationDevice = preferredMediaFoundationDevice;
            _preferredEncoderFamily = preferredEncoderFamily;
            _enableHdr10 = enableHdr10;
            Debug.WriteLine($"[ScreenShare:H264] Creating {CodecName} encoder {width}x{height} @ {_frameRate}fps @ {bitrate}bps.");
            InitializeEncoder(bitrate, preferHardware, requireHardware);
        }

        private void InitializeEncoder(int bitrate, bool preferHardware, bool requireHardware)
        {
            var allowHardware = preferHardware || requireHardware;
            var forceNativeDxgiDeviceManager = false;

            while (true)
            {
                var selection = CreateEncoderTransform(allowHardware, requireHardware, _preferredEncoderFamily, OutputSubtype, InputSubtype, CodecName);
                _encoder = selection.Encoder;
                _encoderMode = selection.Mode;
                _isHardwareAccelerated = selection.IsHardwareAccelerated;
                _dxgiDeviceManagerAttached = false;
                _useHardwareEventPump = selection.IsHardwareAccelerated;
                var isNvidiaEncoder = IsNvidiaEncoderMode(selection.Mode);
                // NVENC can briefly stall asynchronous output if periodic force-IDR
                // aligns with live DXGI submission. Use a longer normal cadence,
                // while explicit receiver recovery still requests immediate IDR.
                _recoveryKeyFrameIntervalFrames = _frameRate * (isNvidiaEncoder ? 8 : 2);
                // Keep HDR P010 surfaces on the selected GPU. The old NVIDIA
                // exclusion forced every 4K frame through a synchronous GPU-to-CPU
                // readback and capped replay near 68 fps. Encoder initialization
                // already retries a native D3D11 manager, and the replay service
                // retains its Intel oneVPL recovery if the driver rejects P010.
                _useDxgiSurfaceInputForHardwareEncoder =
                    ShouldUseDxgiSurfaceInputForEncoder(selection.Mode);
                // NVIDIA's asynchronous MFT is event-driven and should be gated by
                // METransformNeedInput. Intel Quick Sync/Arc can accept several
                // DXGI samples in flight; restricting it to one pending sample
                // serializes conversion/encoding and reduced a requested 60 fps
                // replay to about 16-18 fps.
                _gateHardwareInputOnNeedInput = selection.IsHardwareAccelerated &&
                    _useDxgiSurfaceInputForHardwareEncoder && isNvidiaEncoder;
                _waitBrieflyForHardwareInputRequest = false;
                _allowRepeatedForceKeyFrameRequests = isNvidiaEncoder;

                try
                {
                    if (selection.IsHardwareAccelerated)
                    {
                        // Hardware encoder MFTs can be asynchronous even when they
                        // accept system-memory input. They reject SetInputType with
                        // MF_E_TRANSFORM_ASYNC_LOCKED unless the async transform is
                        // unlocked first. Previously this only happened for DXGI
                        // input, causing replay capture to recreate the encoder and
                        // log the same failure on every captured frame.
                        TryUnlockAsyncHardwareTransform(_encoder);

                        if (_useDxgiSurfaceInputForHardwareEncoder)
                        {
                            TryAttachDxgiDeviceManager(forceNativeDxgiDeviceManager);
                        }
                        else
                        {
                            if (_enableHdr10 && _preferredMediaFoundationDevice != null)
                            {
                                _encoderD3D11Device = _preferredMediaFoundationDevice;
                                EnableMultithreadProtection(_encoderD3D11Device);
                            }
                            _gpuDeviceManagerMode = "System-memory hardware input; DXGI manager intentionally not attached";
                            Debug.WriteLine("[ScreenShare:H264] Using system-memory hardware input with no encoder DXGI manager.");
                        }
                    }

                    try
                    {
                        var enableHardwareAsyncMode = selection.IsHardwareAccelerated &&
                            (_useDxgiSurfaceInputForHardwareEncoder || isNvidiaEncoder);
                        ConfigureEncoder(
                            bitrate,
                            enableHardwareAsyncMode);
                    }
                    catch (SharpDXException ex) when (
                        selection.IsHardwareAccelerated &&
                        _dxgiDeviceManagerAttached &&
                        ex.ResultCode.Code == MfEUnsupportedD3DType &&
                        !forceNativeDxgiDeviceManager)
                    {
                        Debug.WriteLine("[ScreenShare:H264] Hardware encoder rejected the D3D11On12 media input type; recreating it with a native D3D11 DXGI manager.");

                        try
                        {
                            _encoder.Dispose();
                        }
                        catch
                        {
                        }

                        DisposeGpuEncodingResources();
                        forceNativeDxgiDeviceManager = true;
                        continue;
                    }

                    return;
                }
                catch (Exception ex) when (selection.IsHardwareAccelerated && requireHardware)
                {
                    Debug.WriteLine($"[ScreenShare:H264] Forced GPU encoder rejected configuration: {ex.Message}");

                    try
                    {
                        _encoder.Dispose();
                    }
                    catch
                    {
                    }

                    DisposeGpuEncodingResources();
                    throw new InvalidOperationException(
                        "GPU hardware H.264 encoding is required, but the hardware encoder rejected the current configuration.",
                        ex);
                }
                catch (Exception ex) when (selection.IsHardwareAccelerated)
                {
                    Debug.WriteLine($"[ScreenShare:H264] Hardware encoder rejected configuration, falling back to software MFT: {ex.Message}");

                    try
                    {
                        _encoder.Dispose();
                    }
                    catch
                    {
                    }

                    DisposeGpuEncodingResources();
                    allowHardware = false;
                }
            }
        }

        private void ConfigureEncoder(int bitrate, bool enableHardwareAsyncMode)
        {
            _bitrate = bitrate;
            RealtimeModeEnabled = false;
            LowLatencyOutputEnabled = false;
            _useRgb32Input = false;
            _useDxgiSurfaceInput = false;
            _nv12Buffer = null;
            _inputBufferLength = 0;
            var isNvidiaEncoder = IsNvidiaEncoderMode(_encoderMode);
            _useHardwareEventPump = enableHardwareAsyncMode && _isHardwareAccelerated;
            _gateHardwareInputOnNeedInput = enableHardwareAsyncMode &&
                _isHardwareAccelerated && IsNvidiaEncoderMode(_encoderMode);
            _waitBrieflyForHardwareInputRequest = false;

            RealtimeModeEnabled = TryEnableRealtimeEncoderMode(_encoder, enableHardwareAsyncMode);

            using var outputType = CreateH264OutputType(bitrate);
            LowLatencyOutputEnabled = TrySetLowLatencyOutputTypeAttributes(outputType);
            _ = GameReplayLog.WriteAsync(nameof(MediaFoundationH264Encoder),
                $"Setting encoder output type. Encoder={_encoderMode}, Codec={CodecName}, Size={_width}x{_height}, Fps={_frameRate}, Bitrate={bitrate}, HDR10={_enableHdr10}, Async={enableHardwareAsyncMode}");
            _encoder.SetOutputType(0, outputType, 0);
            _ = GameReplayLog.WriteAsync(nameof(MediaFoundationH264Encoder),
                "Encoder output type accepted.");

            if (_isHardwareAccelerated && _useDxgiSurfaceInputForHardwareEncoder)
            {
                if (!_dxgiDeviceManagerAttached || _encoderD3D11Device == null)
                    throw new InvalidOperationException("The GPU encoder requires a DXGI device manager and D3D11 input device.");

                _nv12Buffer = new byte[_enableHdr10
                    ? _width * _height * 3
                    : _width * _height * 3 / 2];
                _inputBufferLength = _nv12Buffer.Length;
                SetInputType(InputSubtype, _enableHdr10 ? "D3D11 P010 DXGI surface" : "D3D11 NV12 DXGI surface");
                _ = GameReplayLog.WriteAsync(nameof(MediaFoundationH264Encoder),
                    $"Encoder input type accepted. Subtype={InputSubtype}, DXGI=True, DeclaredBind={(_enableHdr10 ? BindFlags.RenderTarget : BindFlags.None)}, Usage={ResourceUsage.Default}, DeviceManager={_gpuDeviceManagerMode}");
                EnsureDxgiInputTextures();
                _useDxgiSurfaceInput = true;
                if (_useHardwareEventPump)
                    InitializeHardwareEventPump();
                else
                    Debug.WriteLine("[ScreenShare:H264] Using optimistic synchronous input/output drain for the non-NVIDIA hardware encoder.");
                Debug.WriteLine("[ScreenShare:H264] Using D3D11 NV12 DXGI surface input for the GPU hardware encoder.");
            }
            else
            {
                ConfigureSystemMemoryInput();
            }

            _encoder.ProcessMessage(TMessageType.NotifyBeginStreaming, IntPtr.Zero);
            _encoder.ProcessMessage(TMessageType.NotifyStartOfStream, IntPtr.Zero);
            _ = GameReplayLog.WriteAsync(nameof(MediaFoundationH264Encoder),
                $"Encoder streaming messages accepted. EventPump={_useHardwareEventPump}, InputGate={_gateHardwareInputOnNeedInput}");
            if (_isHardwareAccelerated && !_useDxgiSurfaceInputForHardwareEncoder && _useHardwareEventPump)
                InitializeHardwareEventPump();
            Debug.WriteLine("[ScreenShare:H264] Encoder started.");
        }

        private bool ShouldUseHardwareEventPump()
        {
            return _isHardwareAccelerated && IsNvidiaEncoderMode(_encoderMode);
        }

        private void ConfigureSystemMemoryInput()
        {
            if (_enableHdr10)
            {
                _nv12Buffer = new byte[_width * _height * 3];
                _inputBufferLength = _nv12Buffer.Length;
                SetInputType(VideoFormatGuids.P010, "system-memory P010");
                _ = GameReplayLog.WriteAsync(nameof(MediaFoundationH264Encoder),
                    $"Using system-memory P010 input because the NVIDIA HEVC MFT rejects D3D11 P010 samples. GPUConversionDevice={_gpuDeviceManagerMode}");
                return;
            }

            _useRgb32Input = !_isHardwareAccelerated && TrySetInputType(VideoFormatGuids.Rgb32, "RGB32");
            if (_useRgb32Input)
            {
                _inputBufferLength = _width * _height * 4;
                Debug.WriteLine("[ScreenShare:H264] Using RGB32 input to avoid managed NV12 conversion.");
            }
            else
            {
                if (_isHardwareAccelerated)
                    Debug.WriteLine("[ScreenShare:H264] Using NV12 input for the GPU hardware encoder.");

                _nv12Buffer = new byte[_width * _height * 3 / 2];
                _inputBufferLength = _nv12Buffer.Length;
                if (!TrySetInputType(VideoFormatGuids.NV12, "NV12"))
                    throw new InvalidOperationException("The H.264 encoder did not accept RGB32 or NV12 input.");
            }
        }

        public string EncoderMode => _encoderMode;
        public string InputFormat
        {
            get
            {
                if (_useDxgiSurfaceInput)
                    return _enableHdr10 ? "D3D11 P010 DXGI surface" : "D3D11 NV12 DXGI surface";
                if (_enableHdr10)
                    return "P010 system-memory hardware encoder input";
                return _useRgb32Input
                    ? "RGB32 direct"
                    : (_isHardwareAccelerated ? "NV12 hardware encoder input" : "NV12 managed fallback");
            }
        }
        public bool IsHardwareAccelerated => _isHardwareAccelerated;
        public bool DxgiDeviceManagerAttached => _dxgiDeviceManagerAttached;
        public string GpuDeviceManagerMode => _gpuDeviceManagerMode;
        public int RecoveryKeyFrameInterval => _recoveryKeyFrameIntervalFrames;
        public bool RealtimeModeEnabled { get; private set; }
        public bool LowLatencyOutputEnabled { get; private set; }
        public bool CanEncodeGpuTexture => !_gpuTextureInputDisabled && _useDxgiSurfaceInputForHardwareEncoder && _useDxgiSurfaceInput && _encoderD3D11Device != null;
        public int PendingHardwareInputs => _pendingHardwareInputs.Count;
        public int HardwareInputRequests => Volatile.Read(ref _hardwareInputRequests);
        public int HardwareOutputRequests => Volatile.Read(ref _hardwareOutputRequests);
        public bool UsesHardwareEventPump => _useHardwareEventPump && _hardwareEventGenerator != null;
        public bool RecoveryKeyFrameRequestPending => Volatile.Read(ref _recoveryKeyFrameRequestPending) == 1;

        public void ForceNextKeyFrame()
        {
            if (Interlocked.CompareExchange(ref _recoveryKeyFrameRequestPending, 1, 0) == 0)
                Interlocked.Exchange(ref _forceNextKeyFrame, 1);
        }

        public IReadOnlyList<H264EncodedFrame> Encode(Bitmap bitmap, long? timestampMilliseconds = null)
        {
            if (_useDxgiSurfaceInputForHardwareEncoder && _useDxgiSurfaceInput)
                return EncodeDxgiSurface(bitmap);

            var frames = new List<H264EncodedFrame>();
            var consumedHardwareInputRequest = false;
            if (_hardwareEventGenerator != null && _gateHardwareInputOnNeedInput)
            {
                frames.AddRange(DrainOutput());
                consumedHardwareInputRequest = TryConsumeHardwareInputRequestForFrame();
                if (!consumedHardwareInputRequest)
                {
                    var now = DateTimeOffset.UtcNow;
                    if (now - _lastHardwareInputWaitLogUtc >= TimeSpan.FromSeconds(2))
                    {
                        _loggedWaitingForHardwareInput = true;
                        _lastHardwareInputWaitLogUtc = now;
                        Debug.WriteLine("[ScreenShare:H264] Waiting for hardware encoder METransformNeedInput before submitting system-memory frames.");
                    }

                    return frames;
                }
            }

            using var inputBuffer = MediaFactory.CreateMemoryBuffer(_inputBufferLength);
            int maxLength;
            int currentLength;
            var inputPtr = inputBuffer.Lock(out maxLength, out currentLength);
            int bytesWritten;
            try
            {
                bytesWritten = FillInputBuffer(bitmap, inputPtr);
            }
            finally
            {
                inputBuffer.Unlock();
            }

            inputBuffer.CurrentLength = bytesWritten;

            using var sample = MediaFactory.CreateSample();
            sample.AddBuffer(inputBuffer);
            sample.SampleTime = GetNextInputSampleTime100Ns(timestampMilliseconds);
            sample.SampleDuration = _frameDuration100Ns;

            RequestRecoveryKeyFrameIfNeeded();
            try
            {
                _encoder.ProcessInput(0, sample, 0);
            }
            catch (SharpDXException ex) when (ex.ResultCode.Code == MfENotAccepting)
            {
                if (consumedHardwareInputRequest)
                    ReturnHardwareInputRequest();

                var now = DateTimeOffset.UtcNow;
                if (now - _lastHardwareInputWaitLogUtc >= TimeSpan.FromSeconds(2))
                {
                    _loggedWaitingForHardwareInput = true;
                    _lastHardwareInputWaitLogUtc = now;
                    Debug.WriteLine($"[ScreenShare:H264] System-memory input submit was rejected because the hardware encoder is not accepting input yet: 0x{ex.ResultCode.Code:X8} {ex.Message}");
                }

                return frames;
            }
            catch
            {
                if (consumedHardwareInputRequest)
                    ReturnHardwareInputRequest();

                throw;
            }

            frames.AddRange(DrainOutput());
            _frameIndex++;
            return frames;
        }

        private unsafe IReadOnlyList<H264EncodedFrame> EncodeDxgiSurface(Bitmap bitmap)
        {
            if (_nv12Buffer == null)
                throw new InvalidOperationException("NV12 input buffer was not initialized.");
            if (_encoderD3D11Device == null)
                throw new InvalidOperationException("D3D11 input device was not initialized.");

            var frames = new List<H264EncodedFrame>();
            if (_hardwareEventGenerator == null || IsNvidiaEncoderMode(_encoderMode))
                frames.AddRange(DrainOutput());

            ConvertBitmapToNv12(bitmap, _width, _height, _nv12Buffer);

            var consumedHardwareInputRequest = false;
            if (_hardwareEventGenerator != null && _gateHardwareInputOnNeedInput && !TryConsumeHardwareInputRequestForFrame())
            {
                var now = DateTimeOffset.UtcNow;
                if (now - _lastHardwareInputWaitLogUtc >= TimeSpan.FromSeconds(2))
                {
                    _loggedWaitingForHardwareInput = true;
                    _lastHardwareInputWaitLogUtc = now;
                    Debug.WriteLine("[ScreenShare:H264] Waiting for hardware encoder METransformNeedInput before submitting DXGI frames.");
                }

                return frames;
            }
            else if (_hardwareEventGenerator != null && _gateHardwareInputOnNeedInput)
            {
                consumedHardwareInputRequest = true;
            }

            var texture = TryGetNextAvailableDxgiInputTexture();
            if (texture == null)
            {
                if (consumedHardwareInputRequest)
                    ReturnHardwareInputRequest();

                if (!_loggedHardwareInputBackPressure)
                {
                    _loggedHardwareInputBackPressure = true;
                    Debug.WriteLine("[ScreenShare:H264] GPU input texture pool is full; waiting for hardware encoder output before submitting more frames.");
                }

                return frames;
            }

            fixed (byte* nv12 = _nv12Buffer)
            {
                var box = new DataBox((IntPtr)nv12, _width, _nv12Buffer.Length);
                _encoderD3D11Device.ImmediateContext.UpdateSubresource(box, texture, 0);
                _encoderD3D11Device.ImmediateContext.Flush();
            }

            SubmitDxgiTexture(texture, frames, consumedHardwareInputRequest);
            return frames;
        }

        public IReadOnlyList<H264EncodedFrame> EncodeGpuBgraTexture(Texture2D sourceTexture, int sourceWidth, int sourceHeight)
        {
            return EncodeGpuBgraTexture(sourceTexture, sourceWidth, sourceHeight, timestampMilliseconds: null);
        }

        public IReadOnlyList<H264EncodedFrame> EncodeGpuBgraTexture(Texture2D sourceTexture, int sourceWidth, int sourceHeight, long? timestampMilliseconds)
        {
            if (_enableHdr10 && !_useDxgiSurfaceInput)
                return EncodeGpuHdrSystemMemory(sourceTexture, sourceWidth, sourceHeight, timestampMilliseconds);

            if (_gpuTextureInputDisabled)
                throw new InvalidOperationException("GPU texture input has been disabled after a D3D video processor failure.");
            if (_encoderD3D11Device == null)
                throw new InvalidOperationException("D3D11 input device was not initialized.");
            if (!_useDxgiSurfaceInput)
                throw new InvalidOperationException("The encoder is not using D3D11 DXGI input.");

            var frames = new List<H264EncodedFrame>();
            if (_hardwareEventGenerator == null || IsNvidiaEncoderMode(_encoderMode))
                frames.AddRange(DrainOutput());

            var consumedHardwareInputRequest = false;
            if (_hardwareEventGenerator != null && _gateHardwareInputOnNeedInput)
                consumedHardwareInputRequest = TryConsumeHardwareInputRequestForFrame();

            if (_hardwareEventGenerator != null && _gateHardwareInputOnNeedInput && !consumedHardwareInputRequest)
            {
                frames.AddRange(DrainOutput());

                if (!IsNvidiaEncoderMode(_encoderMode) && ShouldUseNonNvidiaOpportunisticInput())
                {
                    consumedHardwareInputRequest = true;
                }
                else
                {
                    var now = DateTimeOffset.UtcNow;
                    if (now - _lastHardwareInputWaitLogUtc >= TimeSpan.FromSeconds(2))
                    {
                        _loggedWaitingForHardwareInput = true;
                        _lastHardwareInputWaitLogUtc = now;
                        Debug.WriteLine("[ScreenShare:H264] Hardware encoder has not requested input yet; draining output and skipping this frame to keep realtime pacing.");
                    }

                    return frames;
                }
            }

            var texture = TryGetNextAvailableDxgiInputTexture();
            if (texture == null)
            {
                if (consumedHardwareInputRequest)
                    ReturnHardwareInputRequest();

                if (!_loggedHardwareInputBackPressure)
                {
                    _loggedHardwareInputBackPressure = true;
                    Debug.WriteLine("[ScreenShare:H264] GPU input texture pool is full; waiting for hardware encoder output before submitting more frames.");
                }

                return frames;
            }

            try
            {
                ConvertBgraTextureToNv12Texture(sourceTexture, sourceWidth, sourceHeight, texture);
            }
            catch (SharpDXException ex) when (ex.ResultCode.Code == unchecked((int)0x80070057))
            {
                if (consumedHardwareInputRequest)
                    ReturnHardwareInputRequest();
                var description = sourceTexture.Description;
                throw new InvalidOperationException(
                    $"GPU texture video processor input was rejected by the hardware encoder ({ex.ResultCode}). source={description.Width}x{description.Height}; format={description.Format}; bind={description.BindFlags}; usage={description.Usage}; options={description.OptionFlags}. The bitmap fallback is disabled so the GPU path can be fixed.",
                    ex);
            }
            catch
            {
                if (consumedHardwareInputRequest)
                    ReturnHardwareInputRequest();
                throw;
            }

            SubmitDxgiTexture(texture, frames, consumedHardwareInputRequest, timestampMilliseconds);
            return frames;
        }

        public IReadOnlyList<H264EncodedFrame> EncodeRepeatedGpuFrame(long? timestampMilliseconds)
        {
            var frames = new List<H264EncodedFrame>();
            if (_encoderD3D11Device == null || !_useDxgiSurfaceInput ||
                _videoProcessorOutputTexture == null || !_hasConvertedGpuFrame)
                return frames;

            if (_hardwareEventGenerator == null || IsNvidiaEncoderMode(_encoderMode))
                frames.AddRange(DrainOutput());

            var consumedHardwareInputRequest = false;
            if (_hardwareEventGenerator != null && _gateHardwareInputOnNeedInput)
                consumedHardwareInputRequest = TryConsumeHardwareInputRequestForFrame();

            if (_hardwareEventGenerator != null && _gateHardwareInputOnNeedInput && !consumedHardwareInputRequest)
            {
                frames.AddRange(DrainOutput());
                return frames;
            }

            var texture = TryGetNextAvailableDxgiInputTexture();
            if (texture == null)
            {
                if (consumedHardwareInputRequest)
                    ReturnHardwareInputRequest();
                return frames;
            }

            // The desktop image is unchanged. Reuse the most recently converted
            // NV12 surface instead of repeating a full-resolution video-processor
            // conversion. The encoder input texture remains independently owned.
            _encoderD3D11Device.ImmediateContext.CopyResource(_videoProcessorOutputTexture, texture);
            SubmitDxgiTexture(texture, frames, consumedHardwareInputRequest, timestampMilliseconds);
            return frames;
        }

        private IReadOnlyList<H264EncodedFrame> EncodeGpuHdrSystemMemory(
            Texture2D sourceTexture,
            int sourceWidth,
            int sourceHeight,
            long? timestampMilliseconds)
        {
            if (_encoderD3D11Device == null || _nv12Buffer == null)
                throw new InvalidOperationException("HDR P010 conversion resources were not initialized.");

            Texture2D converted = EnsureVideoProcessorOutputTexture();
            Texture2D processorSource = AcquireEncoderSourceTexture(sourceTexture, out KeyedMutex? sourceMutex);
            try
            {
                _hdrGpuConverter ??= new ZinkHdrGpuConverter(_encoderD3D11Device);
                _hdrGpuConverter.Convert(processorSource, converted);
            }
            finally
            {
                sourceMutex?.Release(0);
            }

            _hdrReadbackTexture ??= new Texture2D(_encoderD3D11Device, new Texture2DDescription
            {
                Width = _width,
                Height = _height,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.P010,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Staging,
                BindFlags = BindFlags.None,
                CpuAccessFlags = CpuAccessFlags.Read,
                OptionFlags = ResourceOptionFlags.None
            });

            _encoderD3D11Device.ImmediateContext.CopyResource(converted, _hdrReadbackTexture);
            var mapped = _encoderD3D11Device.ImmediateContext.MapSubresource(
                _hdrReadbackTexture, 0, MapMode.Read, SharpDX.Direct3D11.MapFlags.None);
            try
            {
                int packedStride = _width * 2;
                for (int row = 0; row < _height; row++)
                    Marshal.Copy(mapped.DataPointer + row * mapped.RowPitch, _nv12Buffer, row * packedStride, packedStride);
                IntPtr chroma = mapped.DataPointer + mapped.RowPitch * _height;
                int chromaOffset = packedStride * _height;
                for (int row = 0; row < _height / 2; row++)
                    Marshal.Copy(chroma + row * mapped.RowPitch, _nv12Buffer, chromaOffset + row * packedStride, packedStride);
            }
            finally
            {
                _encoderD3D11Device.ImmediateContext.UnmapSubresource(_hdrReadbackTexture, 0);
            }

            _hasConvertedGpuFrame = true;
            return SubmitSystemMemoryFrame(timestampMilliseconds);
        }

        private IReadOnlyList<H264EncodedFrame> SubmitSystemMemoryFrame(long? timestampMilliseconds)
        {
            var frames = new List<H264EncodedFrame>();
            frames.AddRange(DrainOutput());

            var consumedHardwareInputRequest = false;
            if (_hardwareEventGenerator != null && _gateHardwareInputOnNeedInput)
            {
                consumedHardwareInputRequest = TryConsumeHardwareInputRequestForFrame();
                if (!consumedHardwareInputRequest)
                    return frames;
            }

            using var inputBuffer = MediaFactory.CreateMemoryBuffer(_inputBufferLength);
            int maxLength;
            int currentLength;
            IntPtr input = inputBuffer.Lock(out maxLength, out currentLength);
            try
            {
                Marshal.Copy(_nv12Buffer!, 0, input, _inputBufferLength);
            }
            finally
            {
                inputBuffer.Unlock();
            }
            inputBuffer.CurrentLength = _inputBufferLength;
            using var sample = MediaFactory.CreateSample();
            sample.AddBuffer(inputBuffer);
            sample.SampleTime = GetNextInputSampleTime100Ns(timestampMilliseconds);
            sample.SampleDuration = _frameDuration100Ns;
            RequestRecoveryKeyFrameIfNeeded();
            try
            {
                _encoder.ProcessInput(0, sample, 0);
            }
            catch (SharpDXException ex) when (ex.ResultCode.Code == MfENotAccepting)
            {
                if (consumedHardwareInputRequest)
                    ReturnHardwareInputRequest();
                return frames;
            }
            catch
            {
                if (consumedHardwareInputRequest)
                    ReturnHardwareInputRequest();
                throw;
            }
            frames.AddRange(DrainOutput());
            _frameIndex++;
            return frames;
        }

        private void SubmitDxgiTexture(Texture2D texture, List<H264EncodedFrame> frames, bool hardwareInputRequestConsumed = true, long? timestampMilliseconds = null)
        {
            MediaBuffer? inputBuffer = null;
            Sample? sample = null;

            MediaFactory.CreateDXGISurfaceBuffer(
                typeof(Texture2D).GUID,
                texture,
                0,
                new RawBool(false),
                out inputBuffer);

            try
            {
                sample = MediaFactory.CreateSample();
                inputBuffer.CurrentLength = _inputBufferLength;
                sample.AddBuffer(inputBuffer);
                sample.SampleTime = GetNextInputSampleTime100Ns(timestampMilliseconds);
                sample.SampleDuration = _frameDuration100Ns;

                RequestRecoveryKeyFrameIfNeeded();
                if (!_loggedFirstInputSubmission)
                {
                    _loggedFirstInputSubmission = true;
                    var description = texture.Description;
                    _ = GameReplayLog.WriteAsync(nameof(MediaFoundationH264Encoder),
                        $"Submitting first encoder input. Encoder={_encoderMode}, Texture={description.Width}x{description.Height}/{description.Format}, Bind={description.BindFlags}, Usage={description.Usage}, Options={description.OptionFlags}, SampleTime={sample.SampleTime}, SampleDuration={sample.SampleDuration}, InputRequests={HardwareInputRequests}, OutputRequests={HardwareOutputRequests}");
                }
                try
                {
                    _encoder.ProcessInput(0, sample, 0);
                }
                catch (SharpDXException ex) when (ex.ResultCode.Code == unchecked((int)0xC00D36B5))
                {
                    if (hardwareInputRequestConsumed)
                        ReturnHardwareInputRequest();

                    frames.AddRange(DrainOutput());

                    if (!_loggedWaitingForHardwareInput)
                    {
                        _loggedWaitingForHardwareInput = true;
                        Debug.WriteLine($"[ScreenShare:H264] GPU texture submit was rejected because the hardware encoder is not accepting input yet: 0x{ex.ResultCode.Code:X8} {ex.Message}");
                    }

                    return;
                }
                catch (Exception ex)
                {
                    if (hardwareInputRequestConsumed)
                        ReturnHardwareInputRequest();
                    var description = texture.Description;
                    _ = GameReplayLog.WriteAsync(nameof(MediaFoundationH264Encoder),
                        $"ProcessInput failed. Error={ex}; Encoder={_encoderMode}; HDR10={_enableHdr10}; InputSubtype={InputSubtype}; Texture={description.Width}x{description.Height}/{description.Format}; Bind={description.BindFlags}; Usage={description.Usage}; Options={description.OptionFlags}; DeviceManager={_gpuDeviceManagerMode}; PendingInputs={_pendingHardwareInputs.Count}");
                    throw;
                }

                _pendingHardwareInputs.Enqueue(new PendingHardwareInputSample(sample, inputBuffer, texture));
                if (!_loggedFirstAcceptedInput)
                {
                    _loggedFirstAcceptedInput = true;
                    _ = GameReplayLog.WriteAsync(nameof(MediaFoundationH264Encoder),
                        $"First encoder input accepted. PendingInputs={_pendingHardwareInputs.Count}, InputRequests={HardwareInputRequests}, OutputRequests={HardwareOutputRequests}");
                }
                sample = null;
                inputBuffer = null;
                _loggedWaitingForHardwareInput = false;
            }
            finally
            {
                sample?.Dispose();
                inputBuffer?.Dispose();
            }

            frames.AddRange(DrainOutput());
            _frameIndex++;
        }

        private long GetNextInputSampleTime100Ns(long? timestampMilliseconds = null)
        {
            var requestedSampleTime = timestampMilliseconds.HasValue
                ? Math.Max(0L, timestampMilliseconds.Value) * 10_000L
                : _sampleTime;

            if (requestedSampleTime <= _lastSubmittedSampleTime)
                requestedSampleTime = _lastSubmittedSampleTime + _frameDuration100Ns;

            _lastSubmittedSampleTime = requestedSampleTime;
            _sampleTime = requestedSampleTime + _frameDuration100Ns;
            return requestedSampleTime;
        }

        private int FillInputBuffer(Bitmap bitmap, IntPtr inputPtr)
        {
            if (_useRgb32Input)
            {
                CopyBitmapToRgb32(bitmap, _width, _height, inputPtr);
                return _inputBufferLength;
            }

            if (_nv12Buffer == null)
                throw new InvalidOperationException("NV12 input buffer was not initialized.");

            ConvertBitmapToNv12(bitmap, _width, _height, _nv12Buffer);
            Marshal.Copy(_nv12Buffer, 0, inputPtr, _nv12Buffer.Length);
            return _nv12Buffer.Length;
        }

        private IReadOnlyList<H264EncodedFrame> DrainOutput()
        {
            var frames = new List<H264EncodedFrame>();
            _encoder.GetOutputStreamInfo(0, out var info);
            int outputBufferSize = Math.Max(info.CbSize, _width * _height);
            var useEncoderAllocatedOutput =
                (info.DwFlags & MftOutputStreamProvidesSamples) != 0 ||
                (info.DwFlags & MftOutputStreamCanProvideSamples) != 0;

            if (!_loggedOutputStreamMode)
            {
                _loggedOutputStreamMode = true;
                Debug.WriteLine($"[ScreenShare:H264] Output stream flags=0x{info.DwFlags:X}; encoder-allocated output={useEncoderAllocatedOutput}.");
            }

            if (_isHardwareAccelerated && _hardwareEventGenerator != null)
                return DrainHardwareAsyncOutput(outputBufferSize, useEncoderAllocatedOutput);

            while (true)
            {
                Sample? callerOutputSample = null;
                MediaBuffer? callerOutputBuffer = null;
                TOutputDataBuffer[]? output = null;

                try
                {
                    if (!useEncoderAllocatedOutput)
                    {
                        callerOutputSample = MediaFactory.CreateSample();
                        callerOutputBuffer = MediaFactory.CreateMemoryBuffer(outputBufferSize);
                        callerOutputSample.AddBuffer(callerOutputBuffer);
                    }

                    output = new[]
                    {
                        new TOutputDataBuffer
                        {
                            DwStreamID = 0,
                            PSample = callerOutputSample
                        }
                    };

                    try
                    {
                        _encoder.ProcessOutput(
                            TransformProcessOutputFlags.None,
                            output,
                            out _);
                    }
                    catch (SharpDXException ex) when (ex.ResultCode.Code == MfENeedMoreInput)
                    {
                        break;
                    }
                    catch (SharpDXException ex) when (ex.ResultCode.Code == MfEStreamChange)
                    {
                        TryHandleOutputStreamChange("synchronous ProcessOutput");
                        break;
                    }

                    var sampleToRead = output[0].PSample ?? callerOutputSample;
                    if (sampleToRead == null ||
                        !TryReadEncodedSampleBytes(sampleToRead, out var data, out _))
                        break;

                    AddEncodedFrame(frames, data, GetSampleTimestampMilliseconds(sampleToRead), IsCleanPoint(sampleToRead));
                }
                finally
                {
                    if (output != null)
                    {
                        try
                        {
                            output[0].PEvents?.Dispose();
                        }
                        catch
                        {
                        }

                        if (useEncoderAllocatedOutput)
                        {
                            try
                            {
                                output[0].PSample?.Dispose();
                            }
                            catch
                            {
                            }
                        }
                    }

                    callerOutputBuffer?.Dispose();
                    callerOutputSample?.Dispose();
                }
            }

            return frames;
        }

        private IReadOnlyList<H264EncodedFrame> DrainHardwareAsyncOutput(
            int outputBufferSize,
            bool useEncoderAllocatedOutput)
        {
            var frames = new List<H264EncodedFrame>();
            if (_hardwareEventGenerator == null)
                return frames;

            if (!IsNvidiaEncoderMode(_encoderMode))
            {
                while (TryConsumeHardwareOutputRequest())
                {
                    frames.AddRange(ProcessSingleOutput(outputBufferSize, useEncoderAllocatedOutput));
                    _loggedHardwareInputBackPressure = false;
                }

                if (frames.Count == 0 && ShouldUseNonNvidiaFallbackOutputPoll())
                {
                    var polledFrames = ProcessSingleOutput(outputBufferSize, useEncoderAllocatedOutput);
                    frames.AddRange(polledFrames);
                    if (polledFrames.Count > 0)
                    {
                        _loggedHardwareInputBackPressure = false;
                        while (TryConsumeHardwareOutputRequest())
                        {
                            frames.AddRange(ProcessSingleOutput(outputBufferSize, useEncoderAllocatedOutput));
                        }
                    }
                }

                return frames;
            }

            while (TryConsumeHardwareOutputRequest())
            {
                frames.AddRange(ProcessSingleOutput(outputBufferSize, useEncoderAllocatedOutput));
                _loggedHardwareInputBackPressure = false;
            }

            return frames;
        }

        private bool ShouldUseNonNvidiaFallbackOutputPoll()
        {
            if (_pendingHardwareInputs.Count == 0)
                return false;

            var nowTicks = Stopwatch.GetTimestamp();
            // This is only a safety net for non-conforming async MFTs that fail to
            // raise METransformHaveOutput. Qualcomm returns E_UNEXPECTED while it
            // legitimately has no output ready, so polling this fallback at the
            // replay frame rate creates a continuous handled-exception storm. The
            // normal event-driven output path remains immediate; only the fallback
            // probe is bounded to twice per second.
            var minimumIntervalTicks = Math.Max(1, Stopwatch.Frequency / 2);
            var lastPollTicks = Interlocked.Read(ref _lastNonNvidiaFallbackOutputPollTicks);
            if (lastPollTicks != 0 && nowTicks - lastPollTicks < minimumIntervalTicks)
                return false;

            Interlocked.Exchange(ref _lastNonNvidiaFallbackOutputPollTicks, nowTicks);

            var now = DateTimeOffset.UtcNow;
            if (now - _lastNonNvidiaFallbackOutputPollLogUtc >= TimeSpan.FromSeconds(5))
            {
                _lastNonNvidiaFallbackOutputPollLogUtc = now;
                Debug.WriteLine($"[ScreenShare:H264] Non-NVIDIA hardware encoder output event fallback poll; pendingInputs={_pendingHardwareInputs.Count}.");
            }

            return true;
        }

        private bool ShouldUseNonNvidiaOpportunisticInput()
        {
            if (_pendingHardwareInputs.Count != 0)
                return false;

            var nowTicks = Stopwatch.GetTimestamp();
            var minimumIntervalTicks = Math.Max(1, Stopwatch.Frequency / Math.Max(1, _frameRate));
            var lastInputTicks = Interlocked.Read(ref _lastNonNvidiaOpportunisticInputTicks);
            if (lastInputTicks != 0 && nowTicks - lastInputTicks < minimumIntervalTicks)
                return false;

            Interlocked.Exchange(ref _lastNonNvidiaOpportunisticInputTicks, nowTicks);
            return true;
        }

        private IReadOnlyList<H264EncodedFrame> ProcessSingleOutput(
            int outputBufferSize,
            bool useEncoderAllocatedOutput)
        {
            var frames = new List<H264EncodedFrame>();
            Sample? callerOutputSample = null;
            MediaBuffer? callerOutputBuffer = null;
            TransformProcessOutputStatus processStatus = 0;
            int outputBufferStatus = 0;
            bool processReturnedSample = false;
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
                    nativeOutput = ProcessOutputNative(
                        callerOutputSample,
                        useEncoderAllocatedOutput);
                    processStatus = nativeOutput.ProcessStatus;
                    outputBufferStatus = nativeOutput.OutputBufferStatus;
                    processReturnedSample = nativeOutput.Sample != null || callerOutputSample != null;
                }
                catch (SharpDXException ex) when (ex.ResultCode.Code == MfENeedMoreInput)
                {
                    return frames;
                }
                catch (SharpDXException ex) when (ex.ResultCode.Code == EUnexpected && !IsNvidiaEncoderMode(_encoderMode))
                {
                    return frames;
                }
                catch (SharpDXException ex) when (ex.ResultCode.Code == MfEStreamChange)
                {
                    TryHandleOutputStreamChange("hardware async ProcessOutput");
                    return frames;
                }

                var sampleToRead = nativeOutput.Sample ?? callerOutputSample;
                var data = Array.Empty<byte>();
                var diagnostics = "sample missing";
                var hasFrame = sampleToRead != null &&
                    TryReadEncodedSampleBytes(sampleToRead, out data, out diagnostics);
                if (hasFrame)
                {
                    AddEncodedFrame(frames, data, GetSampleTimestampMilliseconds(sampleToRead), IsCleanPoint(sampleToRead));
                    ReleaseCompletedHardwareInput();
                }
                else
                {
                    if (!_loggedUnreadableHardwareOutput)
                    {
                        _loggedUnreadableHardwareOutput = true;
                        var sampleDescription = sampleToRead == null
                            ? "sample=null"
                            : DescribeSample(sampleToRead);
                        Debug.WriteLine($"[ScreenShare:H264] Hardware encoder signaled output but no encoded bytes were readable; returnedSample={processReturnedSample}; processStatus={processStatus}; bufferStatus=0x{outputBufferStatus:X}; {sampleDescription}; {diagnostics}");
                    }

                    ReleaseCompletedHardwareInput();
                }
            }
            finally
            {
                nativeOutput?.Dispose();
                callerOutputBuffer?.Dispose();
                callerOutputSample?.Dispose();
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
            var processOutputPtr = Marshal.ReadIntPtr(
                vtable,
                IntPtr.Size * ImfTransformProcessOutputVtableSlot);
            _processOutputNative = Marshal.GetDelegateForFunctionPointer<ProcessOutputNativeDelegate>(processOutputPtr);
            return _processOutputNative;
        }

        private bool TryHandleOutputStreamChange(string reason)
        {
            var now = DateTimeOffset.UtcNow;
            if (now - _lastOutputStreamChangeLogUtc >= TimeSpan.FromSeconds(1))
            {
                _lastOutputStreamChangeLogUtc = now;
                Debug.WriteLine($"[ScreenShare:H264] Encoder requested output stream renegotiation after {reason}; refreshing H.264 output type.");
            }

            _loggedOutputStreamMode = false;
            _loggedUnreadableHardwareOutput = false;

            if (TrySetExplicitH264OutputType($"{reason} stream change"))
                return true;

            if (TrySelectAvailableH264OutputType($"{reason} stream change"))
                return true;

            Debug.WriteLine($"[ScreenShare:H264] Encoder output stream renegotiation failed after {reason}; no compatible H.264 output type was accepted.");
            return false;
        }

        private bool TrySetExplicitH264OutputType(string reason)
        {
            try
            {
                using var outputType = CreateH264OutputType(_bitrate);
                LowLatencyOutputEnabled = TrySetLowLatencyOutputTypeAttributes(outputType);
                _encoder.SetOutputType(0, outputType, 0);
                Debug.WriteLine($"[ScreenShare:H264] Encoder output type refreshed after {reason}: H.264 {_width}x{_height} @ {_bitrate}bps.");
                return true;
            }
            catch (SharpDXException ex)
            {
                Debug.WriteLine($"[ScreenShare:H264] Encoder rejected explicit output type after {reason}: 0x{ex.ResultCode.Code:X8} {ex.Message}");
                return false;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ScreenShare:H264] Encoder explicit output type refresh failed after {reason}: {ex.Message}");
                return false;
            }
        }

        private bool TrySelectAvailableH264OutputType(string reason)
        {
            for (var index = 0; index < 32; index++)
            {
                MediaType? candidate = null;
                try
                {
                    if (!_encoder.TryGetOutputAvailableType(0, index, out candidate) ||
                        candidate == null)
                        continue;

                    var subtype = candidate.Get(MediaTypeAttributeKeys.Subtype);
                    if (subtype != OutputSubtype)
                        continue;

                    candidate.Set(MediaTypeAttributeKeys.AvgBitrate, _bitrate);
                    candidate.Set(MediaTypeAttributeKeys.FrameSize, PackRatio(_width, _height));
                    candidate.Set(MediaTypeAttributeKeys.FrameRate, PackRatio(_frameRate, 1));
                    candidate.Set(MediaTypeAttributeKeys.PixelAspectRatio, PackRatio(1, 1));
                    candidate.Set(MediaTypeAttributeKeys.InterlaceMode, (int)VideoInterlaceMode.Progressive);
                    candidate.Set(MediaTypeAttributeKeys.MaxKeyframeSpacing, _recoveryKeyFrameIntervalFrames);
                    LowLatencyOutputEnabled = TrySetLowLatencyOutputTypeAttributes(candidate);
                    _encoder.SetOutputType(0, candidate, 0);
                    Debug.WriteLine($"[ScreenShare:H264] Encoder selected available output type {index} after {reason}: H.264 {_width}x{_height} @ {_bitrate}bps.");
                    return true;
                }
                catch (SharpDXException ex)
                {
                    if (index == 0 || index == 31)
                        Debug.WriteLine($"[ScreenShare:H264] Encoder output candidate {index} rejected after {reason}: 0x{ex.ResultCode.Code:X8} {ex.Message}");
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[ScreenShare:H264] Encoder output type candidate refresh failed after {reason}: {ex.Message}");
                    return false;
                }
                finally
                {
                    candidate?.Dispose();
                }
            }

            Debug.WriteLine($"[ScreenShare:H264] Encoder did not expose a usable H.264 output type after {reason}.");
            return false;
        }

        private static long GetSampleTimestampMilliseconds(Sample sample)
        {
            try
            {
                return Math.Max(0, sample.SampleTime / 10_000L);
            }
            catch
            {
                return 0;
            }
        }

        private void AddEncodedFrame(List<H264EncodedFrame> frames, byte[] data, long timestampMilliseconds, bool cleanPoint)
        {
            var annexBData = NormalizeH264AccessUnitToAnnexB(data, out var convertedToAnnexB);
            var hasIdr = _enableHdr10
                ? ContainsNalUnitType(annexBData, 19, true) || ContainsNalUnitType(annexBData, 20, true) || ContainsNalUnitType(annexBData, 21, true)
                : ContainsNalUnitType(annexBData, 5, false);
            var isKeyFrame = hasIdr || cleanPoint;
            if (isKeyFrame)
                Interlocked.Exchange(ref _recoveryKeyFrameRequestPending, 0);
            var hasVps = _enableHdr10 && ContainsNalUnitType(annexBData, 32, true);
            var hasSps = ContainsNalUnitType(annexBData, _enableHdr10 ? (byte)33 : (byte)7, _enableHdr10);
            var hasPps = ContainsNalUnitType(annexBData, _enableHdr10 ? (byte)34 : (byte)8, _enableHdr10);

            if ((hasVps || hasSps || hasPps) && TryExtractParameterSets(annexBData, _enableHdr10, out var parameterSets))
                _cachedParameterSetsAnnexB = parameterSets;

            if (hasIdr &&
                _cachedParameterSetsAnnexB != null &&
                (!hasSps || !hasPps || (_enableHdr10 && !hasVps)))
            {
                annexBData = CombineParameterSetsWithFrame(_cachedParameterSetsAnnexB, annexBData);
                hasSps = true;
                hasPps = true;
            }

            if (!_loggedFirstOutputFrame)
            {
                _loggedFirstOutputFrame = true;
                Debug.WriteLine(
                    $"[ScreenShare:H264] First {CodecName} frame: raw={data.Length} bytes, send={annexBData.Length} bytes, framing={(convertedToAnnexB ? "length-prefixed->AnnexB" : "AnnexB")}, vps={hasVps}, sps={hasSps}, pps={hasPps}, idr={hasIdr}, cleanPoint={cleanPoint}.");
            }

            if (!isKeyFrame && _frameIndex > 0 && _frameIndex % _recoveryKeyFrameIntervalFrames == 0)
            {
                Debug.WriteLine(
                    $"[ScreenShare:H264] Recovery keyframe interval reached at encoder frame {_frameIndex}, but output had no IDR/clean point; treating it as delta.");
            }

            frames.Add(new H264EncodedFrame(annexBData, isKeyFrame, timestampMilliseconds));
        }

        private static bool IsCleanPoint(Sample sample)
        {
            try
            {
                return sample.Get(SampleAttributeKeys.CleanPoint);
            }
            catch
            {
                return false;
            }
        }

        private static byte[] NormalizeH264AccessUnitToAnnexB(byte[] frame, out bool converted)
        {
            converted = false;

            if (frame.Length < 5 || HasStartCode(frame))
                return frame;

            if (!TryConvertLengthPrefixedNalUnits(frame, out var annexB))
                return frame;

            converted = true;
            return annexB;
        }

        private static bool HasStartCode(byte[] frame)
        {
            return frame.Length >= 4 &&
                frame[0] == 0 &&
                frame[1] == 0 &&
                (frame[2] == 1 || (frame[2] == 0 && frame[3] == 1));
        }

        private static bool TryConvertLengthPrefixedNalUnits(byte[] frame, out byte[] annexB)
        {
            var output = new List<byte>(frame.Length + 16);
            var offset = 0;

            while (offset + 4 <= frame.Length)
            {
                var nalLength =
                    (frame[offset] << 24) |
                    (frame[offset + 1] << 16) |
                    (frame[offset + 2] << 8) |
                    frame[offset + 3];
                offset += 4;

                if (nalLength <= 0 || nalLength > frame.Length - offset)
                {
                    annexB = frame;
                    return false;
                }

                AppendStartCode(output);
                for (var i = 0; i < nalLength; i++)
                    output.Add(frame[offset + i]);

                offset += nalLength;
            }

            if (offset != frame.Length || output.Count == 0)
            {
                annexB = frame;
                return false;
            }

            annexB = output.ToArray();
            return true;
        }

        private static bool TryExtractParameterSets(byte[] annexBFrame, bool hevc, out byte[] parameterSets)
        {
            var output = new List<byte>();
            var found = false;

            foreach (var nal in EnumerateAnnexBNalUnits(annexBFrame, hevc))
            {
                if (hevc ? nal.Type is not (32 or 33 or 34) : nal.Type is not (7 or 8))
                    continue;

                AppendStartCode(output);
                for (var i = nal.Offset; i < nal.Offset + nal.Length; i++)
                    output.Add(annexBFrame[i]);
                found = true;
            }

            parameterSets = output.ToArray();
            return found;
        }

        private static byte[] CombineParameterSetsWithFrame(byte[] parameterSets, byte[] frame)
        {
            var combined = new byte[parameterSets.Length + frame.Length];
            System.Buffer.BlockCopy(parameterSets, 0, combined, 0, parameterSets.Length);
            System.Buffer.BlockCopy(frame, 0, combined, parameterSets.Length, frame.Length);
            return combined;
        }

        private static void AppendStartCode(List<byte> output)
        {
            output.Add(0);
            output.Add(0);
            output.Add(0);
            output.Add(1);
        }

        private static bool TryReadEncodedSampleBytes(
            Sample sample,
            out byte[] data,
            out string diagnostics)
        {
            data = Array.Empty<byte>();
            diagnostics = string.Empty;

            try
            {
                var totalLength = sample.TotalLength;
                if (totalLength > 0)
                {
                    using var copyBuffer = MediaFactory.CreateMemoryBuffer(totalLength);
                    sample.CopyToBuffer(copyBuffer);
                    data = ReadMediaBufferBytes(copyBuffer, totalLength);
                    if (data.Length > 0)
                        return true;
                }
            }
            catch (Exception ex)
            {
                diagnostics = $"copy failed: {ex.Message}";
            }

            try
            {
                using var contiguousBuffer = sample.ConvertToContiguousBuffer();
                data = ReadMediaBufferBytes(contiguousBuffer, sample.TotalLength);
                if (data.Length > 0)
                    return true;
            }
            catch (Exception ex)
            {
                diagnostics = string.IsNullOrEmpty(diagnostics)
                    ? $"contiguous failed: {ex.Message}"
                    : $"{diagnostics}; contiguous failed: {ex.Message}";
            }

            try
            {
                var totalBytes = 0;
                var chunks = new List<byte[]>();
                for (var index = 0; index < sample.BufferCount; index++)
                {
                    using var buffer = sample.GetBufferByIndex(index);
                    var chunk = ReadMediaBufferBytes(buffer);
                    if (chunk.Length == 0)
                        continue;

                    chunks.Add(chunk);
                    totalBytes += chunk.Length;
                }

                if (totalBytes > 0)
                {
                    data = new byte[totalBytes];
                    var offset = 0;
                    foreach (var chunk in chunks)
                    {
                        System.Buffer.BlockCopy(chunk, 0, data, offset, chunk.Length);
                        offset += chunk.Length;
                    }

                    return true;
                }
            }
            catch (Exception ex)
            {
                diagnostics = string.IsNullOrEmpty(diagnostics)
                    ? $"buffers failed: {ex.Message}"
                    : $"{diagnostics}; buffers failed: {ex.Message}";
            }

            if (string.IsNullOrEmpty(diagnostics))
                diagnostics = "no readable buffers";
            return false;
        }

        private static byte[] ReadMediaBufferBytes(MediaBuffer buffer, int preferredLength = 0)
        {
            int maxLength;
            int currentLength;
            var ptr = buffer.Lock(out maxLength, out currentLength);
            try
            {
                var bytesToCopy = currentLength > 0 ? currentLength : buffer.CurrentLength;
                if (bytesToCopy <= 0 && preferredLength > 0)
                    bytesToCopy = Math.Min(preferredLength, maxLength);
                if (bytesToCopy <= 0)
                    return Array.Empty<byte>();

                var data = new byte[bytesToCopy];
                Marshal.Copy(ptr, data, 0, data.Length);
                return data;
            }
            finally
            {
                buffer.Unlock();
            }
        }

        private static string DescribeSample(Sample sample)
        {
            try
            {
                var parts = new List<string>
                {
                    $"sampleBuffers={sample.BufferCount}",
                    $"sampleTotal={sample.TotalLength}"
                };

                var bufferCount = Math.Min(sample.BufferCount, 4);
                for (var index = 0; index < bufferCount; index++)
                {
                    using var buffer = sample.GetBufferByIndex(index);
                    parts.Add($"b{index}=current:{buffer.CurrentLength}/max:{buffer.MaxLength}");
                }

                return string.Join("; ", parts);
            }
            catch (Exception ex)
            {
                return $"sample diagnostic failed: {ex.Message}";
            }
        }

        private static bool ContainsIdrNalUnit(byte[] frame)
        {
            if (HasStartCode(frame))
                return ContainsNalUnitType(frame, 5);

            for (var i = 0; i + 4 < frame.Length; i++)
            {
                var startCodeLength = 0;
                if (frame[i] == 0 && frame[i + 1] == 0 && frame[i + 2] == 1)
                    startCodeLength = 3;
                else if (i + 5 < frame.Length &&
                         frame[i] == 0 &&
                         frame[i + 1] == 0 &&
                         frame[i + 2] == 0 &&
                         frame[i + 3] == 1)
                    startCodeLength = 4;

                if (startCodeLength == 0)
                    continue;

                var nalIndex = i + startCodeLength;
                if (nalIndex < frame.Length && (frame[nalIndex] & 0x1F) == 5)
                    return true;
            }

            var offset = 0;
            while (offset + 5 <= frame.Length)
            {
                var nalLength =
                    (frame[offset] << 24) |
                    (frame[offset + 1] << 16) |
                    (frame[offset + 2] << 8) |
                    frame[offset + 3];
                offset += 4;

                if (nalLength <= 0 || nalLength > frame.Length - offset)
                    return false;

                if ((frame[offset] & 0x1F) == 5)
                    return true;

                offset += nalLength;
            }

            return false;
        }

        private static bool ContainsNalUnitType(byte[] annexBFrame, byte nalType, bool hevc = false)
        {
            foreach (var nal in EnumerateAnnexBNalUnits(annexBFrame, hevc))
            {
                if (nal.Type == nalType)
                    return true;
            }

            return false;
        }

        private static IEnumerable<AnnexBNalUnit> EnumerateAnnexBNalUnits(byte[] frame, bool hevc = false)
        {
            var index = 0;
            while (TryFindStartCode(frame, index, out var startCodeIndex, out var startCodeLength))
            {
                var nalOffset = startCodeIndex + startCodeLength;
                var nextStart = frame.Length;
                if (TryFindStartCode(frame, nalOffset, out var nextStartCodeIndex, out _))
                    nextStart = nextStartCodeIndex;

                var nalLength = nextStart - nalOffset;
                if (nalLength > 0 && nalOffset < frame.Length)
                    yield return new AnnexBNalUnit(
                        nalOffset,
                        nalLength,
                        hevc ? (byte)((frame[nalOffset] >> 1) & 0x3F) : (byte)(frame[nalOffset] & 0x1F));

                index = nextStart;
            }
        }

        private static bool TryFindStartCode(byte[] frame, int startIndex, out int index, out int length)
        {
            for (var i = Math.Max(0, startIndex); i + 3 < frame.Length; i++)
            {
                if (frame[i] == 0 && frame[i + 1] == 0 && frame[i + 2] == 1)
                {
                    index = i;
                    length = 3;
                    return true;
                }

                if (i + 4 < frame.Length &&
                    frame[i] == 0 &&
                    frame[i + 1] == 0 &&
                    frame[i + 2] == 0 &&
                    frame[i + 3] == 1)
                {
                    index = i;
                    length = 4;
                    return true;
                }
            }

            index = -1;
            length = 0;
            return false;
        }

        private static long PackRatio(int high, int low)
        {
            return ((long)high << 32) | (uint)low;
        }

        private static EncoderTransformSelection CreateEncoderTransform(
            bool preferHardware,
            bool requireHardware,
            ScreenShareH264EncoderFamily preferredEncoderFamily,
            Guid outputSubtype,
            Guid inputSubtype,
            string codecName)
        {
            if (preferHardware &&
                TryCreateHardwareEncoderTransform(preferredEncoderFamily, outputSubtype, inputSubtype, codecName, out var hardwareEncoder, out var hardwareMode))
            {
                return new EncoderTransformSelection(hardwareEncoder, hardwareMode, true);
            }

            if (requireHardware)
                throw new InvalidOperationException($"No GPU {codecName} hardware encoder MFT was found on this Windows install.");

            if (outputSubtype != VideoFormatGuids.H264)
                throw new InvalidOperationException($"Software fallback is forbidden for {codecName} replay encoding.");

            Debug.WriteLine("[ScreenShare:H264] Using stable Microsoft H.264 encoder MFT.");
            return new EncoderTransformSelection(
                new Transform(CmsH264EncoderMft),
                "Microsoft H.264 software MFT (safe low-latency)",
                false);
        }

        private static bool TryCreateHardwareEncoderTransform(
            ScreenShareH264EncoderFamily preferredEncoderFamily,
            Guid outputSubtype,
            Guid inputSubtype,
            string codecName,
            out Transform encoder,
            out string mode)
        {
            encoder = null!;
            mode = "";

            var outputInfo = new TRegisterTypeInformation
            {
                GuidMajorType = MediaTypeGuids.Video,
                GuidSubtype = outputSubtype
            };
            var nv12InputInfo = new TRegisterTypeInformation
            {
                GuidMajorType = MediaTypeGuids.Video,
                GuidSubtype = inputSubtype
            };

            var activations = new List<Activate>();
            var inputs = new TRegisterTypeInformation?[] { nv12InputInfo, null };
            foreach (var inputInfo in inputs)
            {
                try
                {
                    activations.AddRange(EnumerateHardwareEncoderActivations(inputInfo, outputInfo));
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[ScreenShare:H264] Hardware encoder enumeration failed: {ex.Message}");
                    DisposeActivations(activations);
                    return false;
                }
            }

            try
            {
                foreach (var activation in OrderHardwareEncoderActivations(activations, preferredEncoderFamily))
                {
                    try
                    {
                        var friendlyName = GetActivationString(
                            activation,
                            TransformAttributeKeys.MftFriendlyNameAttribute,
                            $"hardware {codecName} encoder");
                        var vendorId = GetActivationString(
                            activation,
                            TransformAttributeKeys.MftEnumHardwareVendorIdAttribute,
                            "");

                        encoder = activation.ActivateObject<Transform>();
                        mode = $"GPU {codecName} hardware MFT ({GetHardwareEncoderFamily(friendlyName, vendorId)}): {friendlyName}";
                        Debug.WriteLine($"[ScreenShare:H264] Using {mode}.");
                        return true;
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[ScreenShare:H264] Hardware encoder activation skipped: {ex.Message}");
                    }
                }
            }
            finally
            {
                DisposeActivations(activations);
            }

            return false;
        }

        private static void DisposeActivations(IEnumerable<Activate> activations)
        {
            foreach (var activation in activations)
            {
                try
                {
                    activation.Dispose();
                }
                catch
                {
                }
            }
        }

        private static IEnumerable<Activate> OrderHardwareEncoderActivations(
            IReadOnlyList<Activate> activations,
            ScreenShareH264EncoderFamily preferredEncoderFamily)
        {
            var ordered = new List<(Activate Activation, int Score, int Index, string FriendlyName, string VendorId)>(activations.Count);
            for (var i = 0; i < activations.Count; i++)
            {
                var activation = activations[i];
                var friendlyName = GetActivationString(
                    activation,
                    TransformAttributeKeys.MftFriendlyNameAttribute,
                    "hardware H.264 encoder");
                var vendorId = GetActivationString(
                    activation,
                    TransformAttributeKeys.MftEnumHardwareVendorIdAttribute,
                    "");
                var family = GetHardwareEncoderFamily(friendlyName, vendorId);
                var score = GetHardwareEncoderPreferenceScore(family);
                if (IsPreferredHardwareEncoderFamily(family, preferredEncoderFamily))
                    score = -1;

                ordered.Add((activation, score, i, friendlyName, vendorId));
            }

            ordered.Sort((left, right) =>
            {
                var score = left.Score.CompareTo(right.Score);
                return score != 0 ? score : left.Index.CompareTo(right.Index);
            });

            if (ordered.Count > 1)
            {
                var summary = string.Join(
                    ", ",
                    ordered.ConvertAll(item => $"{GetHardwareEncoderFamily(item.FriendlyName, item.VendorId)}:{item.FriendlyName}"));
                Debug.WriteLine($"[ScreenShare:H264] Hardware encoder preference order: {summary}.");
            }

            foreach (var item in ordered)
                yield return item.Activation;
        }

        private static bool IsPreferredHardwareEncoderFamily(string family, ScreenShareH264EncoderFamily preferredEncoderFamily)
        {
            return preferredEncoderFamily switch
            {
                ScreenShareH264EncoderFamily.Nvidia => string.Equals(family, NvidiaH264EncoderPolicy.FamilyName, StringComparison.OrdinalIgnoreCase),
                ScreenShareH264EncoderFamily.Intel => string.Equals(family, IntelH264EncoderPolicy.FamilyName, StringComparison.OrdinalIgnoreCase),
                _ => false
            };
        }

        private void TryAttachDxgiDeviceManager(bool forceNativeDeviceManager)
        {
            if (_preferredMediaFoundationDevice != null)
            {
                try
                {
                    DisposeGpuEncodingResources();

                    _nativeMediaFoundationDevice = _preferredMediaFoundationDevice;
                    EnableMultithreadProtection(_nativeMediaFoundationDevice);
                    AttachDxgiDeviceManager(_nativeMediaFoundationDevice);
                    _encoderD3D11Device = _nativeMediaFoundationDevice;
                    _gpuDeviceManagerMode = "Capture D3D11 device + Media Foundation DXGI manager";
                    Debug.WriteLine("[ScreenShare:H264] Capture D3D11 device attached to hardware encoder for GPU texture input.");
                    return;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[ScreenShare:H264] WGC shared native D3D11 device was rejected by hardware encoder; retrying with independent media device manager: {ex.Message}");
                    DisposeGpuEncodingResources();
                }
            }

            if (!forceNativeDeviceManager)
            {
                try
                {
                    DisposeGpuEncodingResources();

                    _directX12VideoDeviceManager = DirectX12VideoDeviceManager.Create();
                    AttachDxgiDeviceManager(_directX12VideoDeviceManager.D3D11On12Device);
                    _encoderD3D11Device = _directX12VideoDeviceManager.D3D11On12Device;
                    _gpuDeviceManagerMode = _directX12VideoDeviceManager.Description;
                    Debug.WriteLine("[ScreenShare:H264] DirectX 12 / D3D11On12 video device manager attached to hardware encoder.");
                    return;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[ScreenShare:H264] DirectX 12 / D3D11On12 manager was rejected by hardware encoder; keeping DX12 capture and retrying with Media Foundation native DXGI manager: {ex.Message}");
                    DisposeGpuEncodingResources();
                }
            }
            else
            {
                Debug.WriteLine("[ScreenShare:H264] Skipping D3D11On12 retry; using a native D3D11 DXGI manager.");
            }

            try
            {
                _nativeMediaFoundationDevice = CreateNativeMediaFoundationDeviceForEncoder();

                EnableMultithreadProtection(_nativeMediaFoundationDevice);
                AttachDxgiDeviceManager(_nativeMediaFoundationDevice);
                _encoderD3D11Device = _nativeMediaFoundationDevice;
                _gpuDeviceManagerMode = "DirectX 12 WGC capture + native Media Foundation DXGI manager";
                Debug.WriteLine("[ScreenShare:H264] Native Media Foundation DXGI manager attached to hardware encoder after D3D11On12 rejection.");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ScreenShare:H264] Hardware encoder native Media Foundation DXGI manager failed: {ex}");
                DisposeGpuEncodingResources();
                _gpuDeviceManagerMode = "Hardware encoder DXGI manager failed";
                throw new InvalidOperationException(
                    "The hardware encoder rejected both the D3D11On12 media manager and the native Media Foundation DXGI manager.",
                    ex);
            }
        }

        private void AttachDxgiDeviceManager(ComObject device)
        {
            _dxgiDeviceManager = new DXGIDeviceManager();
            _dxgiDeviceManager.ResetDevice(device);
            _encoder.ProcessMessage(TMessageType.SetD3DManager, _dxgiDeviceManager.NativePointer);
            _dxgiDeviceManagerAttached = true;
        }

        private SharpDX.Direct3D11.Device CreateNativeMediaFoundationDeviceForEncoder()
        {
            var requestedVendorId = GetPreferredAdapterVendorId(_encoderMode);
            var flags = DeviceCreationFlags.BgraSupport | DeviceCreationFlags.VideoSupport;

            using var factory = new Factory1();
            Adapter1? fallbackAdapter = null;
            Adapter1? selectedAdapter = null;

            try
            {
                for (var index = 0; ; index++)
                {
                    Adapter1 adapter;
                    try
                    {
                        adapter = factory.GetAdapter1(index);
                    }
                    catch
                    {
                        break;
                    }

                    var description = adapter.Description1;
                    var isSoftware = (description.Flags & AdapterFlags.Software) == AdapterFlags.Software;
                    if (isSoftware)
                    {
                        adapter.Dispose();
                        continue;
                    }

                    if (fallbackAdapter == null)
                    {
                        fallbackAdapter = adapter;
                    }
                    else if (description.VendorId != requestedVendorId)
                    {
                        adapter.Dispose();
                    }

                    if (requestedVendorId != 0 && description.VendorId == requestedVendorId)
                    {
                        selectedAdapter = adapter;
                        break;
                    }
                }

                selectedAdapter ??= fallbackAdapter;
                if (selectedAdapter == null)
                    throw new InvalidOperationException("No hardware DXGI adapter was found for Media Foundation GPU encoding.");

                var selectedDescription = selectedAdapter.Description1;
                Debug.WriteLine($"[ScreenShare:H264] Creating native D3D11 Media Foundation device on adapter '{selectedDescription.Description}' vendor=0x{selectedDescription.VendorId:X4} for {_encoderMode}.");
                return new SharpDX.Direct3D11.Device(selectedAdapter, flags);
            }
            finally
            {
                if (fallbackAdapter != null && !ReferenceEquals(fallbackAdapter, selectedAdapter))
                    fallbackAdapter.Dispose();

                selectedAdapter?.Dispose();
            }
        }

        private static int GetPreferredAdapterVendorId(string encoderMode)
        {
            if (NvidiaH264EncoderPolicy.MatchesEncoderMode(encoderMode))
                return NvidiaH264EncoderPolicy.AdapterVendorId;

            if (IntelH264EncoderPolicy.MatchesEncoderMode(encoderMode))
                return IntelH264EncoderPolicy.AdapterVendorId;

            if (AmdH264EncoderPolicy.MatchesEncoderMode(encoderMode))
                return AmdH264EncoderPolicy.AdapterVendorId;

            return 0;
        }

        private static bool IsNvidiaEncoderMode(string encoderMode)
        {
            return NvidiaH264EncoderPolicy.MatchesEncoderMode(encoderMode);
        }

        private static bool ShouldUseDxgiSurfaceInputForEncoder(string encoderMode)
        {
            if (IntelH264EncoderPolicy.MatchesEncoderMode(encoderMode))
                return true;

            return true;
        }

        private void DisposeGpuEncodingResources()
        {
            _dxgiDeviceManagerAttached = false;
            _gpuDeviceManagerMode = "Not attached";
            _encoderD3D11Device = null;
            _useDxgiSurfaceInput = false;
            _gpuTextureInputDisabled = false;
            _loggedHardwareInputBackPressure = false;

            DisposePendingHardwareInputs();
            DisposeHardwareEventGenerator();
            DisposeVideoProcessorResources();

            try { _crossAdapterSourceMutex?.Dispose(); } catch { }
            try { _crossAdapterSourceTexture?.Dispose(); } catch { }
            try { _localEncoderSourceTexture?.Dispose(); } catch { }
            _crossAdapterSourceMutex = null;
            _crossAdapterSourceTexture = null;
            _crossAdapterSourceIdentity = IntPtr.Zero;
            _localEncoderSourceTexture = null;

            try
            {
                _videoProcessorOutputTexture?.Dispose();
            }
            catch
            {
            }

            if (_dxgiInputTextures != null)
            {
                foreach (var texture in _dxgiInputTextures)
                {
                    try
                    {
                        texture.Dispose();
                    }
                    catch
                    {
                    }
                }
            }

            try
            {
                _dxgiDeviceManager?.Dispose();
            }
            catch
            {
            }

            try
            {
                _directX12VideoDeviceManager?.Dispose();
            }
            catch
            {
            }

            try
            {
                if (!ReferenceEquals(_nativeMediaFoundationDevice, _preferredMediaFoundationDevice))
                    _nativeMediaFoundationDevice?.Dispose();
            }
            catch
            {
            }

            _dxgiDeviceManager = null;
            _directX12VideoDeviceManager = null;
            _nativeMediaFoundationDevice = null;
            _dxgiInputTextures = null;
            _videoProcessorOutputTexture = null;
            _videoProcessorSourceWidth = 0;
            _videoProcessorSourceHeight = 0;
            _dxgiInputTextureIndex = 0;
        }

        private static IReadOnlyList<Activate> EnumerateHardwareEncoderActivations(
            TRegisterTypeInformation? inputInfo,
            TRegisterTypeInformation outputInfo)
        {
            var activations = new List<Activate>();
            var category = TransformCategoryGuids.VideoEncoder;
            var activationArrayPtr = IntPtr.Zero;
            var inputInfoPtr = AllocateNativeTypeInfo(inputInfo);
            var outputInfoPtr = AllocateNativeTypeInfo(outputInfo);

            try
            {
                var hr = MFTEnumEx(
                    ref category,
                    (int)(TransformEnumFlag.Hardware | TransformEnumFlag.Asyncmft | TransformEnumFlag.SortAndFilter),
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
                        activations.Add(new Activate(activationPtr));
                }
            }
            finally
            {
                if (activationArrayPtr != IntPtr.Zero)
                    CoTaskMemFree(activationArrayPtr);
                if (inputInfoPtr != IntPtr.Zero)
                    Marshal.FreeHGlobal(inputInfoPtr);
                if (outputInfoPtr != IntPtr.Zero)
                    Marshal.FreeHGlobal(outputInfoPtr);
            }

            return activations;
        }

        private static IntPtr AllocateNativeTypeInfo(TRegisterTypeInformation? typeInfo)
        {
            if (!typeInfo.HasValue)
                return IntPtr.Zero;

            return AllocateNativeTypeInfo(typeInfo.Value);
        }

        private static IntPtr AllocateNativeTypeInfo(TRegisterTypeInformation typeInfo)
        {
            var native = new NativeMftRegisterTypeInfo
            {
                GuidMajorType = typeInfo.GuidMajorType,
                GuidSubtype = typeInfo.GuidSubtype
            };
            var ptr = Marshal.AllocHGlobal(Marshal.SizeOf<NativeMftRegisterTypeInfo>());
            Marshal.StructureToPtr(native, ptr, false);
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

        private static string GetHardwareEncoderFamily(string friendlyName, string vendorId)
        {
            var text = $"{friendlyName} {vendorId}";
            if (NvidiaH264EncoderPolicy.MatchesHardwareText(text))
                return NvidiaH264EncoderPolicy.FamilyName;
            if (IntelH264EncoderPolicy.MatchesHardwareText(text))
                return IntelH264EncoderPolicy.FamilyName;
            if (AmdH264EncoderPolicy.MatchesHardwareText(text))
                return AmdH264EncoderPolicy.FamilyName;

            return "GPU";
        }

        private static int GetHardwareEncoderPreferenceScore(string family)
        {
            return family switch
            {
                NvidiaH264EncoderPolicy.FamilyName => NvidiaH264EncoderPolicy.PreferenceScore,
                IntelH264EncoderPolicy.FamilyName => IntelH264EncoderPolicy.PreferenceScore,
                AmdH264EncoderPolicy.FamilyName => AmdH264EncoderPolicy.PreferenceScore,
                _ => 3
            };
        }

        [DllImport("mfplat.dll", ExactSpelling = true)]
        private static extern int MFTEnumEx(
            ref Guid guidCategory,
            int flags,
            IntPtr inputTypeRef,
            IntPtr outputTypeRef,
            out IntPtr activateArrayOut,
            out int activateCountRef);

        [DllImport("mfplat.dll", ExactSpelling = true)]
        private static extern int MFCreateDXGIDeviceManager(
            out int resetToken,
            out IntPtr deviceManager);

        [DllImport("ole32.dll", ExactSpelling = true)]
        private static extern void CoTaskMemFree(IntPtr ptr);

        [DllImport("kernel32.dll", ExactSpelling = true)]
        private static extern bool CloseHandle(IntPtr handle);

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

        private bool TrySetInputType(Guid subtype, string name)
        {
            try
            {
                SetInputType(subtype, name);
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ScreenShare:H264] Encoder rejected {name} input: {ex.Message}");
                return false;
            }
        }

        private void SetInputType(Guid subtype, string name)
        {
            using var inputType = CreateVideoInputType(subtype);
            _encoder.SetInputType(0, inputType, 0);
            Debug.WriteLine($"[ScreenShare:H264] Encoder accepted {name} input type.");
        }

        private MediaType CreateH264OutputType(int bitrate)
        {
            var outputType = new MediaType();
            outputType.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
            outputType.Set(MediaTypeAttributeKeys.Subtype, OutputSubtype);
            outputType.Set(MediaTypeAttributeKeys.AvgBitrate, bitrate);
            outputType.Set(MediaTypeAttributeKeys.FrameSize, PackRatio(_width, _height));
            outputType.Set(MediaTypeAttributeKeys.FrameRate, PackRatio(_frameRate, 1));
            outputType.Set(MediaTypeAttributeKeys.PixelAspectRatio, PackRatio(1, 1));
            outputType.Set(MediaTypeAttributeKeys.InterlaceMode, (int)VideoInterlaceMode.Progressive);
            outputType.Set(MediaTypeAttributeKeys.MaxKeyframeSpacing, _recoveryKeyFrameIntervalFrames);
            if (_enableHdr10)
                SetHdr10ColourMetadata(outputType);
            return outputType;
        }

        private MediaType CreateVideoInputType(Guid subtype)
        {
            var inputType = new MediaType();
            var sampleSize = subtype == VideoFormatGuids.Rgb32
                ? _width * _height * 4
                : subtype == VideoFormatGuids.P010
                    ? _width * _height * 3
                    : _width * _height * 3 / 2;

            inputType.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
            inputType.Set(MediaTypeAttributeKeys.Subtype, subtype);
            inputType.Set(MediaTypeAttributeKeys.FrameSize, PackRatio(_width, _height));
            inputType.Set(MediaTypeAttributeKeys.FrameRate, PackRatio(_frameRate, 1));
            inputType.Set(MediaTypeAttributeKeys.PixelAspectRatio, PackRatio(1, 1));
            inputType.Set(MediaTypeAttributeKeys.InterlaceMode, (int)VideoInterlaceMode.Progressive);
            inputType.Set(MediaTypeAttributeKeys.FixedSizeSamples, 1);
            inputType.Set(MediaTypeAttributeKeys.AllSamplesIndependent, 1);
            inputType.Set(MediaTypeAttributeKeys.SampleSize, sampleSize);
            if (_enableHdr10 && _useDxgiSurfaceInputForHardwareEncoder)
            {
                inputType.Set(
                    TransformAttributeKeys.D3D11Bindflags,
                    (int)BindFlags.RenderTarget);
                inputType.Set(
                    TransformAttributeKeys.D3D11Usage,
                    (int)ResourceUsage.Default);
            }
            inputType.Set(
                MediaTypeAttributeKeys.DefaultStride,
                subtype == VideoFormatGuids.Rgb32
                    ? _width * 4
                    : subtype == VideoFormatGuids.P010
                        ? _width * 2
                        : _width);
            if (_enableHdr10)
                SetHdr10ColourMetadata(inputType);
            return inputType;
        }

        private static void SetHdr10ColourMetadata(MediaType mediaType)
        {
            // MFVideoPrimaries_BT2020=9, MFVideoTransferFunction_2084=15,
            // MFVideoTransferMatrix_BT2020_10=4, MFNominalRange_16_235=2.
            mediaType.Set(MediaTypeAttributeKeys.VideoPrimaries, 9);
            mediaType.Set(MediaTypeAttributeKeys.TransferFunction, 15);
            mediaType.Set(MediaTypeAttributeKeys.YuvMatrix, 4);
            mediaType.Set(MediaTypeAttributeKeys.VideoNominalRange, 2);
        }

        private Texture2D? TryGetNextAvailableDxgiInputTexture()
        {
            EnsureDxgiInputTextures();
            var textures = _dxgiInputTextures ?? throw new InvalidOperationException("DXGI input textures were not initialized.");

            for (var offset = 0; offset < textures.Length; offset++)
            {
                var index = (_dxgiInputTextureIndex + offset) % textures.Length;
                var texture = textures[index];
                if (IsDxgiInputTexturePending(texture))
                    continue;

                _dxgiInputTextureIndex = (index + 1) % textures.Length;
                return texture;
            }

            return null;
        }

        private void EnsureDxgiInputTextures()
        {
            if (_dxgiInputTextures != null)
                return;
            if (_encoderD3D11Device == null)
                throw new InvalidOperationException("D3D11 input device was not initialized.");

            var bindFlagCandidates = _enableHdr10
                ? new[]
                {
                    BindFlags.VideoEncoder | BindFlags.RenderTarget,
                    BindFlags.VideoEncoder,
                    BindFlags.RenderTarget,
                    BindFlags.ShaderResource,
                    BindFlags.None
                }
                : new[]
                {
                    BindFlags.VideoEncoder | BindFlags.RenderTarget,
                    BindFlags.RenderTarget,
                    BindFlags.VideoEncoder,
                    BindFlags.None,
                    BindFlags.ShaderResource
                };

            foreach (var bindFlags in bindFlagCandidates)
            {
                var desc = CreateDxgiInputTextureDescription(bindFlags);
                _ = GameReplayLog.WriteAsync(nameof(MediaFoundationH264Encoder),
                    $"Trying encoder texture pool. Count={DxgiInputTexturePoolSize}, Size={desc.Width}x{desc.Height}, Format={desc.Format}, Bind={desc.BindFlags}, Usage={desc.Usage}, Options={desc.OptionFlags}");

                try
                {
                    var textures = new List<Texture2D>(DxgiInputTexturePoolSize);
                    try
                    {
                        for (var i = 0; i < DxgiInputTexturePoolSize; i++)
                            textures.Add(new Texture2D(_encoderD3D11Device, desc));

                        _dxgiInputTextures = textures.ToArray();
                    }
                    catch
                    {
                        foreach (var texture in textures)
                        {
                            try
                            {
                                texture.Dispose();
                            }
                            catch
                            {
                            }
                        }

                        throw;
                    }

                    Debug.WriteLine($"[ScreenShare:H264] Created D3D11 NV12 DXGI input texture pool with bind flags: {bindFlags}.");
                    _ = GameReplayLog.WriteAsync(nameof(MediaFoundationH264Encoder),
                        $"Encoder texture pool created. Count={_dxgiInputTextures.Length}, Format={desc.Format}, Bind={bindFlags}, Usage={desc.Usage}");
                    return;
                }
                catch (SharpDXException ex)
                {
                    Debug.WriteLine($"[ScreenShare:H264] D3D11 NV12 texture creation failed with bind flags {bindFlags}: {ex.Message}");
                    _ = GameReplayLog.WriteAsync(nameof(MediaFoundationH264Encoder),
                        $"Encoder texture pool rejected. Format={desc.Format}, Bind={bindFlags}, HResult=0x{ex.ResultCode.Code:X8}, Error={ex.Message}");
                    DisposeDxgiInputTextures();
                }
            }

            throw new InvalidOperationException("The GPU encoder D3D11 device did not accept any NV12 DXGI input texture descriptor.");
        }

        private Texture2DDescription CreateDxgiInputTextureDescription(BindFlags bindFlags)
        {
            return new Texture2DDescription
            {
                Width = _width,
                Height = _height,
                MipLevels = 1,
                ArraySize = 1,
                Format = InputTextureFormat,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default,
                BindFlags = bindFlags,
                CpuAccessFlags = CpuAccessFlags.None,
                OptionFlags = ResourceOptionFlags.None
            };
        }

        private void ConvertBgraTextureToNv12Texture(
            Texture2D sourceTexture,
            int sourceWidth,
            int sourceHeight,
            Texture2D targetTexture)
        {
            if (_encoderD3D11Device == null)
                throw new InvalidOperationException("D3D11 input device was not initialized.");

            // Twitch remains an H.264/SDR transport. When Windows supplies the
            // NVIDIA path with the background recorder's FP16 scRGB capture,
            // perform the HDR colour conversion on the capture GPU before the
            // established NV12/NVENC conversion and submission stages.
            if (!_enableHdr10 && sourceTexture.Description.Format == Format.R16G16B16A16_Float)
            {
                if (!_loggedStreamHdrConversion)
                {
                    _loggedStreamHdrConversion = true;
                    var hdrSourceDescription = sourceTexture.Description;
                    DiagnosticLogService.WriteLine($"[ScreenShare:HDR:Nvidia] conversion active; source={hdrSourceDescription.Width}x{hdrSourceDescription.Height}/{hdrSourceDescription.Format}; target={_width}x{_height}/{Format.B8G8R8A8_UNorm}; encoderInput=NV12; encoder='{_encoderMode}'; toneMap='{ZinkHdrToSdrGpuConverter.ToneMapDescription}'.");
                    DiagnosticLogService.Flush();
                }
                EnsureStreamHdrConversionResources(sourceTexture);
                _streamHdrConverter!.Convert(sourceTexture, _streamSdrTexture!);
                sourceTexture = _streamSdrTexture!;
                sourceWidth = _width;
                sourceHeight = _height;
            }

            using var videoContext = _encoderD3D11Device.ImmediateContext.QueryInterface<VideoContext>();
            using var videoDevice = _encoderD3D11Device.QueryInterface<VideoDevice>();
            EnsureVideoProcessor(sourceWidth, sourceHeight);
            var enumerator = _videoProcessorEnumerator ??
                throw new InvalidOperationException("D3D11 video processor enumerator was not initialized.");
            var processor = _videoProcessor ??
                throw new InvalidOperationException("D3D11 video processor was not initialized.");
            var conversionOutputTexture = EnsureVideoProcessorOutputTexture();
            Texture2D processorSource = AcquireEncoderSourceTexture(sourceTexture, out KeyedMutex? sourceMutex);
            if (!_loggedFirstGpuConversion)
            {
                _loggedFirstGpuConversion = true;
                var sourceDescription = sourceTexture.Description;
                var processorDescription = processorSource.Description;
                var targetDescription = targetTexture.Description;
                _ = GameReplayLog.WriteAsync(nameof(MediaFoundationH264Encoder),
                    $"First GPU conversion. HDR10={_enableHdr10}, Source={sourceDescription.Width}x{sourceDescription.Height}/{sourceDescription.Format}/Bind={sourceDescription.BindFlags}/Options={sourceDescription.OptionFlags}, ProcessorSource={processorDescription.Width}x{processorDescription.Height}/{processorDescription.Format}/Bind={processorDescription.BindFlags}/Options={processorDescription.OptionFlags}, Target={targetDescription.Width}x{targetDescription.Height}/{targetDescription.Format}/Bind={targetDescription.BindFlags}/Options={targetDescription.OptionFlags}, SameDevice={sourceTexture.Device.NativePointer == _encoderD3D11Device.NativePointer}");
            }
            if (_enableHdr10)
            {
                try
                {
                    if (sourceTexture.Description.Format != Format.R16G16B16A16_Float)
                        throw new InvalidOperationException(
                            $"HDR10 encoding requires an FP16 scRGB capture texture; received {sourceTexture.Description.Format}.");

                    // Render into a converter-owned P010 surface first. It remains
                    // valid after ProcessInput and is also the source for paced
                    // duplicate frames when desktop duplication has no new image.
                    // Rendering directly into an encoder-pool surface allowed the
                    // asynchronous Intel MFT to observe partially written/reused
                    // planes, which appeared as alternating green and valid frames.
                    var stableHdrFrame = EnsureVideoProcessorOutputTexture();
                    _hdrGpuConverter ??= new ZinkHdrGpuConverter(_encoderD3D11Device);
                    _hdrGpuConverter.Convert(processorSource, stableHdrFrame);
                    _encoderD3D11Device.ImmediateContext.CopyResource(stableHdrFrame, targetTexture);
                    _encoderD3D11Device.ImmediateContext.Flush();
                    _hasConvertedGpuFrame = true;
                    return;
                }
                finally
                {
                    sourceMutex?.Release(0);
                }
            }
            var inputViewDescription = new VideoProcessorInputViewDescription
            {
                Dimension = VpivDimension.Texture2D,
                Texture2D = new Texture2DVpiv
                {
                    MipSlice = 0,
                    ArraySlice = 0
                }
            };
            try
            {
                videoDevice.CreateVideoProcessorInputView(
                    processorSource,
                    enumerator,
                    inputViewDescription,
                    out var inputView);
                using var inputViewScope = inputView;
                var outputView = EnsureVideoProcessorOutputView(conversionOutputTexture);

                var destination = GetAspectFitRectangle(sourceWidth, sourceHeight, _width, _height);
            videoContext.VideoProcessorSetStreamFrameFormat(processor, 0, VideoFrameFormat.Progressive);
            videoContext.VideoProcessorSetStreamSourceRect(
                processor,
                0,
                new RawBool(true),
                new RawRectangle(0, 0, sourceWidth, sourceHeight));
            videoContext.VideoProcessorSetStreamDestRect(
                processor,
                0,
                new RawBool(true),
                destination);
            videoContext.VideoProcessorSetStreamAutoProcessingMode(processor, 0, new RawBool(false));

            // DXGI desktop duplication supplies an RGB desktop surface. Be explicit
            // about both sides of the conversion: driver defaults vary and on an HDR
            // desktop can interpret full-range RGB as limited-range video, producing
            // the heavily clipped white image seen in saved replays. This H.264 path
            // is the SDR compatibility path, so convert to BT.709 limited-range NV12.
            var inputColorSpace = new VideoProcessorColorSpace
            {
                Usage = false,
                RgbRange = false,
                YCbCrMatrix = true,
                YCbCrXvYCC = false,
                NominalRange = 2
            };
            var outputColorSpace = new VideoProcessorColorSpace
            {
                Usage = false,
                RgbRange = true,
                YCbCrMatrix = true,
                YCbCrXvYCC = false,
                NominalRange = 1
            };
            videoContext.VideoProcessorSetStreamColorSpace(processor, 0, inputColorSpace);
            videoContext.VideoProcessorSetOutputColorSpace(processor, outputColorSpace);

            var background = new VideoColor
            {
                Rgba = new VideoColorRgba
                {
                    R = 0,
                    G = 0,
                    B = 0,
                    A = 1
                }
            };
            videoContext.VideoProcessorSetOutputBackgroundColor(processor, new RawBool(false), background);

            var streams = new[]
            {
                new VideoProcessorStream
                {
                    Enable = new RawBool(true),
                    OutputIndex = 0,
                    InputFrameOrField = 0,
                    PastFrames = 0,
                    FutureFrames = 0,
                    PInputSurface = inputView
                }
            };

                videoContext.VideoProcessorBlt(processor, outputView, _frameIndex, streams.Length, streams);
                _encoderD3D11Device.ImmediateContext.CopyResource(conversionOutputTexture, targetTexture);
                _encoderD3D11Device.ImmediateContext.Flush();
                _hasConvertedGpuFrame = true;
            }
            finally
            {
                sourceMutex?.Release(0);
            }
            // ProcessInput consumes the DXGI surface through the device manager.
            // An explicit Flush on every frame needlessly serializes the capture
            // and video queues, especially on Intel integrated graphics.
        }

        private void EnsureStreamHdrConversionResources(Texture2D sourceTexture)
        {
            if (_streamSdrTexture != null &&
                _streamSdrTexture.Description.Width == _width &&
                _streamSdrTexture.Description.Height == _height &&
                _streamSdrTexture.Device.NativePointer == sourceTexture.Device.NativePointer)
            {
                return;
            }

            _streamSdrTexture?.Dispose();
            _streamHdrConverter?.Dispose();
            _streamHdrConverter = new ZinkHdrToSdrGpuConverter(sourceTexture.Device);
            _streamSdrTexture = new Texture2D(sourceTexture.Device, new Texture2DDescription
            {
                Width = _width,
                Height = _height,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.B8G8R8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default,
                BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
                CpuAccessFlags = CpuAccessFlags.None,
                OptionFlags = ResourceOptionFlags.None
            });
        }

        private Texture2D AcquireEncoderSourceTexture(Texture2D sourceTexture, out KeyedMutex? mutex)
        {
            if (_encoderD3D11Device == null)
                throw new InvalidOperationException("D3D11 input device was not initialized.");

            Texture2D encoderSource = sourceTexture;
            if (sourceTexture.Device.NativePointer != _encoderD3D11Device.NativePointer)
            {
                if (_crossAdapterSourceTexture == null ||
                    _crossAdapterSourceIdentity != sourceTexture.NativePointer)
                {
                    _crossAdapterSourceMutex?.Dispose();
                    _crossAdapterSourceTexture?.Dispose();
                    using var resource = sourceTexture.QueryInterface<Resource1>();
                    IntPtr sharedHandle = resource.CreateSharedHandle(
                        null,
                        SharedResourceFlags.Read | SharedResourceFlags.Write,
                        null);
                    try
                    {
                        using var encoderDevice1 = _encoderD3D11Device.QueryInterface<SharpDX.Direct3D11.Device1>();
                        _crossAdapterSourceTexture = encoderDevice1.OpenSharedResource1<Texture2D>(sharedHandle);
                    }
                    finally
                    {
                        CloseHandle(sharedHandle);
                    }
                    _crossAdapterSourceMutex = _crossAdapterSourceTexture.QueryInterface<KeyedMutex>();
                    _crossAdapterSourceIdentity = sourceTexture.NativePointer;
                }

                encoderSource = _crossAdapterSourceTexture;
                mutex = _crossAdapterSourceMutex;
            }
            else
            {
                // The capture surface carries shared/keyed-mutex flags. NVIDIA's
                // video processor accepts the format but rejects VideoProcessorBlt
                // from that shared surface, so copy it to a private same-device
                // texture before BGRA-to-NV12 conversion.
                var sourceDescription = sourceTexture.Description;
                if (_localEncoderSourceTexture == null ||
                    _localEncoderSourceTexture.Description.Width != sourceDescription.Width ||
                    _localEncoderSourceTexture.Description.Height != sourceDescription.Height ||
                    _localEncoderSourceTexture.Description.Format != sourceDescription.Format)
                {
                    _localEncoderSourceTexture?.Dispose();
                    sourceDescription.OptionFlags = ResourceOptionFlags.None;
                    _localEncoderSourceTexture = new Texture2D(_encoderD3D11Device, sourceDescription);
                }

                using var sourceMutex = sourceTexture.QueryInterfaceOrNull<KeyedMutex>();
                sourceMutex?.Acquire(0, -1).CheckError();
                try
                {
                    _encoderD3D11Device.ImmediateContext.CopyResource(
                        sourceTexture,
                        _localEncoderSourceTexture);
                }
                finally
                {
                    sourceMutex?.Release(0);
                }
                encoderSource = _localEncoderSourceTexture;
                mutex = null;
            }

            mutex?.Acquire(0, -1).CheckError();
            return encoderSource;
        }

        private void EnsureVideoProcessor(int sourceWidth, int sourceHeight)
        {
            if (_encoderD3D11Device == null)
                throw new InvalidOperationException("D3D11 input device was not initialized.");

            if (_videoProcessor != null &&
                _videoProcessorEnumerator != null &&
                _videoProcessorSourceWidth == sourceWidth &&
                _videoProcessorSourceHeight == sourceHeight)
            {
                return;
            }

            DisposeVideoProcessorResources();

            using var videoDevice = _encoderD3D11Device.QueryInterface<VideoDevice>();
            var description = new VideoProcessorContentDescription
            {
                InputFrameFormat = VideoFrameFormat.Progressive,
                InputFrameRate = new Rational(_frameRate, 1),
                InputWidth = sourceWidth,
                InputHeight = sourceHeight,
                OutputFrameRate = new Rational(_frameRate, 1),
                OutputWidth = _width,
                OutputHeight = _height,
                Usage = VideoUsage.PlaybackNormal
            };

            videoDevice.CreateVideoProcessorEnumerator(ref description, out _videoProcessorEnumerator);
            videoDevice.CreateVideoProcessor(_videoProcessorEnumerator, 0, out _videoProcessor);
            _videoProcessorSourceWidth = sourceWidth;
            _videoProcessorSourceHeight = sourceHeight;
            Debug.WriteLine($"[ScreenShare:H264] Cached D3D11 video processor for {sourceWidth}x{sourceHeight} -> {_width}x{_height} NV12.");
        }

        private VideoProcessorOutputView EnsureVideoProcessorOutputView(Texture2D conversionOutputTexture)
        {
            if (_encoderD3D11Device == null)
                throw new InvalidOperationException("D3D11 input device was not initialized.");

            if (_videoProcessorOutputView != null)
                return _videoProcessorOutputView;

            var enumerator = _videoProcessorEnumerator ??
                throw new InvalidOperationException("D3D11 video processor enumerator was not initialized.");
            using var videoDevice = _encoderD3D11Device.QueryInterface<VideoDevice>();
            var outputViewDescription = new VideoProcessorOutputViewDescription
            {
                Dimension = VpovDimension.Texture2D,
                Texture2D = new Texture2DVpov
                {
                    MipSlice = 0
                }
            };

            videoDevice.CreateVideoProcessorOutputView(
                conversionOutputTexture,
                enumerator,
                outputViewDescription,
                out _videoProcessorOutputView);

            return _videoProcessorOutputView;
        }

        private Texture2D EnsureVideoProcessorOutputTexture()
        {
            if (_encoderD3D11Device == null)
                throw new InvalidOperationException("D3D11 input device was not initialized.");

            if (_videoProcessorOutputTexture != null)
                return _videoProcessorOutputTexture;

            _videoProcessorOutputTexture = new Texture2D(_encoderD3D11Device, new Texture2DDescription
            {
                Width = _width,
                Height = _height,
                MipLevels = 1,
                ArraySize = 1,
                Format = InputTextureFormat,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default,
                BindFlags = BindFlags.RenderTarget,
                CpuAccessFlags = CpuAccessFlags.None,
                OptionFlags = ResourceOptionFlags.None
            });
            Debug.WriteLine("[ScreenShare:H264] Created D3D11 NV12 video processor output texture with RenderTarget bind flags.");
            return _videoProcessorOutputTexture;
        }

        private RawRectangle GetAspectFitRectangle(
            int sourceWidth,
            int sourceHeight,
            int targetWidth,
            int targetHeight)
        {
            var scale = Math.Min((double)targetWidth / sourceWidth, (double)targetHeight / sourceHeight);
            var width = Math.Max(1, (int)Math.Round(sourceWidth * scale));
            var height = Math.Max(1, (int)Math.Round(sourceHeight * scale));
            var left = (targetWidth - width) / 2;
            var top = (targetHeight - height) / 2;
            return new RawRectangle(left, top, left + width, top + height);
        }

        private bool IsDxgiInputTexturePending(Texture2D texture)
        {
            foreach (var pendingInput in _pendingHardwareInputs)
            {
                if (ReferenceEquals(pendingInput.Texture, texture))
                    return true;
            }

            return false;
        }

        private void DisposeDxgiInputTextures()
        {
            try { _streamSdrTexture?.Dispose(); } catch { }
            _streamSdrTexture = null;
            try { _streamHdrConverter?.Dispose(); } catch { }
            _streamHdrConverter = null;

            try
            {
                _hdrGpuConverter?.Dispose();
            }
            catch
            {
            }
            _hdrGpuConverter = null;

            try
            {
                _videoProcessorOutputTexture?.Dispose();
            }
            catch
            {
            }

            _videoProcessorOutputTexture = null;
            try { _hdrReadbackTexture?.Dispose(); } catch { }
            _hdrReadbackTexture = null;
            _hasConvertedGpuFrame = false;
            DisposeVideoProcessorResources();

            if (_dxgiInputTextures == null)
                return;

            foreach (var texture in _dxgiInputTextures)
            {
                try
                {
                    texture.Dispose();
                }
                catch
                {
                }
            }

            _dxgiInputTextures = null;
            _dxgiInputTextureIndex = 0;
        }

        private void DisposeVideoProcessorResources()
        {
            try
            {
                _videoProcessorOutputView?.Dispose();
            }
            catch
            {
            }

            try
            {
                _videoProcessor?.Dispose();
            }
            catch
            {
            }

            try
            {
                _videoProcessorEnumerator?.Dispose();
            }
            catch
            {
            }

            _videoProcessorOutputView = null;
            _videoProcessor = null;
            _videoProcessorEnumerator = null;
            _videoProcessorSourceWidth = 0;
            _videoProcessorSourceHeight = 0;
        }

        private void InitializeHardwareEventPump()
        {
            DisposeHardwareEventGenerator();

            try
            {
                _hardwareEventGenerator = _encoder.QueryInterfaceOrNull<MediaEventGenerator>();
                if (_hardwareEventGenerator != null)
                {
                    lock (_hardwareEventSync)
                    {
                        _hardwareInputRequests = 0;
                        _hardwareOutputRequests = 0;
                        _loggedHardwareEvents = 0;
                        _stopHardwareEventThread = false;
                    }

                    _hardwareEventThread = new Thread(HardwareEventLoop)
                    {
                        IsBackground = true,
                        Name = "Zink Hardware H264 MFT Event Pump",
                        Priority = ThreadPriority.AboveNormal
                    };
                    _hardwareEventThread.Start();
                    Debug.WriteLine("[ScreenShare:H264] Hardware encoder async event pump enabled.");
                }
                else
                {
                    Debug.WriteLine("[ScreenShare:H264] Hardware encoder did not expose an async event generator; using synchronous output drain.");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ScreenShare:H264] Hardware encoder async event pump unavailable: {ex.Message}");
                _hardwareEventGenerator = null;
            }
        }

        private void HardwareEventLoop()
        {
            while (true)
            {
                MediaEventGenerator? eventGenerator;
                lock (_hardwareEventSync)
                {
                    if (_stopHardwareEventThread)
                        return;

                    eventGenerator = _hardwareEventGenerator;
                }

                if (eventGenerator == null)
                    return;

                try
                {
                    using var mediaEvent = eventGenerator.GetEvent(isBlocking: true);
                    mediaEvent.Status.CheckError();
                    HandleHardwareEvent(mediaEvent.TypeInfo);
                }
                catch (SharpDXException ex)
                {
                    lock (_hardwareEventSync)
                    {
                        if (_stopHardwareEventThread)
                            return;
                    }

                    if (ex.ResultCode.Code == unchecked((int)0xC00D3E85))
                        return;

                    Debug.WriteLine($"[ScreenShare:H264] Hardware encoder event pump error: 0x{ex.ResultCode.Code:X8} {ex.Message}");
                    Thread.Sleep(5);
                }
                catch (ObjectDisposedException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    lock (_hardwareEventSync)
                    {
                        if (_stopHardwareEventThread)
                            return;
                    }

                    Debug.WriteLine($"[ScreenShare:H264] Hardware encoder event pump failed: {ex.Message}");
                    Thread.Sleep(5);
                }
            }
        }

        private void HandleHardwareEvent(MediaEventTypes eventType)
        {
            lock (_hardwareEventSync)
            {
                switch (eventType)
                {
                    case MediaEventTypes.TransformNeedInput:
                        _hardwareInputRequests++;
                        break;

                    case MediaEventTypes.TransformHaveOutput:
                        _hardwareOutputRequests++;
                        break;

                    case MediaEventTypes.Error:
                        Debug.WriteLine("[ScreenShare:H264] GPU hardware encoder reported a Media Foundation error event.");
                        break;
                }

                if (_loggedHardwareEvents < 24)
                {
                    _loggedHardwareEvents++;
                    Debug.WriteLine($"[ScreenShare:H264] Hardware encoder event: {eventType}; needInput={_hardwareInputRequests}; haveOutput={_hardwareOutputRequests}.");
                }
            }
        }

        private bool TryConsumeHardwareInputRequest()
        {
            lock (_hardwareEventSync)
            {
                if (_hardwareInputRequests <= 0)
                    return false;

                _hardwareInputRequests--;
                return true;
            }
        }

        private bool TryConsumeHardwareInputRequestForFrame()
        {
            if (TryConsumeHardwareInputRequest())
                return true;

            if (!_waitBrieflyForHardwareInputRequest)
                return false;

            var waitStartedAt = Stopwatch.StartNew();
            var frameDurationMilliseconds = Math.Max(1, (int)Math.Ceiling(_frameDuration100Ns / 10_000.0));
            var waitBudgetMilliseconds = Math.Clamp(frameDurationMilliseconds - 2, 18, 45);
            while (waitStartedAt.ElapsedMilliseconds < waitBudgetMilliseconds)
            {
                if (TryConsumeHardwareInputRequest())
                    return true;

                Thread.Yield();
            }

            return false;
        }

        private void ReturnHardwareInputRequest()
        {
            if (_hardwareEventGenerator == null)
                return;

            lock (_hardwareEventSync)
            {
                _hardwareInputRequests++;
            }
        }

        private bool TryConsumeHardwareOutputRequest()
        {
            lock (_hardwareEventSync)
            {
                if (_hardwareOutputRequests <= 0)
                    return false;

                _hardwareOutputRequests--;
                return true;
            }
        }

        private void ReleaseCompletedHardwareInput()
        {
            if (_pendingHardwareInputs.Count == 0)
                return;

            _pendingHardwareInputs.Dequeue().Dispose();
        }

        private void DisposePendingHardwareInputs()
        {
            while (_pendingHardwareInputs.Count > 0)
                _pendingHardwareInputs.Dequeue().Dispose();
        }

        private void DisposeHardwareEventGenerator()
        {
            lock (_hardwareEventSync)
            {
                _stopHardwareEventThread = true;
                _hardwareInputRequests = 0;
                _hardwareOutputRequests = 0;
                _loggedWaitingForHardwareInput = false;
            }

            try
            {
                _hardwareEventGenerator?.Dispose();
            }
            catch
            {
            }

            try
            {
                if (_hardwareEventThread != null &&
                    _hardwareEventThread != Thread.CurrentThread &&
                    !_hardwareEventThread.Join(200))
                {
                    Debug.WriteLine("[ScreenShare:H264] Hardware encoder event pump did not stop within 200ms.");
                }
            }
            catch
            {
            }

            _hardwareEventGenerator = null;
            _hardwareEventThread = null;
        }

        private static void EnableMultithreadProtection(SharpDX.Direct3D11.Device device)
        {
            try
            {
                using var multithread = device.QueryInterface<Multithread>();
                var wasProtected = multithread.SetMultithreadProtected(true);
                Debug.WriteLine($"[ScreenShare:H264] Native D3D11 multithread protection enabled; previously protected={wasProtected}.");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ScreenShare:H264] Native D3D11 multithread protection skipped: {ex.Message}");
            }
        }

        private bool TryEnableRealtimeEncoderMode(Transform encoder, bool enableHardwareAsyncMode)
        {
            try
            {
                var attributes = encoder.Attributes;
                using (attributes)
                {
                    if (enableHardwareAsyncMode)
                    {
                        attributes.Set(TransformAttributeKeys.TransformAsyncUnlock, 1);
                        attributes.Set(TransformAttributeKeys.MftHwTimestampWithQpcAttribute, 1);
                    }

                    attributes.Set(SinkWriterAttributeKeys.LowLatency.Guid, 1);
                    attributes.Set(CodecApiAvLowLatencyMode, 1);
                    attributes.Set(CodecApiAvEncCommonLowLatency, 1);
                    attributes.Set(CodecApiAvEncCommonRealTime, 1);
                    attributes.Set(CodecApiAvEncCommonQualityVsSpeed, 100);
                    attributes.Set(CodecApiAvEncVideoMaxKeyframeDistance, _recoveryKeyFrameIntervalFrames);
                    attributes.Set(CodecApiAvEncVideoNumGopsPerIdr, 1);
                }

                TryConfigureRealtimeCodecApi(encoder);

                Debug.WriteLine(enableHardwareAsyncMode
                    ? "[ScreenShare:H264] Realtime/low-latency encoder attributes enabled with hardware async unlock."
                    : "[ScreenShare:H264] Realtime/low-latency encoder attributes enabled in synchronous mode.");
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ScreenShare:H264] Realtime encoder attributes skipped: {ex.Message}");
                return false;
            }
        }

        private void TryConfigureRealtimeCodecApi(Transform encoder)
        {
            IntPtr codecApiPtr = IntPtr.Zero;
            try
            {
                var iid = CodecApiInterfaceId;
                var hr = Marshal.QueryInterface(encoder.NativePointer, ref iid, out codecApiPtr);
                if (hr != 0 || codecApiPtr == IntPtr.Zero)
                {
                    Debug.WriteLine($"[ScreenShare:H264] ICodecAPI realtime controls unavailable for encoder '{_encoderMode}'; hr=0x{hr:X8}.");
                    return;
                }

                var codecApi = (ICodecApi)Marshal.GetObjectForIUnknown(codecApiPtr);
                TrySetCodecApiUInt32(codecApi, CodecApiAvLowLatencyMode, 1, "AVLowLatencyMode");
                TrySetCodecApiUInt32(codecApi, CodecApiAvEncCommonLowLatency, 1, "AVEncCommonLowLatency");
                TrySetCodecApiUInt32(codecApi, CodecApiAvEncCommonRealTime, 1, "AVEncCommonRealTime");
                TrySetCodecApiUInt32(codecApi, CodecApiAvEncCommonQualityVsSpeed, 100, "AVEncCommonQualityVsSpeed");
                TrySetCodecApiUInt32(codecApi, CodecApiAvEncMpvDefaultBPictureCount, 0, "AVEncMPVDefaultBPictureCount");
                TrySetCodecApiUInt32(codecApi, CodecApiAvEncMpvGopSize, (uint)_recoveryKeyFrameIntervalFrames, "AVEncMPVGOPSize");
                TrySetCodecApiUInt32(codecApi, CodecApiAvEncVideoMaxKeyframeDistance, (uint)_recoveryKeyFrameIntervalFrames, "AVEncVideoMaxKeyframeDistance");
                TrySetCodecApiUInt32(codecApi, CodecApiAvEncVideoNumGopsPerIdr, 1, "AVEncVideoNumGopsPerIdr");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ScreenShare:H264] ICodecAPI realtime controls skipped for encoder '{_encoderMode}': {ex.Message}");
            }
            finally
            {
                if (codecApiPtr != IntPtr.Zero)
                    Marshal.Release(codecApiPtr);
            }
        }

        private void TrySetCodecApiUInt32(ICodecApi codecApi, Guid property, uint value, string name)
        {
            try
            {
                object variantValue = value;
                var hr = codecApi.SetValue(ref property, ref variantValue);
                if (hr == 0)
                    Debug.WriteLine($"[ScreenShare:H264] ICodecAPI {name}={value} applied for encoder '{_encoderMode}'.");
                else
                    Debug.WriteLine($"[ScreenShare:H264] ICodecAPI {name}={value} rejected for encoder '{_encoderMode}'; hr=0x{hr:X8}.");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ScreenShare:H264] ICodecAPI {name}={value} failed for encoder '{_encoderMode}': {ex.Message}");
            }
        }

        private static void TryUnlockAsyncHardwareTransform(Transform encoder)
        {
            try
            {
                using var attributes = encoder.Attributes;
                attributes.Set(TransformAttributeKeys.TransformAsyncUnlock, 1);
                attributes.Set(TransformAttributeKeys.MftHwTimestampWithQpcAttribute, 1);
                Debug.WriteLine("[ScreenShare:H264] Hardware MFT async unlock enabled before GPU manager attachment.");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ScreenShare:H264] Hardware MFT async unlock skipped before GPU manager attachment: {ex.Message}");
            }
        }

        private void RequestRecoveryKeyFrameIfNeeded()
        {
            var forced = Interlocked.Exchange(ref _forceNextKeyFrame, 0) == 1;
            if (!forced && (_frameIndex == 0 || _frameIndex % _recoveryKeyFrameIntervalFrames != 0))
                return;
            if (!_allowRepeatedForceKeyFrameRequests && !forced && _frameIndex > 0)
                return;

            if (!forced && Interlocked.CompareExchange(ref _recoveryKeyFrameRequestPending, 1, 0) != 0)
                return;

            IntPtr codecApiPtr = IntPtr.Zero;
            try
            {
                var iid = CodecApiInterfaceId;
                var hr = Marshal.QueryInterface(_encoder.NativePointer, ref iid, out codecApiPtr);
                if (hr != 0 || codecApiPtr == IntPtr.Zero)
                    throw new COMException("ICodecAPI is unavailable for the active encoder.", hr);

                var codecApi = (ICodecApi)Marshal.GetObjectForIUnknown(codecApiPtr);
                object requestKeyFrame = 1u;
                var forceKeyFrameProperty = CodecApiAvEncVideoForceKeyFrame;
                var setResult = codecApi.SetValue(ref forceKeyFrameProperty, ref requestKeyFrame);
                if (setResult != 0)
                    throw new COMException("The encoder rejected AVEncVideoForceKeyFrame.", setResult);

                if (forced)
                    Debug.WriteLine($"[ScreenShare:H264] Forced recovery keyframe requested. encoder='{_encoderMode}'; frameIndex={_frameIndex}; repeatedAllowed={_allowRepeatedForceKeyFrameRequests}.");
            }
            catch (Exception ex)
            {
                Interlocked.Exchange(ref _recoveryKeyFrameRequestPending, 0);
                if (!_loggedForceKeyFrameUnavailable)
                {
                    _loggedForceKeyFrameUnavailable = true;
                    Debug.WriteLine($"[ScreenShare:H264] Force keyframe request skipped: {ex.Message}");
                }
            }
            finally
            {
                if (codecApiPtr != IntPtr.Zero)
                    Marshal.Release(codecApiPtr);
            }
        }

        private static bool TrySetLowLatencyOutputTypeAttributes(MediaType outputType)
        {
            try
            {
                outputType.Set(MediaTypeAttributeKeys.H264MaxCodecConfigDelay, 0);
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ScreenShare:H264] Low-latency output type attributes skipped: {ex.Message}");
                return false;
            }
        }

        private static unsafe void CopyBitmapToRgb32(Bitmap bitmap, int width, int height, IntPtr destination)
        {
            var disposeSource = bitmap.Width != width ||
                bitmap.Height != height ||
                bitmap.PixelFormat != PixelFormat.Format32bppArgb;
            Bitmap? converted = null;
            var source = bitmap;

            if (disposeSource)
            {
                converted = new Bitmap(width, height, PixelFormat.Format32bppArgb);
                using var graphics = Graphics.FromImage(converted);
                graphics.DrawImage(bitmap, 0, 0, width, height);
                source = converted;
            }

            var rect = new Rectangle(0, 0, width, height);
            var data = source.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                byte* src = (byte*)data.Scan0;
                byte* dst = (byte*)destination;
                var rowBytes = width * 4;

                for (var y = 0; y < height; y++)
                {
                    System.Buffer.MemoryCopy(
                        src + y * data.Stride,
                        dst + y * rowBytes,
                        rowBytes,
                        rowBytes);
                }
            }
            finally
            {
                source.UnlockBits(data);
                converted?.Dispose();
            }
        }

        private static unsafe void ConvertBitmapToNv12(Bitmap bitmap, int width, int height, byte[] nv12)
        {
            var disposeSource = bitmap.Width != width || bitmap.Height != height;
            var source = disposeSource ? new Bitmap(bitmap, width, height) : bitmap;

            var rect = new Rectangle(0, 0, width, height);
            var data = source.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                int uvStart = width * height;
                fixed (byte* nv12Base = nv12)
                {
                    ConvertBitmapToNv12Rows(
                        data.Scan0,
                        data.Stride,
                        width,
                        height,
                        (IntPtr)nv12Base,
                        uvStart);
                }
            }
            finally
            {
                source.UnlockBits(data);
                if (disposeSource)
                    source.Dispose();
            }
        }

        private static unsafe void ConvertBitmapToNv12Rows(
            IntPtr sourceBasePtr,
            int sourceStride,
            int width,
            int height,
            IntPtr nv12BasePtr,
            int uvStart)
        {
            var rowPairs = (height + 1) / 2;
            var sourceBase = (byte*)sourceBasePtr;
            var nv12Base = (byte*)nv12BasePtr;

            Parallel.For(0, rowPairs, rowPair =>
            {
                var y = rowPair * 2;
                var nextY = Math.Min(y + 1, height - 1);
                var row0 = sourceBase + y * sourceStride;
                var row1 = sourceBase + nextY * sourceStride;
                var yPlane0 = nv12Base + y * width;
                var yPlane1 = nv12Base + nextY * width;
                var uvPlane = nv12Base + uvStart + rowPair * width;

                for (var x = 0; x < width; x += 2)
                {
                    var nextX = Math.Min(x + 1, width - 1);
                    var p00 = row0 + x * 4;
                    var p01 = row0 + nextX * 4;
                    var p10 = row1 + x * 4;
                    var p11 = row1 + nextX * 4;

                    yPlane0[x] = GetY(p00);
                    yPlane0[nextX] = GetY(p01);
                    yPlane1[x] = GetY(p10);
                    yPlane1[nextX] = GetY(p11);

                    var u = (GetU(p00) + GetU(p01) + GetU(p10) + GetU(p11)) >> 2;
                    var v = (GetV(p00) + GetV(p01) + GetV(p10) + GetV(p11)) >> 2;
                    uvPlane[x] = (byte)u;
                    uvPlane[x + 1] = (byte)v;
                }
            });
        }

        private static unsafe byte GetY(byte* src)
        {
            return ClampToByte((YFromR[src[2]] + YFromG[src[1]] + YFromB[src[0]]) >> 8);
        }

        private static unsafe byte GetU(byte* src)
        {
            return ClampToByte((UFromR[src[2]] + UFromG[src[1]] + UFromB[src[0]]) >> 8);
        }

        private static unsafe byte GetV(byte* src)
        {
            return ClampToByte((VFromR[src[2]] + VFromG[src[1]] + VFromB[src[0]]) >> 8);
        }

        private static int[] BuildContributionTable(int coefficient, int offset)
        {
            var table = new int[256];
            for (var i = 0; i < table.Length; i++)
                table[i] = coefficient * i + offset;
            return table;
        }

        private static byte ClampToByte(int value)
        {
            if (value < 0) return 0;
            if (value > 255) return 255;
            return (byte)value;
        }

        public void Dispose()
        {
            DisposeHardwareEventGenerator();

            try
            {
                _encoder.ProcessMessage(TMessageType.NotifyEndOfStream, IntPtr.Zero);
                _encoder.ProcessMessage(TMessageType.NotifyEndStreaming, IntPtr.Zero);
            }
            catch
            {
            }

            _encoder.Dispose();
            DisposeGpuEncodingResources();
        }

        private sealed class PendingHardwareInputSample : IDisposable
        {
            public PendingHardwareInputSample(Sample sample, MediaBuffer buffer, Texture2D texture)
            {
                Sample = sample;
                Buffer = buffer;
                Texture = texture;
            }

            public Sample Sample { get; }
            public MediaBuffer Buffer { get; }
            public Texture2D Texture { get; }

            public void Dispose()
            {
                try
                {
                    Sample.Dispose();
                }
                catch
                {
                }

                try
                {
                    Buffer.Dispose();
                }
                catch
                {
                }
            }
        }

        private sealed class EncoderTransformSelection
        {
            public EncoderTransformSelection(Transform encoder, string mode, bool isHardwareAccelerated)
            {
                Encoder = encoder;
                Mode = mode;
                IsHardwareAccelerated = isHardwareAccelerated;
            }

            public Transform Encoder { get; }
            public string Mode { get; }
            public bool IsHardwareAccelerated { get; }
        }

        [ComImport]
        [Guid("901DB4C7-31CE-41A2-85DC-8FA0BF41B8DA")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface ICodecApi
        {
            [PreserveSig]
            int IsSupported(ref Guid api);

            [PreserveSig]
            int IsModifiable(ref Guid api);

            [PreserveSig]
            int GetParameterRange(
                ref Guid api,
                [MarshalAs(UnmanagedType.Struct)] out object valueMin,
                [MarshalAs(UnmanagedType.Struct)] out object valueMax,
                [MarshalAs(UnmanagedType.Struct)] out object steppingDelta);

            [PreserveSig]
            int GetParameterValues(
                ref Guid api,
                out IntPtr values,
                out int valuesCount);

            [PreserveSig]
            int GetDefaultValue(
                ref Guid api,
                [MarshalAs(UnmanagedType.Struct)] out object value);

            [PreserveSig]
            int GetValue(
                ref Guid api,
                [MarshalAs(UnmanagedType.Struct)] out object value);

            [PreserveSig]
            int SetValue(
                ref Guid api,
                [MarshalAs(UnmanagedType.Struct)] ref object value);
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
                try
                {
                    Sample?.Dispose();
                }
                catch
                {
                }

                try
                {
                    Events?.Dispose();
                }
                catch
                {
                }
            }
        }

        private readonly record struct AnnexBNalUnit(int Offset, int Length, byte Type);
    }

    public sealed class H264EncodedFrame
    {
        public H264EncodedFrame(byte[] data, bool isKeyFrame, long timestampMilliseconds)
        {
            Data = data;
            IsKeyFrame = isKeyFrame;
            TimestampMilliseconds = timestampMilliseconds;
        }

        public byte[] Data { get; }
        public bool IsKeyFrame { get; }
        public long TimestampMilliseconds { get; }
    }
}

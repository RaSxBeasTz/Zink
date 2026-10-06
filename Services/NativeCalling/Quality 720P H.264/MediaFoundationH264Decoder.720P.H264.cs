using SharpDX;
using SharpDX.MediaFoundation;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace Zink.Services.NativeCalling.Quality720p
{
    public sealed class MediaFoundationH264Decoder : IDisposable
    {
        // Colour conversion is frequent and CPU-bound. Using every thread-pool
        // worker for each frame delayed UI presentation and audio refill work on
        // the receiver. Two workers keep 720p conversion inside the frame budget
        // without causing system-wide scheduler bursts.
        private static readonly ParallelOptions ColourConversionParallelOptions = new()
        {
            MaxDegreeOfParallelism = 2
        };

        private static readonly Guid CmsH264DecoderMft = new("62CE7E72-4C71-4D20-B15D-452831A87D9D");
        // CODECAPI_AVLowLatencyMode. Without this, the Windows H.264 decoder can
        // retain roughly a GOP-sized reorder/look-ahead queue even when the
        // stream contains no B-frames. That is useful for file playback, but it
        // adds close to a second of avoidable delay to interactive screen share.
        private static readonly Guid CodecApiAvLowLatencyMode = new("9C27891A-ED7A-40E1-88E8-B22727A024EE");
        private const int MfENotAccepting = unchecked((int)0xC00D36B5);
        private const int MfENeedMoreInput = unchecked((int)0xC00D6D72);
        private const int MfEStreamChange = unchecked((int)0xC00D6D61);
        private const int MfETypeNotSet = unchecked((int)0xC00D6D60);
        private const int MftOutputStreamProvidesSamples = 0x00000100;
        private const int MftOutputStreamCanProvideSamples = 0x00000200;
        private const long FrameDuration100Ns = 10_000_000L / ScreenShareCapturePipeline.TargetFps;

        private readonly Transform _decoder;
        private readonly string _codec;
        private readonly Guid _inputSubtype;
        private int _width;
        private int _height;
        private long _sampleTime;
        private long _inputSamples;
        private long _needMoreInputCount;
        private bool _loggedOutputStreamMode;
        private bool _preferLengthPrefixedInput;
        private bool _loggedFirstOutputSample;
        private Guid _outputSubtype = VideoFormatGuids.NV12;

        static MediaFoundationH264Decoder()
        {
            try
            {
                MediaManager.Startup();
            }
            catch
            {
            }
        }

        public MediaFoundationH264Decoder(int width, int height)
            : this(width, height, ScreenShareCodecNames.H264)
        {
        }

        public MediaFoundationH264Decoder(int width, int height, string codec)
        {
            _width = width;
            _height = height;
            _codec = ScreenShareCodecNames.IsAv1(codec) ? ScreenShareCodecNames.Av1 : ScreenShareCodecNames.H264;
            _inputSubtype = ScreenShareCodecNames.IsAv1(_codec) ? MediaFoundationAv1Encoder.Av1Subtype : VideoFormatGuids.H264;
            _decoder = ScreenShareCodecNames.IsAv1(_codec)
                ? CreateAv1DecoderTransform()
                : new Transform(CmsH264DecoderMft);
            ConfigureLowLatencyDecoder();
            ConfigureTypes(width, height);
        }

        private void ConfigureLowLatencyDecoder()
        {
            var enabled = false;
            try
            {
                _decoder.Attributes.Set(SinkWriterAttributeKeys.LowLatency.Guid, 1);
                enabled = true;
            }
            catch
            {
            }

            try
            {
                _decoder.Attributes.Set(CodecApiAvLowLatencyMode, 1);
                enabled = true;
            }
            catch
            {
            }

            Debug.WriteLine(
                $"[ScreenShare:{GetLogCodecName()}:DECODER] Low-latency decoder attributes {(enabled ? "enabled" : "were not accepted") }.");
            DiagnosticLogService.WriteLine(
                $"[ScreenShare:{GetLogCodecName()}:DECODER] Low-latency decoder attributes {(enabled ? "enabled" : "were not accepted") }.");
        }

        public void Reconfigure(int width, int height)
        {
            if (width == _width && height == _height)
                return;

            _width = width;
            _height = height;
            _decoder.ProcessMessage(TMessageType.CommandFlush, IntPtr.Zero);
            ConfigureTypes(width, height);
        }

        public byte[]? DecodeToBgra(byte[] h264Frame, int width, int height, bool materializeOutput = true)
        {
            Reconfigure(width, height);
            var inputFrame = ScreenShareCodecNames.IsAv1(_codec)
                ? h264Frame
                : NormalizeH264Input(h264Frame);
            if (!ScreenShareCodecNames.IsAv1(_codec) &&
                _preferLengthPrefixedInput &&
                HasStartCode(inputFrame) &&
                TryConvertAnnexBToLengthPrefixedNalUnits(inputFrame, out var lengthPrefixed))
            {
                inputFrame = lengthPrefixed;
            }

            var inputIndex = ++_inputSamples;

            if (inputIndex == 1 || inputIndex % 120 == 0 || (!ScreenShareCodecNames.IsAv1(_codec) && ContainsNalUnitType(inputFrame, 5)))
            {
                Debug.WriteLine(
                    $"[ScreenShare:{GetLogCodecName()}:DECODER] Input sample {inputIndex}: bytes={h264Frame.Length}->{inputFrame.Length}.");
            }

            using var inputBuffer = MediaFactory.CreateMemoryBuffer(inputFrame.Length);
            int maxLength;
            int currentLength;
            var inputPtr = inputBuffer.Lock(out maxLength, out currentLength);
            try
            {
                Marshal.Copy(inputFrame, 0, inputPtr, inputFrame.Length);
            }
            finally
            {
                inputBuffer.Unlock();
            }

            inputBuffer.CurrentLength = inputFrame.Length;

            using var sample = MediaFactory.CreateSample();
            sample.AddBuffer(inputBuffer);
            sample.SampleTime = _sampleTime;
            sample.SampleDuration = FrameDuration100Ns;
            if (ScreenShareCodecNames.IsAv1(_codec) || ContainsNalUnitType(inputFrame, 5))
                sample.Set(SampleAttributeKeys.CleanPoint, true);
            if (inputIndex == 1)
                sample.Set(SampleAttributeKeys.Discontinuity, true);
            _sampleTime += FrameDuration100Ns;

            try
            {
                _decoder.ProcessInput(0, sample, 0);
            }
            catch (SharpDXException ex) when (ex.ResultCode.Code == MfENotAccepting)
            {
                var pendingFrame = TryDrainFrame(materializeOutput: materializeOutput);
                try
                {
                    _decoder.ProcessInput(0, sample, 0);
                }
                catch (SharpDXException retryEx) when (retryEx.ResultCode.Code == MfENotAccepting)
                {
                    return pendingFrame;
                }
            }
            catch (NullReferenceException ex)
            {
                Debug.WriteLine($"[ScreenShare:H264:DECODER] Media Foundation ProcessInput hit a null reference; dropping compressed frame and requesting decoder recovery. sampleNull={sample == null}; decoderNull={_decoder == null}; inputBytes={inputFrame.Length}; nals={DescribeNalUnits(inputFrame)}; error={ex.Message}");
                return null;
            }

            return TryDrainFrame(materializeOutput: materializeOutput);
        }

        public bool TrySwitchToLengthPrefixedInput(string reason)
        {
            if (_preferLengthPrefixedInput)
                return false;

            _preferLengthPrefixedInput = true;
            _sampleTime = 0;
            _inputSamples = 0;
            _needMoreInputCount = 0;
            _loggedOutputStreamMode = false;

            try
            {
                _decoder.ProcessMessage(TMessageType.CommandFlush, IntPtr.Zero);
                _decoder.ProcessMessage(TMessageType.NotifyStartOfStream, IntPtr.Zero);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ScreenShare:H264:DECODER] Length-prefixed input switch flush failed after {reason}: {ex.Message}");
            }

            Debug.WriteLine($"[ScreenShare:H264:DECODER] Switching receiver decoder input to length-prefixed H.264 NAL units after {reason}.");
            return true;
        }

        private static byte[] NormalizeH264Input(byte[] frame)
        {
            if (frame.Length < 5)
                return frame;

            if (HasStartCode(frame))
            {
                return frame;
            }

            if (TryConvertLengthPrefixedNalUnits(frame, out var annexB))
            {
                return annexB;
            }

            return frame;
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

                output.Add(0);
                output.Add(0);
                output.Add(0);
                output.Add(1);
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

        private static bool TryConvertAnnexBToLengthPrefixedNalUnits(byte[] frame, out byte[] lengthPrefixed)
        {
            var output = new List<byte>(frame.Length);
            foreach (var nal in EnumerateAnnexBNalUnits(frame))
            {
                if (nal.Length <= 0)
                    continue;

                output.Add((byte)((nal.Length >> 24) & 0xFF));
                output.Add((byte)((nal.Length >> 16) & 0xFF));
                output.Add((byte)((nal.Length >> 8) & 0xFF));
                output.Add((byte)(nal.Length & 0xFF));
                for (var i = nal.Offset; i < nal.Offset + nal.Length; i++)
                    output.Add(frame[i]);
            }

            if (output.Count == 0)
            {
                lengthPrefixed = frame;
                return false;
            }

            lengthPrefixed = output.ToArray();
            return true;
        }

        private void ConfigureTypes(int width, int height)
        {
            using var inputType = new MediaType();
            inputType.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
            inputType.Set(MediaTypeAttributeKeys.Subtype, _inputSubtype);
            inputType.Set(MediaTypeAttributeKeys.FrameSize, PackRatio(width, height));
            inputType.Set(MediaTypeAttributeKeys.FrameRate, PackRatio(ScreenShareCapturePipeline.TargetFps, 1));
            inputType.Set(MediaTypeAttributeKeys.PixelAspectRatio, PackRatio(1, 1));
            inputType.Set(MediaTypeAttributeKeys.InterlaceMode, (int)VideoInterlaceMode.Progressive);
            _decoder.SetInputType(0, inputType, 0);

            if (!TrySelectDecoderOutputType("initial configure"))
            {
                if (!TrySetExplicitDecoderOutputType(VideoFormatGuids.Rgb32, "explicit RGB32 fallback") &&
                    !TrySetExplicitDecoderOutputType(VideoFormatGuids.NV12, "explicit NV12 fallback"))
                {
                    Debug.WriteLine($"[ScreenShare:H264] Decoder could not set an explicit output type for {width}x{height}.");
                }
            }

            _loggedFirstOutputSample = false;
            _decoder.ProcessMessage(TMessageType.NotifyBeginStreaming, IntPtr.Zero);
            _decoder.ProcessMessage(TMessageType.NotifyStartOfStream, IntPtr.Zero);
        }

        private static Transform CreateAv1DecoderTransform()
        {
            var inputInfo = new TRegisterTypeInformation
            {
                GuidMajorType = MediaTypeGuids.Video,
                GuidSubtype = MediaFoundationAv1Encoder.Av1Subtype
            };
            var outputInfo = new TRegisterTypeInformation
            {
                GuidMajorType = MediaTypeGuids.Video,
                GuidSubtype = VideoFormatGuids.NV12
            };

            var activations = EnumerateDecoderActivations(
                inputInfo,
                outputInfo,
                TransformEnumFlag.Hardware | TransformEnumFlag.SortAndFilter).ToList();
            if (activations.Count == 0)
            {
                Debug.WriteLine("[ScreenShare:AV1X:DECODER] No hardware AV1 decoder was exposed; trying the Windows software AV1 decoder.");
                activations = EnumerateDecoderActivations(inputInfo, outputInfo, TransformEnumFlag.SortAndFilter).ToList();
            }

            foreach (var activation in activations)
            {
                try
                {
                    var name = GetActivationString(activation, TransformAttributeKeys.MftFriendlyNameAttribute, "Windows AV1 decoder");
                    Debug.WriteLine($"[ScreenShare:AV1X:DECODER] Using {name}.");
                    return activation.ActivateObject<Transform>();
                }
                finally
                {
                    activation.Dispose();
                }
            }

            throw new InvalidOperationException("Windows Media Foundation did not expose an AV1 decoder MFT.");
        }

        private byte[]? TryDrainFrame(
            bool handledStreamChange = false,
            int emptyRetryCount = 0,
            bool materializeOutput = true)
        {
            var outputBufferSize = GetOutputBufferSize(out var outputFlags);
            var useDecoderAllocatedOutput =
                (outputFlags & MftOutputStreamProvidesSamples) != 0 ||
                (outputFlags & MftOutputStreamCanProvideSamples) != 0;

            if (!_loggedOutputStreamMode)
            {
                _loggedOutputStreamMode = true;
                Debug.WriteLine(
                    $"[ScreenShare:H264:DECODER] Output stream flags=0x{outputFlags:X}; decoder-allocated output={useDecoderAllocatedOutput}; outputBufferSize={outputBufferSize}.");
            }

            Sample? outputSample = null;
            MediaBuffer? outputBuffer = null;
            if (!useDecoderAllocatedOutput)
            {
                outputSample = MediaFactory.CreateSample();
                outputBuffer = MediaFactory.CreateMemoryBuffer(outputBufferSize);
                outputSample.AddBuffer(outputBuffer);
            }

            var output = new[]
            {
                new TOutputDataBuffer
                {
                    DwStreamID = 0,
                    PSample = outputSample
                }
            };

            try
            {
                _decoder.ProcessOutput(TransformProcessOutputFlags.None, output, out _);
            }
            catch (SharpDXException ex) when (ex.ResultCode.Code == MfENeedMoreInput)
            {
                var count = ++_needMoreInputCount;
                if (count == 1 || count % 120 == 0)
                {
                    Debug.WriteLine(
                        $"[ScreenShare:H264:DECODER] Need more input after {_inputSamples} samples; count={count}; lastOutputFlags=0x{output[0].DwStatus:X}.");
                }
                return null;
            }
            catch (SharpDXException ex) when (ex.ResultCode.Code == MfEStreamChange)
            {
                if (!handledStreamChange && TrySelectDecoderOutputType("stream change"))
                    return TryDrainFrame(handledStreamChange: true, emptyRetryCount: emptyRetryCount, materializeOutput: materializeOutput);

                return null;
            }
            catch (SharpDXException ex) when (ex.ResultCode.Code == MfETypeNotSet)
            {
                if (!handledStreamChange && TrySelectDecoderOutputType("output type not set"))
                    return TryDrainFrame(handledStreamChange: true, emptyRetryCount: emptyRetryCount, materializeOutput: materializeOutput);

                return null;
            }
            catch (SharpDXException ex)
            {
                Debug.WriteLine($"[ScreenShare:H264] Decoder output failed: 0x{ex.ResultCode.Code:X8} {ex.Message}");
                return null;
            }

            try
            {
                var sampleToRead = output[0].PSample ?? outputSample;
                if (sampleToRead == null)
                {
                    var count = ++_needMoreInputCount;
                    if (count == 1 || count % 120 == 0)
                        Debug.WriteLine($"[ScreenShare:H264:DECODER] ProcessOutput succeeded but returned no output sample; status=0x{output[0].DwStatus:X}; count={count}.");
                    return null;
                }

                using var contiguous = sampleToRead.ConvertToContiguousBuffer();
                int length = contiguous.CurrentLength;
                if (length <= 0)
                {
                    var count = ++_needMoreInputCount;
                    if (count == 1 || count % 120 == 0)
                        Debug.WriteLine($"[ScreenShare:H264:DECODER] ProcessOutput returned an empty output sample; status=0x{output[0].DwStatus:X}; count={count}.");
                    return null;
                }

                int minOutputLength = _outputSubtype == VideoFormatGuids.Rgb32
                    ? _width * _height * 4
                    : _width * _height * 3 / 2;
                if (length < minOutputLength)
                {
                    Debug.WriteLine($"[ScreenShare:H264] Decoder output too small for {DescribeVideoSubtype(_outputSubtype)}: {length} < {minOutputLength}.");
                    return null;
                }

                // Every compressed H.264 sample must still pass through the decoder
                // so its reference chain remains valid. When playback is behind,
                // however, converting every decoded NV12 surface into a 3.6 MB BGRA
                // frame only makes the backlog grow. Drain the decoded surface and
                // skip that conversion; the caller will materialize the newest frame
                // once it has caught back up.
                if (!materializeOutput)
                    return null;

                int maxLength;
                int currentLength;
                var ptr = contiguous.Lock(out maxLength, out currentLength);
                try
                {
                    if (!_loggedFirstOutputSample || _inputSamples % 120 == 0)
                    {
                        _loggedFirstOutputSample = true;
                        Debug.WriteLine(
                            $"[ScreenShare:H264:DECODER] Output sample: subtype={DescribeVideoSubtype(_outputSubtype)}; bytes={length}; expectedRgb32={_width * _height * 4}; expectedNv12={_width * _height * 3 / 2}; currentLength={currentLength}; maxLength={maxLength}.");
                    }

                    if (_outputSubtype == VideoFormatGuids.Rgb32 || length >= _width * _height * 4)
                    {
                        if (_outputSubtype != VideoFormatGuids.Rgb32)
                            Debug.WriteLine("[ScreenShare:H264:DECODER] Decoder returned RGB32-sized output while NV12 was selected; treating sample as RGB32 to prevent colour corruption.");

                        return Rgb32ToBgra(ptr, length, _width, _height);
                    }

                    return Nv12ToBgra(ptr, length, _width, _height);
                }
                finally
                {
                    contiguous.Unlock();
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ScreenShare:H264] Decoder output conversion failed: {ex.Message}");
                return null;
            }
            finally
            {
                output[0].PSample?.Dispose();
                outputBuffer?.Dispose();
                outputSample?.Dispose();
            }
        }

        private bool TrySelectDecoderOutputType(string reason)
        {
            if (TrySelectDecoderOutputType(reason, VideoFormatGuids.Rgb32))
                return true;

            return TrySelectDecoderOutputType(reason, VideoFormatGuids.NV12);
        }

        private bool TrySelectDecoderOutputType(string reason, Guid requestedSubtype)
        {
            for (var index = 0; index < 16; index++)
            {
                MediaType? candidate = null;
                try
                {
                    if (!_decoder.TryGetOutputAvailableType(0, index, out candidate) ||
                        candidate == null)
                        continue;

                    var subtype = candidate.Get(MediaTypeAttributeKeys.Subtype);
                    if (subtype != requestedSubtype)
                        continue;

                    candidate.Set(MediaTypeAttributeKeys.FrameSize, PackRatio(_width, _height));
                    candidate.Set(MediaTypeAttributeKeys.FrameRate, PackRatio(ScreenShareCapturePipeline.TargetFps, 1));
                    candidate.Set(MediaTypeAttributeKeys.PixelAspectRatio, PackRatio(1, 1));
                    candidate.Set(MediaTypeAttributeKeys.InterlaceMode, (int)VideoInterlaceMode.Progressive);
                    candidate.Set(MediaTypeAttributeKeys.FixedSizeSamples, 1);
                    candidate.Set(MediaTypeAttributeKeys.SampleSize, GetVideoSampleSize(requestedSubtype, _width, _height));
                    candidate.Set(MediaTypeAttributeKeys.DefaultStride, requestedSubtype == VideoFormatGuids.Rgb32 ? _width * 4 : _width);

                    _decoder.SetOutputType(0, candidate, 0);
                    _outputSubtype = requestedSubtype;
                    Debug.WriteLine($"[ScreenShare:H264] Decoder output type refreshed after {reason}: {DescribeVideoSubtype(requestedSubtype)} {_width}x{_height}.");
                    return true;
                }
                catch (SharpDXException ex)
                {
                    if (index == 0 || index == 15)
                        Debug.WriteLine($"[ScreenShare:H264] Decoder output type candidate {index} ({DescribeVideoSubtype(requestedSubtype)}) rejected after {reason}: 0x{ex.ResultCode.Code:X8} {ex.Message}");
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[ScreenShare:H264] Decoder output type refresh failed after {reason}: {ex.Message}");
                    return false;
                }
                finally
                {
                    candidate?.Dispose();
                }
            }

            Debug.WriteLine($"[ScreenShare:H264] Decoder did not expose a {DescribeVideoSubtype(requestedSubtype)} output type after {reason}.");
            return false;
        }

        private bool TrySetExplicitDecoderOutputType(Guid subtype, string reason)
        {
            try
            {
                using var outputType = new MediaType();
                outputType.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
                outputType.Set(MediaTypeAttributeKeys.Subtype, subtype);
                outputType.Set(MediaTypeAttributeKeys.FrameSize, PackRatio(_width, _height));
                outputType.Set(MediaTypeAttributeKeys.FrameRate, PackRatio(ScreenShareCapturePipeline.TargetFps, 1));
                outputType.Set(MediaTypeAttributeKeys.PixelAspectRatio, PackRatio(1, 1));
                outputType.Set(MediaTypeAttributeKeys.InterlaceMode, (int)VideoInterlaceMode.Progressive);
                outputType.Set(MediaTypeAttributeKeys.FixedSizeSamples, 1);
                outputType.Set(MediaTypeAttributeKeys.SampleSize, GetVideoSampleSize(subtype, _width, _height));
                outputType.Set(MediaTypeAttributeKeys.DefaultStride, subtype == VideoFormatGuids.Rgb32 ? _width * 4 : _width);
                _decoder.SetOutputType(0, outputType, 0);
                _outputSubtype = subtype;
                Debug.WriteLine($"[ScreenShare:H264] Decoder using {reason}: {DescribeVideoSubtype(subtype)} {_width}x{_height}.");
                return true;
            }
            catch (SharpDXException ex)
            {
                Debug.WriteLine($"[ScreenShare:H264] Decoder rejected {reason}: {DescribeVideoSubtype(subtype)}; 0x{ex.ResultCode.Code:X8} {ex.Message}");
                return false;
            }
        }

        private int GetOutputBufferSize(out int streamFlags)
        {
            int minimumNv12Size = _width * _height * 3 / 2;
            int fallbackSize = _width * _height * 4;

            try
            {
                _decoder.GetOutputStreamInfo(0, out var streamInfo);
                streamFlags = streamInfo.DwFlags;
                return Math.Max(Math.Max(streamInfo.CbSize, minimumNv12Size), fallbackSize);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ScreenShare:H264] Decoder stream info unavailable: {ex.Message}");
                streamFlags = 0;
                return Math.Max(minimumNv12Size, fallbackSize);
            }
        }

        private static bool ContainsNalUnitType(byte[] frame, byte nalType)
        {
            foreach (var nal in EnumerateNalUnits(frame))
            {
                if (nal.Type == nalType)
                    return true;
            }

            return false;
        }

        private static string DescribeNalUnits(byte[] frame)
        {
            var parts = new List<string>();
            foreach (var nal in EnumerateNalUnits(frame))
            {
                parts.Add($"{nal.Type}:{nal.Length}");
                if (parts.Count >= 8)
                    break;
            }

            return parts.Count == 0 ? "none" : string.Join(",", parts);
        }

        private static IEnumerable<NalUnit> EnumerateNalUnits(byte[] frame)
        {
            if (HasStartCode(frame))
                return EnumerateAnnexBNalUnits(frame);

            return EnumerateLengthPrefixedNalUnits(frame);
        }

        private static IEnumerable<NalUnit> EnumerateLengthPrefixedNalUnits(byte[] frame)
        {
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
                    yield break;

                yield return new NalUnit(offset, nalLength, (byte)(frame[offset] & 0x1F));
                offset += nalLength;
            }
        }

        private static IEnumerable<NalUnit> EnumerateAnnexBNalUnits(byte[] frame)
        {
            var index = 0;
            while (TryFindStartCode(frame, index, out var startCodeIndex, out var startCodeLength))
            {
                var nalOffset = startCodeIndex + startCodeLength;
                var nextSearchStart = nalOffset;
                var nextStart = frame.Length;
                if (TryFindStartCode(frame, nextSearchStart, out var foundNextStart, out _))
                    nextStart = foundNextStart;

                var nalLength = nextStart - nalOffset;
                if (nalLength > 0)
                    yield return new NalUnit(nalOffset, nalLength, (byte)(frame[nalOffset] & 0x1F));

                index = nextStart;
            }
        }

        private static bool TryFindStartCode(byte[] frame, int start, out int index, out int length)
        {
            for (var i = Math.Max(0, start); i + 3 < frame.Length; i++)
            {
                if (frame[i] != 0 || frame[i + 1] != 0)
                    continue;

                if (frame[i + 2] == 1)
                {
                    index = i;
                    length = 3;
                    return true;
                }

                if (i + 4 < frame.Length && frame[i + 2] == 0 && frame[i + 3] == 1)
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

        private static unsafe byte[] Nv12ToBgra(IntPtr nv12Pointer, int nv12Length, int width, int height)
        {
            byte[] bgra = new byte[width * height * 4];
            int lumaHeight = GetNv12LumaHeight(nv12Length, width, height);
            int uvStart = width * lumaHeight;

            // The 720p receiver was spending 33-55 ms per frame in this colour
            // conversion. Process independent scanlines in parallel so decode
            // stays within the 30 fps realtime budget and does not trigger the
            // dependent-frame backlog/keyframe recovery loop.
            var outputHandle = GCHandle.Alloc(bgra, GCHandleType.Pinned);
            try
            {
                var outputPointer = outputHandle.AddrOfPinnedObject();
                Parallel.For(0, height, ColourConversionParallelOptions, y =>
                {
                    var nv12Base = (byte*)nv12Pointer.ToPointer();
                    var bgraBase = (byte*)outputPointer.ToPointer();
                    var yRow = nv12Base + (y * width);
                    var uvRowOffset = uvStart + ((y / 2) * width);
                    var destination = bgraBase + (y * width * 4);

                    for (int x = 0; x < width; x++)
                    {
                        var uv = uvRowOffset + (x & ~1);
                        if (uv + 1 >= nv12Length)
                            break;

                        int c = Math.Max(0, yRow[x] - 16);
                        int u = nv12Base[uv] - 128;
                        int v = nv12Base[uv + 1] - 128;

                        var dst = destination + (x * 4);
                        dst[0] = ClampToByte((298 * c + 541 * u + 128) >> 8);
                        dst[1] = ClampToByte((298 * c - 55 * u - 136 * v + 128) >> 8);
                        dst[2] = ClampToByte((298 * c + 459 * v + 128) >> 8);
                        dst[3] = 255;
                    }
                });
            }
            finally
            {
                outputHandle.Free();
            }

            return bgra;
        }

        private static int GetNv12LumaHeight(int byteLength, int width, int visibleHeight)
        {
            if (width <= 0)
                return visibleHeight;

            var nominalLength = width * visibleHeight * 3 / 2;
            if (byteLength <= nominalLength)
                return visibleHeight;

            var paddedLumaBytesTimesTwo = byteLength * 2;
            var divisor = width * 3;
            if (divisor > 0 && paddedLumaBytesTimesTwo % divisor == 0)
            {
                var paddedHeight = paddedLumaBytesTimesTwo / divisor;
                if (paddedHeight >= visibleHeight)
                    return paddedHeight;
            }

            return visibleHeight;
        }

        private static unsafe byte[] Rgb32ToBgra(IntPtr rgb32Pointer, int rgb32Length, int width, int height)
        {
            var outputLength = width * height * 4;
            var bgra = new byte[outputLength];
            Marshal.Copy(rgb32Pointer, bgra, 0, Math.Min(rgb32Length, outputLength));

            for (var i = 3; i < bgra.Length; i += 4)
                bgra[i] = 255;

            return bgra;
        }

        private static int GetVideoSampleSize(Guid subtype, int width, int height)
        {
            return subtype == VideoFormatGuids.Rgb32
                ? width * height * 4
                : width * height * 3 / 2;
        }

        private static string DescribeVideoSubtype(Guid subtype)
        {
            if (subtype == VideoFormatGuids.Rgb32)
                return "RGB32";
            if (subtype == VideoFormatGuids.NV12)
                return "NV12";

            return subtype.ToString();
        }

        private static long PackRatio(int high, int low)
        {
            return ((long)high << 32) | (uint)low;
        }

        private string GetLogCodecName()
        {
            return ScreenShareCodecNames.IsAv1(_codec) ? ScreenShareCodecNames.AV1XDisplayName : "H264";
        }

        private static IEnumerable<Activate> EnumerateDecoderActivations(
            TRegisterTypeInformation inputInfo,
            TRegisterTypeInformation outputInfo,
            TransformEnumFlag flags)
        {
            var category = TransformCategoryGuids.VideoDecoder;
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

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeMftRegisterTypeInfo
        {
            public Guid GuidMajorType;
            public Guid GuidSubtype;
        }

        private static byte ClampToByte(int value)
        {
            if (value < 0) return 0;
            if (value > 255) return 255;
            return (byte)value;
        }

        private readonly record struct NalUnit(int Offset, int Length, byte Type);

        public void Dispose()
        {
            try
            {
                _decoder.ProcessMessage(TMessageType.NotifyEndOfStream, IntPtr.Zero);
                _decoder.ProcessMessage(TMessageType.NotifyEndStreaming, IntPtr.Zero);
            }
            catch
            {
            }

            _decoder.Dispose();
        }
    }
}

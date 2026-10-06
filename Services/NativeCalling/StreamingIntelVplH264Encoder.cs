using SharpDX;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace Zink.Services.NativeCalling
{
    public sealed class StreamingIntelVplH264Encoder : IH264VideoEncoder
    {
        private static readonly int[] YFromR = BuildContributionTable(47, 16 << 8);
        private static readonly int[] YFromG = BuildContributionTable(157, 0);
        private static readonly int[] YFromB = BuildContributionTable(16, 0);
        private static readonly int[] UFromR = BuildContributionTable(-26, 128 << 8);
        private static readonly int[] UFromG = BuildContributionTable(-87, 0);
        private static readonly int[] UFromB = BuildContributionTable(112, 0);
        private static readonly int[] VFromR = BuildContributionTable(112, 128 << 8);
        private static readonly int[] VFromG = BuildContributionTable(-102, 0);
        private static readonly int[] VFromB = BuildContributionTable(-10, 0);

        private readonly int _width;
        private readonly int _height;
        private readonly int _frameRate;
        private readonly byte[] _nv12Buffer;
        private readonly byte[] _outputBuffer;
        private IntPtr _handle;
        private int _forceNextKeyFrame = 1;
        private Texture2D? _readbackTexture;
        private int _readbackWidth;
        private int _readbackHeight;
        private ZinkHdrToSdrGpuConverter? _hdrConverter;
        private Texture2D? _hdrSdrTexture;
        private bool _loggedHdrStreamConversion;

        public StreamingIntelVplH264Encoder(int width, int height, int bitrate, int frameRate)
        {
            _width = width;
            _height = height;
            _frameRate = Math.Clamp(frameRate, 1, StreamingVideoCapturePipeline.TargetFps);
            _nv12Buffer = new byte[width * height * 3 / 2];
            _outputBuffer = new byte[Math.Max(8 * 1024 * 1024, width * height)];

            var result = NativeMethods.ZinkIntelVpl_CreateEncoder(width, height, _frameRate, bitrate, 0, out _handle);
            if (result != 0 || _handle == IntPtr.Zero)
            {
                throw new InvalidOperationException(
                    $"Intel oneVPL H.264 encoder failed to start. result={result}. Install/update the Intel graphics driver with oneVPL/Quick Sync support.");
            }

            Debug.WriteLine($"[ScreenShare:H264:IntelVPL] Official Intel oneVPL H.264 encoder started {width}x{height} @ {_frameRate}fps @ {bitrate}bps.");
            DiagnosticLogService.WriteLine($"[ScreenShare:H264:IntelVPL] Official Intel oneVPL H.264 encoder selected. No Intel Media Foundation fallback is enabled. {width}x{height} @ {_frameRate}fps @ {bitrate / 1000}k.");
        }

        public string EncoderMode => "Official Intel oneVPL H.264 (Quick Sync)";
        public string InputFormat => "WGC D3D11 BGRA texture readback to NV12 Intel oneVPL";
        public string GpuDeviceManagerMode => "Intel oneVPL hardware implementation; Media Foundation bypassed";
        public bool IsHardwareAccelerated => true;
        public bool CanEncodeGpuTexture => true;
        public bool RealtimeModeEnabled => true;
        public bool LowLatencyOutputEnabled => true;
        public int RecoveryKeyFrameInterval => _frameRate * 2;
        public int PendingHardwareInputs => 0;
        public int HardwareInputRequests => 0;
        public int HardwareOutputRequests => 0;
        public bool UsesHardwareEventPump => false;
        public bool RecoveryKeyFrameRequestPending => Volatile.Read(ref _forceNextKeyFrame) != 0;

        public IReadOnlyList<H264EncodedFrame> Encode(Bitmap bitmap, long? timestampMilliseconds = null)
        {
            if (_handle == IntPtr.Zero)
                throw new ObjectDisposedException(nameof(StreamingIntelVplH264Encoder));

            ConvertBitmapToNv12(bitmap, _width, _height, _nv12Buffer);

            if (System.Threading.Interlocked.Exchange(ref _forceNextKeyFrame, 0) != 0)
                NativeMethods.ZinkIntelVpl_ForceKeyFrame(_handle);

            var timestampMs = Math.Max(0L, timestampMilliseconds ?? 0L);
            var timestamp90k = timestampMs * 90L;
            var result = NativeMethods.ZinkIntelVpl_EncodeNv12(
                _handle,
                _nv12Buffer,
                _nv12Buffer.Length,
                timestamp90k,
                _outputBuffer,
                _outputBuffer.Length,
                out var outputLength,
                out var keyFrame);

            if (result != 0)
                throw new InvalidOperationException($"Intel oneVPL H.264 encode failed. result={result}.");

            if (outputLength <= 0)
                return Array.Empty<H264EncodedFrame>();

            var output = new byte[outputLength];
            System.Buffer.BlockCopy(_outputBuffer, 0, output, 0, outputLength);
            return new[] { new H264EncodedFrame(output, keyFrame != 0 || ContainsH264IdrFrame(output), timestampMs) };
        }

        public IReadOnlyList<H264EncodedFrame> EncodeGpuBgraTexture(Texture2D sourceTexture, int sourceWidth, int sourceHeight, long? timestampMilliseconds)
        {
            if (_handle == IntPtr.Zero)
                throw new ObjectDisposedException(nameof(StreamingIntelVplH264Encoder));

            ConvertGpuBgraTextureToNv12(sourceTexture, sourceWidth, sourceHeight, _nv12Buffer);
            return EncodePreparedNv12(timestampMilliseconds);
        }

        public void ForceNextKeyFrame()
        {
            System.Threading.Interlocked.Exchange(ref _forceNextKeyFrame, 1);
            if (_handle != IntPtr.Zero)
                NativeMethods.ZinkIntelVpl_ForceKeyFrame(_handle);
        }

        public void Dispose()
        {
            _hdrSdrTexture?.Dispose();
            _hdrSdrTexture = null;
            _hdrConverter?.Dispose();
            _hdrConverter = null;
            _readbackTexture?.Dispose();
            _readbackTexture = null;

            var handle = _handle;
            _handle = IntPtr.Zero;
            if (handle != IntPtr.Zero)
                NativeMethods.ZinkIntelVpl_DestroyEncoder(handle);
        }

        private IReadOnlyList<H264EncodedFrame> EncodePreparedNv12(long? timestampMilliseconds)
        {
            if (System.Threading.Interlocked.Exchange(ref _forceNextKeyFrame, 0) != 0)
                NativeMethods.ZinkIntelVpl_ForceKeyFrame(_handle);

            var timestampMs = Math.Max(0L, timestampMilliseconds ?? 0L);
            var timestamp90k = timestampMs * 90L;
            var result = NativeMethods.ZinkIntelVpl_EncodeNv12(
                _handle,
                _nv12Buffer,
                _nv12Buffer.Length,
                timestamp90k,
                _outputBuffer,
                _outputBuffer.Length,
                out var outputLength,
                out var keyFrame);

            if (result != 0)
                throw new InvalidOperationException($"Intel oneVPL H.264 encode failed. result={result}.");

            if (outputLength <= 0)
                return Array.Empty<H264EncodedFrame>();

            var output = new byte[outputLength];
            System.Buffer.BlockCopy(_outputBuffer, 0, output, 0, outputLength);
            return new[] { new H264EncodedFrame(output, keyFrame != 0 || ContainsH264IdrFrame(output), timestampMs) };
        }

        private unsafe void ConvertGpuBgraTextureToNv12(Texture2D sourceTexture, int sourceWidth, int sourceHeight, byte[] nv12)
        {
            var sourceDescription = sourceTexture.Description;
            var actualSourceWidth = sourceWidth > 0 ? sourceWidth : sourceDescription.Width;
            var actualSourceHeight = sourceHeight > 0 ? sourceHeight : sourceDescription.Height;
            if (actualSourceWidth <= 0 || actualSourceHeight <= 0)
                throw new InvalidOperationException("Intel oneVPL GPU encode received an empty capture texture.");

            Texture2D conversionSource = sourceTexture;
            if (sourceDescription.Format == Format.R16G16B16A16_Float)
            {
                if (!_loggedHdrStreamConversion)
                {
                    _loggedHdrStreamConversion = true;
                    DiagnosticLogService.WriteLine($"[ScreenShare:HDR:Intel] conversion active; source={sourceDescription.Width}x{sourceDescription.Height}/{sourceDescription.Format}; target={_width}x{_height}/{Format.B8G8R8A8_UNorm}; encoderInput=NV12; encoder=Intel oneVPL H.264; toneMap='{ZinkHdrToSdrGpuConverter.ToneMapDescription}'.");
                    DiagnosticLogService.Flush();
                }
                EnsureHdrConversionResources(sourceTexture);
                _hdrConverter!.Convert(sourceTexture, _hdrSdrTexture!);
                conversionSource = _hdrSdrTexture!;
                sourceDescription = conversionSource.Description;
                actualSourceWidth = sourceDescription.Width;
                actualSourceHeight = sourceDescription.Height;
            }

            EnsureReadbackTexture(conversionSource, sourceDescription.Width, sourceDescription.Height);

            var context = conversionSource.Device.ImmediateContext;
            context.CopyResource(conversionSource, _readbackTexture);

            DataBox mapped = default;
            var mappedSuccessfully = false;
            try
            {
                mapped = context.MapSubresource(_readbackTexture, 0, MapMode.Read, SharpDX.Direct3D11.MapFlags.None);
                mappedSuccessfully = true;

                fixed (byte* nv12Base = nv12)
                {
                    ConvertBgraRowsToNv12(
                        mapped.DataPointer,
                        mapped.RowPitch,
                        actualSourceWidth,
                        actualSourceHeight,
                        _width,
                        _height,
                        (IntPtr)nv12Base,
                        _width * _height);
                }
            }
            finally
            {
                if (mappedSuccessfully)
                    context.UnmapSubresource(_readbackTexture, 0);
            }
        }

        private void EnsureHdrConversionResources(Texture2D sourceTexture)
        {
            if (_hdrSdrTexture != null &&
                _hdrSdrTexture.Description.Width == _width &&
                _hdrSdrTexture.Description.Height == _height &&
                _hdrSdrTexture.Device.NativePointer == sourceTexture.Device.NativePointer)
            {
                return;
            }

            _hdrSdrTexture?.Dispose();
            _hdrConverter?.Dispose();
            _hdrConverter = new ZinkHdrToSdrGpuConverter(sourceTexture.Device);
            _hdrSdrTexture = new Texture2D(sourceTexture.Device, new Texture2DDescription
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

        private void EnsureReadbackTexture(Texture2D sourceTexture, int width, int height)
        {
            if (_readbackTexture != null && _readbackWidth == width && _readbackHeight == height)
                return;

            _readbackTexture?.Dispose();
            var sourceDescription = sourceTexture.Description;
            var readbackDescription = new Texture2DDescription
            {
                Width = width,
                Height = height,
                MipLevels = 1,
                ArraySize = 1,
                Format = sourceDescription.Format == Format.Unknown ? Format.B8G8R8A8_UNorm : sourceDescription.Format,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Staging,
                BindFlags = BindFlags.None,
                CpuAccessFlags = CpuAccessFlags.Read,
                OptionFlags = ResourceOptionFlags.None
            };

            _readbackTexture = new Texture2D(sourceTexture.Device, readbackDescription);
            _readbackWidth = width;
            _readbackHeight = height;
        }

        private static unsafe void ConvertBitmapToNv12(Bitmap bitmap, int width, int height, byte[] nv12)
        {
            var disposeSource = bitmap.Width != width || bitmap.Height != height || bitmap.PixelFormat != PixelFormat.Format32bppArgb;
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
                var uvStart = width * height;
                fixed (byte* nv12Base = nv12)
                {
                    ConvertBitmapToNv12Rows(data.Scan0, data.Stride, width, height, (IntPtr)nv12Base, uvStart);
                }
            }
            finally
            {
                source.UnlockBits(data);
                converted?.Dispose();
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

        private static unsafe void ConvertBgraRowsToNv12(
            IntPtr sourceBasePtr,
            int sourceStride,
            int sourceWidth,
            int sourceHeight,
            int targetWidth,
            int targetHeight,
            IntPtr nv12BasePtr,
            int uvStart)
        {
            var rowPairs = (targetHeight + 1) / 2;
            var sourceBase = (byte*)sourceBasePtr;
            var nv12Base = (byte*)nv12BasePtr;

            Parallel.For(0, rowPairs, rowPair =>
            {
                var y = rowPair * 2;
                var nextY = Math.Min(y + 1, targetHeight - 1);
                var sourceY0 = Math.Min(sourceHeight - 1, y * sourceHeight / targetHeight);
                var sourceY1 = Math.Min(sourceHeight - 1, nextY * sourceHeight / targetHeight);
                var yPlane0 = nv12Base + y * targetWidth;
                var yPlane1 = nv12Base + nextY * targetWidth;
                var uvPlane = nv12Base + uvStart + rowPair * targetWidth;

                for (var x = 0; x < targetWidth; x += 2)
                {
                    var nextX = Math.Min(x + 1, targetWidth - 1);
                    var sourceX0 = Math.Min(sourceWidth - 1, x * sourceWidth / targetWidth);
                    var sourceX1 = Math.Min(sourceWidth - 1, nextX * sourceWidth / targetWidth);

                    var p00 = sourceBase + sourceY0 * sourceStride + sourceX0 * 4;
                    var p01 = sourceBase + sourceY0 * sourceStride + sourceX1 * 4;
                    var p10 = sourceBase + sourceY1 * sourceStride + sourceX0 * 4;
                    var p11 = sourceBase + sourceY1 * sourceStride + sourceX1 * 4;

                    yPlane0[x] = GetY(p00);
                    yPlane0[nextX] = GetY(p01);
                    yPlane1[x] = GetY(p10);
                    yPlane1[nextX] = GetY(p11);

                    var u = (GetU(p00) + GetU(p01) + GetU(p10) + GetU(p11)) >> 2;
                    var v = (GetV(p00) + GetV(p01) + GetV(p10) + GetV(p11)) >> 2;
                    uvPlane[x] = (byte)u;
                    if (x + 1 < targetWidth)
                        uvPlane[x + 1] = (byte)v;
                }
            });
        }

        private static unsafe byte GetY(byte* src) => ClampToByte((YFromR[src[2]] + YFromG[src[1]] + YFromB[src[0]]) >> 8);
        private static unsafe byte GetU(byte* src) => ClampToByte((UFromR[src[2]] + UFromG[src[1]] + UFromB[src[0]]) >> 8);
        private static unsafe byte GetV(byte* src) => ClampToByte((VFromR[src[2]] + VFromG[src[1]] + VFromB[src[0]]) >> 8);

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

        private static bool ContainsH264IdrFrame(byte[] data)
        {
            for (var i = 0; i + 4 < data.Length; i++)
            {
                var startCodeLength = 0;
                if (data[i] == 0 && data[i + 1] == 0 && data[i + 2] == 1)
                    startCodeLength = 3;
                else if (i + 4 < data.Length && data[i] == 0 && data[i + 1] == 0 && data[i + 2] == 0 && data[i + 3] == 1)
                    startCodeLength = 4;

                if (startCodeLength == 0)
                    continue;

                var nalType = data[i + startCodeLength] & 0x1F;
                if (nalType == 5)
                    return true;
            }

            return false;
        }

        private static class NativeMethods
        {
            [DllImport("ZinkIntelVplEncoder.dll", CallingConvention = CallingConvention.Cdecl)]
            public static extern int ZinkIntelVpl_CreateEncoder(int width, int height, int frameRate, int bitrate, int enableHdr10, out IntPtr handle);

            [DllImport("ZinkIntelVplEncoder.dll", CallingConvention = CallingConvention.Cdecl)]
            public static extern int ZinkIntelVpl_EncodeNv12(
                IntPtr handle,
                byte[] nv12,
                int nv12Length,
                long timestamp90k,
                byte[] output,
                int outputCapacity,
                out int outputLength,
                out int isKeyFrame);

            [DllImport("ZinkIntelVplEncoder.dll", CallingConvention = CallingConvention.Cdecl)]
            public static extern int ZinkIntelVpl_ForceKeyFrame(IntPtr handle);

            [DllImport("ZinkIntelVplEncoder.dll", CallingConvention = CallingConvention.Cdecl)]
            public static extern void ZinkIntelVpl_DestroyEncoder(IntPtr handle);
        }
    }
}

using SharpDX.Direct3D11;
using SharpDX.DXGI;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;
using D3D11Device = SharpDX.Direct3D11.Device;

namespace Zink.Services.NativeCalling
{
    /// <summary>
    /// Zink-owned HDR10 encoder: the capture GPU converts FP16 scRGB to P010,
    /// then Intel oneVPL encodes HEVC Main10 without an external process.
    /// </summary>
    public sealed class IntelVplHevcEncoder : IH264VideoEncoder
    {
        private const int OutputCapacity = 32 * 1024 * 1024;
        private readonly int _width;
        private readonly int _height;
        private readonly int _frameRate;
        private readonly byte[] _p010;
        private readonly byte[] _output = new byte[OutputCapacity];
        private IntPtr _handle;
        private D3D11Device? _device;
        private ZinkHdrGpuConverter? _converter;
        private Texture2D? _p010Texture;
        private Texture2D? _readbackTexture;

        public IntelVplHevcEncoder(int width, int height, int bitrate, int frameRate)
        {
            _width = width;
            _height = height;
            _frameRate = Math.Clamp(frameRate, 1, 120);
            _p010 = new byte[checked(width * height * 3)];
            int result = NativeMethods.ZinkIntelVpl_CreateEncoder(
                width, height, _frameRate, bitrate, 1, out _handle);
            if (result != 0 || _handle == IntPtr.Zero)
                throw new InvalidOperationException($"Intel oneVPL HEVC Main10 initialization failed ({result}).");
        }

        public string EncoderMode => "Zink native Intel oneVPL HEVC Main10";
        public string InputFormat => "D3D11 FP16 scRGB -> Zink P010 shader -> Intel oneVPL";
        public bool IsHardwareAccelerated => true;
        public bool CanEncodeGpuTexture => true;
        public bool RealtimeModeEnabled => true;
        public bool LowLatencyOutputEnabled => true;
        public string GpuDeviceManagerMode => "Zink D3D11 shader + Intel oneVPL system-memory P010";
        public int RecoveryKeyFrameInterval => _frameRate * 2;
        public int PendingHardwareInputs => 0;
        public int HardwareInputRequests => 0;
        public int HardwareOutputRequests => 0;
        public bool UsesHardwareEventPump => false;
        public bool RecoveryKeyFrameRequestPending => false;

        public IReadOnlyList<H264EncodedFrame> Encode(Bitmap bitmap, long? timestampMilliseconds = null) =>
            throw new NotSupportedException("HDR10 encoding requires an FP16 GPU texture.");

        public IReadOnlyList<H264EncodedFrame> EncodeGpuBgraTexture(
            Texture2D sourceTexture,
            int sourceWidth,
            int sourceHeight,
            long? timestampMilliseconds)
        {
            if (_handle == IntPtr.Zero)
                throw new ObjectDisposedException(nameof(IntelVplHevcEncoder));
            if (sourceTexture.Description.Format != Format.R16G16B16A16_Float)
                throw new InvalidOperationException("HDR10 encoding requires an FP16 scRGB capture texture.");

            EnsureConversionResources(sourceTexture.Device);
            _converter!.Convert(sourceTexture, _p010Texture!);
            _device!.ImmediateContext.CopyResource(_p010Texture!, _readbackTexture!);
            var mapped = _device.ImmediateContext.MapSubresource(
                _readbackTexture!, 0, MapMode.Read, SharpDX.Direct3D11.MapFlags.None);
            try
            {
                int rowBytes = _width * 2;
                if (mapped.RowPitch == rowBytes)
                {
                    // A native-resolution P010 staging surface on the capture GPU
                    // is tightly packed. Copy both planes in one transfer instead
                    // of issuing thousands of Marshal.Copy calls per frame.
                    Marshal.Copy(mapped.DataPointer, _p010, 0, _p010.Length);
                }
                else
                {
                    for (int row = 0; row < _height; row++)
                        Marshal.Copy(mapped.DataPointer + row * mapped.RowPitch, _p010, row * rowBytes, rowBytes);
                    IntPtr chroma = mapped.DataPointer + mapped.RowPitch * _height;
                    int chromaOffset = rowBytes * _height;
                    for (int row = 0; row < _height / 2; row++)
                        Marshal.Copy(chroma + row * mapped.RowPitch, _p010, chromaOffset + row * rowBytes, rowBytes);
                }
            }
            finally
            {
                _device.ImmediateContext.UnmapSubresource(_readbackTexture!, 0);
            }

            long timestampMs = Math.Max(0, timestampMilliseconds ?? 0);
            long timestamp90k = timestampMs * 90;
            int result = NativeMethods.ZinkIntelVpl_EncodeP010(
                _handle, _p010, _p010.Length, timestamp90k,
                _output, _output.Length, out int outputLength, out int keyFrame);
            if (result != 0)
                throw new InvalidOperationException($"Intel oneVPL HEVC encode failed ({result}).");
            if (outputLength <= 0)
                return Array.Empty<H264EncodedFrame>();

            var encoded = new byte[outputLength];
            System.Buffer.BlockCopy(_output, 0, encoded, 0, outputLength);
            return new[] { new H264EncodedFrame(encoded, keyFrame != 0, timestampMs) };
        }

        public void ForceNextKeyFrame()
        {
            if (_handle != IntPtr.Zero)
                NativeMethods.ZinkIntelVpl_ForceKeyFrame(_handle);
        }

        private void EnsureConversionResources(D3D11Device device)
        {
            if (_device != null && _device.NativePointer == device.NativePointer)
                return;
            DisposeConversionResources();
            _device = device;
            _converter = new ZinkHdrGpuConverter(device);
            _p010Texture = new Texture2D(device, new Texture2DDescription
            {
                Width = _width,
                Height = _height,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.P010,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default,
                BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
                CpuAccessFlags = CpuAccessFlags.None,
                OptionFlags = ResourceOptionFlags.None
            });
            _readbackTexture = new Texture2D(device, new Texture2DDescription
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
        }

        private void DisposeConversionResources()
        {
            _readbackTexture?.Dispose();
            _p010Texture?.Dispose();
            _converter?.Dispose();
            _readbackTexture = null;
            _p010Texture = null;
            _converter = null;
            _device = null;
        }

        public void Dispose()
        {
            DisposeConversionResources();
            IntPtr handle = _handle;
            _handle = IntPtr.Zero;
            if (handle != IntPtr.Zero)
                NativeMethods.ZinkIntelVpl_DestroyEncoder(handle);
        }

        private static class NativeMethods
        {
            [DllImport("ZinkIntelVplEncoder.dll", CallingConvention = CallingConvention.Cdecl)]
            public static extern int ZinkIntelVpl_CreateEncoder(
                int width, int height, int frameRate, int bitrate, int enableHdr10, out IntPtr handle);

            [DllImport("ZinkIntelVplEncoder.dll", CallingConvention = CallingConvention.Cdecl)]
            public static extern int ZinkIntelVpl_EncodeP010(
                IntPtr handle,
                byte[] p010,
                int p010Length,
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

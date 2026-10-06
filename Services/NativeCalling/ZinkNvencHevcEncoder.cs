using SharpDX.Direct3D11;
using SharpDX.DXGI;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;
using D3D11Device = SharpDX.Direct3D11.Device;

namespace Zink.Services.NativeCalling
{
    public sealed class ZinkNvencHevcEncoder : IH264VideoEncoder
    {
        private const int OutputCapacity = 32 * 1024 * 1024;
        private readonly int _width;
        private readonly int _height;
        private readonly int _frameRate;
        private readonly byte[] _output = new byte[OutputCapacity];
        private readonly D3D11Device _device;
        private readonly ZinkHdrGpuConverter _converter;
        private readonly Texture2D _p010Texture;
        private IntPtr _handle;
        private bool _forceKeyFrame = true;

        public ZinkNvencHevcEncoder(
            D3D11Device device,
            int width,
            int height,
            int bitrate,
            int frameRate)
        {
            _device = device ?? throw new ArgumentNullException(nameof(device));
            _width = width;
            _height = height;
            _frameRate = Math.Clamp(frameRate, 1, 120);
            _converter = new ZinkHdrGpuConverter(device);
            _p010Texture = new Texture2D(device, new Texture2DDescription
            {
                Width = width,
                Height = height,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.P010,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default,
                BindFlags = BindFlags.RenderTarget,
                CpuAccessFlags = CpuAccessFlags.None,
                OptionFlags = ResourceOptionFlags.None
            });

            int result = NativeMethods.ZinkNvenc_Create(
                device.NativePointer,
                width,
                height,
                _frameRate,
                bitrate,
                out _handle);
            if (result != 0 || _handle == IntPtr.Zero)
                throw new InvalidOperationException($"Zink native NVENC HEVC Main10 initialization failed ({result}).");
        }

        public string EncoderMode => "Zink native NVIDIA NVENC HEVC Main10";
        public string InputFormat => "D3D11 FP16 scRGB -> Zink P010 shader -> native NVENC";
        public bool IsHardwareAccelerated => true;
        public bool CanEncodeGpuTexture => true;
        public bool RealtimeModeEnabled => true;
        public bool LowLatencyOutputEnabled => true;
        public string GpuDeviceManagerMode => "Zink direct D3D11 P010 NVENC resource";
        public int RecoveryKeyFrameInterval => _frameRate * 2;
        public int PendingHardwareInputs => 0;
        public int HardwareInputRequests => 0;
        public int HardwareOutputRequests => 0;
        public bool UsesHardwareEventPump => false;
        public bool RecoveryKeyFrameRequestPending => _forceKeyFrame;

        public IReadOnlyList<H264EncodedFrame> Encode(Bitmap bitmap, long? timestampMilliseconds = null) =>
            throw new NotSupportedException("HDR10 NVENC requires an FP16 D3D11 texture.");

        public IReadOnlyList<H264EncodedFrame> EncodeGpuBgraTexture(
            Texture2D sourceTexture,
            int sourceWidth,
            int sourceHeight,
            long? timestampMilliseconds = null)
        {
            if (_handle == IntPtr.Zero)
                throw new ObjectDisposedException(nameof(ZinkNvencHevcEncoder));
            if (sourceTexture.Description.Format != Format.R16G16B16A16_Float)
                throw new InvalidOperationException("Native HDR10 NVENC requires an FP16 scRGB capture texture.");

            _converter.Convert(sourceTexture, _p010Texture);
            long timestamp100Ns = Math.Max(0, timestampMilliseconds ?? 0) * 10_000L;
            int result = NativeMethods.ZinkNvenc_Encode(
                _handle,
                _p010Texture.NativePointer,
                timestamp100Ns,
                _forceKeyFrame ? 1 : 0,
                _output,
                _output.Length,
                out int outputLength,
                out int keyFrame);
            _forceKeyFrame = false;
            if (result == 1 || outputLength == 0)
                return Array.Empty<H264EncodedFrame>();
            if (result != 0)
                throw new InvalidOperationException($"Zink native NVENC HEVC encode failed ({result}).");

            byte[] encoded = new byte[outputLength];
            System.Buffer.BlockCopy(_output, 0, encoded, 0, outputLength);
            return new[]
            {
                new H264EncodedFrame(
                    encoded,
                    keyFrame != 0,
                    Math.Max(0, timestampMilliseconds ?? 0))
            };
        }

        public void ForceNextKeyFrame() => _forceKeyFrame = true;

        public void Dispose()
        {
            IntPtr handle = _handle;
            _handle = IntPtr.Zero;
            if (handle != IntPtr.Zero)
                NativeMethods.ZinkNvenc_Destroy(handle);
            _p010Texture.Dispose();
            _converter.Dispose();
        }

        private static class NativeMethods
        {
            [DllImport("ZinkNvencEncoder.dll", CallingConvention = CallingConvention.Cdecl)]
            internal static extern int ZinkNvenc_Create(
                IntPtr d3d11Device,
                int width,
                int height,
                int frameRate,
                int bitrate,
                out IntPtr handle);

            [DllImport("ZinkNvencEncoder.dll", CallingConvention = CallingConvention.Cdecl)]
            internal static extern int ZinkNvenc_Encode(
                IntPtr handle,
                IntPtr d3d11Texture,
                long timestamp100Ns,
                int forceIdr,
                byte[] output,
                int outputCapacity,
                out int outputLength,
                out int keyFrame);

            [DllImport("ZinkNvencEncoder.dll", CallingConvention = CallingConvention.Cdecl)]
            internal static extern void ZinkNvenc_Destroy(IntPtr handle);
        }
    }
}

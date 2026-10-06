using SharpDX.Direct3D11;
using System;
using System.Collections.Generic;
using System.Drawing;

namespace Zink.Services.NativeCalling
{
    public interface IH264VideoEncoder : IDisposable
    {
        string EncoderMode { get; }
        string InputFormat { get; }
        string GpuDeviceManagerMode { get; }
        bool IsHardwareAccelerated { get; }
        bool CanEncodeGpuTexture { get; }
        bool RealtimeModeEnabled { get; }
        bool LowLatencyOutputEnabled { get; }
        int RecoveryKeyFrameInterval { get; }
        int PendingHardwareInputs { get; }
        int HardwareInputRequests { get; }
        int HardwareOutputRequests { get; }
        bool UsesHardwareEventPump { get; }
        bool RecoveryKeyFrameRequestPending { get; }

        IReadOnlyList<H264EncodedFrame> Encode(Bitmap bitmap, long? timestampMilliseconds = null);
        IReadOnlyList<H264EncodedFrame> EncodeGpuBgraTexture(Texture2D sourceTexture, int sourceWidth, int sourceHeight, long? timestampMilliseconds);
        void ForceNextKeyFrame();
    }
}

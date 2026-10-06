using SharpDX.Direct3D11;
using SharpDX.DXGI;
using System;

namespace Zink.Services.Recording
{
    internal sealed class GpuVideoFramePacket : EventArgs
    {
        public GpuVideoFramePacket(Texture2D texture, TimeSpan timestamp, int width, int height, bool isDuplicate = false, Format format = Format.B8G8R8A8_UNorm)
        {
            Texture = texture;
            Timestamp = timestamp;
            Width = width;
            Height = height;
            IsDuplicate = isDuplicate;
            Format = format;
        }

        public Texture2D Texture { get; }
        public TimeSpan Timestamp { get; }
        public int Width { get; }
        public int Height { get; }
        public bool IsDuplicate { get; }
        public Format Format { get; }
    }
}

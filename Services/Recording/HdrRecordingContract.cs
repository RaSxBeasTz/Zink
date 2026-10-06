using SharpDX.DXGI;

namespace Zink.Services.Recording
{
    internal static class HdrRecordingContract
    {
        public const string VideoCodec = "HEVC Main10";
        public const string CaptureFormat = "R16G16B16A16_FLOAT scRGB";
        public const string EncoderInputFormat = "P010";
        public const string ColourPrimaries = "BT.2020";
        public const string TransferFunction = "SMPTE ST 2084 (PQ)";
        public const string Matrix = "BT.2020 non-constant luminance";
        public const int BitsPerComponent = 10;

        public static bool IsHdrCaptureTexture(Format format) =>
            format == Format.R16G16B16A16_Float;

        public static bool IsP010EncoderTexture(Format format) =>
            format == Format.P010;
    }
}

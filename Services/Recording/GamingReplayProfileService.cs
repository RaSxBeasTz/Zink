using SharpDX.Direct3D11;
using SharpDX.DXGI;
using System;
using Zink.Models;
using Zink.Services.NativeCalling;

namespace Zink.Services.Recording
{
    internal sealed record GamingReplayProfile(
        int SourceWidth,
        int SourceHeight,
        int OutputWidth,
        int OutputHeight,
        int FrameRate,
        int VideoBitrate,
        int VendorId,
        string AdapterName,
        long DedicatedVideoMemoryBytes,
        bool IsHdr,
        string Reason);

    internal static class GamingReplayProfileService
    {
        public static GamingReplayProfile Select(
            SharpDX.Direct3D11.Device? captureDevice,
            int sourceWidth,
            int sourceHeight,
            RecordingOptions requested,
            bool isHdr)
        {
            int vendorId = 0;
            string adapterName = "Unknown adapter";
            long dedicatedVideoMemory = 0;
            RecordingHardwareProfile? hardwareProfile = null;
            try
            {
                using var factory = new Factory1();
                int bestAdapterIndex = 0;
                long bestDedicatedMemory = -1;
                for (int index = 0; ; index++)
                {
                    Adapter1? candidate = null;
                    try { candidate = factory.GetAdapter1(index); }
                    catch { break; }
                    using (candidate)
                    {
                        AdapterDescription1 description = candidate.Description1;
                        if ((description.Flags & AdapterFlags.Software) != 0)
                            continue;
                        if (description.DedicatedVideoMemory > bestDedicatedMemory)
                        {
                            bestDedicatedMemory = description.DedicatedVideoMemory;
                            bestAdapterIndex = index;
                        }
                    }
                }

                using Adapter1 adapter = factory.GetAdapter1(bestAdapterIndex);
                AdapterDescription1 selected = adapter.Description1;
                vendorId = selected.VendorId;
                adapterName = selected.Description?.Trim() ?? adapterName;
                dedicatedVideoMemory = selected.DedicatedVideoMemory;
                hardwareProfile = RecordingHardwareProfileService.Detect(
                    factory, bestAdapterIndex, sourceWidth, sourceHeight);
            }
            catch
            {
            }

            int requestedFps = (int)Math.Clamp(requested.FrameRate, 30u, 120u);
            int outputWidth = MakeEven(hardwareProfile?.Width ?? Math.Min(sourceWidth, 1920));
            int outputHeight = MakeEven(hardwareProfile?.Height ?? Math.Min(sourceHeight, 1080));
            int fps = Math.Min(requestedFps, hardwareProfile?.FrameRate ?? 60);
            int bitrate = CalculateDisplayNativeBitrate(
                outputWidth,
                outputHeight,
                fps,
                isHdr);
            string reason = hardwareProfile?.IsHighEnd == true
                ? "High-end replay profile selected from DDR5, system RAM, CPU, discrete GPU model and dedicated VRAM"
                : "Low-hardware replay profile selected from the detected system memory, CPU and GPU capability";

            return new GamingReplayProfile(
                sourceWidth,
                sourceHeight,
                outputWidth,
                outputHeight,
                fps,
                requested.VideoBitrate > 0 ? (int)requested.VideoBitrate : bitrate,
                vendorId,
                adapterName,
                dedicatedVideoMemory,
                isHdr,
                reason);
        }

        private static int CalculateDisplayNativeBitrate(
            int width,
            int height,
            int frameRate,
            bool isHdr)
        {
            double pixelsPerSecond = (double)width * height * frameRate;
            double referencePixelsPerSecond = 1920d * 1080d * 60d;
            double referenceBitrate = isHdr ? 24_000_000d : 20_000_000d;
            return (int)Math.Clamp(
                Math.Round(referenceBitrate * pixelsPerSecond / referencePixelsPerSecond),
                16_000_000d,
                160_000_000d);
        }

        private static int MakeEven(int value) => Math.Max(2, value & ~1);
    }
}

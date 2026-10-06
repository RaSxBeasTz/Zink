using SharpDX.DXGI;
using Microsoft.Win32;
using System;
using System.Runtime.InteropServices;

namespace Zink.Services.Recording
{
    internal sealed record RecordingHardwareProfile(
        int Width,
        int Height,
        int FrameRate,
        int Bitrate,
        string MemoryGeneration,
        ulong InstalledMemoryBytes,
        int ProcessorCount,
        string Processor,
        string GraphicsCard,
        long DedicatedVideoMemoryBytes,
        bool IsHighEnd);

    internal static class RecordingHardwareProfileService
    {
        private const uint RawSmbiosProvider = 0x52534D42;

        [DllImport("kernel32.dll")]
        private static extern bool GetPhysicallyInstalledSystemMemory(out ulong totalMemoryInKilobytes);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint GetSystemFirmwareTable(
            uint firmwareTableProviderSignature,
            uint firmwareTableId,
            IntPtr firmwareTableBuffer,
            uint bufferSize);

        public static RecordingHardwareProfile Detect(Factory1 factory, int adapterIndex, int sourceWidth, int sourceHeight)
        {
            string gpuName = "Unknown graphics adapter";
            long dedicatedVideoMemory = 0;
            try
            {
                using Adapter1 adapter = factory.GetAdapter1(adapterIndex);
                AdapterDescription1 description = adapter.Description1;
                gpuName = description.Description?.Trim() ?? gpuName;
                dedicatedVideoMemory = description.DedicatedVideoMemory;
            }
            catch
            {
            }

            ulong installedMemoryBytes = 0;
            if (GetPhysicallyInstalledSystemMemory(out ulong installedKilobytes))
                installedMemoryBytes = installedKilobytes * 1024UL;

            string memoryGeneration = ReadMemoryGeneration();
            int processorCount = Environment.ProcessorCount;
            string processor = ReadProcessorName();
            bool modernDiscreteGpu = IsModernHighEndGpu(gpuName, dedicatedVideoMemory);
            bool highEnd =
                string.Equals(memoryGeneration, "DDR5", StringComparison.OrdinalIgnoreCase) &&
                installedMemoryBytes >= 32UL * 1024 * 1024 * 1024 &&
                processorCount >= 16 &&
                dedicatedVideoMemory >= 8L * 1024 * 1024 * 1024 &&
                modernDiscreteGpu;

            if (highEnd)
            {
                return new RecordingHardwareProfile(
                    Math.Max(2, sourceWidth & ~1),
                    Math.Max(2, sourceHeight & ~1),
                    120,
                    45_000_000,
                    memoryGeneration,
                    installedMemoryBytes,
                    processorCount,
                    processor,
                    gpuName,
                    dedicatedVideoMemory,
                    true);
            }

            int height = sourceHeight > sourceWidth ? 1920 : 1080;
            int width = sourceHeight > sourceWidth ? 1080 : 1920;
            return new RecordingHardwareProfile(
                Math.Min(sourceWidth, width),
                Math.Min(sourceHeight, height),
                60,
                16_000_000,
                memoryGeneration,
                installedMemoryBytes,
                processorCount,
                processor,
                gpuName,
                dedicatedVideoMemory,
                false);
        }

        private static bool IsModernHighEndGpu(string name, long dedicatedVideoMemory)
        {
            if (dedicatedVideoMemory < 8L * 1024 * 1024 * 1024)
                return false;

            string[] highEndFamilies =
            {
                "RTX 4070", "RTX 4080", "RTX 4090", "RTX 5070", "RTX 5080", "RTX 5090",
                "RX 7800", "RX 7900", "RX 8800", "RX 8900", "RX 9070",
                "Arc A770", "Arc B580", "Arc B770"
            };
            foreach (string family in highEndFamilies)
            {
                if (name.Contains(family, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        private static string ReadMemoryGeneration()
        {
            uint size = GetSystemFirmwareTable(RawSmbiosProvider, 0, IntPtr.Zero, 0);
            if (size < 8)
                return "Unknown";

            IntPtr buffer = Marshal.AllocHGlobal((int)size);
            try
            {
                if (GetSystemFirmwareTable(RawSmbiosProvider, 0, buffer, size) != size)
                    return "Unknown";

                byte[] raw = new byte[size];
                Marshal.Copy(buffer, raw, 0, raw.Length);
                int offset = 8;
                bool sawDdr4 = false;
                while (offset + 4 <= raw.Length)
                {
                    int structureLength = raw[offset + 1];
                    if (structureLength < 4 || offset + structureLength > raw.Length)
                        break;

                    if (raw[offset] == 17 && structureLength > 0x12)
                    {
                        byte memoryType = raw[offset + 0x12];
                        if (memoryType is 34 or 35)
                            return "DDR5";
                        if (memoryType is 26 or 30)
                            sawDdr4 = true;
                    }

                    int next = offset + structureLength;
                    while (next + 1 < raw.Length && (raw[next] != 0 || raw[next + 1] != 0))
                        next++;
                    offset = next + 2;
                }
                return sawDdr4 ? "DDR4" : "Unknown";
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        private static string ReadProcessorName()
        {
            try
            {
                using RegistryKey? key = Registry.LocalMachine.OpenSubKey(
                    @"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
                return key?.GetValue("ProcessorNameString")?.ToString()?.Trim()
                    ?? "Unknown processor";
            }
            catch
            {
                return "Unknown processor";
            }
        }
    }
}

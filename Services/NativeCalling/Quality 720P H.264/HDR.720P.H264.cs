using System;
using SharpDX.DXGI;

namespace Zink.Services.NativeCalling
{
    /// <summary>
    /// Detects Windows HDR without using the UI-thread-bound DisplayInformation
    /// API. Capture setup runs on a worker thread, so DXGI output colour spaces
    /// are the reliable source of truth here.
    /// </summary>
    internal static class HDR
    {
        internal readonly record struct DisplayState(bool IsSupported, bool IsEnabled, string Kind, string? Error);

        public static DisplayState GetDisplayState()
        {
            try
            {
                bool supported = false;
                using var factory = new Factory1();
                foreach (var adapter in factory.Adapters1)
                {
                    using (adapter)
                    foreach (var output in adapter.Outputs)
                    {
                        using (output)
                        using (var output6 = output.QueryInterfaceOrNull<Output6>())
                        {
                            if (output6 == null)
                                continue;

                            var description = output6.Description1;
                            if (!description.AttachedToDesktop)
                                continue;

                            bool enabled = description.ColorSpace == ColorSpaceType.RgbFullG2084NoneP2020 ||
                                description.ColorSpace == ColorSpaceType.RgbStudioG2084NoneP2020;
                            supported |= enabled || description.BitsPerColor >= 10;
                            if (enabled)
                                return new(true, true, description.ColorSpace.ToString(), null);
                        }
                    }
                }

                return new(supported, false, "SDR", null);
            }
            catch (Exception ex)
            {
                return new(false, false, "Unavailable", ex.Message);
            }
        }
    }
}

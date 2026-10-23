using SharpDX;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Windows.Graphics.Capture;
using Zink.Models;

using D3D11Device = SharpDX.Direct3D11.Device;
using D3D11Texture2D = SharpDX.Direct3D11.Texture2D;
using D3D11MapMode = SharpDX.Direct3D11.MapMode;
using D3D11MapFlags = SharpDX.Direct3D11.MapFlags;
using D3D11CpuAccessFlags = SharpDX.Direct3D11.CpuAccessFlags;
using D3D11BindFlags = SharpDX.Direct3D11.BindFlags;
using D3D11Usage = SharpDX.Direct3D11.ResourceUsage;
using D3D11ResourceOptionFlags = SharpDX.Direct3D11.ResourceOptionFlags;

using DxgiFactory1 = SharpDX.DXGI.Factory1;
using DxgiAdapter1 = SharpDX.DXGI.Adapter1;
using DxgiOutput = SharpDX.DXGI.Output;
using DxgiOutput1 = SharpDX.DXGI.Output1;
using DxgiOutputDuplication = SharpDX.DXGI.OutputDuplication;
using DxgiResource = SharpDX.DXGI.Resource;
using DxgiFormat = SharpDX.DXGI.Format;
using DxgiSampleDescription = SharpDX.DXGI.SampleDescription;
using DxgiOutputDuplicateFrameInformation = SharpDX.DXGI.OutputDuplicateFrameInformation;
using DxgiResultCode = SharpDX.DXGI.ResultCode;

namespace Zink.Services.Recording
{
    public sealed class CaptureEngine : IAsyncDisposable
    {
        private D3D11Device? _device;
        private DxgiOutputDuplication? _duplication;
        private D3D11Texture2D? _stagingTexture;
        private D3D11Texture2D? _lastGpuFrameTexture;
        private KeyedMutex? _lastGpuFrameMutex;
        private CancellationTokenSource? _cts;
        private Task? _captureTask;
        private Stopwatch? _stopwatch;

        private bool _isRunning;
        private long _frameCount;
        private int _width;
        private int _height;
        private DxgiFormat _captureFormat = DxgiFormat.B8G8R8A8_UNorm;
        private bool _hdrCapture;
        private byte[]? _lastFrameBytes;
        private bool _loggedFirstCapturedSurface;

        // Fixed pacing is much steadier for replay segments than "capture whenever + Task.Delay jitter".
        private uint _targetFps = 120;
        private TimeSpan _targetFrameInterval = TimeSpan.FromMilliseconds(1000.0 / 120);

        public event EventHandler<VideoFramePacket>? VideoFrameArrived;
        internal event EventHandler<GpuVideoFramePacket>? GpuFrameArrived;

        private const int MONITORINFOF_PRIMARY = 0x00000001;
        private delegate bool MonitorEnumProc(IntPtr monitor, IntPtr hdc, ref RECT rect, IntPtr data);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private struct MONITORINFOEX
        {
            public int cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public int dwFlags;

            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string szDevice;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int left;
            public int top;
            public int right;
            public int bottom;
        }

        [DllImport("user32.dll")]
        private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc callback, IntPtr data);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFOEX lpmi);

        public bool IsRunning => _isRunning;

        public async Task StartAsync(GraphicsCaptureItem? item = null, RecordingOptions? options = null)
        {
            if (_isRunning)
                return;

            try
            {
                _targetFps = Math.Clamp(options?.FrameRate ?? 60, 1u, 240u);
                _frameCount = 0;
                InitializeDuplication();
                if (options?.AllowHighFrameRateCapture != true)
                    ApplyResolutionAwareFrameRate();
                _targetFrameInterval = TimeSpan.FromMilliseconds(1000.0 / _targetFps);

                _cts = new CancellationTokenSource();
                _stopwatch = Stopwatch.StartNew();
                _captureTask = Task.Run(() => CaptureLoop(_cts.Token), _cts.Token);

                _isRunning = true;

                await RecorderLog.InfoAsync(nameof(CaptureEngine),
                    $"DXGI duplication capture started. Size={_width}x{_height}, TargetFps={_targetFps}, Format={_captureFormat}, HDR={_hdrCapture}");
                await GameReplayLog.WriteAsync(nameof(CaptureEngine),
                    $"Capture started. Size={_width}x{_height}, TargetFps={_targetFps}, Format={_captureFormat}, HDR={_hdrCapture}, DeviceFlags=BGRA|VideoSupport");
            }
            catch (Exception ex)
            {
                await RecorderLog.ErrorAsync(nameof(CaptureEngine), ex, "StartAsync failed");
                await GameReplayLog.WriteAsync(nameof(CaptureEngine), $"Start failed: {ex}");
                throw;
            }
        }

        private void InitializeDuplication()
        {
            using var factory = new DxgiFactory1();

            string? primaryDeviceName = GetPrimaryMonitorDeviceName();
            if (string.IsNullOrWhiteSpace(primaryDeviceName))
                throw new InvalidOperationException("Could not determine the primary monitor device name.");

            DxgiAdapter1? chosenAdapter = null;
            DxgiOutput? chosenOutput = null;

            for (int adapterIndex = 0; ; adapterIndex++)
            {
                DxgiAdapter1 adapter;
                try
                {
                    adapter = factory.GetAdapter1(adapterIndex);
                }
                catch
                {
                    break;
                }

                bool found = false;

                for (int outputIndex = 0; ; outputIndex++)
                {
                    DxgiOutput output;
                    try
                    {
                        output = adapter.GetOutput(outputIndex);
                    }
                    catch
                    {
                        break;
                    }

                    var desc = output.Description;

                    if (!desc.IsAttachedToDesktop)
                    {
                        output.Dispose();
                        continue;
                    }

                    if (string.Equals(desc.DeviceName?.Trim(), primaryDeviceName.Trim(), StringComparison.OrdinalIgnoreCase))
                    {
                        chosenAdapter = adapter;
                        chosenOutput = output;
                        found = true;
                        break;
                    }

                    output.Dispose();
                }

                if (found)
                    break;

                adapter.Dispose();
            }

            if (chosenAdapter == null || chosenOutput == null)
                throw new InvalidOperationException("Primary monitor output was not found for DXGI duplication.");

            _device = new D3D11Device(
                chosenAdapter,
                DeviceCreationFlags.BgraSupport | DeviceCreationFlags.VideoSupport);
            AdapterDescription1 selectedAdapterDescription = chosenAdapter.Description1;
            var selectedOutputDescription = chosenOutput.Description;
            try
            {
                using var output6 = chosenOutput.QueryInterface<Output6>();
                OutputDescription1 outputDescription = output6.Description1;
                _hdrCapture = outputDescription.BitsPerColor >= 10 &&
                    outputDescription.ColorSpace == ColorSpaceType.RgbFullG2084NoneP2020;
            }
            catch
            {
                _hdrCapture = false;
            }

            _ = GameReplayLog.WriteAsync(nameof(CaptureEngine),
                $"Capture adapter selected. Adapter='{selectedAdapterDescription.Description?.Trim()}', Vendor=0x{selectedAdapterDescription.VendorId:X4}, DedicatedVideoMemory={selectedAdapterDescription.DedicatedVideoMemory}, Output='{selectedOutputDescription.DeviceName}', Bounds={selectedOutputDescription.DesktopBounds.Left},{selectedOutputDescription.DesktopBounds.Top}-{selectedOutputDescription.DesktopBounds.Right},{selectedOutputDescription.DesktopBounds.Bottom}, HDR={_hdrCapture}");

            // Preserve the HDR desktop as linear FP16. Replay converts this with
            // Zink's shader-based P010 path, avoiding the D3D11 video processor
            // that rejects FP16 input on some adapters. Non-HDR displays continue
            // through the established BGRA SDR capture path.
            // Preserve the HDR desktop as FP16 scRGB. HardwareReplayBuffer applies
            // Zink's shader conversion to P010 at the selected output resolution;
            // NVIDIA's HEVC MFT uses the existing system-memory P010 fallback when
            // it cannot accept a P010 DXGI surface directly.
            bool useNativeFp16ReplayCapture = _hdrCapture;
            if (_hdrCapture && useNativeFp16ReplayCapture)
            {
                using var output5 = chosenOutput.QueryInterface<Output5>();
                var formats = new[] { DxgiFormat.R16G16B16A16_Float };
                _duplication = output5.DuplicateOutput1(_device, 0, formats.Length, formats);
                _captureFormat = DxgiFormat.R16G16B16A16_Float;
            }
            else
            {
                using var output1 = chosenOutput.QueryInterface<DxgiOutput1>();
                _duplication = output1.DuplicateOutput(_device);
                _captureFormat = DxgiFormat.B8G8R8A8_UNorm;
                if (_hdrCapture)
                {
                    _ = RecordingDiagnostics.WriteHdrAsync(
                        "NativeFp16ToP010ConversionUnavailable",
                        new
                        {
                            GenuineHdr = false,
                            CaptureFormat = _captureFormat.ToString(),
                            Reason = "Intel D3D11 video processor rejected FP16 input with E_INVALIDARG; corrupt green HDR output was blocked."
                        });
                }
            }

            var outputDesc = chosenOutput.Description;
            _width = outputDesc.DesktopBounds.Right - outputDesc.DesktopBounds.Left;
            _height = outputDesc.DesktopBounds.Bottom - outputDesc.DesktopBounds.Top;

            _stagingTexture = new D3D11Texture2D(_device, new Texture2DDescription
            {
                Width = _width,
                Height = _height,
                MipLevels = 1,
                ArraySize = 1,
                Format = _captureFormat,
                SampleDescription = new DxgiSampleDescription(1, 0),
                Usage = D3D11Usage.Staging,
                BindFlags = D3D11BindFlags.None,
                CpuAccessFlags = D3D11CpuAccessFlags.Read,
                OptionFlags = D3D11ResourceOptionFlags.None
            });

            _lastGpuFrameTexture = new D3D11Texture2D(_device, new Texture2DDescription
            {
                Width = _width,
                Height = _height,
                MipLevels = 1,
                ArraySize = 1,
                Format = _captureFormat,
                SampleDescription = new DxgiSampleDescription(1, 0),
                Usage = D3D11Usage.Default,
                BindFlags = D3D11BindFlags.ShaderResource | D3D11BindFlags.RenderTarget,
                CpuAccessFlags = D3D11CpuAccessFlags.None,
                // Capture and encoding run synchronously on this D3D11 device.
                // A shared keyed-mutex texture was unnecessary here and could
                // hand the converter an uninitialised/black shared surface.
                OptionFlags = D3D11ResourceOptionFlags.None
            });
            _lastGpuFrameMutex = null;

            chosenOutput.Dispose();
            chosenAdapter.Dispose();
        }

        private void ApplyResolutionAwareFrameRate()
        {
            if (_width <= 0 || _height <= 0)
                return;

            // Encoding uses a smaller hardware-selected output surface. Capping
            // cadence from the 3840x2400 capture size forced a requested 60 fps
            // replay to approximately 30 fps even while encoding only 1152x720.
            _targetFps = Math.Min(_targetFps, 60);
        }

        internal static string? GetPrimaryMonitorDeviceName()
        {
            string? deviceName = null;
            // Use Windows' primary flag rather than display order or coordinates,
            // which can differ across multi-monitor and mixed-DPI layouts.
            EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero,
                (IntPtr monitor, IntPtr hdc, ref RECT rect, IntPtr data) =>
                {
                    var info = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
                    if (GetMonitorInfo(monitor, ref info) &&
                        (info.dwFlags & MONITORINFOF_PRIMARY) != 0)
                    {
                        deviceName = info.szDevice;
                        return false;
                    }
                    return true;
                }, IntPtr.Zero);
            return deviceName;
        }

        private async Task CaptureLoop(CancellationToken token)
        {
            if (_stopwatch == null)
                return;

            TimeSpan nextFrameDue = TimeSpan.Zero;

            while (!token.IsCancellationRequested)
            {
                DxgiResource? desktopResource = null;
                bool frameAcquired = false;

                try
                {
                    if (_duplication == null || _device == null || _stagingTexture == null || _stopwatch == null)
                        return;

                    TimeSpan now = _stopwatch.Elapsed;
                    TimeSpan wait = nextFrameDue - now;
                    if (wait > TimeSpan.Zero)
                    {
                        await Task.Delay(wait, token);
                        now = _stopwatch.Elapsed;
                    }

                    TimeSpan packetTimestamp = now;
                    nextFrameDue += _targetFrameInterval;
                    if (nextFrameDue < now - _targetFrameInterval)
                        nextFrameDue = now + _targetFrameInterval;

                    DxgiOutputDuplicateFrameInformation frameInfo;
                    int acquireTimeoutMs = _targetFps > 60 ? 0 : 8;
                    var result = _duplication.TryAcquireNextFrame(
                        acquireTimeoutMs,
                        out frameInfo,
                        out desktopResource);

                    if (result == DxgiResultCode.WaitTimeout || desktopResource == null)
                    {
                        EmitDuplicateGpuFrame(packetTimestamp);
                        EmitDuplicateFrame(packetTimestamp);
                        continue;
                    }

                    result.CheckError();
                    frameAcquired = true;

                    using var desktopImage = desktopResource.QueryInterface<D3D11Texture2D>();
                    if (!_loggedFirstCapturedSurface)
                    {
                        _loggedFirstCapturedSurface = true;
                        var desktopDescription = desktopImage.Description;
                        _ = GameReplayLog.WriteAsync(nameof(CaptureEngine),
                            $"First desktop surface acquired. Size={desktopDescription.Width}x{desktopDescription.Height}, Format={desktopDescription.Format}, Bind={desktopDescription.BindFlags}, Usage={desktopDescription.Usage}, Options={desktopDescription.OptionFlags}, TargetFps={_targetFps}");
                    }
                    if (_lastGpuFrameTexture != null)
                    {
                        _lastGpuFrameMutex?.Acquire(0, -1).CheckError();
                        try
                        {
                            _device.ImmediateContext.CopyResource(desktopImage, _lastGpuFrameTexture);
                            _device.ImmediateContext.Flush();
                        }
                        finally
                        {
                            _lastGpuFrameMutex?.Release(0);
                        }
                        GpuFrameArrived?.Invoke(this, new GpuVideoFramePacket(_lastGpuFrameTexture, packetTimestamp, _width, _height, format: _captureFormat));
                    }

                    if (VideoFrameArrived == null)
                        continue;

                    _device.ImmediateContext.CopyResource(desktopImage, _stagingTexture);

                    var dataBox = _device.ImmediateContext.MapSubresource(
                        _stagingTexture,
                        0,
                        D3D11MapMode.Read,
                        D3D11MapFlags.None);

                    byte[] bgraBytes = FrameBufferPool.Rent(_width * _height * 4);

                    try
                    {
                        int rowSize = _width * 4;

                        for (int y = 0; y < _height; y++)
                        {
                            IntPtr sourcePtr = IntPtr.Add(dataBox.DataPointer, y * dataBox.RowPitch);
                            Marshal.Copy(sourcePtr, bgraBytes, y * rowSize, rowSize);
                        }

                        _lastFrameBytes ??= new byte[bgraBytes.Length];
                        System.Buffer.BlockCopy(bgraBytes, 0, _lastFrameBytes, 0, bgraBytes.Length);
                    }
                    finally
                    {
                        _device.ImmediateContext.UnmapSubresource(_stagingTexture, 0);
                    }

                    var packet = new VideoFramePacket(
                        packetTimestamp,
                        _width,
                        _height,
                        bgraBytes);

                    _frameCount++;

                    if (_frameCount % 120 == 0)
                    {
                        await RecorderLog.InfoAsync(nameof(CaptureEngine),
                            $"DXGI frames captured: {_frameCount}, Timestamp={packet.Timestamp}");
                    }

                    VideoFrameArrived?.Invoke(this, packet);
                }
                catch (SharpDXException ex) when (ex.ResultCode == DxgiResultCode.WaitTimeout)
                {
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    await RecorderLog.ErrorAsync(nameof(CaptureEngine), ex, "CaptureLoop failed");
                    await GameReplayLog.WriteAsync(nameof(CaptureEngine), $"Capture loop failed: {ex}");

                    try
                    {
                        await Task.Delay(50, token);
                    }
                    catch
                    {
                    }
                }
                finally
                {
                    desktopResource?.Dispose();

                    if (frameAcquired && _duplication != null)
                    {
                        try
                        {
                            _duplication.ReleaseFrame();
                        }
                        catch
                        {
                        }
                    }
                }
            }
        }

        private void EmitDuplicateFrame(TimeSpan timestamp)
        {
            if (_lastFrameBytes is null || _lastFrameBytes.Length == 0 || _width <= 0 || _height <= 0)
                return;

            byte[] bgraBytes = FrameBufferPool.Rent(_lastFrameBytes.Length);
            System.Buffer.BlockCopy(_lastFrameBytes, 0, bgraBytes, 0, _lastFrameBytes.Length);

            var packet = new VideoFramePacket(
                timestamp,
                _width,
                _height,
                bgraBytes);

            _frameCount++;
            VideoFrameArrived?.Invoke(this, packet);
        }

        private void EmitDuplicateGpuFrame(TimeSpan timestamp)
        {
            if (_lastGpuFrameTexture == null || GpuFrameArrived == null)
                return;

            _frameCount++;
            GpuFrameArrived.Invoke(this, new GpuVideoFramePacket(
                _lastGpuFrameTexture,
                timestamp,
                _width,
                _height,
                isDuplicate: true,
                format: _captureFormat));
        }

        public async Task StopAsync()
        {
            try
            {
                _isRunning = false;

                if (_cts != null)
                {
                    _cts.Cancel();
                }

                if (_captureTask != null)
                {
                    try
                    {
                        await _captureTask;
                    }
                    catch
                    {
                    }
                }

                _duplication?.Dispose();
                _duplication = null;

                _stagingTexture?.Dispose();
                _stagingTexture = null;

                _lastGpuFrameMutex?.Dispose();
                _lastGpuFrameMutex = null;
                _lastGpuFrameTexture?.Dispose();
                _lastGpuFrameTexture = null;

                _device?.Dispose();
                _device = null;

                _cts?.Dispose();
                _cts = null;

                _captureTask = null;
                _stopwatch = null;
                _lastFrameBytes = null;

                await RecorderLog.InfoAsync(nameof(CaptureEngine),
                    $"DXGI capture stopped. Total frames={_frameCount}");
            }
            catch (Exception ex)
            {
                await RecorderLog.ErrorAsync(nameof(CaptureEngine), ex, "StopAsync failed");
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            await StopAsync();
        }
    }
}

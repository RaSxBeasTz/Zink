using SharpDX.Direct3D11;
using SharpDX.DXGI;
using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Zink.Services;
using Zink.Services.Recording;

namespace Zink.Services.NativeCalling
{
    internal sealed class WindowsGraphicsCaptureScreenSource : IDisposable
    {
        private readonly object _syncRoot = new();
        private readonly object _disposeSync = new();

        private IDirect3DDevice? _winRtDevice;
        private SharpDX.Direct3D11.Device? _sharpDxDevice;
        private Direct3D11CaptureFramePool? _framePool;
        private GraphicsCaptureSession? _session;
        private GraphicsCaptureItem? _captureItem;
        private Texture2D? _stagingTexture;
        private ZinkHdrToSdrGpuConverter? _previewHdrConverter;
        private Texture2D? _previewSdrTexture;
        private bool _loggedHdrPreviewConversion;
        private Bitmap? _latestFrame;
        private CapturedGpuFrame? _latestGpuFrame;
        private long _frameArrivedCount;
        private uint _lastFrameFingerprint;
        private int _sameFrameCount;
        private DateTimeOffset _lastFrameLogUtc = DateTimeOffset.MinValue;
        private DateTimeOffset _lastGpuSampleUtc = DateTimeOffset.MinValue;
        private DateTimeOffset _lastPreviewReadbackUtc = DateTimeOffset.MinValue;
        private DateTimeOffset _lastPreviewSkipLogUtc = DateTimeOffset.MinValue;
        private int _startAttemptId;
        private bool _started;
        private bool _disabled;
        private bool _disposed = true;

        public bool IsAvailable => !_disabled;

        public SharpDX.Direct3D11.Device? CaptureDevice => _sharpDxDevice;

        private static bool IsArm64Process =>
            RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ||
            RuntimeInformation.OSArchitecture == Architecture.Arm64;

        public async Task<bool> StartAsync()
        {
            if (_started)
                return true;

            if (_disabled)
                return false;

            try
            {
                if (!GraphicsCaptureSession.IsSupported())
                {
                    _disabled = true;
                    return false;
                }

                DiagnosticLogService.WriteLine("[ScreenShare:WGC] StartAsync entered.");
                DiagnosticLogService.Flush();

                var hwnd = App.MainWindow?.GetWindowHandle() ?? IntPtr.Zero;
                var captureMode = ScreenShareCapturePipeline.Instance.PreferredCaptureSourceMode;
                if (!await TryRequestProgrammaticCaptureAccessAsync())
                {
                    DiagnosticLogService.WriteLine("[ScreenShare:WGC] Programmatic capture access was not granted for the Zink source picker.");
                    DiagnosticLogService.Flush();
                    _disabled = true;
                    return false;
                }
                await TryRequestBorderlessCaptureAccessAsync();

                var item = captureMode == NativeCaptureSourceMode.GameOrWindow
                    ? await CaptureSourceHelper.GetOrCreateAsync(hwnd, preferCachedSelection: true)
                    : await CaptureSourceHelper.GetPrimaryScreenOrPromptAsync(hwnd);
                if (item == null)
                {
                    Debug.WriteLine("[ScreenShare:WGC] No Windows Graphics Capture item was created.");
                    DiagnosticLogService.WriteLine("[ScreenShare:WGC] No Windows Graphics Capture item was created.");
                    _disabled = true;
                    return false;
                }

                _captureItem = item;
                return await StartCaptureSessionAsync(item, $"mode={captureMode}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ScreenShare:WGC] Failed to start Windows Graphics Capture: {ex}");
                DiagnosticLogService.WriteLine($"[ScreenShare:WGC] Failed to start Windows Graphics Capture: {ex}");
                DiagnosticLogService.Flush();
                _disabled = true;
                Dispose();
                return false;
            }
        }

        private static SharpDX.Direct3D11.Device CreateCaptureDevice()
        {
            var flags = DeviceCreationFlags.BgraSupport;
            if (!IsArm64Process)
                flags |= DeviceCreationFlags.VideoSupport;

            try
            {
                return new SharpDX.Direct3D11.Device(
                    SharpDX.Direct3D.DriverType.Hardware,
                    flags);
            }
            catch (Exception ex) when ((flags & DeviceCreationFlags.VideoSupport) != 0)
            {
                Debug.WriteLine($"[ScreenShare:WGC] D3D11 capture device with VideoSupport failed; retrying BGRA-only: {ex.Message}");
                DiagnosticLogService.WriteLine($"[ScreenShare:WGC] D3D11 capture device with VideoSupport failed; retrying BGRA-only: {ex.Message}");
                return new SharpDX.Direct3D11.Device(
                    SharpDX.Direct3D.DriverType.Hardware,
                    DeviceCreationFlags.BgraSupport);
            }
        }

        public Bitmap? TryGetLatestFrame()
        {
            lock (_syncRoot)
            {
                var frame = _latestFrame;
                _latestFrame = null;
                return frame;
            }
        }

        public CapturedGpuFrame? TryGetLatestGpuFrame()
        {
            lock (_syncRoot)
            {
                var frame = _latestGpuFrame;
                _latestGpuFrame = null;
                return frame;
            }
        }

        private void FramePool_FrameArrived(Direct3D11CaptureFramePool sender, object args)
        {
            lock (_disposeSync)
            {
                if (_disposed || _sharpDxDevice == null)
                    return;

                try
                {
                    using var frame = sender.TryGetNextFrame();
                    if (frame == null)
                        return;

                    var quality = ScreenShareCapturePipeline.Instance.CurrentQuality;
                    using var sourceTexture = Direct3D11Helpers.CreateSharpDXTexture2D(frame.Surface);
                    var description = sourceTexture.Description;
                    var gpuFrame = CaptureGpuFrame(sourceTexture, description);
                    if (gpuFrame != null)
                    {
                        lock (_syncRoot)
                        {
                            _latestGpuFrame?.Dispose();
                            _latestGpuFrame = gpuFrame;
                            gpuFrame = null;
                        }

                        var streamingService = ScreenShareCapturePipeline.Instance;
                        var streamingPerformanceMode = streamingService.PrioritizeStreamingPerformance;
                        var requiresRealtimeBitmapFrames = streamingService.RequiresRealtimeBitmapFrames;
                        var sample = streamingPerformanceMode
                            ? (0, null)
                            : TrySampleGpuFrame(sourceTexture, description);
                        Bitmap? previewFrame = null;
                        var previewInterval = requiresRealtimeBitmapFrames
                            ? TimeSpan.FromMilliseconds(Math.Max(1, 1000.0 / streamingService.CurrentTargetFps))
                            : TimeSpan.FromMilliseconds(100);
                        if ((!streamingPerformanceMode || requiresRealtimeBitmapFrames) &&
                            streamingService.EnablePreviewFrames &&
                            DateTimeOffset.UtcNow - _lastPreviewReadbackUtc >= previewInterval)
                        {
                            previewFrame = TryCreatePreviewFrame(sourceTexture, description, quality);
                            _lastPreviewReadbackUtc = DateTimeOffset.UtcNow;
                        }
                        else if (streamingPerformanceMode &&
                                 !requiresRealtimeBitmapFrames &&
                                 DateTimeOffset.UtcNow - _lastPreviewSkipLogUtc >= TimeSpan.FromSeconds(5))
                        {
                            _lastPreviewSkipLogUtc = DateTimeOffset.UtcNow;
                            Debug.WriteLine("[ScreenShare:WGC] Preview readback throttled so GPU capture can keep realtime priority.");
                        }

                        if (previewFrame != null)
                        {
                            lock (_syncRoot)
                            {
                                _latestFrame?.Dispose();
                                _latestFrame = previewFrame;
                                previewFrame = null;
                            }
                        }

                    LogFrameArrival(description.Width, description.Height, quality.Width, quality.Height, sample.Fingerprint, sample.AverageLuma);
                    return;
                }

                    if (ScreenShareCapturePipeline.EnableDirectGpuTexturePath)
                    {
                        DiagnosticLogService.WriteLine("[ScreenShare:WGC] GPU frame copy returned no texture; bitmap readback fallback is disabled.");
                        return;
                    }

                    EnsureStagingTexture(description);
                    if (_stagingTexture == null || _sharpDxDevice == null)
                        return;

                    _sharpDxDevice.ImmediateContext.CopyResource(sourceTexture, _stagingTexture);
                    _sharpDxDevice.ImmediateContext.Flush();
                    var dataBox = _sharpDxDevice.ImmediateContext.MapSubresource(_stagingTexture, 0, MapMode.Read, SharpDX.Direct3D11.MapFlags.None);
                    try
                    {
                        var fingerprint = SampleFrameFingerprint(
                            dataBox.DataPointer,
                            dataBox.RowPitch,
                            description.Width,
                            description.Height);
                        var captured = CreateScaledBitmapFromBgra(
                            dataBox.DataPointer,
                            dataBox.RowPitch,
                            description.Width,
                            description.Height,
                            quality.Width,
                            quality.Height);

                        lock (_syncRoot)
                        {
                            _latestFrame?.Dispose();
                            _latestGpuFrame?.Dispose();
                            _latestFrame = captured;
                            _latestGpuFrame = gpuFrame;
                            gpuFrame = null;
                        }

                        LogFrameArrival(description.Width, description.Height, quality.Width, quality.Height, fingerprint, null);
                    }
                    finally
                    {
                        _sharpDxDevice?.ImmediateContext.UnmapSubresource(_stagingTexture, 0);
                        gpuFrame?.Dispose();
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[ScreenShare:WGC] Frame readback failed: {ex.Message}");
                }
            }
        }

        private Bitmap? TryCreatePreviewFrame(Texture2D sourceTexture, Texture2DDescription description, ScreenShareQualityProfile quality)
        {
            try
            {
                var previewSize = GetPreviewBitmapSize(quality);
                if (description.Format == Format.R16G16B16A16_Float)
                {
                    if (!_loggedHdrPreviewConversion)
                    {
                        _loggedHdrPreviewConversion = true;
                        DiagnosticLogService.WriteLine($"[ScreenShare:HDR:Preview] conversion active; source={description.Width}x{description.Height}/{description.Format}; preview={previewSize.Width}x{previewSize.Height}/{Format.B8G8R8A8_UNorm}; encoderFamily={ScreenShareCapturePipeline.Instance.PreferredH264EncoderFamily}; toneMap='{ZinkHdrToSdrGpuConverter.ToneMapDescription}'.");
                        DiagnosticLogService.Flush();
                    }
                    EnsureHdrPreviewResources(sourceTexture, previewSize.Width, previewSize.Height);
                    _previewHdrConverter!.Convert(sourceTexture, _previewSdrTexture!);
                    sourceTexture = _previewSdrTexture!;
                    description = sourceTexture.Description;
                }

                EnsureStagingTexture(description);
                if (_stagingTexture == null || _sharpDxDevice == null)
                    return null;

                _sharpDxDevice.ImmediateContext.CopyResource(sourceTexture, _stagingTexture);
                _sharpDxDevice.ImmediateContext.Flush();
                var dataBox = _sharpDxDevice.ImmediateContext.MapSubresource(_stagingTexture, 0, MapMode.Read, SharpDX.Direct3D11.MapFlags.None);
                try
                {
                    return CreateScaledBitmapFromBgra(
                        dataBox.DataPointer,
                        dataBox.RowPitch,
                        description.Width,
                        description.Height,
                        previewSize.Width,
                        previewSize.Height);
                }
                finally
                {
                    _sharpDxDevice.ImmediateContext.UnmapSubresource(_stagingTexture, 0);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ScreenShare:WGC] Preview readback failed: {ex.Message}");
                return null;
            }
        }

        private void EnsureHdrPreviewResources(Texture2D sourceTexture, int width, int height)
        {
            if (_previewSdrTexture != null &&
                _previewSdrTexture.Description.Width == width &&
                _previewSdrTexture.Description.Height == height &&
                _previewSdrTexture.Device.NativePointer == sourceTexture.Device.NativePointer)
            {
                return;
            }

            _previewSdrTexture?.Dispose();
            _previewHdrConverter?.Dispose();
            _previewHdrConverter = new ZinkHdrToSdrGpuConverter(sourceTexture.Device);
            _previewSdrTexture = new Texture2D(sourceTexture.Device, new Texture2DDescription
            {
                CpuAccessFlags = CpuAccessFlags.None,
                BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
                Format = Format.B8G8R8A8_UNorm,
                Width = width,
                Height = height,
                OptionFlags = ResourceOptionFlags.None,
                MipLevels = 1,
                ArraySize = 1,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default
            });
        }

        private static (int Width, int Height) GetPreviewBitmapSize(ScreenShareQualityProfile quality)
        {
            var streamingPerformanceMode = ScreenShareCapturePipeline.Instance.PrioritizeStreamingPerformance;
            var maxPreviewWidth = streamingPerformanceMode
                ? Math.Min(quality.PreviewMaxWidth, quality.Height >= 1080 ? 960 : 854)
                : quality.PreviewMaxWidth;
            var width = Math.Min(quality.Width, Math.Max(1, maxPreviewWidth));
            var height = Math.Max(1, (int)Math.Round(width * (double)quality.Height / quality.Width));
            return (width, height);
        }

        private CapturedGpuFrame? CaptureGpuFrame(Texture2D sourceTexture, Texture2DDescription sourceDescription)
        {
            if (!ScreenShareCapturePipeline.EnableDirectGpuTexturePath)
                return null;

            if (_sharpDxDevice == null)
                return null;

            try
            {
                var bindFlagCandidates = new[]
                {
                    BindFlags.ShaderResource | BindFlags.RenderTarget,
                    BindFlags.ShaderResource
                };

                Exception? lastError = null;
                foreach (var bindFlags in bindFlagCandidates)
                {
                    try
                    {
                        var gpuTexture = new Texture2D(_sharpDxDevice, new Texture2DDescription
                        {
                            CpuAccessFlags = CpuAccessFlags.None,
                            BindFlags = bindFlags,
                            Format = sourceDescription.Format,
                            Width = sourceDescription.Width,
                            Height = sourceDescription.Height,
                            OptionFlags = ResourceOptionFlags.None,
                            MipLevels = 1,
                            ArraySize = 1,
                            SampleDescription = new SampleDescription(1, 0),
                            Usage = ResourceUsage.Default
                        });

                        _sharpDxDevice.ImmediateContext.CopyResource(sourceTexture, gpuTexture);
                        return new CapturedGpuFrame(gpuTexture, sourceDescription.Width, sourceDescription.Height, sourceDescription.Format, bindFlags);
                    }
                    catch (Exception ex)
                    {
                        lastError = ex;
                        Debug.WriteLine($"[ScreenShare:WGC] GPU frame texture copy failed with bind flags {bindFlags}: {ex.Message}");
                    }
                }

                throw new InvalidOperationException(
                    $"WGC GPU frame copy failed for all GPU texture descriptors. source={sourceDescription.Width}x{sourceDescription.Height}; format={sourceDescription.Format}; last={lastError?.Message}",
                    lastError);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ScreenShare:WGC] GPU frame copy failed: {ex.Message}");
                DiagnosticLogService.WriteLine($"[ScreenShare:WGC] GPU frame copy failed: {ex}");
                return null;
            }
        }

        public Task<bool> RestartAsync()
        {
            var item = _captureItem;
            if (item == null || _disabled)
                return Task.FromResult(false);

            DisposeCaptureSession();
            return StartCaptureSessionAsync(item, "stale-frame recovery");
        }

        private async Task<bool> StartCaptureSessionAsync(GraphicsCaptureItem item, string reason)
        {
            var attemptId = Interlocked.Increment(ref _startAttemptId);
            var startTask = Task.Run(() => StartCaptureSessionCore(item, reason, attemptId));
            var completedTask = await Task.WhenAny(startTask, Task.Delay(TimeSpan.FromSeconds(5)));
            if (completedTask != startTask)
            {
                Interlocked.Increment(ref _startAttemptId);
                DiagnosticLogService.WriteLine("[ScreenShare:WGC] Capture session setup timed out; Windows did not return from capture session creation/start.");
                DiagnosticLogService.Flush();
                return false;
            }

            return await startTask;
        }

        private bool StartCaptureSessionCore(GraphicsCaptureItem item, string reason, int attemptId)
        {
            DiagnosticLogService.WriteLine($"[ScreenShare:WGC] Capture item ready {item.Size.Width}x{item.Size.Height}; {reason}; arm64={IsArm64Process}.");
            DiagnosticLogService.Flush();

            var sharpDxDevice = CreateCaptureDevice();
            EnableMultithreadProtection(sharpDxDevice);

            DiagnosticLogService.WriteLine("[ScreenShare:WGC] D3D11 capture device created.");
            DiagnosticLogService.Flush();

            var winRtDevice = Direct3D11Helpers.CreateD3DDevice(sharpDxDevice);

            DiagnosticLogService.WriteLine("[ScreenShare:WGC] WinRT Direct3D device created.");
            DiagnosticLogService.Flush();

            var framePoolBufferCount = ScreenShareCapturePipeline.Instance.PrioritizeStreamingPerformance ? 12 : 4;
            var hdr = HDR.GetDisplayState();
            // H.264 is an SDR transport in this calling stack. When Windows HDR
            // is active, preserve the desktop as FP16 scRGB until the encoder's
            // GPU tone mapper converts it once to Rec.709/NV12. SDR displays stay
            // BGRA and the viewing device must not tone-map the result again.
            var capturePixelFormat = hdr.IsEnabled &&
                ScreenShareCapturePipeline.Instance.PreferredVideoCodec == ScreenShareVideoCodec.H264
                ? DirectXPixelFormat.R16G16B16A16Float
                : DirectXPixelFormat.B8G8R8A8UIntNormalized;
            var framePool = Direct3D11CaptureFramePool.CreateFreeThreaded(
                winRtDevice,
                capturePixelFormat,
                framePoolBufferCount,
                item.Size);

            DiagnosticLogService.WriteLine($"[ScreenShare:HDR] supported={hdr.IsSupported}; enabled={hdr.IsEnabled}; kind={hdr.Kind}; captureFormat={capturePixelFormat}; conversion={(hdr.IsEnabled ? "sender HDR-to-SDR before H.264" : "off (native SDR)")}; viewerConversion=off; detectionError='{hdr.Error ?? ""}'.");
            DiagnosticLogService.WriteLine($"[ScreenShare:WGC] Free-threaded frame pool created with {framePoolBufferCount} buffers; format={capturePixelFormat}.");
            DiagnosticLogService.Flush();

            DiagnosticLogService.WriteLine("[ScreenShare:WGC] Creating capture session.");
            DiagnosticLogService.Flush();
            var session = framePool.CreateCaptureSession(item);
            DiagnosticLogService.WriteLine("[ScreenShare:WGC] Capture session created.");
            DiagnosticLogService.Flush();

            TryDisableCaptureBorder(session);
            TryEnableCursorCapture(session);

            DiagnosticLogService.WriteLine("[ScreenShare:WGC] Starting capture session.");
            DiagnosticLogService.Flush();
            session.StartCapture();

            lock (_disposeSync)
            {
                if (_disabled || attemptId != Volatile.Read(ref _startAttemptId))
                {
                    session.Dispose();
                    framePool.Dispose();
                    sharpDxDevice.Dispose();
                    return false;
                }

                _sharpDxDevice = sharpDxDevice;
                _winRtDevice = winRtDevice;
                _framePool = framePool;
                _session = session;
                _disposed = false;
                _started = true;
                _framePool.FrameArrived += FramePool_FrameArrived;
            }

            Debug.WriteLine($"[ScreenShare:WGC] Windows Graphics Capture started {item.Size.Width}x{item.Size.Height} via native D3D11 GPU capture device.");
            DiagnosticLogService.WriteLine($"[ScreenShare:WGC] Windows Graphics Capture started {item.Size.Width}x{item.Size.Height} via native D3D11 GPU capture device.");
            return true;
        }

        private (uint Fingerprint, double? AverageLuma) TrySampleGpuFrame(Texture2D sourceTexture, Texture2DDescription description)
        {
            var now = DateTimeOffset.UtcNow;
            if (_frameArrivedCount > 0 && now - _lastGpuSampleUtc < TimeSpan.FromSeconds(2))
                return (0, null);

            if (_sharpDxDevice == null)
                return (0, null);

            try
            {
                EnsureStagingTexture(description);
                if (_stagingTexture == null)
                    return (0, null);

                _sharpDxDevice.ImmediateContext.CopyResource(sourceTexture, _stagingTexture);
                _sharpDxDevice.ImmediateContext.Flush();
                var dataBox = _sharpDxDevice.ImmediateContext.MapSubresource(_stagingTexture, 0, MapMode.Read, SharpDX.Direct3D11.MapFlags.None);
                try
                {
                    _lastGpuSampleUtc = now;
                    return SampleFrameDiagnostics(
                        dataBox.DataPointer,
                        dataBox.RowPitch,
                        description.Width,
                        description.Height);
                }
                finally
                {
                    _sharpDxDevice.ImmediateContext.UnmapSubresource(_stagingTexture, 0);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ScreenShare:WGC] GPU frame pixel sample failed: {ex.Message}");
                return (0, null);
            }
        }

        private void LogFrameArrival(int sourceWidth, int sourceHeight, int targetWidth, int targetHeight, uint fingerprint, double? averageLuma)
        {
            var frameCount = ++_frameArrivedCount;
            if (fingerprint != 0 && fingerprint == _lastFrameFingerprint)
                _sameFrameCount++;
            else
                _sameFrameCount = 0;

            if (fingerprint != 0)
                _lastFrameFingerprint = fingerprint;

            var now = DateTimeOffset.UtcNow;
            if (frameCount == 1 || now - _lastFrameLogUtc >= TimeSpan.FromSeconds(2) || _sameFrameCount == 120)
            {
                _lastFrameLogUtc = now;
                var lumaText = averageLuma.HasValue
                    ? $"; avgLuma={averageLuma.Value:0.0}"
                    : string.Empty;
                var message = $"[ScreenShare:WGC] frame={frameCount}; source={sourceWidth}x{sourceHeight}; target={targetWidth}x{targetHeight}; hash=0x{fingerprint:X8}; sameFrame={_sameFrameCount}{lumaText}.";
                Debug.WriteLine(message);
                DiagnosticLogService.WriteLine(message);
            }
        }

        private static void EnableMultithreadProtection(SharpDX.Direct3D11.Device device)
        {
            try
            {
                using var multithread = device.QueryInterface<Multithread>();
                var wasProtected = multithread.SetMultithreadProtected(true);
                Debug.WriteLine($"[ScreenShare:WGC] Native D3D11 multithread protection enabled; previously protected={wasProtected}.");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ScreenShare:WGC] Native D3D11 multithread protection unavailable: {ex.Message}");
            }
        }

        private void EnsureStagingTexture(Texture2DDescription sourceDescription)
        {
            if (_sharpDxDevice == null)
                return;

            if (_stagingTexture != null)
            {
                var current = _stagingTexture.Description;
                if (current.Width == sourceDescription.Width &&
                    current.Height == sourceDescription.Height &&
                    current.Format == sourceDescription.Format)
                    return;

                _stagingTexture.Dispose();
                _stagingTexture = null;
            }

            _stagingTexture = new Texture2D(_sharpDxDevice, new Texture2DDescription
            {
                CpuAccessFlags = CpuAccessFlags.Read,
                BindFlags = BindFlags.None,
                Format = sourceDescription.Format,
                Width = sourceDescription.Width,
                Height = sourceDescription.Height,
                OptionFlags = ResourceOptionFlags.None,
                MipLevels = 1,
                ArraySize = 1,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Staging
            });
        }

        private static unsafe Bitmap CreateScaledBitmapFromBgra(
            IntPtr sourcePtr,
            int sourceStride,
            int sourceWidth,
            int sourceHeight,
            int targetWidth,
            int targetHeight)
        {
            var target = new Bitmap(targetWidth, targetHeight, PixelFormat.Format32bppArgb);
            var destination = GetAspectFitRectangle(sourceWidth, sourceHeight, targetWidth, targetHeight);
            var targetData = target.LockBits(
                new Rectangle(0, 0, targetWidth, targetHeight),
                ImageLockMode.WriteOnly,
                PixelFormat.Format32bppArgb);

            try
            {
                var targetBase = (byte*)targetData.Scan0;
                for (var y = 0; y < targetHeight; y++)
                {
                    var targetRow = targetBase + y * targetData.Stride;
                    for (var x = 0; x < targetWidth * 4; x++)
                        targetRow[x] = 0;
                }

                var sourceBase = (byte*)sourcePtr;
                const int weightScale = 256;
                var x0Map = new int[destination.Width];
                var x1Map = new int[destination.Width];
                var xWeightMap = new int[destination.Width];
                var scaleX = (double)sourceWidth / destination.Width;
                var scaleY = (double)sourceHeight / destination.Height;

                for (var x = 0; x < destination.Width; x++)
                {
                    var sourceX = ((x + 0.5) * scaleX) - 0.5;
                    if (sourceX < 0)
                        sourceX = 0;

                    var x0 = (int)sourceX;
                    var xWeight = (int)Math.Round((sourceX - x0) * weightScale);
                    var x1 = x0 + 1;
                    if (x1 >= sourceWidth)
                    {
                        x1 = x0;
                        xWeight = 0;
                    }

                    x0Map[x] = x0;
                    x1Map[x] = x1;
                    xWeightMap[x] = Math.Clamp(xWeight, 0, weightScale);
                }

                for (var y = 0; y < destination.Height; y++)
                {
                    var sourceY = ((y + 0.5) * scaleY) - 0.5;
                    if (sourceY < 0)
                        sourceY = 0;

                    var y0 = (int)sourceY;
                    var yWeight = (int)Math.Round((sourceY - y0) * weightScale);
                    var y1 = y0 + 1;
                    if (y1 >= sourceHeight)
                    {
                        y1 = y0;
                        yWeight = 0;
                    }
                    yWeight = Math.Clamp(yWeight, 0, weightScale);

                    var sourceRow0 = sourceBase + y0 * sourceStride;
                    var sourceRow1 = sourceBase + y1 * sourceStride;
                    var targetRow = targetBase + (destination.Y + y) * targetData.Stride + destination.X * 4;

                    for (var x = 0; x < destination.Width; x++)
                    {
                        var x0 = x0Map[x];
                        var x1 = x1Map[x];
                        var xWeight = xWeightMap[x];
                        var inverseXWeight = weightScale - xWeight;
                        var inverseYWeight = weightScale - yWeight;

                        var pixel00 = sourceRow0 + x0 * 4;
                        var pixel10 = sourceRow0 + x1 * 4;
                        var pixel01 = sourceRow1 + x0 * 4;
                        var pixel11 = sourceRow1 + x1 * 4;
                        var targetPixel = targetRow + x * 4;

                        for (var channel = 0; channel < 4; channel++)
                        {
                            var top = (pixel00[channel] * inverseXWeight) + (pixel10[channel] * xWeight);
                            var bottom = (pixel01[channel] * inverseXWeight) + (pixel11[channel] * xWeight);
                            targetPixel[channel] = (byte)(((top * inverseYWeight) + (bottom * yWeight) + 32768) >> 16);
                        }
                    }
                }
            }
            finally
            {
                target.UnlockBits(targetData);
            }

            return target;
        }

        private static Rectangle GetAspectFitRectangle(
            int sourceWidth,
            int sourceHeight,
            int targetWidth,
            int targetHeight)
        {
            var scale = Math.Min((double)targetWidth / sourceWidth, (double)targetHeight / sourceHeight);
            var width = Math.Max(1, (int)Math.Round(sourceWidth * scale));
            var height = Math.Max(1, (int)Math.Round(sourceHeight * scale));
            return new Rectangle(
                (targetWidth - width) / 2,
                (targetHeight - height) / 2,
                width,
                height);
        }

        private static unsafe uint SampleFrameFingerprint(IntPtr sourcePtr, int sourceStride, int width, int height)
        {
            return SampleFrameDiagnostics(sourcePtr, sourceStride, width, height).Fingerprint;
        }

        private static unsafe (uint Fingerprint, double AverageLuma) SampleFrameDiagnostics(IntPtr sourcePtr, int sourceStride, int width, int height)
        {
            var hash = 2166136261u;
            long lumaTotal = 0;
            var sampleCount = 0;
            var sourceBase = (byte*)sourcePtr;
            var sampleRows = Math.Min(12, Math.Max(1, height));
            var sampleColumns = Math.Min(16, Math.Max(1, width));

            for (var y = 0; y < sampleRows; y++)
            {
                var sourceY = Math.Min(height - 1, (int)((long)y * height / sampleRows));
                var sourceRow = sourceBase + sourceY * sourceStride;
                for (var x = 0; x < sampleColumns; x++)
                {
                    var sourceX = Math.Min(width - 1, (int)((long)x * width / sampleColumns));
                    var pixel = sourceRow + sourceX * 4;
                    hash = (hash ^ pixel[0]) * 16777619u;
                    hash = (hash ^ pixel[1]) * 16777619u;
                    hash = (hash ^ pixel[2]) * 16777619u;
                    lumaTotal += (pixel[2] * 54) + (pixel[1] * 183) + (pixel[0] * 19);
                    sampleCount++;
                }
            }

            var averageLuma = sampleCount > 0
                ? (double)lumaTotal / (sampleCount * 256)
                : 0;
            return (hash, averageLuma);
        }

        private static void TryEnableCursorCapture(GraphicsCaptureSession session)
        {
            try
            {
                session.IsCursorCaptureEnabled = true;
            }
            catch
            {
            }
        }

        private static void TryDisableCaptureBorder(GraphicsCaptureSession session)
        {
            try
            {
                session.IsBorderRequired = false;
                return;
            }
            catch
            {
            }

            try
            {
                var borderProperty = session.GetType().GetProperty("IsBorderRequired");
                if (borderProperty?.CanWrite == true)
                    borderProperty.SetValue(session, false);
            }
            catch
            {
            }
        }

        private static async Task<bool> TryRequestProgrammaticCaptureAccessAsync()
        {
            try
            {
                var status = await GraphicsCaptureAccess.RequestAccessAsync(GraphicsCaptureAccessKind.Programmatic);
                DiagnosticLogService.WriteLine($"[ScreenShare:WGC] Programmatic capture access status: {status}.");
                return string.Equals(status.ToString(), "Allowed", StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception ex)
            {
                DiagnosticLogService.WriteLine($"[ScreenShare:WGC] Programmatic capture access request failed: {ex.Message}");
                return true;
            }
        }

        private static async Task TryRequestBorderlessCaptureAccessAsync()
        {
            try
            {
                await GraphicsCaptureAccess.RequestAccessAsync(GraphicsCaptureAccessKind.Borderless);
            }
            catch
            {
            }
        }

        public void Dispose()
        {
            DisposeCaptureSession();

            lock (_syncRoot)
            {
                _latestFrame?.Dispose();
                _latestGpuFrame?.Dispose();
                _latestFrame = null;
                _latestGpuFrame = null;
            }
        }

        private void DisposeCaptureSession()
        {
            lock (_disposeSync)
            {
                _disposed = true;
                _started = false;
                if (_framePool != null)
                    _framePool.FrameArrived -= FramePool_FrameArrived;

                _framePool?.Dispose();
                _framePool = null;
                _session?.Dispose();
                _session = null;
                _stagingTexture?.Dispose();
                _stagingTexture = null;
                _previewSdrTexture?.Dispose();
                _previewSdrTexture = null;
                _previewHdrConverter?.Dispose();
                _previewHdrConverter = null;
                _loggedHdrPreviewConversion = false;
                _sharpDxDevice?.Dispose();
                _sharpDxDevice = null;
                _winRtDevice = null;
            }
        }
    }

    public sealed class CapturedGpuFrame : IDisposable
    {
        private bool _detached;

        public CapturedGpuFrame(Texture2D texture, int width, int height, Format format, BindFlags bindFlags)
        {
            Texture = texture;
            Width = width;
            Height = height;
            Format = format;
            BindFlags = bindFlags;
        }

        public Texture2D Texture { get; }
        public int Width { get; }
        public int Height { get; }
        public Format Format { get; }
        public BindFlags BindFlags { get; }

        public CapturedGpuFrame Detach()
        {
            _detached = true;
            return this;
        }

        public void Dispose()
        {
            if (_detached)
            {
                _detached = false;
                return;
            }

            Texture.Dispose();
        }
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Windows.Graphics.Capture;
using Windows.Storage;
using Zink.Models;

namespace Zink.Services.Recording
{
    public sealed class ManualRecordingService : IAsyncDisposable
    {
        public static ManualRecordingService Instance { get; } = new();

        private readonly object _gate = new();

        private TimeSpan _replayBufferDuration = TimeSpan.FromSeconds(45);

        // Keep active raw segment short so high-resolution replay stays off the managed heap.
        private readonly TimeSpan _segmentDuration = TimeSpan.FromMilliseconds(250);

        private CaptureEngine? _captureEngine;
        private IAudioCaptureService? _systemAudioCapture;
        private IAudioCaptureService? _microphoneCapture;

        private RollingClipBuffer? _activeVideoBuffer;
        private RollingAudioBuffer? _activeSystemAudioBuffer;
        private RollingAudioBuffer? _activeMicrophoneBuffer;
        private HardwareReplayBuffer? _hardwareReplayBuffer;

        private string? _currentOutputPath;
        private GraphicsCaptureItem? _captureItem;
        private RecordingOptions _options = new();

        private bool _isSavingReplay;
        private bool _resumeReplayAfterManual;
        private RecordingOptions? _lastReplayOptions;

        private string? _sessionFolderPath;
        private readonly List<string> _segmentPaths = new();
        private DateTimeOffset _currentSegmentStartedUtc;
        private int _nextSegmentIndex;

        private Task? _segmentWriteTask;
        private long _droppedVideoFrames;

        private readonly object _directWriterGate = new();
        private bool _useDirectManualWriter;
        private bool _directManualWriterStarted;
        private bool _directManualWriterFailed;
        private TimeSpan? _directManualOrigin;
        private int _directManualWidth;
        private int _directManualHeight;
        private int _directManualFps;
        private long _directManualVideoFrames;
        private long _directManualAudioPackets;
        private TimeSpan? _directManualAudioTimestampOffset;
        private long _directManualLastVideoTimestamp100ns;
        private long _directManualNextVideoTimestamp100ns;
        private TimeSpan? _lastObservedVideoTimestamp;
        private TimeSpan? _systemAudioTimestampOffset;
        private TimeSpan? _microphoneTimestampOffset;

        public bool IsRecording { get; private set; }
        public bool IsReplayMode { get; private set; }

        public bool IsManualRecording => IsRecording && !IsReplayMode;
        public bool IsReplayBufferRunning => IsRecording && IsReplayMode;

        public event EventHandler<string>? StatusChanged;

        private ManualRecordingService()
        {
        }

        public void SetCaptureItem(GraphicsCaptureItem item, RecordingOptions options)
        {
            _captureItem = item;
            _options = options.Clone();
            StatusChanged?.Invoke(this, "Recorder target updated.");
        }

        public TimeSpan GetReplayBufferedDuration()
        {
            lock (_gate)
            {
                if (!IsReplayMode || !IsRecording)
                    return TimeSpan.Zero;

                if (_hardwareReplayBuffer != null)
                    return _hardwareReplayBuffer.BufferedDuration;

                double finalizedSeconds = _segmentPaths.Count * _segmentDuration.TotalSeconds;
                double activeSeconds = 0;

                if (_activeVideoBuffer != null)
                {
                    activeSeconds = Math.Max(0, (DateTimeOffset.UtcNow - _currentSegmentStartedUtc).TotalSeconds);
                }

                return TimeSpan.FromSeconds(finalizedSeconds + activeSeconds);
            }
        }

        public async Task<string> StartAsync()
        {
            bool replayWasRunning;
            RecordingOptions? replayOptionsToResume;

            lock (_gate)
            {
                if (IsRecording && !IsReplayMode)
                    return _currentOutputPath ?? string.Empty;

                replayWasRunning = IsRecording && IsReplayMode;
                replayOptionsToResume = replayWasRunning
                    ? (_lastReplayOptions?.Clone() ?? _options.Clone())
                    : null;

                if (replayWasRunning)
                {
                    _resumeReplayAfterManual = true;
                    _lastReplayOptions = replayOptionsToResume?.Clone();
                }
            }

            if (replayWasRunning)
            {
                await StopCoreAsync(allowReplayResume: false);
            }

            return await StartCoreAsync(replayMode: false);
        }

        public async Task<string> StartReplayAsync(GraphicsCaptureItem item, RecordingOptions options)
        {
            options = ApplyReplayPreferences(options);
            SetCaptureItem(item, options);

            lock (_gate)
            {
                _lastReplayOptions = options.Clone();
                _resumeReplayAfterManual = false;
            }

            return await StartCoreAsync(replayMode: true);
        }

        public async Task<string> StartReplayAsync(RecordingOptions options)
        {
            options = ApplyReplayPreferences(options);
            _captureItem = null;
            _options = options.Clone();

            lock (_gate)
            {
                _lastReplayOptions = options.Clone();
                _resumeReplayAfterManual = false;
            }

            return await StartCoreAsync(replayMode: true);
        }

        private async Task<string> StartCoreAsync(bool replayMode)
        {
            if (!replayMode && _captureItem is null)
                throw new InvalidOperationException("Choose a capture source first.");

            lock (_gate)
            {
                if (IsRecording)
                    return _currentOutputPath ?? string.Empty;

                IsRecording = true;
                IsReplayMode = replayMode;
                _isSavingReplay = false;
                _droppedVideoFrames = 0;
                _lastObservedVideoTimestamp = null;
                _systemAudioTimestampOffset = null;
                _microphoneTimestampOffset = null;
            }

            try
            {
                if (replayMode)
                {
                    _replayBufferDuration = TimeSpan.FromSeconds(_options.RetrospectiveSeconds);
                    await GameReplayLog.WriteAsync(nameof(ManualRecordingService),
                        $"Replay start requested. Buffer={_replayBufferDuration}, RequestedSize={_options.OutputWidth}x{_options.OutputHeight}, RequestedFps={_options.FrameRate}, SystemAudio={_options.IncludeSystemAudio}, Microphone={_options.IncludeMicrophone}");
                }

                _currentOutputPath = replayMode
                    ? null
                    : await RecordingOutputService.CreateNewOutputPathAsync("Zink Recording");

                await CreateNewSessionFolderAsync();

                lock (_gate)
                {
                    _useDirectManualWriter = !replayMode && ShouldUseDirectManualWriter(_options);
                    ResetDirectManualWriterStateNoLock();
                }

                if (replayMode)
                {
                    _hardwareReplayBuffer = new HardwareReplayBuffer(_replayBufferDuration, _options);
                    _activeSystemAudioBuffer = _options.IncludeSystemAudio
                        ? new RollingAudioBuffer(_replayBufferDuration)
                        : null;
                    _activeMicrophoneBuffer = _options.IncludeMicrophone
                        ? new RollingAudioBuffer(_replayBufferDuration)
                        : null;
                }
                else if (!_useDirectManualWriter)
                {
                    InitializeActiveSegmentBuffers();
                }

                // Direct NVENC replay owns the desktop-duplication session. Starting
                // CaptureEngine as well can exceed DXGI's duplication-session limit.
                if (!replayMode || _hardwareReplayBuffer?.UsesDirectCapture != true)
                {
                    _captureEngine = new CaptureEngine();
                    if (replayMode)
                        _captureEngine.GpuFrameArrived += CaptureEngine_GpuFrameArrived;
                    else
                        _captureEngine.VideoFrameArrived += CaptureEngine_VideoFrameArrived;
                    await _captureEngine.StartAsync(_captureItem, _options);
                }

                if (_options.IncludeSystemAudio)
                {
                    _systemAudioCapture = new SystemLoopbackCaptureService();
                    _systemAudioCapture.AudioPacketArrived += SystemAudioCapture_AudioPacketArrived;
                    await _systemAudioCapture.StartAsync(_options.SelectedRenderDeviceId);
                }

                if (_options.IncludeMicrophone)
                {
                    _microphoneCapture = new MicrophoneCaptureService();
                    _microphoneCapture.AudioPacketArrived += MicrophoneCapture_AudioPacketArrived;
                    await _microphoneCapture.StartAsync(_options.SelectedMicDeviceId);
                }

                if (replayMode)
                    _hardwareReplayBuffer?.SynchronizeDirectCaptureClock();

                await RecorderLog.InfoAsync(nameof(ManualRecordingService),
                    $"Recording started. ReplayMode={replayMode}, DirectManualWriter={_useDirectManualWriter}, SystemAudio={_options.IncludeSystemAudio}, Mic={_options.IncludeMicrophone}, SegmentDuration={_segmentDuration}");
                if (replayMode)
                    await GameReplayLog.WriteAsync(nameof(ManualRecordingService),
                        "Replay capture and requested audio services started successfully.");

                StatusChanged?.Invoke(this,
                    replayMode
                        ? "Instant replay started."
                        : $"Manual recording started: {_currentOutputPath}");

                return _currentOutputPath ?? string.Empty;
            }
            catch (Exception ex)
            {
                await RecorderLog.ErrorAsync(nameof(ManualRecordingService), ex, "StartCoreAsync failed");
                if (replayMode)
                    await GameReplayLog.WriteAsync(nameof(ManualRecordingService), $"Replay start failed: {ex}");

                lock (_gate)
                {
                    IsRecording = false;
                    IsReplayMode = false;
                    _isSavingReplay = false;
                }

                await SafeStopInternalAsync();
                throw;
            }
        }

        private void CaptureEngine_VideoFrameArrived(object? sender, VideoFramePacket packet)
        {
            HardwareReplayBuffer? replayBuffer;
            lock (_gate)
            {
                _lastObservedVideoTimestamp = packet.Timestamp;
                replayBuffer = IsReplayMode ? _hardwareReplayBuffer : null;
            }

            if (replayBuffer != null)
            {
                replayBuffer.Add(packet);
                return;
            }

            if (TryWriteDirectManualVideo(packet))
                return;

            SegmentBatch? batchToFinalize = null;
            Task? startedWriteTask = null;

            lock (_gate)
            {
                if (_useDirectManualWriter && _directManualWriterFailed && _activeVideoBuffer is null)
                {
                    InitializeActiveSegmentBuffersNoLock();
                }

                bool packetAccepted = _activeVideoBuffer is not null;
                long beforeBytes = _activeVideoBuffer?.TotalBufferedBytes ?? 0;
                _activeVideoBuffer?.Add(packet);
                long afterBytes = _activeVideoBuffer?.TotalBufferedBytes ?? 0;

                if (!packetAccepted)
                {
                    packet.Dispose();
                }

                if (afterBytes < beforeBytes)
                {
                    _droppedVideoFrames++;
                }

                bool writeBusy = _segmentWriteTask != null && !_segmentWriteTask.IsCompleted;

                if (ShouldRotateSegmentNoLock() && !writeBusy)
                {
                    batchToFinalize = CreateSegmentBatchNoLock();
                    InitializeActiveSegmentBuffersNoLock();

                    if (batchToFinalize is not null)
                    {
                        startedWriteTask = FinalizeSegmentAsync(batchToFinalize);
                        _segmentWriteTask = startedWriteTask;
                    }
                }
            }

            if (startedWriteTask != null)
            {
                _ = startedWriteTask.ContinueWith(_ =>
                {
                    lock (_gate)
                    {
                        if (ReferenceEquals(_segmentWriteTask, startedWriteTask))
                            _segmentWriteTask = null;
                    }
                });
            }
        }

        private void CaptureEngine_GpuFrameArrived(object? sender, GpuVideoFramePacket packet)
        {
            HardwareReplayBuffer? replayBuffer;
            lock (_gate)
            {
                _lastObservedVideoTimestamp = packet.Timestamp;
                replayBuffer = IsReplayMode ? _hardwareReplayBuffer : null;
            }

            replayBuffer?.Add(packet);
        }

        private void SystemAudioCapture_AudioPacketArrived(object? sender, AudioPacket packet)
        {
            if (TryWriteDirectManualAudio(packet))
                return;

            lock (_gate)
            {
                packet = AlignAudioPacketToVideoClockNoLock(packet, ref _systemAudioTimestampOffset);
                _activeSystemAudioBuffer?.Add(packet);
            }
        }

        private void MicrophoneCapture_AudioPacketArrived(object? sender, AudioPacket packet)
        {
            lock (_gate)
            {
                packet = AlignAudioPacketToVideoClockNoLock(packet, ref _microphoneTimestampOffset);
                _activeMicrophoneBuffer?.Add(packet);
            }
        }

        public async Task<string> SaveReplayAsync()
            => await SaveReplayAsync(_replayBufferDuration);

        public async Task<string> SaveReplayAsync(TimeSpan requestedDuration)
        {
            if (!IsReplayBufferRunning)
                throw new InvalidOperationException("Replay buffer is not running yet.");

            lock (_gate)
            {
                if (_isSavingReplay)
                    throw new InvalidOperationException("Replay save is already in progress.");

                _isSavingReplay = true;
            }

            try
            {
                await GameReplayLog.WriteAsync(nameof(ManualRecordingService),
                    $"Replay save requested. Duration={requestedDuration}, BufferedDuration={GetReplayBufferedDuration()}");
                HardwareReplayBuffer? hardwareReplay;
                List<AudioPacket>? replaySystemAudio;
                List<AudioPacket>? replayMicAudio;
                lock (_gate)
                {
                    hardwareReplay = _hardwareReplayBuffer;
                    replaySystemAudio = CloneAudioPackets(_activeSystemAudioBuffer?.Snapshot());
                    replayMicAudio = CloneAudioPackets(_activeMicrophoneBuffer?.Snapshot());
                }

                if (hardwareReplay != null)
                {
                    string hardwareOutputPath = await RecordingOutputService.CreateNewOutputPathAsync("Zink Replay", ".mp4");
                    string replayExportFolder = Path.Combine(
                        ReplayTemporaryFileCleanup.FolderPath,
                        "Exports",
                        Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(replayExportFolder);
                    string tempH264 = Path.Combine(replayExportFolder, "video.h264");
                    string tempWave = Path.Combine(replayExportFolder, "combined.wav");
                    string tempDesktopWave = Path.Combine(replayExportFolder, "desktop.wav");
                    string tempMicrophoneWave = Path.Combine(replayExportFolder, "microphone.wav");
                    string tempMuxOutput = Path.Combine(replayExportFolder, "finished.mp4");
                    try
                    {
                        // The in-process mux consumes the encoded replay snapshot directly.
                        // Do not write a second multi-gigabyte elementary stream to disk.
                        await hardwareReplay.WriteVideoAsync(
                            tempH264,
                            requestedDuration,
                            writeElementaryStream: false);
                        TimeSpan exportDuration = hardwareReplay.ExportDuration > TimeSpan.Zero
                            ? hardwareReplay.ExportDuration
                            : requestedDuration;
                        PrepareReplayAudio(
                            replaySystemAudio,
                            replayMicAudio,
                            exportDuration,
                            hardwareReplay.ExportStartTimestamp,
                            out var shiftedSystemAudio,
                            out var shiftedMicAudio);
                        bool includeSystemAudio = RecordingPreferences.IsBackgroundReplaySystemAudioEnabled;
                        bool includeMicrophone = RecordingPreferences.IsBackgroundReplayMicrophoneEnabled;
                        bool separateTracks = RecordingPreferences.IsBackgroundReplaySeparateTracksEnabled;
                        string? combinedWavePath = null;
                        string? desktopWavePath = null;
                        string? microphoneWavePath = null;

                        if (includeSystemAudio || includeMicrophone)
                        {
                            var mixedAudio = AudioMixHelpers.MixPcm16(
                                includeSystemAudio ? shiftedSystemAudio : null,
                                includeMicrophone ? shiftedMicAudio : null);
                            WaveFileWriter.WritePcm16Wave(
                                tempWave,
                                BuildExactReplayAudioTrack(mixedAudio, exportDuration));
                            combinedWavePath = tempWave;
                        }

                        if (separateTracks &&
                            includeSystemAudio &&
                            RecordingPreferences.IsBackgroundReplaySeparateDesktopTrackEnabled)
                        {
                            var desktopAudio = AudioMixHelpers.MixPcm16(shiftedSystemAudio, null);
                            WaveFileWriter.WritePcm16Wave(
                                tempDesktopWave,
                                BuildExactReplayAudioTrack(desktopAudio, exportDuration));
                            desktopWavePath = tempDesktopWave;
                        }

                        if (separateTracks &&
                            includeMicrophone &&
                            RecordingPreferences.IsBackgroundReplaySeparateMicrophoneTrackEnabled)
                        {
                            var microphoneAudio = AudioMixHelpers.MixPcm16(null, shiftedMicAudio);
                            WaveFileWriter.WritePcm16Wave(
                                tempMicrophoneWave,
                                BuildExactReplayAudioTrack(microphoneAudio, exportDuration));
                            microphoneWavePath = tempMicrophoneWave;
                        }

                        await hardwareReplay.MuxAsync(
                            tempH264,
                            combinedWavePath,
                            desktopWavePath,
                            microphoneWavePath,
                            tempMuxOutput,
                            exportDuration,
                            RecordingPreferences.BackgroundReplayAudioBitrateKbps);
                        string? finalOutputDirectory = Path.GetDirectoryName(hardwareOutputPath);
                        if (string.IsNullOrWhiteSpace(finalOutputDirectory))
                            throw new InvalidOperationException("The replay output folder path is invalid.");
                        Directory.CreateDirectory(finalOutputDirectory);
                        try
                        {
                            File.Move(tempMuxOutput, hardwareOutputPath, overwrite: false);
                        }
                        catch (Exception ex) when (
                            ex is UnauthorizedAccessException ||
                            ex is IOException)
                        {
                            hardwareOutputPath =
                                RecordingOutputService.CreateFallbackOutputPath("Zink Replay", ".mp4");
                            File.Move(tempMuxOutput, hardwareOutputPath, overwrite: false);
                        }
                        StatusChanged?.Invoke(this, $"Replay clip saved: {hardwareOutputPath}");
                        await GameReplayLog.WriteAsync(nameof(ManualRecordingService),
                            $"Replay save completed. Output={hardwareOutputPath}, Exists={File.Exists(hardwareOutputPath)}, Bytes={(File.Exists(hardwareOutputPath) ? new FileInfo(hardwareOutputPath).Length : 0)}");
                        return hardwareOutputPath;
                    }
                    finally
                    {
                        TryDeleteFile(tempH264);
                        TryDeleteFile(tempWave);
                        TryDeleteFile(tempDesktopWave);
                        TryDeleteFile(tempMicrophoneWave);
                        TryDeleteFile(tempMuxOutput);
                        try
                        {
                            if (Directory.Exists(replayExportFolder))
                                Directory.Delete(replayExportFolder, recursive: true);
                        }
                        catch
                        {
                        }
                    }
                }

                await AwaitCurrentSegmentWriteAsync();

                SegmentBatch? finalBatch;

                lock (_gate)
                {
                    finalBatch = CreateSegmentBatchNoLock();
                    InitializeActiveSegmentBuffersNoLock();
                }

                if (finalBatch is not null)
                {
                    await FinalizeSegmentAsync(finalBatch);
                }

                List<string> segments;
                lock (_gate)
                {
                    segments = SelectReplaySegmentsNoLock(requestedDuration);
                }

                if (segments.Count == 0)
                    throw new InvalidOperationException("No buffered replay segments are available yet.");

                string outputPath = await RecordingOutputService.CreateNewOutputPathAsync("Zink Replay");

                if (segments.Count == 1)
                {
                    File.Copy(segments[0], outputPath, true);
                }
                else
                {
                    var mux = new MediaMuxService();
                    await mux.ConcatVideosAsync(segments, outputPath);
                }

                if (!File.Exists(outputPath))
                    throw new InvalidOperationException("Replay save finished but the output file was not created.");

                var outputInfo = new FileInfo(outputPath);
                if (!outputInfo.Exists || outputInfo.Length == 0)
                    throw new InvalidOperationException("Replay save finished but the output file is empty.");

                await RecorderLog.InfoAsync(nameof(ManualRecordingService),
                    $"Replay clip saved successfully. Output='{outputPath}', RequestedDuration={requestedDuration}, Segments={segments.Count}, Bytes={outputInfo.Length}");

                StatusChanged?.Invoke(this, $"Replay clip saved: {outputPath}");
                return outputPath;
            }
            catch (Exception ex)
            {
                await RecorderLog.ErrorAsync(nameof(ManualRecordingService), ex, "SaveReplayAsync failed");
                await GameReplayLog.WriteAsync(nameof(ManualRecordingService), $"Replay save failed: {ex}");
                StatusChanged?.Invoke(this, $"Replay save failed: {ex.Message}");
                throw;
            }
            finally
            {
                lock (_gate)
                {
                    _isSavingReplay = false;
                }
            }
        }

        public async Task StopAsync()
        {
            await StopCoreAsync(allowReplayResume: true);
        }

        private async Task StopCoreAsync(bool allowReplayResume)
        {
            string? outputPath;
            CaptureEngine? localEngine;
            IAudioCaptureService? localSystemCapture;
            IAudioCaptureService? localMicCapture;
            bool localReplayMode;
            bool restartReplayAfterManual = false;
            RecordingOptions? replayOptionsToResume = null;

            lock (_gate)
            {
                if (!IsRecording)
                    return;

                localReplayMode = IsReplayMode;

                if (!localReplayMode && allowReplayResume && _resumeReplayAfterManual)
                {
                    restartReplayAfterManual = true;
                    replayOptionsToResume = _lastReplayOptions?.Clone() ?? _options.Clone();
                }

                IsRecording = false;
                IsReplayMode = false;
                _isSavingReplay = false;
                _resumeReplayAfterManual = false;

                outputPath = _currentOutputPath;
                localEngine = _captureEngine;
                localSystemCapture = _systemAudioCapture;
                localMicCapture = _microphoneCapture;

                _captureEngine = null;
                _systemAudioCapture = null;
                _microphoneCapture = null;
                _currentOutputPath = null;
            }

            try
            {
                if (localEngine is not null)
                {
                    localEngine.VideoFrameArrived -= CaptureEngine_VideoFrameArrived;
                    localEngine.GpuFrameArrived -= CaptureEngine_GpuFrameArrived;
                    await localEngine.StopAsync();
                    await localEngine.DisposeAsync();
                }

                if (localSystemCapture is not null)
                {
                    localSystemCapture.AudioPacketArrived -= SystemAudioCapture_AudioPacketArrived;
                    await localSystemCapture.StopAsync();
                    await localSystemCapture.DisposeAsync();
                }

                if (localMicCapture is not null)
                {
                    localMicCapture.AudioPacketArrived -= MicrophoneCapture_AudioPacketArrived;
                    await localMicCapture.StopAsync();
                    await localMicCapture.DisposeAsync();
                }

                await AwaitCurrentSegmentWriteAsync();

                bool usedDirectWriter;
                bool directWriterFailed;
                lock (_gate)
                {
                    usedDirectWriter = _useDirectManualWriter;
                    directWriterFailed = _directManualWriterFailed;
                }

                if (!localReplayMode && usedDirectWriter && !directWriterFailed)
                {
                    if (string.IsNullOrWhiteSpace(outputPath))
                        throw new InvalidOperationException("Manual recording output path was not created.");

                    await FinalizeDirectManualWriterAsync(outputPath);

                    if (!File.Exists(outputPath))
                        throw new InvalidOperationException("Manual recording finished but the output file was not created.");

                    var directOutputInfo = new FileInfo(outputPath);
                    if (!directOutputInfo.Exists || directOutputInfo.Length == 0)
                        throw new InvalidOperationException("Manual recording finished but the output file is empty.");

                    await CleanupSessionFolderAsync();
                    await Task.Run(ReleaseRecorderMemory);
                    StatusChanged?.Invoke(this, $"Manual recording saved: {outputPath}");
                    StatusChanged?.Invoke(this, "Manual recording stopped.");
                    return;
                }

                SegmentBatch? finalBatch;
                lock (_gate)
                {
                    finalBatch = CreateSegmentBatchNoLock();
                    _activeVideoBuffer = null;
                    _activeSystemAudioBuffer = null;
                    _activeMicrophoneBuffer = null;
                }

                if (finalBatch is not null)
                {
                    await FinalizeSegmentAsync(finalBatch);
                }

                List<string> segments;
                lock (_gate)
                {
                    segments = _segmentPaths.ToList();
                }

                if (localReplayMode)
                {
                    _hardwareReplayBuffer?.Dispose();
                    _hardwareReplayBuffer = null;
                    _activeSystemAudioBuffer?.Clear();
                    _activeSystemAudioBuffer = null;
                    _activeMicrophoneBuffer?.Clear();
                    _activeMicrophoneBuffer = null;
                    await CleanupSessionFolderAsync();

                    await RecorderLog.InfoAsync(nameof(ManualRecordingService), "Replay buffering stopped.");
                    StatusChanged?.Invoke(this, "Replay buffering stopped.");
                    return;
                }

                if (!string.IsNullOrWhiteSpace(outputPath))
                {
                    if (segments.Count == 0)
                        throw new InvalidOperationException("No recording segments were created, so no recording can be saved.");

                    if (segments.Count == 1)
                    {
                        File.Copy(segments[0], outputPath, true);
                    }
                    else
                    {
                        var mux = new MediaMuxService();
                        await mux.ConcatVideosAsync(segments, outputPath);
                    }

                    if (!File.Exists(outputPath))
                        throw new InvalidOperationException("Manual recording finished but the output file was not created.");

                    var outputInfo = new FileInfo(outputPath);
                    if (!outputInfo.Exists || outputInfo.Length == 0)
                        throw new InvalidOperationException("Manual recording finished but the output file is empty.");

                    StatusChanged?.Invoke(this, $"Manual recording saved: {outputPath}");
                }

                await CleanupSessionFolderAsync();
                await Task.Run(ReleaseRecorderMemory);
                StatusChanged?.Invoke(this, "Manual recording stopped.");
            }
            catch (Exception ex)
            {
                await RecorderLog.ErrorAsync(nameof(ManualRecordingService), ex, "StopAsync failed");
                StatusChanged?.Invoke(this, $"Recording stop failed: {ex.Message}");
                throw;
            }
            finally
            {
                if (restartReplayAfterManual &&
                    replayOptionsToResume != null &&
                    RecordingPreferences.IsGamingBackgroundReplayEnabled)
                {
                    try
                    {
                        if (_captureItem is not null)
                        {
                            await StartReplayAsync(_captureItem, replayOptionsToResume);
                        }
                        else
                        {
                            await StartReplayAsync(replayOptionsToResume);
                        }

                        await RecorderLog.InfoAsync(nameof(ManualRecordingService),
                            "Replay buffering restarted automatically after manual recording stop.");
                        StatusChanged?.Invoke(this, "Replay buffering restarted.");
                    }
                    catch (Exception replayEx)
                    {
                        await RecorderLog.ErrorAsync(nameof(ManualRecordingService), replayEx,
                            "Failed to restart replay buffering after manual recording stop.");
                    }
                }
            }
        }

        private async Task AwaitCurrentSegmentWriteAsync()
        {
            Task? task;
            lock (_gate)
            {
                task = _segmentWriteTask;
            }

            if (task != null)
            {
                await task;
            }

            lock (_gate)
            {
                _segmentWriteTask = null;
            }
        }

        private async Task SafeStopInternalAsync()
        {
            CaptureEngine? localEngine = _captureEngine;
            IAudioCaptureService? localSystemCapture = _systemAudioCapture;
            IAudioCaptureService? localMicCapture = _microphoneCapture;

            _captureEngine = null;
            _systemAudioCapture = null;
            _microphoneCapture = null;
            _isSavingReplay = false;
            IsReplayMode = false;
            IsRecording = false;
            _resumeReplayAfterManual = false;
            _useDirectManualWriter = false;
            ResetDirectManualWriterStateNoLock();

            if (localEngine is not null)
            {
                localEngine.VideoFrameArrived -= CaptureEngine_VideoFrameArrived;
                localEngine.GpuFrameArrived -= CaptureEngine_GpuFrameArrived;
                await localEngine.StopAsync();
                await localEngine.DisposeAsync();
            }

            if (localSystemCapture is not null)
            {
                localSystemCapture.AudioPacketArrived -= SystemAudioCapture_AudioPacketArrived;
                await localSystemCapture.StopAsync();
                await localSystemCapture.DisposeAsync();
            }

            if (localMicCapture is not null)
            {
                localMicCapture.AudioPacketArrived -= MicrophoneCapture_AudioPacketArrived;
                await localMicCapture.StopAsync();
                await localMicCapture.DisposeAsync();
            }

            _activeVideoBuffer?.Dispose();
            _activeVideoBuffer = null;

            _activeSystemAudioBuffer?.Clear();
            _activeSystemAudioBuffer = null;

            _activeMicrophoneBuffer?.Clear();
            _activeMicrophoneBuffer = null;

            _hardwareReplayBuffer?.Dispose();
            _hardwareReplayBuffer = null;

            await CleanupSessionFolderAsync();
            await Task.Run(ReleaseRecorderMemory);
        }

        private static bool ShouldUseDirectManualWriter(RecordingOptions options)
        {
            // The legacy native mux writer exposes one audio stream. Keep mic mixing on the
            // existing segment path until the native writer can mix two live PCM sources.
            return !options.IncludeMicrophone;
        }

        private void ResetDirectManualWriterStateNoLock()
        {
            lock (_directWriterGate)
            {
                if (_directManualWriterStarted)
                {
                    try
                    {
                        NativeMuxWriter.ZrmShutdownWriter();
                    }
                    catch
                    {
                    }
                }

                _directManualWriterStarted = false;
                _directManualWriterFailed = false;
                _directManualOrigin = null;
                _directManualWidth = 0;
                _directManualHeight = 0;
                _directManualFps = 0;
                _directManualVideoFrames = 0;
                _directManualAudioPackets = 0;
                _directManualAudioTimestampOffset = null;
                _directManualLastVideoTimestamp100ns = 0;
                _directManualNextVideoTimestamp100ns = 0;
            }
        }

        private AudioPacket AlignAudioPacketToVideoClockNoLock(AudioPacket packet, ref TimeSpan? audioTimestampOffset)
        {
            if (_lastObservedVideoTimestamp is null)
                return packet;

            audioTimestampOffset ??= _lastObservedVideoTimestamp.Value - packet.Timestamp;
            packet.Timestamp += audioTimestampOffset.Value;
            return packet;
        }

        private bool TryWriteDirectManualVideo(VideoFramePacket packet)
        {
            bool shouldUseDirect;
            string? outputPath;

            lock (_gate)
            {
                shouldUseDirect = IsManualRecording && _useDirectManualWriter && !_directManualWriterFailed;
                outputPath = _currentOutputPath;
            }

            if (!shouldUseDirect || string.IsNullOrWhiteSpace(outputPath))
                return false;

            if (packet.Bgra32Bytes is null ||
                packet.Width <= 0 ||
                packet.Height <= 0 ||
                packet.Bgra32Bytes.Length != packet.Width * packet.Height * 4)
            {
                packet.Dispose();
                return true;
            }

            try
            {
                lock (_directWriterGate)
                {
                    if (_directManualWriterFailed)
                        return false;

                    if (!_directManualWriterStarted)
                    {
                        _directManualOrigin = packet.Timestamp;
                        _directManualWidth = packet.Width;
                        _directManualHeight = packet.Height;
                        _directManualFps = CalculateDirectManualFps(packet.Width, packet.Height, _options.FrameRate);

                        int hr = NativeMuxWriter.ZrmCreateWriter(
                            outputPath,
                            (uint)_directManualWidth,
                            (uint)_directManualHeight,
                            (uint)_directManualFps,
                            1,
                            CalculateDirectManualBitrate((uint)_directManualWidth, (uint)_directManualHeight, (uint)_directManualFps),
                            48000,
                            2,
                            16);

                        NativeMuxWriter.ThrowIfFailed(hr, nameof(NativeMuxWriter.ZrmCreateWriter));
                        _directManualWriterStarted = true;
                        _directManualNextVideoTimestamp100ns = 0;
                        _directManualLastVideoTimestamp100ns = 0;

                        _ = RecorderLog.InfoAsync(nameof(ManualRecordingService),
                            $"Direct manual writer started. Output='{outputPath}', Size={_directManualWidth}x{_directManualHeight}, Fps={_directManualFps}");
                    }

                    long duration100ns = TimeSpan.FromMilliseconds(1000.0 / Math.Max(1, _directManualFps)).Ticks;
                    long elapsed100ns = _directManualOrigin is null
                        ? 0
                        : Math.Max(0, (packet.Timestamp - _directManualOrigin.Value).Ticks);

                    long timestamp100ns = Math.Max(elapsed100ns, _directManualLastVideoTimestamp100ns + duration100ns);
                    if (_directManualVideoFrames == 0)
                        timestamp100ns = 0;

                    _directManualLastVideoTimestamp100ns = timestamp100ns;

                    int hrWrite = NativeMuxWriter.ZrmWriteVideoFrame(
                        timestamp100ns,
                        duration100ns,
                        packet.Bgra32Bytes,
                        (uint)packet.Bgra32Bytes.Length);

                    NativeMuxWriter.ThrowIfFailed(hrWrite, nameof(NativeMuxWriter.ZrmWriteVideoFrame));
                    _directManualVideoFrames++;
                    _directManualNextVideoTimestamp100ns = timestamp100ns + duration100ns;
                }

                packet.Dispose();
                return true;
            }
            catch (Exception ex)
            {
                MarkDirectManualWriterFailed(ex, "Direct manual video writer failed; falling back to buffered segment writing.");
                return false;
            }
        }

        private bool TryWriteDirectManualAudio(AudioPacket packet)
        {
            bool shouldUseDirect;

            lock (_gate)
            {
                shouldUseDirect = IsManualRecording && _useDirectManualWriter && !_directManualWriterFailed;
            }

            if (!shouldUseDirect)
                return false;

            try
            {
                lock (_directWriterGate)
                {
                    if (_directManualWriterFailed)
                        return false;

                    if (!_directManualWriterStarted || _directManualOrigin is null)
                        return true;

                    var normalizedPacket = AudioMixHelpers.NormalizePacket(packet);

                    if (normalizedPacket.PcmData.Length == 0)
                        return true;

                    _directManualAudioTimestampOffset ??=
                        TimeSpan.FromTicks(_directManualLastVideoTimestamp100ns) - normalizedPacket.Timestamp;

                    long timestamp100ns = (normalizedPacket.Timestamp + _directManualAudioTimestampOffset.Value).Ticks;
                    if (timestamp100ns < 0)
                        return true;

                    int blockAlign = normalizedPacket.Channels * (normalizedPacket.BitsPerSample / 8);
                    long duration100ns = blockAlign > 0 && normalizedPacket.SampleRate > 0
                        ? (10_000_000L * normalizedPacket.PcmData.Length) / ((long)normalizedPacket.SampleRate * blockAlign)
                        : 0;

                    int hr = NativeMuxWriter.ZrmWriteAudioPacket(
                        timestamp100ns,
                        duration100ns,
                        normalizedPacket.PcmData,
                        (uint)normalizedPacket.PcmData.Length);

                    NativeMuxWriter.ThrowIfFailed(hr, nameof(NativeMuxWriter.ZrmWriteAudioPacket));
                    _directManualAudioPackets++;
                    return true;
                }
            }
            catch (Exception ex)
            {
                MarkDirectManualWriterFailed(ex, "Direct manual audio writer failed; falling back to buffered segment writing.");
                return false;
            }
        }

        private void MarkDirectManualWriterFailed(Exception ex, string message)
        {
            lock (_directWriterGate)
            {
                _directManualWriterFailed = true;

                try
                {
                    NativeMuxWriter.ZrmShutdownWriter();
                }
                catch
                {
                }

                _directManualWriterStarted = false;
            }

            lock (_gate)
            {
                _directManualWriterFailed = true;
            }

            _ = RecorderLog.ErrorAsync(nameof(ManualRecordingService), ex, message);
        }

        private async Task FinalizeDirectManualWriterAsync(string outputPath)
        {
            var finalizeStartedUtc = DateTimeOffset.UtcNow;
            long videoFrames = 0;
            long audioPackets = 0;

            await Task.Run(() =>
            {
                lock (_directWriterGate)
                {
                    if (!_directManualWriterStarted || _directManualVideoFrames == 0)
                        throw new InvalidOperationException("No direct manual frames were written.");

                    NativeMuxWriter.ThrowIfFailed(
                        NativeMuxWriter.ZrmFinalizeWriter(),
                        nameof(NativeMuxWriter.ZrmFinalizeWriter));

                    NativeMuxWriter.ZrmShutdownWriter();
                    videoFrames = _directManualVideoFrames;
                    audioPackets = _directManualAudioPackets;
                    _directManualWriterStarted = false;
                }
            });

            await RecorderLog.InfoAsync(nameof(ManualRecordingService),
                $"Direct manual writer finalized. Output='{outputPath}', VideoFrames={videoFrames}, AudioPackets={audioPackets}, ElapsedMs={(DateTimeOffset.UtcNow - finalizeStartedUtc).TotalMilliseconds:F0}");
        }

        private static int CalculateDirectManualFps(int width, int height, uint requestedFps)
        {
            int fps = (int)Math.Clamp(requestedFps == 0 ? 120 : requestedFps, 1u, 120u);
            return fps;
        }

        private static uint CalculateDirectManualBitrate(uint width, uint height, uint fps)
        {
            double bitsPerSecond = width * (double)height * Math.Max(1, fps) * 0.16;
            return (uint)Math.Clamp(bitsPerSecond, 12_000_000, 100_000_000);
        }

        private static void ReleaseRecorderMemory()
        {
            FrameBufferPool.Clear();
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }

        private async Task CreateNewSessionFolderAsync()
        {
            string root = Path.Combine(ApplicationData.Current.LocalCacheFolder.Path, "ZinkSegments");
            Directory.CreateDirectory(root);

            string sessionFolder = Path.Combine(root, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(sessionFolder);

            lock (_gate)
            {
                _sessionFolderPath = sessionFolder;
                _segmentPaths.Clear();
                _nextSegmentIndex = 0;
                _segmentWriteTask = null;
            }

            await Task.CompletedTask;
        }

        private async Task CleanupSessionFolderAsync()
        {
            string? folder;
            lock (_gate)
            {
                folder = _sessionFolderPath;
                _sessionFolderPath = null;
                _segmentPaths.Clear();
                _nextSegmentIndex = 0;
                _segmentWriteTask = null;
            }

            if (!string.IsNullOrWhiteSpace(folder))
            {
                try
                {
                    if (Directory.Exists(folder))
                        Directory.Delete(folder, true);
                }
                catch
                {
                }
            }

            await Task.CompletedTask;
        }

        private void InitializeActiveSegmentBuffers()
        {
            lock (_gate)
            {
                InitializeActiveSegmentBuffersNoLock();
            }
        }

        private void InitializeActiveSegmentBuffersNoLock()
        {
            _activeVideoBuffer = new RollingClipBuffer(_segmentDuration);

            _activeSystemAudioBuffer = _options.IncludeSystemAudio
                ? new RollingAudioBuffer(_segmentDuration)
                : null;

            _activeMicrophoneBuffer = _options.IncludeMicrophone
                ? new RollingAudioBuffer(_segmentDuration)
                : null;

            _currentSegmentStartedUtc = DateTimeOffset.UtcNow;
        }

        private bool ShouldRotateSegmentNoLock()
        {
            return (DateTimeOffset.UtcNow - _currentSegmentStartedUtc) >= _segmentDuration;
        }

        private List<string> SelectReplaySegmentsNoLock(TimeSpan requestedDuration)
        {
            TimeSpan duration = requestedDuration <= TimeSpan.Zero
                ? _replayBufferDuration
                : requestedDuration;

            duration = duration > _replayBufferDuration
                ? _replayBufferDuration
                : duration;

            int maxSegments = Math.Max(1, (int)Math.Ceiling(duration.TotalSeconds / _segmentDuration.TotalSeconds));
            int skip = Math.Max(0, _segmentPaths.Count - maxSegments);

            return _segmentPaths
                .Skip(skip)
                .ToList();
        }

        private SegmentBatch? CreateSegmentBatchNoLock()
        {
            if (_activeVideoBuffer is null || _activeVideoBuffer.Count == 0 || string.IsNullOrWhiteSpace(_sessionFolderPath))
                return null;

            string segmentPath = Path.Combine(_sessionFolderPath, $"segment_{_nextSegmentIndex++:000000}.mp4");

            return new SegmentBatch(
                segmentPath,
                _activeVideoBuffer,
                _activeSystemAudioBuffer,
                _activeMicrophoneBuffer,
                IsReplayMode);
        }

        private async Task FinalizeSegmentAsync(SegmentBatch batch)
        {
            var finalizeStartedUtc = DateTimeOffset.UtcNow;

            List<VideoFramePacket> videoFrames = batch.VideoBuffer.Snapshot()
                .Where(f => f.Bgra32Bytes is not null)
                .OrderBy(f => f.Timestamp)
                .ToList();

            List<AudioPacket>? systemPackets = batch.SystemAudioBuffer?.Snapshot()
                .OrderBy(p => p.Timestamp)
                .ToList();

            List<AudioPacket>? micPackets = batch.MicrophoneAudioBuffer?.Snapshot()
                .OrderBy(p => p.Timestamp)
                .ToList();

            if (videoFrames.Count == 0)
            {
                batch.VideoBuffer.Dispose();
                batch.SystemAudioBuffer?.Clear();
                batch.MicrophoneAudioBuffer?.Clear();
                return;
            }

            string tempVideo = Path.ChangeExtension(batch.OutputPath, null) + "_video.mp4";
            string tempAudio = Path.ChangeExtension(batch.OutputPath, null) + "_audio.wav";

            List<VideoFramePacket>? syncedVideo = null;

            try
            {
                await RecorderLog.InfoAsync(nameof(ManualRecordingService),
                    $"Finalizing segment. Replay={batch.IsReplaySegment}, VideoFrames={videoFrames.Count}, SystemPackets={systemPackets?.Count ?? 0}, MicPackets={micPackets?.Count ?? 0}, ManagedMemoryMB={GC.GetTotalMemory(false) / 1024 / 1024}, DroppedFrames={_droppedVideoFrames}");

                var origin = AvSyncHelpers.ComputeCommonOrigin(videoFrames, systemPackets, micPackets);

                syncedVideo = AvSyncHelpers.ShiftVideo(videoFrames, origin);
                var syncedSystem = AvSyncHelpers.ShiftAudio(systemPackets, origin);
                var syncedMic = AvSyncHelpers.ShiftAudio(micPackets, origin);

                if (syncedVideo.Count == 0)
                    throw new InvalidOperationException("No valid video frames remained after A/V sync alignment.");

                var videoWriter = new Mp4VideoWriter();
                await videoWriter.WriteAsync(syncedVideo, tempVideo, _options);

                var mixedAudio = AudioMixHelpers.MixPcm16(syncedSystem, syncedMic);

                if (mixedAudio.Count > 0 && mixedAudio.Any(p => p.PcmData.Length > 0))
                {
                    WaveFileWriter.WritePcm16Wave(tempAudio, mixedAudio);

                    var mux = new MediaMuxService();
                    await mux.MuxAsync(tempVideo, tempAudio, batch.OutputPath);
                }
                else
                {
                    if (File.Exists(batch.OutputPath))
                        File.Delete(batch.OutputPath);

                    File.Move(tempVideo, batch.OutputPath);
                }

                lock (_gate)
                {
                    _segmentPaths.Add(batch.OutputPath);

                    if (batch.IsReplaySegment)
                    {
                        int maxSegments = Math.Max(1, (int)Math.Ceiling(_replayBufferDuration.TotalSeconds / _segmentDuration.TotalSeconds));
                        while (_segmentPaths.Count > maxSegments)
                        {
                            string oldSegment = _segmentPaths[0];
                            _segmentPaths.RemoveAt(0);

                            try
                            {
                                if (File.Exists(oldSegment))
                                    File.Delete(oldSegment);
                            }
                            catch
                            {
                            }
                        }
                    }
                }

                var elapsed = DateTimeOffset.UtcNow - finalizeStartedUtc;
                await RecorderLog.InfoAsync(nameof(ManualRecordingService),
                    $"Segment finalized. Replay={batch.IsReplaySegment}, ElapsedMs={elapsed.TotalMilliseconds:F0}, Output='{batch.OutputPath}'");

            }
            finally
            {
                if (syncedVideo is not null)
                {
                    foreach (var frame in syncedVideo)
                    {
                        frame.Dispose();
                    }
                }

                foreach (var frame in videoFrames)
                {
                    frame.Dispose();
                }

                batch.VideoBuffer.Dispose();
                batch.SystemAudioBuffer?.Clear();
                batch.MicrophoneAudioBuffer?.Clear();

                try
                {
                    if (File.Exists(tempVideo))
                        File.Delete(tempVideo);
                }
                catch
                {
                }

                try
                {
                    if (File.Exists(tempAudio))
                        File.Delete(tempAudio);
                }
                catch
                {
                }

                if (batch.IsReplaySegment)
                {
                    ReleaseRecorderMemory();
                }
            }
        }

        private static List<AudioPacket>? CloneAudioPackets(IReadOnlyList<AudioPacket>? packets)
        {
            if (packets == null)
                return null;

            return packets.Select(packet => new AudioPacket
            {
                Timestamp = packet.Timestamp,
                PcmData = packet.PcmData.ToArray(),
                SampleRate = packet.SampleRate,
                Channels = packet.Channels,
                BitsPerSample = packet.BitsPerSample,
                FormatTag = packet.FormatTag,
                IsFloatFormat = packet.IsFloatFormat
            }).ToList();
        }

        private static RecordingOptions ApplyReplayPreferences(RecordingOptions options)
        {
            var replayOptions = options.Clone();
            replayOptions.RetrospectiveSeconds = RecordingPreferences.BackgroundReplayDurationSeconds;
            replayOptions.IncludeSystemAudio = RecordingPreferences.IsBackgroundReplaySystemAudioEnabled;
            replayOptions.IncludeMicrophone = RecordingPreferences.IsBackgroundReplayMicrophoneEnabled;
            replayOptions.FrameRate = 120;
            replayOptions.AllowHighFrameRateCapture = true;
            return replayOptions;
        }

        private static void PrepareReplayAudio(
            List<AudioPacket>? systemPackets,
            List<AudioPacket>? micPackets,
            TimeSpan requestedDuration,
            TimeSpan videoStartTimestamp,
            out List<AudioPacket> shiftedSystem,
            out List<AudioPacket> shiftedMic)
        {
            var allPackets = (systemPackets ?? Enumerable.Empty<AudioPacket>())
                .Concat(micPackets ?? Enumerable.Empty<AudioPacket>())
                .ToList();
            if (allPackets.Count == 0)
            {
                shiftedSystem = new List<AudioPacket>();
                shiftedMic = new List<AudioPacket>();
                return;
            }

            TimeSpan duration = requestedDuration <= TimeSpan.Zero || requestedDuration > TimeSpan.FromMinutes(10)
                ? TimeSpan.FromSeconds(RecordingPreferences.BackgroundReplayDurationSeconds)
                : requestedDuration;
            TimeSpan latest = allPackets.Max(packet => packet.Timestamp);
            TimeSpan cutoff = videoStartTimestamp > TimeSpan.Zero
                ? videoStartTimestamp
                : latest - duration;
            TimeSpan end = cutoff + duration;
            var selectedSystem = (systemPackets ?? new List<AudioPacket>())
                .Where(packet => packet.Timestamp >= cutoff && packet.Timestamp < end)
                .ToList();
            var selectedMic = (micPackets ?? new List<AudioPacket>())
                .Where(packet => packet.Timestamp >= cutoff && packet.Timestamp < end)
                .ToList();
            shiftedSystem = AvSyncHelpers.ShiftAudio(selectedSystem, cutoff);
            shiftedMic = AvSyncHelpers.ShiftAudio(selectedMic, cutoff);
        }

        private static List<AudioPacket> BuildExactReplayAudioTrack(
            IReadOnlyList<AudioPacket> mixedAudio,
            TimeSpan requestedDuration)
        {
            TimeSpan duration = requestedDuration <= TimeSpan.Zero || requestedDuration > TimeSpan.FromMinutes(10)
                ? TimeSpan.FromSeconds(RecordingPreferences.BackgroundReplayDurationSeconds)
                : requestedDuration;
            int outputChannels = mixedAudio.Count > 0
                ? mixedAudio[0].Channels
                : AudioMixHelpers.TargetChannels;
            int bytesPerSecond = AudioMixHelpers.TargetSampleRate *
                outputChannels *
                (AudioMixHelpers.TargetBitsPerSample / 8);
            int requiredBytes = checked((int)Math.Round(duration.TotalSeconds * bytesPerSecond));
            byte[] exactPcm = new byte[requiredBytes];

            if (mixedAudio.Count > 0 && mixedAudio[0].PcmData.Length > 0)
            {
                Buffer.BlockCopy(
                    mixedAudio[0].PcmData,
                    0,
                    exactPcm,
                    0,
                    Math.Min(exactPcm.Length, mixedAudio[0].PcmData.Length));
            }

            return new List<AudioPacket>
            {
                new AudioPacket
                {
                    Timestamp = TimeSpan.Zero,
                    PcmData = exactPcm,
                    SampleRate = AudioMixHelpers.TargetSampleRate,
                    Channels = outputChannels,
                    BitsPerSample = AudioMixHelpers.TargetBitsPerSample
                }
            };
        }

        private static void TryDeleteFile(string path)
        {
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch
            {
            }
        }

        private sealed class SegmentBatch
        {
            public string OutputPath { get; }
            public RollingClipBuffer VideoBuffer { get; }
            public RollingAudioBuffer? SystemAudioBuffer { get; }
            public RollingAudioBuffer? MicrophoneAudioBuffer { get; }
            public bool IsReplaySegment { get; }

            public SegmentBatch(
                string outputPath,
                RollingClipBuffer videoBuffer,
                RollingAudioBuffer? systemAudioBuffer,
                RollingAudioBuffer? microphoneAudioBuffer,
                bool isReplaySegment)
            {
                OutputPath = outputPath;
                VideoBuffer = videoBuffer;
                SystemAudioBuffer = systemAudioBuffer;
                MicrophoneAudioBuffer = microphoneAudioBuffer;
                IsReplaySegment = isReplaySegment;
            }
        }

        public async ValueTask DisposeAsync()
        {
            await StopAsync();
        }
    }
}

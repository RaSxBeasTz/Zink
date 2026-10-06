using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Zink.Services.Recording;
using Windows.Storage;
using Windows.System;

namespace Zink.Services.Capture
{
    public sealed class ZinkAreaCaptureService
    {
        public static ZinkAreaCaptureService Instance { get; } = new();

        private Process? _recordingProcess;
        private string? _recordingPath;
        private string? _recordingVideoPath;
        private string? _recordingWavePath;
        private SystemLoopbackCaptureService? _systemAudioCapture;
        private readonly List<AudioPacket> _systemAudioPackets = new();
        private readonly object _audioSync = new();
        private readonly SemaphoreSlim _finishLock = new(1, 1);
        private bool _stopRequested;

        public event EventHandler<string>? StatusChanged;
        public event EventHandler<bool>? RecordingStateChanged;

        public bool IsRecording => _recordingProcess is { HasExited: false };

        public async Task CaptureScreenshotAsync(Rectangle region, int outputWidth, int outputHeight)
        {
            try
            {
                await Task.Delay(220);
                var folder = await GetCapturesFolderAsync();
                var path = Path.Combine(folder.Path, $"Zink Screenshot {DateTime.Now:yyyy-MM-dd HH-mm-ss}.png");

                using var source = new Bitmap(region.Width, region.Height, PixelFormat.Format32bppArgb);
                using (var graphics = Graphics.FromImage(source))
                    graphics.CopyFromScreen(region.Location, Point.Empty, region.Size, CopyPixelOperation.SourceCopy);

                using var output = new Bitmap(outputWidth, outputHeight, PixelFormat.Format32bppArgb);
                using (var graphics = Graphics.FromImage(output))
                {
                    graphics.Clear(Color.Black);
                    graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    var destination = Fit(region.Width, region.Height, outputWidth, outputHeight);
                    graphics.DrawImage(source, destination);
                }
                output.Save(path, ImageFormat.Png);
                Publish($"{outputWidth} × {outputHeight} screenshot saved: {Path.GetFileName(path)}");
            }
            catch (Exception ex)
            {
                Publish($"Screenshot failed: {ex.Message}");
            }
        }

        public async Task StartRecordingAsync(
            Rectangle region,
            int outputWidth,
            int outputHeight,
            bool includeSystemAudio)
        {
            try
            {
                await Task.Delay(220);
                var folder = await GetCapturesFolderAsync();
                _recordingPath = Path.Combine(folder.Path, $"Zink Recording {DateTime.Now:yyyy-MM-dd HH-mm-ss}.mp4");
                _recordingVideoPath = Path.Combine(folder.Path, $".zink-video-{Guid.NewGuid():N}.mp4");
                _recordingWavePath = Path.Combine(folder.Path, $".zink-audio-{Guid.NewGuid():N}.wav");
                var ffmpeg = ResolveFfmpegPath();

                var width = Math.Max(2, region.Width & ~1);
                var height = Math.Max(2, region.Height & ~1);
                const int bitrate = 30_000_000;
                const int maxRate = 45_000_000;
                const int bufferSize = 60_000_000;
                var arguments =
                    $"-hide_banner -loglevel error -y -filter_complex " +
                    $"\"ddagrab=output_idx=0:video_size={width}x{height}:offset_x={region.X}:offset_y={region.Y}:" +
                    $"framerate=120:dup_frames=1:draw_mouse=1,hwdownload,format=bgra," +
                    $"scale={outputWidth}:{outputHeight}:force_original_aspect_ratio=decrease," +
                    $"pad={outputWidth}:{outputHeight}:(ow-iw)/2:(oh-ih)/2:black,format=yuv420p[v]\" " +
                    "-map \"[v]\" " +
                    $"-an -c:v h264_nvenc -preset p1 -tune ull -rc vbr -b:v {bitrate} " +
                    $"-maxrate {maxRate} -bufsize {bufferSize} -g 120 -keyint_min 120 " +
                    "-bf 0 -forced-idr 1 -fps_mode cfr -r 120 -pix_fmt yuv420p -movflags +faststart " +
                    $"\"{_recordingVideoPath}\"";

                lock (_audioSync)
                    _systemAudioPackets.Clear();
                if (includeSystemAudio)
                {
                    _systemAudioCapture = new SystemLoopbackCaptureService();
                    _systemAudioCapture.AudioPacketArrived += SystemAudioCapture_AudioPacketArrived;
                    await _systemAudioCapture.StartAsync(null);
                }

                _recordingProcess = Process.Start(new ProcessStartInfo
                {
                    FileName = ffmpeg,
                    Arguments = arguments,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardInput = true,
                    RedirectStandardError = true
                }) ?? throw new InvalidOperationException("The Zink recorder could not start.");

                _recordingProcess.EnableRaisingEvents = true;
                _recordingProcess.BeginErrorReadLine();
                _stopRequested = false;
                _recordingProcess.Exited += (_, _) =>
                {
                    if (!_stopRequested)
                        _ = FinishRecordingAsync(false);
                };

                RecordingStateChanged?.Invoke(this, true);
                Publish($"Recording area at {outputWidth} × {outputHeight} • Direct3D capture • H.264 NVENC • 120 FPS" +
                        (includeSystemAudio ? " • System audio…" : "…"));
            }
            catch (Exception ex)
            {
                Publish($"Recording failed: {ex.Message}");
                RecordingStateChanged?.Invoke(this, false);
            }
        }

        public async Task StopRecordingAsync()
        {
            if (!IsRecording || _recordingProcess is null)
                return;

            _stopRequested = true;
            await FinishRecordingAsync(true);
        }

        private async Task FinishRecordingAsync(bool requestEncoderStop)
        {
            await _finishLock.WaitAsync();
            try
            {
                var process = _recordingProcess;
                if (process is null)
                    return;

                if (_systemAudioCapture is not null)
                {
                    _systemAudioCapture.AudioPacketArrived -= SystemAudioCapture_AudioPacketArrived;
                    await _systemAudioCapture.StopAsync();
                    _systemAudioCapture = null;
                }

                if (requestEncoderStop && !process.HasExited)
                {
                    await process.StandardInput.WriteLineAsync("q");
                    await process.StandardInput.FlushAsync();
                }
                if (!process.HasExited)
                    await process.WaitForExitAsync();

                await FinalizeRecordingAsync();
                Publish($"Recording saved with {GetCapturedAudioPacketCount()} system-audio packets: " +
                        Path.GetFileName(_recordingPath));
            }
            catch (Exception ex)
            {
                Publish($"Could not finish recording: {ex.Message}");
            }
            finally
            {
                _recordingProcess?.Dispose();
                _recordingProcess = null;
                RecordingStateChanged?.Invoke(this, false);
                _finishLock.Release();
            }
        }

        private int GetCapturedAudioPacketCount()
        {
            lock (_audioSync)
                return _systemAudioPackets.Count;
        }

        private void SystemAudioCapture_AudioPacketArrived(object? sender, AudioPacket packet)
        {
            lock (_audioSync)
                _systemAudioPackets.Add(packet);
        }

        private async Task FinalizeRecordingAsync()
        {
            if (string.IsNullOrWhiteSpace(_recordingVideoPath) ||
                string.IsNullOrWhiteSpace(_recordingPath))
                return;

            List<AudioPacket> audio;
            lock (_audioSync)
                audio = new List<AudioPacket>(_systemAudioPackets);

            if (audio.Count == 0 || string.IsNullOrWhiteSpace(_recordingWavePath))
            {
                File.Move(_recordingVideoPath, _recordingPath, true);
                return;
            }

            var origin = audio.Min(packet => packet.Timestamp);
            var shifted = AudioMixHelpers.ShiftAudio(audio, origin);
            var normalized = AudioMixHelpers.MixPcm16(shifted, null);
            WaveFileWriter.WritePcm16Wave(_recordingWavePath, normalized);

            var mux = Process.Start(new ProcessStartInfo
            {
                FileName = ResolveFfmpegPath(),
                Arguments =
                    $"-hide_banner -loglevel error -y -i \"{_recordingVideoPath}\" " +
                    $"-i \"{_recordingWavePath}\" -map 0:v:0 -map 1:a:0 " +
                    $"-c:v copy -c:a aac -b:a 320k -af apad -shortest " +
                    $"-metadata:s:a:0 title=\"Desktop and game audio\" " +
                    $"-disposition:a:0 default -movflags +faststart \"{_recordingPath}\"",
                UseShellExecute = false,
                CreateNoWindow = true
            }) ?? throw new InvalidOperationException("The system-audio mux could not start.");
            await mux.WaitForExitAsync();
            if (mux.ExitCode != 0)
                throw new InvalidOperationException("The system-audio track could not be added.");
            File.Delete(_recordingVideoPath);
            File.Delete(_recordingWavePath);
        }

        public async Task OpenCapturesFolderAsync()
        {
            var folder = await GetCapturesFolderAsync();
            await Launcher.LaunchFolderAsync(folder);
        }

        private static async Task<StorageFolder> GetCapturesFolderAsync()
        {
            return await KnownFolders.VideosLibrary.CreateFolderAsync(
                "Zink Captures",
                CreationCollisionOption.OpenIfExists);
        }

        private static string ResolveFfmpegPath()
        {
            var bundled = Path.Combine(AppContext.BaseDirectory, "Tools", "ffmpeg.exe");
            if (File.Exists(bundled))
                return bundled;
            throw new FileNotFoundException("The bundled Zink video encoder was not found.", bundled);
        }

        private static Rectangle Fit(int sourceWidth, int sourceHeight, int targetWidth, int targetHeight)
        {
            var scale = Math.Min((double)targetWidth / sourceWidth, (double)targetHeight / sourceHeight);
            var width = (int)Math.Round(sourceWidth * scale);
            var height = (int)Math.Round(sourceHeight * scale);
            return new Rectangle((targetWidth - width) / 2, (targetHeight - height) / 2, width, height);
        }

        private void Publish(string message) => StatusChanged?.Invoke(this, message);
    }
}

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Windows.Media.Playback;
using Windows.Storage;
using Windows.ApplicationModel;
using Windows.ApplicationModel.Activation;
using Microsoft.Windows.AppNotifications;
using AppLifecycleInstance = Microsoft.Windows.AppLifecycle.AppInstance;

using Zink.Services;
using Zink.Services.NativeCalling;
using Zink.Services.Recording;

namespace Zink
{
    public partial class App : Application
    {
        public static MainWindow MainWindow { get; private set; }

        private TrayIconService _trayService;
        private bool _exitRequested;
        private bool _closeChoiceDialogOpen;
        private Microsoft.UI.Windowing.AppWindow? _mainAppWindow;

        private Task? _replayStartupTask;
        private Task _replayCleanupTask = Task.CompletedTask;
        private bool _replayReady;
        private string? _replayStartupError;

        private DateTimeOffset? _replayBufferStartedAtUtc;
        private static TimeSpan RequiredReplayBufferDuration =>
            TimeSpan.FromSeconds(RecordingPreferences.BackgroundReplayDurationSeconds);
        private static readonly TimeSpan VideoLibraryBackgroundScanInterval = TimeSpan.FromMinutes(5);
        private const string BackgroundRunSettingKey = "ZinkBackgroundRunEnabled";
        private const string MainInstanceKey = "Zink.MainInstance";
        private AppLifecycleInstance? _mainInstance;

        public App()
        {
            this.InitializeComponent();
            DiagnosticLogService.InitializeFromSettings();
            DiagnosticLogService.WriteLine("Zink app constructor started.");
            DiagnosticLogService.WriteLaunchContext("constructor");
            HookDiagnosticCrashLogging();

            try
            {
                AppNotificationManager.Default.Register();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("AppNotification Register FAILED: " + ex);
            }

            this.UnhandledException += (s, e) =>
            {
                try
                {
                    DiagnosticLogService.WriteLine("Unhandled exception: " + e.Exception);
                    Directory.CreateDirectory(DiagnosticLogService.LogDirectoryPath);
                    var crashLogPath = Path.Combine(DiagnosticLogService.LogDirectoryPath, $"CrashLog-{DiagnosticLogService.DeviceName}-latest.txt");
                    File.WriteAllText(crashLogPath, e.Exception + Environment.NewLine);
                }
                catch { }
                finally
                {
                    DiagnosticLogService.Flush();
                }

                e.Handled = true;
            };
        }

        private static void HookDiagnosticCrashLogging()
        {
            AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            {
                try
                {
                    DiagnosticLogService.WriteLine("AppDomain unhandled exception: " + e.ExceptionObject);
                }
                catch
                {
                }
                finally
                {
                    DiagnosticLogService.Flush();
                }
            };

            AppDomain.CurrentDomain.FirstChanceException += (_, e) =>
            {
                try
                {
                    // Qualcomm's async H.264 MFT reports E_UNEXPECTED when a
                    // best-effort output probe finds no sample. The encoder handles
                    // that status as "not ready". Writing a full exception and stack
                    // trace for every probe floods the shared diagnostic lock and can
                    // delay RTP/audio callbacks while a screen share is active.
                    if (!IsHandledQualcommOutputNotReady(e.Exception) &&
                        IsScreenShareDiagnosticException(e.Exception))
                        DiagnosticLogService.WriteLine("First-chance screen-share exception: " + e.Exception);
                }
                catch
                {
                }
            };

            TaskScheduler.UnobservedTaskException += (_, e) =>
            {
                try
                {
                    DiagnosticLogService.WriteLine("Unobserved task exception: " + e.Exception);
                }
                catch
                {
                }
                finally
                {
                    DiagnosticLogService.Flush();
                }
            };

            AppDomain.CurrentDomain.ProcessExit += (_, __) =>
            {
                try
                {
                    DiagnosticLogService.WriteLine("Process exit.");
                    ManualRecordingService.Instance.StopAsync().GetAwaiter().GetResult();
                }
                catch
                {
                }
                finally
                {
                    DiagnosticLogService.Flush();
                }
            };
        }

        private static bool IsScreenShareDiagnosticException(Exception exception)
        {
            var typeName = exception.GetType().FullName ?? "";
            var stack = exception.StackTrace ?? "";

            return typeName.Contains("SharpDX", StringComparison.OrdinalIgnoreCase) ||
                typeName.Contains("Vortice", StringComparison.OrdinalIgnoreCase) ||
                stack.Contains("ScreenShare", StringComparison.OrdinalIgnoreCase) ||
                stack.Contains("MediaFoundationH264Encoder", StringComparison.OrdinalIgnoreCase) ||
                stack.Contains("NativePeerConnectionService", StringComparison.OrdinalIgnoreCase) ||
                stack.Contains("SIPSorcery", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsHandledQualcommOutputNotReady(Exception exception)
        {
            const int EUnexpected = unchecked((int)0x8000FFFF);
            return exception is SharpDX.SharpDXException sharpDxException &&
                sharpDxException.ResultCode.Code == EUnexpected;
        }

        protected override async void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
        {
            DiagnosticLogService.EnsureLogFile("Visual Studio/app launch");
            DiagnosticLogService.WriteLaunchContext("OnLaunched", args.Arguments);
            DiagnosticLogService.Flush();
            LocalizationService.InitializeForStartup();

            var currentInstance = AppLifecycleInstance.GetCurrent();
            _mainInstance = AppLifecycleInstance.FindOrRegisterForKey(MainInstanceKey);

            if (!_mainInstance.IsCurrent)
            {
                await _mainInstance.RedirectActivationToAsync(currentInstance.GetActivatedEventArgs());
                Exit();
                return;
            }

            _mainInstance.Activated += MainInstance_Activated;

            _replayCleanupTask = Task.Run(ReplayTemporaryFileCleanup.DeleteContents);
            _ = InitializeAppServicesAsync();

            SetupTray();
            _ = ZinkBackgroundModeService.Instance.ApplyAsync();

            StorageFile fileToOpen = null;
            string pathToOpen = null;
            bool launchedFromStartupTask = IsStartupTaskLaunch();
            bool backgroundRunEnabled = IsBackgroundRunEnabled();
            bool gamingReplayEnabled = RecordingPreferences.IsGamingBackgroundReplayEnabled;

            try
            {
                var activatedArgs = AppLifecycleInstance.GetCurrent().GetActivatedEventArgs();
                if (activatedArgs != null && activatedArgs.Kind == Microsoft.Windows.AppLifecycle.ExtendedActivationKind.File)
                {
                    if (activatedArgs.Data is IFileActivatedEventArgs fileArgs &&
                        fileArgs.Files != null &&
                        fileArgs.Files.Count > 0 &&
                        fileArgs.Files[0] is StorageFile sf)
                    {
                        fileToOpen = sf;
                    }
                }
            }
            catch { }

            if (fileToOpen == null)
            {
                pathToOpen = TryGetMediaPathFromLaunchArgs(args);
            }

            if (fileToOpen != null || !string.IsNullOrWhiteSpace(pathToOpen))
            {
                MainWindow = new MainWindow();
                MainWindow.SuppressInitialNavigation = true;
                AttachMainWindowLifecycle(MainWindow);
                MainWindow.Activate();

                _ = VideoLibraryService.Current.StartAsync(VideoLibraryBackgroundScanInterval);

                if (backgroundRunEnabled && gamingReplayEnabled)
                {
                    _replayStartupTask = DelayedReplayStartupAsync(TimeSpan.FromSeconds(2));
                }

                if (fileToOpen != null)
                    NavigateToActivatedMediaFile(fileToOpen);
                else
                    NavigateToActivatedMediaPath(pathToOpen);

                return;
            }

            if (launchedFromStartupTask)
            {
                MainWindow = new MainWindow();
                AttachMainWindowLifecycle(MainWindow);
                MainWindow.Activate();

                MainWindow.DispatcherQueue.TryEnqueue(() =>
                {
                    try
                    {
                        MainWindow.HideToTray();
                    }
                    catch { }
                });

                _ = VideoLibraryService.Current.StartAsync(VideoLibraryBackgroundScanInterval);

                if (backgroundRunEnabled && gamingReplayEnabled)
                {
                    _replayStartupTask = DelayedReplayStartupAsync(TimeSpan.FromSeconds(25));
                }

                return;
            }

            MainWindow = new MainWindow();
            AttachMainWindowLifecycle(MainWindow);
            MainWindow.Activate();

            _ = VideoLibraryService.Current.StartAsync(VideoLibraryBackgroundScanInterval);

            if (backgroundRunEnabled && gamingReplayEnabled)
            {
                _replayStartupTask = DelayedReplayStartupAsync(TimeSpan.FromSeconds(2));
            }
        }

        private bool IsBackgroundRunEnabled()
        {
            try
            {
                object value = ApplicationData.Current.LocalSettings.Values[BackgroundRunSettingKey];
                if (value is bool boolValue)
                    return boolValue;
            }
            catch
            {
            }

            return true;
        }

        private async Task DelayedReplayStartupAsync(TimeSpan delay)
        {
            try
            {
                await Task.Delay(delay);
                await StartReplayBufferAsync();
            }
            catch (Exception ex)
            {
                _replayStartupError = ex.Message;
                _replayReady = false;
                System.Diagnostics.Debug.WriteLine("Delayed replay startup failed: " + ex);
            }
        }

        public void NotifyReplayBufferStarted()
        {
            _replayReady = true;
            _replayStartupError = null;
            _replayBufferStartedAtUtc = DateTimeOffset.UtcNow;
        }

        public void NotifyReplayBufferStopped()
        {
            _replayReady = false;
            _replayBufferStartedAtUtc = null;
        }

        private bool IsStartupTaskLaunch()
        {
            try
            {
                var activatedArgs = AppLifecycleInstance.GetCurrent().GetActivatedEventArgs();
                if (activatedArgs == null)
                    return false;

                return string.Equals(
                    activatedArgs.Kind.ToString(),
                    "StartupTask",
                    StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        private void SetupTray()
        {
            try
            {
                _trayService = new TrayIconService();
                _trayService.Create();

                _trayService.OpenClicked += TrayService_OpenClicked;
                _trayService.SaveLast45SecondsClicked += TrayService_SaveLast45SecondsClicked;
                _trayService.ExitClicked += TrayService_ExitClicked;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("Tray init failed: " + ex);
            }
        }

        private void TrayService_OpenClicked(object sender, EventArgs e)
        {
            try
            {
                if (MainWindow == null)
                {
                    MainWindow = new MainWindow();
                    AttachMainWindowLifecycle(MainWindow);
                    MainWindow.Activate();
                }

                MainWindow.ShowFromTray();
            }
            catch { }
        }

        private void TrayService_SaveLast45SecondsClicked(object sender, EventArgs e)
        {
            try
            {
                if (MainWindow != null)
                {
                    MainWindow.DispatcherQueue.TryEnqueue(async () =>
                    {
                        await SaveReplayFromTrayAsync();
                    });
                }
                else
                {
                    _ = Task.Run(async () => await SaveReplayFromTrayAsync());
                }
            }
            catch (Exception ex)
            {
                try
                {
                    _trayService?.ShowBalloonTip("Zink Replay Error", ex.Message);
                }
                catch
                {
                }
            }
        }

        private async Task SaveReplayFromTrayAsync()
        {
            try
            {
                if (_replayStartupTask != null)
                {
                    await _replayStartupTask;
                }

                var service = Zink.Services.Recording.ManualRecordingService.Instance;

                if (!RecordingPreferences.IsGamingBackgroundReplayEnabled)
                {
                    _trayService?.ShowBalloonTip(
                        "Zink Replay",
                        "Background replay for gaming is turned off in Settings.");
                    return;
                }

                if (!service.IsReplayBufferRunning)
                {
                    _trayService?.ShowBalloonTip(
                        "Zink Replay Error",
                        "Replay buffer is not running.");
                    return;
                }

                TimeSpan actualBuffered = service.GetReplayBufferedDuration();
                if (actualBuffered < RequiredReplayBufferDuration)
                {
                    int secondsReady = Math.Max(0, (int)actualBuffered.TotalSeconds);

                    _trayService?.ShowBalloonTip(
                        "Zink Replay",
                        $"Replay buffer is still filling: {secondsReady}/{(int)RequiredReplayBufferDuration.TotalSeconds} seconds.");
                    return;
                }

                string savedPath = await service.SaveReplayAsync(RequiredReplayBufferDuration);

                _trayService?.ShowBalloonTip(
                    "Zink Replay",
                    $"Saved: {Path.GetFileName(savedPath)}");
            }
            catch (Exception ex)
            {
                try
                {
                    _trayService?.ShowBalloonTip(
                        "Zink Replay Error",
                        ex.Message);
                }
                catch
                {
                }
            }
        }

        private void TrayService_ExitClicked(object sender, EventArgs e)
        {
            if (_exitRequested)
                return;

            try
            {
                _exitRequested = true;
                DiagnosticLogService.WriteLine("Tray Exit selected; dismissing app and starting full process shutdown.");
                DiagnosticLogService.Flush();

                // Dismiss the app UI first. MainWindow.Close() is deliberately avoided here
                // because its synchronous Closed handler performs slow media cleanup and can
                // leave the visible window frozen while that work completes.
                try
                {
                    if (MainWindow != null)
                    {
                        StopActiveVideoPlayback(MainWindow);
                        MainWindow.HideToTray();
                    }
                }
                catch { }

                // Once the app UI has gone, remove the shell icon and finish service cleanup
                // away from the UI thread before terminating the complete process.
                try
                {
                    _trayService?.Dispose();
                }
                catch { }

                _ = Task.Run(CompleteTrayExitAsync);
            }
            catch
            {
                Environment.Exit(0);
            }
        }

        private async Task StartReplayBufferAsync()
        {
            _replayReady = false;
            _replayStartupError = null;

            try
            {
                await _replayCleanupTask;

                if (!RecordingPreferences.IsGamingBackgroundReplayEnabled)
                {
                    _replayStartupError = "Background replay for gaming is turned off in Settings.";
                    _replayReady = false;
                    return;
                }

                var service = Zink.Services.Recording.ManualRecordingService.Instance;

                if (service.IsReplayBufferRunning)
                {
                    NotifyReplayBufferStarted();
                    return;
                }

                if (service.IsManualRecording)
                {
                    _replayStartupError = "Manual recording is active.";
                    _replayReady = false;
                    return;
                }

                await Task.Delay(1000);

                var options = new Zink.Models.RecordingOptions
                {
                    IncludeSystemAudio = true,
                    IncludeMicrophone = RecordingPreferences.IsBackgroundReplayMicrophoneEnabled,
                    RetrospectiveSeconds = RecordingPreferences.BackgroundReplayDurationSeconds
                };

                await service.StartReplayAsync(options);

                NotifyReplayBufferStarted();

                _trayService?.ShowBalloonTip("Zink", "Replay buffer started");
            }
            catch (Exception ex)
            {
                _replayStartupError = ex.Message;
                _replayReady = false;
                _replayBufferStartedAtUtc = null;

                _trayService?.ShowBalloonTip("Zink Replay Error", ex.Message);
            }
        }

        private async Task InitializeAppServicesAsync()
        {
            try
            {
                await LikedRadioLikesService.Instance.LoadAsync();
            }
            catch { }

            try
            {
                await SpotifyAuthHelper.LoadStoredTokenAsync();
            }
            catch { }

            try
            {
                DiscordPresenceService.Instance.Initialize();
            }
            catch { }
        }

        private Frame GetRootFrame()
        {
            if (MainWindow?.Content is Frame directFrame)
                return directFrame;

            var prop = MainWindow?.GetType().GetProperty("MainFrame");
            if (prop != null)
                return prop.GetValue(MainWindow) as Frame;

            return null;
        }

        private void NavigateToActivatedMediaFile(StorageFile file)
        {
            if (MainWindow == null || file == null)
                return;

            MainWindow.NavigateFromFileActivation(IsMusicFile(file) ? typeof(MusicPlayerPage) : typeof(VideoPlayerPage), file);
        }

        private void NavigateToActivatedMediaPath(string path)
        {
            if (MainWindow == null || string.IsNullOrWhiteSpace(path))
                return;

            MainWindow.NavigateFromFileActivation(IsMusicPath(path) ? typeof(MusicPlayerPage) : typeof(VideoPlayerPage), path);
        }

        private static string TryGetMediaPathFromLaunchArgs(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
        {
            try
            {
                var launchArgument = args?.Arguments;
                var launchMediaPath = TryNormalizeExistingMediaPath(launchArgument);
                if (!string.IsNullOrWhiteSpace(launchMediaPath))
                    return launchMediaPath;
            }
            catch { }

            try
            {
                var commandLineArgs = Environment.GetCommandLineArgs();
                if (commandLineArgs != null)
                {
                    for (int i = 1; i < commandLineArgs.Length; i++)
                    {
                        var candidate = commandLineArgs[i];
                        var mediaPath = TryNormalizeExistingMediaPath(candidate);
                        if (!string.IsNullOrWhiteSpace(mediaPath))
                            return mediaPath;
                    }
                }
            }
            catch { }

            return null;
        }

        private static string TryNormalizeExistingMediaPath(string path)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(path))
                    return null;

                path = path.Trim().Trim('"');
                if (!File.Exists(path))
                    return null;

                return IsMusicPath(path) || IsVideoPath(path) ? path : null;
            }
            catch
            {
                return null;
            }
        }

        private async Task CompleteTrayExitAsync()
        {
            NotifyActiveCallEndedBeforeExit();

            try { VideoLibraryService.Current.Stop(); } catch { }
            try { DiscordPresenceService.Instance.Clear(); } catch { }
            try { DiscordPresenceService.Instance.Shutdown(); } catch { }

            try
            {
                await Zink.Services.Recording.ManualRecordingService.Instance.StopAsync();
            }
            catch { }

            try { ReplayTemporaryFileCleanup.DeleteContents(); } catch { }

            DiagnosticLogService.WriteLine("Tray Exit cleanup completed; terminating process.");
            DiagnosticLogService.Flush();
            Environment.Exit(0);
        }

        private void MainInstance_Activated(
            object? sender,
            Microsoft.Windows.AppLifecycle.AppActivationArguments activatedArgs)
        {
            StorageFile? activatedFile = null;

            try
            {
                if (activatedArgs.Kind == Microsoft.Windows.AppLifecycle.ExtendedActivationKind.File &&
                    activatedArgs.Data is IFileActivatedEventArgs fileArgs &&
                    fileArgs.Files != null &&
                    fileArgs.Files.Count > 0 &&
                    fileArgs.Files[0] is StorageFile file)
                {
                    activatedFile = file;
                }
            }
            catch (System.Runtime.InteropServices.COMException)
            {
            }

            MainWindow?.DispatcherQueue.TryEnqueue(() =>
            {
                if (MainWindow == null)
                    return;

                MainWindow.ShowFromTray();

                if (activatedFile != null)
                {
                    NavigateToActivatedMediaFile(activatedFile);
                }
            });
        }

        private static bool IsMusicFile(StorageFile file)
        {
            var extension = file.FileType ?? "";

            return IsMusicExtension(extension);
        }

        private static bool IsMusicPath(string path)
        {
            return IsMusicExtension(Path.GetExtension(path) ?? "");
        }

        private static bool IsVideoPath(string path)
        {
            var extension = Path.GetExtension(path) ?? "";

            return extension.Equals(".mp4", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".m4v", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".mkv", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".avi", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".mov", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".wmv", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".webm", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsMusicExtension(string extension)
        {
            return extension.Equals(".mp3", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".m4a", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".wav", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".flac", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".aac", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".wma", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".ogg", StringComparison.OrdinalIgnoreCase);
        }

        private void AttachMainWindowLifecycle(MainWindow window)
        {
            window.Closed += OnMainWindowClosed;

            var hwnd = window.GetWindowHandle();
            var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
            _mainAppWindow = Microsoft.UI.Windowing.AppWindow.GetFromWindowId(windowId);
            _mainAppWindow.Closing += OnMainAppWindowClosing;
        }

        private async void OnMainAppWindowClosing(
            Microsoft.UI.Windowing.AppWindow sender,
            Microsoft.UI.Windowing.AppWindowClosingEventArgs args)
        {
            if (_exitRequested)
                return;

            args.Cancel = true;

            if (_closeChoiceDialogOpen)
                return;

            _closeChoiceDialogOpen = true;

            try
            {
                if (MainWindow?.Content is not FrameworkElement root || root.XamlRoot == null)
                    return;

                var message = new StackPanel
                {
                    Spacing = 10,
                    MaxWidth = 430
                };
                message.Children.Add(new TextBlock
                {
                    Text = "Do you want to fully close ZINK and shut it down, or minimize it to the system tray? " +
                    "" +
                    "Shutting down the app takes a few seconds for zink to fully shut down and close the system tray",
                    TextWrapping = TextWrapping.Wrap,
                    FontSize = 15
                });
                message.Children.Add(new TextBlock
                {
                    Text = "Minimizing keeps ZINK available in the background. You can reopen it from the tray icon.",
                    TextWrapping = TextWrapping.Wrap,
                    Opacity = 0.72
                });

                var dialog = new ContentDialog
                {
                    XamlRoot = root.XamlRoot,
                    Title = "Close ZINK?",
                    Content = message,
                    PrimaryButtonText = "Fully close ZINK",
                    SecondaryButtonText = "Minimize the app",
                    DefaultButton = ContentDialogButton.Secondary,
                    Background = new AcrylicBrush
                    {
                        TintColor = global::Windows.UI.Color.FromArgb(255, 24, 31, 42),
                        TintOpacity = 0.78,
                        FallbackColor = global::Windows.UI.Color.FromArgb(255, 24, 31, 42)
                    }
                };

                var result = await dialog.ShowAsync();
                if (result == ContentDialogResult.Primary)
                {
                    _exitRequested = true;
                    StopActiveVideoPlayback(MainWindow);
                    DiagnosticLogService.WriteLine("User chose to fully close ZINK.");
                    DiagnosticLogService.Flush();
                    sender.Destroy();
                }
                else if (result == ContentDialogResult.Secondary)
                {
                    StopActiveVideoPlayback(MainWindow);
                    MainWindow?.HideToTray();
                    DiagnosticLogService.WriteLine("User chose to minimize ZINK to the system tray.");
                    DiagnosticLogService.Flush();
                }
            }
            catch (Exception ex)
            {
                DiagnosticLogService.WriteLine("Unable to show the close choice dialog: " + ex);
                DiagnosticLogService.Flush();
            }
            finally
            {
                _closeChoiceDialogOpen = false;
            }
        }

        private void OnMainWindowClosed(object sender, Microsoft.UI.Xaml.WindowEventArgs e)
        {
            StopActiveVideoPlayback(MainWindow);

            var hasActiveCall = IsActiveCallState(NativeCallCoordinator.Instance.CurrentSession.State);
            DiagnosticLogService.WriteLine($"Main window closing; exitRequested={_exitRequested}; activeCall={hasActiveCall}.");
            DiagnosticLogService.Flush();

            if (!_exitRequested && !hasActiveCall)
            {
                try
                {
                    MainWindow?.HideToTray();
                    DiagnosticLogService.WriteLine("Main window hidden to tray.");
                    DiagnosticLogService.Flush();
                    return;
                }
                catch
                {
                }
            }

            if (!_exitRequested && hasActiveCall)
            {
                DiagnosticLogService.WriteLine("Main window close requested during an active call; leaving the call instead of hiding to tray.");
                DiagnosticLogService.Flush();
            }

            NotifyActiveCallEndedBeforeExit();

            try { VideoLibraryService.Current.Stop(); } catch { }
            try { DiscordPresenceService.Instance.Clear(); } catch { }
            try { DiscordPresenceService.Instance.Shutdown(); } catch { }

            try
            {
                Zink.Services.Recording.ManualRecordingService.Instance.StopAsync().GetAwaiter().GetResult();
            }
            catch { }

            ReplayTemporaryFileCleanup.DeleteContents();

            StopAllMediaPlayers(MainWindow);

            try
            {
                if (_mainAppWindow != null)
                {
                    _mainAppWindow.Closing -= OnMainAppWindowClosing;
                    _mainAppWindow = null;
                }

                if (_trayService != null)
                {
                    _trayService.OpenClicked -= TrayService_OpenClicked;
                    _trayService.SaveLast45SecondsClicked -= TrayService_SaveLast45SecondsClicked;
                    _trayService.ExitClicked -= TrayService_ExitClicked;
                    _trayService.Dispose();
                }
            }
            catch { }

            DiagnosticLogService.WriteLine("Main window closed; exiting process.");
            DiagnosticLogService.Flush();
            Environment.Exit(0);
        }

        private void NotifyActiveCallEndedBeforeExit()
        {
            try
            {
                var session = NativeCallCoordinator.Instance.CurrentSession;
                if (!IsActiveCallState(session.State) || string.IsNullOrWhiteSpace(session.CallId))
                    return;

                var endTask = Task.Run(async () =>
                {
                    try
                    {
                        DiagnosticLogService.WriteLine("Stopping screen share during app exit.");
                        await ScreenShareCapturePipeline.Instance.StopAsync();
                    }
                    catch
                    {
                        DiagnosticLogService.WriteLine("Stopping screen share during app exit failed.");
                    }

                    await NativeCallCoordinator.Instance.EndAsync("closed-app");
                });

                if (!endTask.Wait(TimeSpan.FromMilliseconds(650)))
                {
                    DiagnosticLogService.WriteLine("Timed out quickly while notifying call end during app exit; continuing shutdown.");
                }
                else
                {
                    DiagnosticLogService.WriteLine("Call end notification completed during app exit.");
                }
            }
            catch (Exception ex)
            {
                DiagnosticLogService.WriteLine("Failed to notify call end during app exit: " + ex);
            }
        }

        private static bool IsActiveCallState(NativeCallState state)
        {
            return state == NativeCallState.Calling ||
                state == NativeCallState.Incoming ||
                state == NativeCallState.Accepted ||
                state == NativeCallState.Negotiating ||
                state == NativeCallState.Connected;
        }

        private void StopAllMediaPlayers(Window window)
        {
            if (window.Content is FrameworkElement root)
            {
                StopMediaInElement(root);
            }
        }

        private void StopActiveVideoPlayback(Window window)
        {
            if (window?.Content is FrameworkElement root)
            {
                StopVideoPlaybackInElement(root);
            }
        }

        private void StopVideoPlaybackInElement(FrameworkElement element)
        {
            if (element is VideoPlayerPage videoPlayerPage)
            {
                videoPlayerPage.StopPlaybackForAppClose();
                return;
            }

            int childrenCount = VisualTreeHelper.GetChildrenCount(element);
            for (int i = 0; i < childrenCount; i++)
            {
                if (VisualTreeHelper.GetChild(element, i) is FrameworkElement child)
                {
                    StopVideoPlaybackInElement(child);
                }
            }
        }

        private void StopMediaInElement(FrameworkElement element)
        {
            if (element is MediaPlayerElement mpe && mpe.MediaPlayer != null)
            {
                mpe.MediaPlayer.Pause();
                mpe.MediaPlayer.Dispose();
            }

            int childrenCount = VisualTreeHelper.GetChildrenCount(element);
            for (int i = 0; i < childrenCount; i++)
            {
                if (VisualTreeHelper.GetChild(element, i) is FrameworkElement child)
                {
                    StopMediaInElement(child);
                }
            }
        }
    }
}

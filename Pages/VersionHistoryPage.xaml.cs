using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using System;
using System.Collections.Generic;

namespace Zink.Pages
{
    public sealed partial class VersionHistoryPage : Page
    {
        private readonly Dictionary<string, (string Title, DateTime? Released, string Notes)> _changelog =
            new(StringComparer.OrdinalIgnoreCase)
            {
                ["4.0.9.0"] = (
                    Title: "Version 4.0.9.0",
                    Released: new DateTime(2026, 10, 23),
                    Notes:
@"Version 4.0.9.0
- Added independent tabs for sidebar pages, with right-click opening and page state retained when switching tabs.
- Added Fluent glass tabs and hover thumbnails for app pages and embedded websites.
- Aligned the tab strip backdrop with the sidebar spacing and saved glass colour, including light and dark themes.
- Right-click a Twitch stream link to open it in a separate Twitch tab without replacing the current page.
- Right-click a Twitch tab to open another instance of its current page. Twitch tabs retain their own stream address.
- Added playback-state tracking for muted Twitch streams so automatic pauses during tab switching and window focus changes can be resumed, while keeping manual pause and mute controls."
                ),
                ["4.0.8.0"] = (
                    Title: "Version 4.0.8.0",
                    Released: new DateTime(2026, 10, 18),
                    Notes:
@"Version 4.0.8.0
- Fixed the title bar remaining visible when watching YouTube in full screen in the Zink Connect browser. The title bar now hides in full screen and returns when you exit."
                ),
                ["4.0.6.0"] = (
                    Title: "Version 4.0.6.0",
                    Released: new DateTime(2026, 10, 18),
                    Notes:
@"Version 4.0.6.0
- Added OneDrive video streaming."
                ),
                // 4.0.0.0 is the last major update for a while.
                ["4.0.0.0"] = (
                    Title: "Version 4.0.0.0",
                    Released: new DateTime(2026, 06, 28),
                    Notes:
@"Version 4.0.0.0. 
Version 4.0 is the last major update for a while.
Bug Fixes: 
- Fixed when a song is playing on the music player and you navigate to another page and then back again the music volume would be put back at fifty percent. The volume will now stay the same when navigating pages and returning.
- Fixed a RAM spike for the background recording clip feature so when you record the app will stay under 1GB of ram.
- Fixed a bug in the call page where it would keep showing some one as speaking even when they are not.
- Fixed the play with youtube on the liked songs page where the loading for youtube would take some time.
- Fixed the youtube player for the liked songs play with youtube not playing or getting stuck on loading.
- Prevented accidental YouTube embed-page redirects from causing video player configuration error 153.
- Fixed local MSIX package signing so Windows App Installer can verify the publisher certificate and enable the Install button.
- Fixed when right clicking the system tray 'exit' button will now shut down zink and not freeze.
- Fixed the system tray right click 'exit' zink button so it shuts down the app and the system tray and the background processes on the first right click exit.
- Fixed the non webview radio stations that would stop playing after a few minutes of being opened fixed it so after a while of them playing they would stop playing. The non webview radio stations will now play with out stopping.
- Fixed the widget radio size so it now fits into the widget window and doesn't get cut off.
- Fixed opening movie files directly from File Explorer with Zink so file activation now reaches the video player instead of hanging on the startup loading screen.
- Fixed the startup overlay so it clears immediately when a video or music file is opened directly with Zink.
- Fixed direct-open movie playback hanging on the video player page by keeping Windows-playable AAC and other supported audio streams on the native Windows media path instead of starting the custom FFmpeg audio pipe.
- Fixed slow, laggy, and sometimes unresponsive app behaviour by reducing repeated Home Dashboard Spotify checks and throttling diagnostic log flushing/mirroring.
- Fixed the Video Library file count so it stays on the first calculated total after navigating away and only updates when new files are found.
- Fixed a Zink Connect browser issue where opening a YouTube video in a new window from a playing video could create a blank tab, break the original YouTube tab, or destabilise the browser.
- YouTube new-window requests now open as independent Zink Connect tabs and navigate directly to the requested video page.
- Fixed background game replay storage so Zink continuously deletes the oldest unused temporary video segments, keeps only the active replay window, and clears abandoned replay session folders on launch and exit without affecting saved clips.
- Fixed replay clip saving when Windows Controlled Folder Access protects the Videos folder by completing clips in Zink's local workspace and saving them to Downloads\Zink Captures when Videos\Zink Captures is blocked.

Updated features:
- Updated the Zink video file overlay badge in File Explorer to use the refreshed Zink logo artwork.
- Updated the package certificate to match the app publisher identity and extended the local signing certificate validity to 2046.
- Updated Discord presence text so each Zink page now describes what that page is doing, such as using the Zink Connect browser, watching a video, playing Spotify, playing Zink Radio, using social pages, or playing on cloud gaming pages.

- Improvements: 
- Removed the non working repeat button and replaced it with a newer working version.
- Improved video seeking so playback can continue smoothly while dragging through the timeline.
- Reduced background Video Library scan pressure for large folders by spacing automatic deep rescans further apart.
- Reduced shell/startup memory pressure by decoding large background and startup images at display-friendly sizes.
- Reduced duplicate film-cover lookups and unnecessary artwork work while keeping fallback video thumbnails.
- Improved film cover artwork loading so poster artwork can replace plain video thumbnails when artwork is found.
- Reduced repeated Video Library collection rebuilds while adding films so the page stays more responsive during large imports.
- Improved Made saved films appear faster by loading the library list first, then filling thumbnails, durations, and poster artwork afterward.
- Improved Kept Zink Discord Rich Presence enabled by default while still allowing it to clear properly when the setting is turned off.
- Improved Video Library performance for large film folders by letting the film grid virtualize items instead of creating every film card at once.
- Improved video subtitles so Zink now turns on one subtitle track at a time, supports matching sidecar subtitle files, and prevents duplicated caption text.
- Improved video audio track selection so films with cinema audio tracks can fall back to a Windows-playable audio track when available.
- Improved the naming of the Zink app to be more clear and concise for users.
- Improved web-based Discord presence updates so Spotify, YouTube Music, and Amazon Music say they are playing the current page or track title when the embedded web page title is available.
- Improved video, streaming, social, and cloud gaming web presence updates so Zink can show more specific activity from the active page title when available.
- Improved the packaging setup so Visual Studio local package builds are signed by default while Store upload builds can still opt out when needed.
- Improved Discord Rich Presence so Zink no longer advertises normal app pages as a main Playing activity, helping games and other Discord activities stay more visible.

- New features added:
- Added a feature where the music library page would rename songs to the correct naming so the app can find the information it needs to show the art work for song.
- Added live search to the Music Library by song name, artist, album and filename.
- Added lryics support to the music player.
- Added a newer glass effect to the side bar for a cleaner look.
- Added a repeat button to the music player so you can repeat the current playing song over and over.
- Control and Z is the background recording clip hotkey on the keyboard.
- Added HDR support for the background replay gaming record clips.
- Added support for media paths passed through Explorer command-line launches as well as packaged Windows file activation.
- Added live video scrubbing to the video player so moving the time bar updates the video frame in real time while choosing where to watch.
- Added real timeouts to video and audio metadata probing so slow or unusual movie files cannot leave the app waiting forever before playback.
- Added clearer progress text while films are being added, including how many films have loaded out of the total.
- Added a second loading phase for cover artwork so Zink shows progress while adding cover artwork for the films.
- Added film poster artwork to the Video Library so film cards can show title poster art instead of only video thumbnails.
- Added Duplicate Video Searcher to the Video section in the sidebar so duplicate videos in the Zink Video Library can be found in one place.
- Added duplicate video results grouped by matching video name and file size, with quick actions to play each video.
- Added Open file location for duplicate videos so Zink opens the folder containing the selected file and highlights it when Windows allows.
- Added a delete action for duplicate videos, including a custom Zink confirmation UI before removing the file.
- Added a Delete button to duplicate video results so unwanted copies can be removed directly from the Duplicate Video Searcher page.
- Added a custom Zink deletion confirmation after a duplicate video has been deleted.
- Added search to the Video Library page so films can be found by name, file type, folder, and duration.
- Added Zink video file thumbnails so videos opened by default with Zink can show a video-style Zink thumbnail in Windows.
- Added a Zink Connect setting to remember website sign-ins, saved passwords, cookies, and cookie preferences across app restarts, updates, and reinstalls when local app data is kept.
- Added scrolling to the Zink Connect settings page so every browser setting remains reachable on smaller windows.
- Added clearer Discord presence labels for Video Library, Duplicate Video Searcher, Radio Discovery, Equalizer, Visualizer, Widget Hub, Settings, About, and other sidebar pages.
- Added a compatibility check for the gaming background recording so the Zink app will detect what's best for your system for recording settings and this inculdes 1080P at 60FPS  with HDR support as normal for lower ends devices and 4K 120FPS with HDR support for higher end devices.
- Added Moved the zink app from .net 8 to .net 10 for better performance and stability.
- Added a close or minimize dialog when you click the close button on the title bar, so you can choose to close the zink app or minimize the Zink app to the system tray..
- Added automatic app-language detection so Zink follows the language selected in Windows by default.
- Added a manual app-language selector in Settings so users can choose a different language and apply it across Zink's pages, buttons, dialogs, menus, and secondary windows.
- Added live language search in Settings so languages can be found by language name, native name, country, regional variant, or language code."
                ),
                // 3.0.33.0
                ["3.0.33.0"] = (
                    Title: "Version 3.0.33.0",
                    Released: new DateTime(2026, 05, 24),
                    Notes:
@"Version 3.0.33.0
- Turned the Visualizer into a working live audio visualizer using real system output audio instead of simulated demo data.
- Added new visualizer designs: Mirror bars, Dots, Tunnel, and Pulse, alongside the existing Bars, Wave, and Circle styles.
- Added colour themes for the visualizer: Sky, Fire, Neon, Ocean, and Mono.
- Added real 0% to 100% Sensitivity and Smoothing sliders with exact percentage labels.
- Added saved visualizer settings so selected design, colour, sensitivity, and smoothing are restored after navigating away and back.
- Added Save and Reset buttons for visualizer settings.
- Improved the visualizer drawing pipeline with smoother live levels, waveform samples, pulse response, and ambient background rendering.
- Kept the Zink Connect page layout focused on the main browser launch card."
                ),
                // 3.0.0.0 (NEW)
                ["3.0.0.0"] = (
                    Title: "Version 3.0.0.0",
                    Released: new DateTime(2026, 05, 03),
                    Notes:
@"Version 3.0.0.0
- Rebranded the app to Zink across the visible product experience.
- Revamped the home dashboard with a new glass-style Zink identity, stronger quick actions, social pulse shortcuts, and clearer media, calling, screen sharing, gaming, and diagnostics sections.
- Added background mode controls so Zink can keep running when closed, with notification settings that are separate from startup behaviour.
- Improved Zink Connect with locked social navigation, safer registration flow, browsing history, a close-data clearing setting, call feedback surfaces, and richer support-report actions.
- Added the Zink Connect browser window and ad-block engine for a cleaner in-app web experience.
- Stabilised native calling and screen sharing with safer ARM64 startup, automatic Windows Graphics Capture startup, fail-safe diagnostics uploads, crash breadcrumbs, screen-share feedback reports, and smoother receiver playback when using fullscreen.
- Improved 720p60 screen sharing with faster WGC scaling, reduced sender preview load, stronger RTP connection fallback behaviour, and multiple fullscreen exit/control fixes.
- Added Zink health diagnostics reports, diagnostics upload support, safer health checks, and stronger support-ready logging.
- Reworked the screen recorder with a refreshed UI, direct manual capture, improved frame output, lower memory pressure, stronger capture-source handling, and bundled FFmpeg/FFprobe support.
- Added Twitch streaming support, including the Streaming page, native Twitch streaming service, and OBS-style streaming pipeline.
- Added the Spotify Beta page and improved Spotify authentication and now-playing surfaces.
- Added real FPS overlay support with RTSS tooling and improved FPS monitor snapshots.
- Revamped theme customization with glass tinting controls and fixed title bar glass styling while hiding the unwanted title bar logo.
- Added more UK radio stations, including Heart Milton Keynes, and updated radio search support.
- Fixed video fullscreen overlay behaviour and improved video player fullscreen controls.
- Fixed light theme sidebar readability.
- Removed the Feedback page from the main navigation while keeping support and review flows available elsewhere.
- Hardened Store submission readiness with packaging, manifest, build, certificate, and store submission note updates.
- Updated package settings, build scripts, README documentation, diagnostics logging, and Fluent glass branding assets for the Zink 3 milestone."
                ),

                // 2.4.1.0
                ["2.4.1.0"] = (
    Title: "Version 2.4.1.0",
    Released: new DateTime(2026, 02, 11),   // <-- set build date here
    Notes:
@"Version 2.4.1.0
- Added Twitch.tv to the social tab.
- Added twitch to the power tools section on the homedashboard page.
- Added a new customisation page.
- Added a new home dashboard customisation page where you can edit all of the things on the dashboard to your own liking
- Added an app customisation theme control page where you can change the theme of Zink from dark to light or from light to dark or even set it to your windows theme.
- Updated the leave a review page.
- Added a new pop up message for the video library page.
- Added a new pop up message for the music library page.
- Added a search button to the sidebar
- Added a search page after you click the search button to search on Zink.
- Added a photo viewer page to zink."
),

                // 2.3.5 (NEW)
                ["2.3.5.0"] = (
                    Title: "Version 2.3.5.0",
                    Released: new DateTime(2026, 01, 16),
                    Notes:
@"Added a new home dashboard page which is the new landing page when opening the zink app. Which should help new users get started with the zink app.
Added power tools with quick access so you can find the most used tools in one place. Which contains Youtube, spotify, radio, discord, video player, music player, settings, equalizer, visualizer, video library, music library, and version history.
Added insights to what you use the most in zink.
Added recent activity to the dashboard.
Added a card at the top of the home dashboard so you can open a music or video file.
Added a card a the top of the home dashboard which lets you resume your last played music or video file.
Added a card at the top of the home dashboard so you can open a folder containing music or video files.
Fixed a bug with the navigation of the sidebar where sometimes the selected item wouldn't match the current page.
Added artist pictures to the now palying card on the home dashboard."
                ),

                // 2.2.8 (NEW)
                ["2.2.8.0"] = (
                    Title: "Version 2.2.8.0",
                    Released: new DateTime(2026, 01, 12),
                    Notes:
@"Added support for ARM Systems.
Added subtitles to the video player so when the subtitles button is clicked you can turn on subtitles for videos/films.
Added a progress bar to the video player so you can move the time of a video to any point to pick where you want to watch.
Added time progression in the video player so you can see how much time has passed and how much time is left in a video.
Added new button designs to the video player for a better user experience.
Added the .net 8 runtime so it won't be asked for new installs from the microsoft store.
Moved the video player and library buttons from the films section to its own video section in the sidebar for easier access."
                ),

                // 2.1.6
                ["2.1.6.0"] = (
                    Title: "Version 2.1.6.0",
                    Released: new DateTime(2025, 11, 25), // today
                    Notes:
@"Fixed a bug where the app wouldn't launch on Windows 10 for x32 and x64 bit systems."
                ),

                // 2.1.5
                ["2.1.5.0"] = (
                    Title: "Version 2.1.5.0",
                    Released: new DateTime(2025, 11, 25), // today
                    Notes:
@"Added support for Windows 10 for both x32 & x64 systems.
Updated the apps runtime version from 1.7 to 1.8 for better performance and stability.
Added a like song button to the radio player page where it lets you like songs while listening to a radio station.
Added a radio songs page where you can see all the songs you've liked when you've listened to a radio station and liked the songs."
                ),

                // 2.0
                ["2.0.0.0"] = (
                    Title: "Version 2.0.0.0",
                    Released: new DateTime(2025, 9, 5),
                    Notes:
@"The Bugs in the Zink app that have been fixed in the new update. --
(Sorry for the delay in fixing this bug, it was a big one!) 
- Finally fixed the bug where the BBC radio stations would close before the player opened. The BBC radio stations will now open for sign-in and playback without closing early.
- Fixed a bug where audio would continue to play on any audio page after Zink was shut. Audio will now stop playing when Zink is closed.
- Fixed a bug where YouTube wasn't going into fullscreen mode when clicking the fullscreen button. YouTube videos will now go fullscreen correctly.
- Fixed a bug where TikTok videos were muted by default. Videos will now play with sound by default.
- Fixed a bug where starting audio (like a video on the YouTube page) and navigating to another page would keep playing the audio. Now it stops correctly.
- Fixed a bug where any radio playing on the Radio page stopped incorrectly when navigating away.
- Fixed a bug where audio playback didn't stop after leaving the Music Player page.
- Fixed a bug where playing a song and then going to the Music Library page to play another song wouldn't overwrite the previous track. The new song will now replace the old one.
- Fixed a bug where Zink wasn't shutting down properly when the close button was clicked. Zink will now shut down cleanly.
+ Scroll down further to see the new features and changes in this version of Zink.

The New features that have been added to the Zink App. --
- Added a new simple sidebar.
- Added the Video Library page. You can now view your videos when you import them into the Video Library page. 
- Added thumbnails to the Video Library page. You can now see thumbnails for your videos.
- Added support for clicking to play videos in the Video Library page. You can now click a thumbnail or the 'Play with Video Player' button.
- Added MP4 file support to the Music Player page. You can now play MP4 files.
- Added M4A file support to the Music Player page.
- Added Greatest Hits Radio to the Radio page.
- Added BBC Radio 1 Xtra to the Radio page.
- Added a loading section for YouTube on the YouTube page to help while waiting.
- Added the Hits Radio image to the Radio page.
- Added notifications to the Music Player page when a new station starts playing.
- Added a new Radio Widget page. You can now listen to the radio in a small window that shows the current station, song, and artist live with images.
- Added GEM 106, Premier Christian Radio, BBC Radio Derby, Jazz FM, MKFM, Capital Xtra, Radio Essex, Magic Radio, and talkSPORT to the Radio page.
- Added YouTube Music and Amazon Music to the Music section.
- Added BBC iPlayer and My5 to the TV section.
- Added Netflix, Amazon Prime Video, Disney Plus, Paramount Plus, and Now TV to the Films section.
- Added GeForce Now, Amazon Luna, Boosteroid, and Shadow PC to the Gaming section.
- Added X (formerly Twitter), Facebook, Telegram Web, WhatsApp Web, Messenger, LinkedIn, Threads, Bluesky, Mastodon (any instance), Pinterest, Tumblr, and Reddit to the Social section.

The Changed features in the Zink App --
- Removed the music library section from the Music Player page as it has been replaced with the new Music Library page."
                ),

                // 1.0 (set to 24/07/2025)
                ["1.0.0.0"] = (
                    Title: "Version 1.0.0.0",
                    Released: new DateTime(2025, 7, 24),
                    Notes:
@"Initial release of Zink.
• Core music, radio, and video foundations.
• First sidebar layout and navigation.
• Basic player controls and page structure."
                ),
            };

        public VersionHistoryPage()
        {
            this.InitializeComponent();
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);

            var requested = e?.Parameter as string;

            if (string.IsNullOrWhiteSpace(requested) || !_changelog.ContainsKey(requested))
                requested = PickLatestVersionFallback();

            RenderVersion(requested!);
        }

        private string PickLatestVersionFallback()
        {
            string latest = null;
            Version best = null;

            foreach (var key in _changelog.Keys)
            {
                if (Version.TryParse(key, out var v))
                {
                    if (best == null || v > best)
                    {
                        best = v;
                        latest = key;
                    }
                }
                else if (latest == null)
                {
                    latest = key;
                }
            }

            // ? FIX: must return a key that actually exists in _changelog
            return latest ?? "2.2.8.0";
        }

        private void RenderVersion(string versionKey)
        {
            var data = _changelog[versionKey];

            PageTitle.Text = "Version History";
            VersionHeader.Text = data.Title;

            if (data.Released.HasValue)
            {
                ReleaseDateText.Text = $"Released: {data.Released.Value:dd MMM yyyy}";
                ReleaseDateText.Visibility = Visibility.Visible;
            }
            else
            {
                ReleaseDateText.Text = "";
                ReleaseDateText.Visibility = Visibility.Collapsed;
            }

            NotesText.Text = string.IsNullOrWhiteSpace(data.Notes)
                ? "No notes available yet."
                : data.Notes;
        }

        private void BackButton_Click(object sender, RoutedEventArgs e)
        {
            if (Frame.CanGoBack)
                Frame.GoBack();
            else
                Frame.Navigate(typeof(AboutPage));
        }
    }
}

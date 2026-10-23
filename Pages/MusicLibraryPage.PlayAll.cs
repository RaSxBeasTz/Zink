using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Windows.Storage;

namespace Zink.Pages
{
    public sealed partial class MusicLibraryPage
    {
        private bool _playAllDialogOpen;

        private sealed record PlaybackChoice(string Name, string Description);
        private sealed record PlaybackMetadata(SongInfo Song, DateTimeOffset Created, uint Year,
            double Rating, uint Disc, uint Track, TimeSpan Duration);

        private async Task ShowPlayAllOptionsAsync()
        {
            var root = GetDialogXamlRoot();
            if (_playAllDialogOpen || root == null) return;
            _playAllDialogOpen = true;
            try
            {
                var choices = new[]
                {
                    new PlaybackChoice("Library order", "Play every song in the library's current order."),
                    new PlaybackChoice("Shuffle everything", "A fresh random order, with every song played once."),
                    new PlaybackChoice("Newest files first", "Start with the most recently created files in your music folder."),
                    new PlaybackChoice("Best-ranked songs first", "Highest saved file ratings first. Unrated songs come last."),
                    new PlaybackChoice("Newest releases first", "Newest release year in the music tags first. Missing years come last."),
                    new PlaybackChoice("Best artists first", "Artists with the highest average saved song rating first. Unrated artists come last."),
                    new PlaybackChoice("Album journey", "Group by artist and album, then follow disc and track numbers."),
                    new PlaybackChoice("Artist mix", "Rotate between artists for variety, shuffling each artist's songs."),
                    new PlaybackChoice("Classics first", "Oldest release year first. Missing years come last."),
                    new PlaybackChoice("Short and sweet", "Shortest songs first. Songs with unknown lengths come last."),
                    new PlaybackChoice("Long listening", "Longest songs first. Songs with unknown lengths come last."),
                    new PlaybackChoice("Songs A–Z", "Play alphabetically by song title.")
                };
                var picker = new ComboBox
                {
                    ItemsSource = choices, DisplayMemberPath = nameof(PlaybackChoice.Name),
                    SelectedIndex = 0, HorizontalAlignment = HorizontalAlignment.Stretch,
                    Header = "Choose your listening order"
                };
                var description = new TextBlock { Text = choices[0].Description,
                    TextWrapping = TextWrapping.Wrap, Opacity = 0.75 };
                picker.SelectionChanged += (_, _) =>
                    description.Text = (picker.SelectedItem as PlaybackChoice)?.Description ?? "";
                var status = new TextBlock { TextWrapping = TextWrapping.Wrap };
                var progress = new ProgressRing { Width = 24, Height = 24,
                    Visibility = Visibility.Collapsed, HorizontalAlignment = HorizontalAlignment.Left };
                var panel = new StackPanel { Spacing = 14, MinWidth = 280, MaxWidth = 420 };
                panel.Children.Add(new TextBlock { Text = $"Play all {Songs.Count} songs",
                    FontSize = 18, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
                panel.Children.Add(picker);
                panel.Children.Add(description);
                panel.Children.Add(status);
                panel.Children.Add(progress);
                var dialog = new ContentDialog
                {
                    Title = "Make it your mix", Content = panel, XamlRoot = root,
                    PrimaryButtonText = "Play all", CloseButtonText = "Cancel",
                    DefaultButton = ContentDialogButton.Primary, IsPrimaryButtonEnabled = Songs.Count > 0
                };
                List<SongInfo> queue = null;
                bool preparing = false;
                dialog.Closing += (_, args) => { if (preparing) args.Cancel = true; };
                dialog.PrimaryButtonClick += async (_, args) =>
                {
                    var deferral = args.GetDeferral();
                    preparing = true;
                    picker.IsEnabled = false;
                    dialog.IsPrimaryButtonEnabled = false;
                    progress.Visibility = Visibility.Visible;
                    progress.IsActive = true;
                    status.Text = "Preparing your listening order…";
                    try
                    {
                        // Snapshot the full library; search and library display order stay unchanged.
                        queue = await BuildPlayAllQueueAsync(Songs.ToList(), picker.SelectedIndex);
                        if (queue.Count == 0)
                        {
                            args.Cancel = true;
                            status.Text = "Your library is empty. Add a music folder first.";
                        }
                    }
                    catch
                    {
                        args.Cancel = true;
                        status.Text = "Could not prepare the queue. Please try again.";
                    }
                    finally
                    {
                        progress.IsActive = false;
                        progress.Visibility = Visibility.Collapsed;
                        picker.IsEnabled = true;
                        dialog.IsPrimaryButtonEnabled = Songs.Count > 0;
                        preparing = false;
                        deferral.Complete();
                    }
                };
                if (await dialog.ShowAsync() != ContentDialogResult.Primary || queue == null || queue.Count == 0)
                    return;
                LibraryData.PlaySongList(queue);
                var main = (MainWindow)App.MainWindow;
                main.ContentFrame.Navigate(typeof(MusicPlayerPage), "autoplay");
                var playerItem = main.SidebarNav.MenuItems.OfType<NavigationViewItem>()
                    .FirstOrDefault(i => (string)i.Tag == "MusicPlayer");
                if (playerItem != null) main.SidebarNav.SelectedItem = playerItem;
            }
            finally { _playAllDialogOpen = false; }
        }

        private static async Task<List<SongInfo>> BuildPlayAllQueueAsync(List<SongInfo> songs, int mode)
        {
            if (mode == 0) return songs;
            if (mode == 1) return ShufflePlayAll(songs);
            if (mode == 11) return songs.OrderBy(s => s.Title, StringComparer.OrdinalIgnoreCase).ToList();
            if (mode == 7)
            {
                var artists = ShufflePlayAll(songs).GroupBy(s => s.Artist?.Trim() ?? "",
                    StringComparer.OrdinalIgnoreCase).Select(g => new Queue<SongInfo>(g)).ToList();
                var mix = new List<SongInfo>();
                while (artists.Any(g => g.Count > 0))
                    foreach (var artist in artists.Where(g => g.Count > 0)) mix.Add(artist.Dequeue());
                return mix;
            }

            var metadata = new List<PlaybackMetadata>();
            foreach (var song in songs)
                metadata.Add(await ReadPlayAllMetadataAsync(song, mode));
            IEnumerable<PlaybackMetadata> ordered = mode switch
            {
                2 => metadata.OrderByDescending(m => m.Created),
                3 => metadata.OrderByDescending(m => m.Rating),
                4 => metadata.OrderByDescending(m => m.Year),
                5 => metadata.GroupBy(m => m.Song.Artist?.Trim() ?? "", StringComparer.OrdinalIgnoreCase)
                    .OrderByDescending(g => string.IsNullOrWhiteSpace(g.Key) ? 0 :
                        g.Where(m => m.Rating > 0).Select(m => m.Rating).DefaultIfEmpty(0).Average())
                    .SelectMany(g => g.OrderByDescending(m => m.Rating)),
                6 => metadata.OrderBy(m => m.Song.Artist, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(m => m.Song.Album, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(m => m.Disc == 0 ? uint.MaxValue : m.Disc)
                    .ThenBy(m => m.Track == 0 ? uint.MaxValue : m.Track)
                    .ThenBy(m => m.Song.Title, StringComparer.OrdinalIgnoreCase),
                8 => metadata.OrderBy(m => m.Year == 0 ? uint.MaxValue : m.Year),
                9 => metadata.OrderBy(m => m.Duration > TimeSpan.Zero ? m.Duration : TimeSpan.MaxValue),
                10 => metadata.OrderByDescending(m => m.Duration),
                _ => metadata
            };
            return ordered.Select(m => m.Song).ToList();
        }

        private static List<SongInfo> ShufflePlayAll(List<SongInfo> songs)
        {
            var result = songs.ToList();
            for (var i = result.Count - 1; i > 0; i--)
            {
                var j = Random.Shared.Next(i + 1);
                (result[i], result[j]) = (result[j], result[i]);
            }
            return result;
        }

        private static async Task<PlaybackMetadata> ReadPlayAllMetadataAsync(SongInfo song, int mode)
        {
            DateTimeOffset created = DateTimeOffset.MinValue;
            uint year = 0, disc = 0, track = 0;
            double rating = 0;
            TimeSpan.TryParseExact(song.Duration, @"mm\:ss", null, out var duration);
            try
            {
                var file = await StorageFile.GetFileFromPathAsync(song.FilePath);
                if (mode == 2) created = file.DateCreated;
                if (mode == 3 || mode == 5)
                {
                    var properties = await file.Properties.RetrievePropertiesAsync(new[] { "System.Rating" });
                    if (properties.TryGetValue("System.Rating", out var value) && value != null)
                        rating = Convert.ToDouble(value);
                }
                if (mode == 4 || mode == 6 || mode == 8 || mode == 9 || mode == 10)
                {
                    // Read tags only for the chosen order, without modifying files or import behavior.
                    using var stream = await file.OpenStreamForReadAsync();
                    using var tags = TagLib.File.Create(new StreamFileAbstraction(file.Name, stream, stream));
                    year = tags.Tag.Year;
                    disc = tags.Tag.Disc;
                    track = tags.Tag.Track;
                    duration = tags.Properties.Duration;
                }
            }
            catch
            {
                // Missing metadata stays at the end; keep every song in the queue.
            }
            return new PlaybackMetadata(song, created, year, rating, disc, track, duration);
        }
    }
}

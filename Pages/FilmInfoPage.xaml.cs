using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using System;
using Zink.Services;

namespace Zink.Pages
{
    public sealed partial class FilmInfoPage : Page
    {
        private VideoItem? _film;

        public FilmInfoPage()
        {
            InitializeComponent();
        }

        protected override async void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);

            _film = e.Parameter as VideoItem;
            if (_film == null)
            {
                ShowMissingFilm();
                return;
            }

            PosterTitleText.Text = _film.Name;
            PosterFileText.Text = _film.FileName;
            PosterImage.Source = _film.Thumbnail;
            TitleText.Text = _film.Name;
            ReleaseDateText.Text = _film.ReleaseDateText;

            try
            {
                var info = await FilmInfoService.GetFilmInfoAsync(_film.Name);
                LoadingPanel.Visibility = Visibility.Collapsed;

                if (info == null)
                {
                    OverviewText.Text = "Zink could not find film details online for this title.";
                    return;
                }

                TitleText.Text = string.IsNullOrWhiteSpace(info.Title) ? _film.Name : info.Title;
                ReleaseDateText.Text = !string.IsNullOrWhiteSpace(info.ReleaseDateText)
                    ? info.ReleaseDateText
                    : _film.ReleaseDateText;

                _film.ReleaseDateText = ReleaseDateText.Text;
                OverviewText.Text = string.IsNullOrWhiteSpace(info.Overview)
                    ? "Zink found this film online, but there was no summary available."
                    : info.Overview;

                if (!string.IsNullOrWhiteSpace(info.SourceUrl) &&
                    Uri.TryCreate(info.SourceUrl, UriKind.Absolute, out var sourceUri))
                {
                    SourceButton.NavigateUri = sourceUri;
                    SourceButton.Content = string.IsNullOrWhiteSpace(info.SourceName)
                        ? "Open source"
                        : $"Open {info.SourceName}";
                    SourceButton.Visibility = Visibility.Visible;
                }
            }
            catch
            {
                LoadingPanel.Visibility = Visibility.Collapsed;
                OverviewText.Text = "Zink could not load film details right now. Please check your internet connection and try again.";
            }
        }

        private void BackButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (Frame.CanGoBack)
                    Frame.GoBack();
                else
                    Frame.Navigate(typeof(VideoLibraryPage));
            }
            catch { }
        }

        private void ShowMissingFilm()
        {
            LoadingPanel.Visibility = Visibility.Collapsed;
            TitleText.Text = "Film info";
            OverviewText.Text = "Zink could not open details for this film.";
        }
    }
}

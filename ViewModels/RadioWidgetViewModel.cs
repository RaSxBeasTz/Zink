using System;
using System.ComponentModel;
using Microsoft.UI.Dispatching;
using Zink.Services;

namespace Zink.ViewModels
{
    public sealed class RadioWidgetViewModel : INotifyPropertyChanged
    {
        private readonly AppPlaybackService _s = AppPlaybackService.Instance;
        private readonly DispatcherQueue _ui;
        private Uri? _lastArtworkOrLogo;

        public RadioWidgetViewModel()
        {
            // Capture the UI thread's DispatcherQueue
            _ui = DispatcherQueue.GetForCurrentThread();
            _lastArtworkOrLogo = _s.ArtworkOrLogo;

            // Forward service changes to the ViewModel, but marshal to UI thread
            _s.PropertyChanged += (_, e) =>
            {
                if (e is null) return;

                if (e.PropertyName == nameof(AppPlaybackService.ArtworkOrLogo))
                {
                    var artworkOrLogo = _s.ArtworkOrLogo;
                    if (Equals(_lastArtworkOrLogo, artworkOrLogo))
                        return;

                    _lastArtworkOrLogo = artworkOrLogo;
                }

                if (_ui?.HasThreadAccess == true)
                {
                    PropertyChanged?.Invoke(this, e);
                }
                else
                {
                    _ui?.TryEnqueue(() => PropertyChanged?.Invoke(this, e));
                }
            };
        }

        public string StationTitle => _s.StationTitle;
        public string ArtistName => _s.ArtistName;
        public string SongTitle => _s.SongTitle;
        public TimeSpan Elapsed => _s.Elapsed;
        public TimeSpan? Duration => _s.Duration;
        public Uri? ArtworkOrLogo => _s.ArtworkOrLogo;
        public bool IsPlaying => _s.IsPlaying;

        public event PropertyChangedEventHandler? PropertyChanged;
    }
}

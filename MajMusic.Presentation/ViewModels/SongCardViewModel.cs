#nullable enable

using System.ComponentModel;
using Avalonia.Media.Imaging;
using MajMusic.Core.Models;

namespace MajMusic.Presentation.ViewModels
{
    /// <summary>Retains one music card and a shared thumbnail lease while its control is attached.</summary>
    public sealed class SongCardViewModel : INotifyPropertyChanged, IDisposable
    {
        /// <summary>Owns shared bounded thumbnail requests.</summary>
        private readonly CoverThumbnailCache _cache;
        /// <summary>Stores the current immutable row metadata.</summary>
        private MusicTrack _track;
        /// <summary>Retains the decoded bitmap until every attached control releases it.</summary>
        private CoverThumbnailCache.Lease? _lease;
        /// <summary>Cancels only this card's interest in a shared request.</summary>
        private CancellationTokenSource? _activation;
        /// <summary>Tracks the currently observed activation.</summary>
        private Task _activationTask = Task.CompletedTask;
        /// <summary>Counts controls currently displaying this stable card instance.</summary>
        private int _attachments;
        /// <summary>Rejects a result belonging to an older descriptor or attachment.</summary>
        private int _generation;
        /// <summary>Stores the current selection highlight.</summary>
        private bool _isSelected;
        /// <summary>Rejects activation after the card leaves the owning view.</summary>
        private bool _disposed;

        /// <summary>Creates a card backed by its owning view's shared thumbnail cache.</summary>
        /// <param name="track">The music entry displayed by this card.</param>
        /// <param name="cache">The shared thumbnail owner.</param>
        internal SongCardViewModel(MusicTrack track, CoverThumbnailCache cache)
        {
            _track = track;
            _cache = cache;
        }

        /// <summary>Occurs when card metadata, selection or its thumbnail changes.</summary>
        public event PropertyChangedEventHandler? PropertyChanged;
        /// <summary>Gets current music metadata independently of playback.</summary>
        public MusicTrack Track => _track;
        /// <summary>Gets the shared decoded thumbnail, or null for an empty placeholder.</summary>
        public Bitmap? Cover => _lease?.Bitmap;
        /// <summary>Gets whether this card represents the selected music entry.</summary>
        public bool IsSelected
        {
            get => _isSelected;
            internal set
            {
                if (_isSelected != value)
                {
                    _isSelected = value;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
                }
            }
        }

        /// <summary>Requests only the cover while a control displays this card.</summary>
        /// <returns>A task completing when this activation has published or discarded its thumbnail.</returns>
        public Task ActivateAsync()
        {
            if (_disposed)
            {
                return Task.CompletedTask;
            }

            _attachments++;
            if (_attachments == 1)
            {
                StartThumbnail();
            }

            return _activationTask;
        }

        /// <summary>Releases one visible control without canceling another card's shared cover request.</summary>
        public void Deactivate()
        {
            if (_attachments > 0 && --_attachments == 0)
            {
                StopThumbnail();
            }
        }

        /// <summary>Updates metadata while retaining a bitmap whose cover descriptor is unchanged.</summary>
        /// <param name="track">The current entry with matching card ownership.</param>
        internal void UpdateTrack(MusicTrack track)
        {
            var changedCover = CoverThumbnailCache.Identity(_track) != CoverThumbnailCache.Identity(track);
            _track = track;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Track)));
            if (changedCover && _attachments > 0)
            {
                StartThumbnail();
            }
        }

        /// <summary>Releases all attached-control interest when this card leaves the view model.</summary>
        public void Dispose()
        {
            _disposed = true;
            _attachments = 0;
            StopThumbnail();
        }

        /// <summary>Starts one new descriptor request while retaining the preceding bitmap until replacement.</summary>
        private void StartThumbnail()
        {
            _activation?.Cancel();
            var cancellation = new CancellationTokenSource();
            _activation = cancellation;
            var generation = ++_generation;
            _activationTask = LoadThumbnailAsync(_track, generation, cancellation);
        }

        /// <summary>Publishes a requested bitmap only while the same attached card still owns the result.</summary>
        /// <param name="track">The captured cover descriptor.</param>
        /// <param name="generation">The captured activation generation.</param>
        /// <param name="cancellation">The interest owned by this activation.</param>
        /// <returns>A task representing the observed shared request.</returns>
        private async Task LoadThumbnailAsync(MusicTrack track, int generation, CancellationTokenSource cancellation)
        {
            CoverThumbnailCache.Lease? candidate = null;
            try
            {
                candidate = await _cache.AcquireAsync(track, cancellation.Token);
                if (!_disposed && _attachments > 0 && generation == _generation && !cancellation.IsCancellationRequested)
                {
                    var previous = _lease;
                    _lease = candidate;
                    candidate = null;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Cover)));
                    previous?.Dispose();
                }
            }
            catch (OperationCanceledException)
            {
                // A detached card or a newer descriptor no longer needs this image.
            }
            finally
            {
                candidate?.Dispose();
                if (ReferenceEquals(_activation, cancellation))
                {
                    _activation = null;
                }

                cancellation.Dispose();
            }
        }

        /// <summary>Cancels this card's request and clears its binding before releasing the shared bitmap.</summary>
        private void StopThumbnail()
        {
            _generation++;
            _activation?.Cancel();
            _activation = null;
            var previous = _lease;
            _lease = null;
            if (previous is not null)
            {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Cover)));
                previous.Dispose();
            }
        }
    }
}

#nullable enable

using System.ComponentModel;
using Avalonia.Media.Imaging;
using MajMusic.Core.Models;

namespace MajMusic.Presentation.ViewModels
{
    /// <summary>Retains one music card and a shared thumbnail lease while its control is attached.</summary>
    public sealed partial class SongCardViewModel : INotifyPropertyChanged, IDisposable
    {
        /// <summary>Owns shared bounded thumbnail requests.</summary>
        private readonly CoverThumbnailCache _cache;
        /// <summary>Stores the current immutable row metadata.</summary>
        private MusicTrack _track;
        /// <summary>Retains the decoded bitmap until every attached control releases it.</summary>
        private CoverThumbnailCache.Lease? _lease;
        /// <summary>Freezes the exact prepared cover or placeholder until this viewport releases it.</summary>
        private CoverThumbnailCache.CoverIdentity? _preparedCoverIdentity;
        /// <summary>Cancels only this card's interest in a shared request.</summary>
        private CancellationTokenSource? _activation;
        /// <summary>Tracks the currently observed activation.</summary>
        private Task _activationTask = Task.CompletedTask;
        /// <summary>Retains all pending activation work until the owning view drains card resources.</summary>
        private Task _resourceCompletion = Task.CompletedTask;
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
        /// <summary>Gets completion of current and superseded visible-resource reads.</summary>
        internal Task ResourceCompletion => _resourceCompletion;
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

        /// <summary>Adopts prepared artwork before the card's first binding without attaching or inspecting audio.</summary>
        /// <param name="batch">The bounded preparation snapshot owned by the current navigation.</param>
        /// <returns>Whether this batch prepared the card's current source-scoped cover identity.</returns>
        internal bool AdoptPreloadedCover(CoverThumbnailCache.PreloadBatch batch)
        {
            if (_disposed || !batch.TryAcquire(_track, out var candidate))
            {
                return false;
            }

            _generation++;
            _activation?.Cancel();
            _activation = null;
            _preparedCoverIdentity = CoverThumbnailCache.Identity(_track);
            var previous = _lease;
            if (candidate?.Bitmap is null && previous?.Bitmap is not null && previous.Identity == _preparedCoverIdentity)
            {
                candidate?.Dispose();
            }
            else
            {
                _lease = candidate;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Cover)));
                previous?.Dispose();
            }
            if (_attachments > 0)
            {
                StartThumbnail();
            }

            return true;
        }

        /// <summary>Permits a prepared placeholder to retry after its owning surface transition has settled.</summary>
        internal void ResumeCoverLoading()
        {
            if (_disposed || _preparedCoverIdentity is null)
            {
                return;
            }

            _preparedCoverIdentity = null;
            if (_attachments > 0 && _lease?.Bitmap is null)
            {
                StartThumbnail();
            }
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
            var previous = _track;
            var changedCover = CoverThumbnailCache.Identity(_track) != CoverThumbnailCache.Identity(track);
            if (changedCover)
            {
                _preparedCoverIdentity = null;
                if (_attachments == 0 && _lease is { } obsolete)
                {
                    _lease = null;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Cover)));
                    obsolete.Dispose();
                }
            }
            _track = track;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Track)));
            var changedDownload = UpdateDownloadTrack(previous, track);
            if ((changedCover || changedDownload) && _attachments > 0)
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
            var token = cancellation.Token;
            _activationTask = LoadCardResourcesAsync(_track, generation, cancellation, token);
            _resourceCompletion = _resourceCompletion.IsCompleted ? _activationTask : Task.WhenAll(_resourceCompletion, _activationTask);
        }

        /// <summary>Owns cancellation for artwork and audio availability until both visible-resource reads finish.</summary>
        /// <param name="track">The captured music descriptor.</param>
        /// <param name="generation">The captured attachment generation.</param>
        /// <param name="cancellation">The cancellation owner released after both operations.</param>
        /// <param name="token">The token captured before either operation can release its source.</param>
        /// <returns>A task representing all resources owned by this activation.</returns>
        private async Task LoadCardResourcesAsync(MusicTrack track, int generation, CancellationTokenSource cancellation, CancellationToken token)
        {
            try
            {
                await Task.WhenAll(LoadThumbnailAsync(track, generation, token), CheckDownloadAvailabilityAsync(track, generation, token));
            }
            catch (OperationCanceledException)
            {
                // Detached or replaced card resources no longer own the visible presentation.
            }
            finally
            {
                if (ReferenceEquals(_activation, cancellation))
                {
                    _activation = null;
                }

                cancellation.Dispose();
            }
        }

        /// <summary>Publishes a requested bitmap only while the same attached card still owns the result.</summary>
        /// <param name="track">The captured cover descriptor.</param>
        /// <param name="generation">The captured activation generation.</param>
        /// <param name="token">The interest owned by this activation.</param>
        /// <returns>A task representing the observed shared request.</returns>
        private async Task LoadThumbnailAsync(MusicTrack track, int generation, CancellationToken token)
        {
            if (_preparedCoverIdentity == CoverThumbnailCache.Identity(track))
            {
                return;
            }

            CoverThumbnailCache.Lease? candidate = null;
            try
            {
                candidate = await _cache.AcquireAsync(track, token);
                if (!_disposed && _attachments > 0 && generation == _generation && !token.IsCancellationRequested)
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
            }
        }

        /// <summary>Cancels this card's request and clears its binding before releasing the shared bitmap.</summary>
        private void StopThumbnail()
        {
            _preparedCoverIdentity = null;
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

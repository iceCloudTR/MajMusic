#nullable enable

using Avalonia.Media.Imaging;
using MajMusic.Core.Abstractions;
using MajMusic.Core.Models;

namespace MajMusic.Presentation.ViewModels
{
    /// <summary>Coalesces cover-only requests with bounded decoding and reference-counted bitmap ownership.</summary>
    internal sealed class CoverThumbnailCache : IAsyncDisposable
    {
        /// <summary>Bounds the retained cache while allowing currently displayed images to remain pinned.</summary>
        private const int Capacity = 96;
        /// <summary>Limits navigation preparation to a bounded first viewport rather than a complete catalog.</summary>
        private const int MaximumPreloadedCovers = 12;
        /// <summary>Protects request and lease ownership.</summary>
        private readonly object _gate = new();
        /// <summary>Resolves cover resources without opening audio.</summary>
        private readonly IMusicWorkspace _workspace;
        /// <summary>Limits concurrent resolution and decoding work.</summary>
        private readonly SemaphoreSlim _decoders = new(4, 4);
        /// <summary>Cancels all requests when the owning view closes.</summary>
        private readonly CancellationTokenSource _lifetime;
        /// <summary>Shares pending and completed resources by their actual cover identity.</summary>
        private readonly Dictionary<CoverIdentity, Entry> _entries = new();
        /// <summary>Retains canceled requests until their media access has finished.</summary>
        private readonly HashSet<Task> _pending = new();
        /// <summary>Orders unused resources for eviction.</summary>
        private long _clock;
        /// <summary>Rejects new work after shutdown starts.</summary>
        private bool _disposed;

        /// <summary>Creates a cache tied to the view lifetime.</summary>
        /// <param name="workspace">The owner of local and online cover resources.</param>
        /// <param name="lifetime">The view's shutdown token.</param>
        public CoverThumbnailCache(IMusicWorkspace workspace, CancellationToken lifetime)
        {
            _workspace = workspace;
            _lifetime = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
        }

        /// <summary>Looks up shared local files for one visible song without starting an audio transfer.</summary>
        /// <param name="track">The currently attached source revision.</param>
        /// <param name="cancellationToken">Cancels lookup when this card leaves the viewport.</param>
        /// <returns>The locally refreshed descriptor, or the unchanged entry when unsupported.</returns>
        internal Task<MusicTrack> ResolveLocalTrackAsync(MusicTrack track, CancellationToken cancellationToken)
        {
            return _workspace is ILocalTrackMediaResolver resolver
                ? resolver.ResolveLocalTrackAsync(track, cancellationToken)
                : Task.FromResult(track);
        }

        /// <summary>Gets a cover identity unaffected by audio metadata changes.</summary>
        /// <param name="track">The entry carrying a cover descriptor.</param>
        /// <returns>The resource identity, including source ownership and sparse-descriptor fallback.</returns>
        public static CoverIdentity Identity(MusicTrack track)
        {
            return new CoverIdentity(track.SourceId, track.EndpointId, track.Cover,
                track.Cover is null ? track.RemoteId : null, track.Cover is null ? track.Revision : null);
        }

        /// <summary>Acquires one reference to a shared cover request.</summary>
        /// <param name="track">The resource descriptor to resolve.</param>
        /// <param name="cancellationToken">Cancels only this consumer's interest.</param>
        /// <returns>A bitmap lease, or null when the entry has no cover.</returns>
        public async Task<Lease?> AcquireAsync(MusicTrack track, CancellationToken cancellationToken)
        {
            if (track.Cover is null && track.RemoteId is null)
            {
                return null;
            }

            Entry entry;
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                cancellationToken.ThrowIfCancellationRequested();
                var key = Identity(track);
                if (!_entries.TryGetValue(key, out entry!))
                {
                    entry = new Entry(key, CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token));
                    _entries.Add(key, entry);
                    entry.Users = 1;
                    entry.Loading = LoadAsync(entry, track);
                    _pending.Add(entry.Loading);
                    _ = ObserveAsync(entry);
                }
                else
                {
                    entry.Users++;
                }

                entry.Stamp = ++_clock;
            }

            try
            {
                var bitmap = await entry.Loading.WaitAsync(cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                if (bitmap is null)
                {
                    Release(entry);
                    return null;
                }

                return new Lease(this, entry, bitmap);
            }
            catch
            {
                Release(entry);
                throw;
            }
        }

        /// <summary>Prepares only the first bounded set of covers without attaching cards or inspecting audio.</summary>
        /// <param name="tracks">The next catalog in display order; at most its first twelve entries are prepared.</param>
        /// <param name="deadline">The finite time allowed for this caller's cover preparation.</param>
        /// <param name="cancellationToken">Cancels this navigation's interest without canceling another consumer.</param>
        /// <returns>A disposable snapshot containing ready cover pins and stable unavailable placeholders.</returns>
        /// <exception cref="ArgumentOutOfRangeException">The deadline is not a positive finite cancellation interval.</exception>
        /// <exception cref="OperationCanceledException">Navigation or the cache lifetime is canceled.</exception>
        public async Task<PreloadBatch> PreloadAsync(IReadOnlyList<MusicTrack> tracks, TimeSpan deadline,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(tracks);
            if (deadline <= TimeSpan.Zero || deadline.TotalMilliseconds > uint.MaxValue - 1d)
            {
                throw new ArgumentOutOfRangeException(nameof(deadline));
            }

            CancellationToken lifetime;
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                lifetime = _lifetime.Token;
            }

            cancellationToken.ThrowIfCancellationRequested();
            using var preparation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime);
            preparation.CancelAfter(deadline);
            var descriptors = tracks.Take(MaximumPreloadedCovers).DistinctBy(Identity).ToArray();
            var prepared = new Lease?[descriptors.Length];
            try
            {
                await Task.WhenAll(descriptors.Select(async (track, index) =>
                {
                    try
                    {
                        prepared[index] = await AcquireAsync(track, preparation.Token).ConfigureAwait(false);
                        if (prepared[index] is { Bitmap: null } unavailable)
                        {
                            prepared[index] = null;
                            unavailable.Dispose();
                        }
                    }
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && !lifetime.IsCancellationRequested)
                    {
                        // A preparation deadline fixes this frame's placeholder without retaining a late request.
                    }
                })).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                lifetime.ThrowIfCancellationRequested();
                var batch = new PreloadBatch(descriptors.Select((track, index) => (Identity(track), prepared[index])));
                Array.Clear(prepared);
                return batch;
            }
            finally
            {
                foreach (var lease in prepared)
                {
                    lease?.Dispose();
                }
            }
        }

        /// <summary>Captures only already completed cached artwork without resolving, queuing or waiting for resources.</summary>
        /// <param name="tracks">The target catalog; at most its first twelve entries contribute distinct identities.</param>
        /// <returns>An independently pinned ready-image snapshot with explicit placeholders for unavailable images.</returns>
        /// <exception cref="ObjectDisposedException">The thumbnail cache has already closed.</exception>
        public PreloadBatch CaptureReady(IReadOnlyList<MusicTrack> tracks)
        {
            ArgumentNullException.ThrowIfNull(tracks);
            var identities = tracks.Take(MaximumPreloadedCovers).Select(Identity).Distinct().ToArray();
            var prepared = new (CoverIdentity Identity, Lease? Lease)[identities.Length];
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                for (var index = 0; index < identities.Length; index++)
                {
                    var key = identities[index];
                    var ready = _entries.TryGetValue(key, out var entry)
                        && !entry.Cleaned && !entry.Retired && !entry.Failed
                        && entry.Loading.IsCompletedSuccessfully && entry.Bitmap is not null;
                    prepared[index] = (key, ready ? Retain(entry!) : null);
                }
            }

            return new PreloadBatch(prepared);
        }

        /// <summary>Duplicates a still-pinned completed resource for a card receiving a prepared cover.</summary>
        /// <param name="entry">The entry pinned by the navigation batch.</param>
        /// <returns>An independent card lease, or null after cache shutdown.</returns>
        private Lease? Retain(Entry entry)
        {
            lock (_gate)
            {
                if (_disposed || entry.Cleaned || entry.Retired)
                {
                    return null;
                }

                entry.Users++;
                entry.Stamp = ++_clock;
                return new Lease(this, entry, entry.Bitmap);
            }
        }

        /// <summary>Cancels all thumbnail work and waits before disposing bitmap resources.</summary>
        /// <returns>A task representing completed cache shutdown.</returns>
        public async ValueTask DisposeAsync()
        {
            Task[] pending;
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                pending = _pending.ToArray();
            }

            await _lifetime.CancelAsync().ConfigureAwait(false);
            await Task.WhenAll(pending).ConfigureAwait(false);
            lock (_gate)
            {
                foreach (var entry in _entries.Values)
                {
                    DisposeEntry(entry);
                }

                _entries.Clear();
            }

            _decoders.Dispose();
            _lifetime.Dispose();
        }

        /// <summary>Resolves and decodes one cover while holding a bounded worker slot.</summary>
        /// <param name="entry">The shared request owner.</param>
        /// <param name="track">The cover descriptor to resolve.</param>
        /// <returns>The decoded image, or null for an unavailable image.</returns>
        private async Task<Bitmap?> LoadAsync(Entry entry, MusicTrack track)
        {
            Bitmap? bitmap = null;
            var entered = false;
            try
            {
                var token = entry.Cancellation.Token;
                await _decoders.WaitAsync(token).ConfigureAwait(false);
                entered = true;
                var path = await _workspace.ResolveCoverAsync(track, token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                if (path is not null)
                {
                    bitmap = await Task.Run(() => CoverThumbnailDecoder.Decode(path), token).ConfigureAwait(false);
                }

                lock (_gate)
                {
                    if (_disposed || entry.Retired || token.IsCancellationRequested)
                    {
                        bitmap?.Dispose();
                        return null;
                    }

                    entry.Failed = bitmap is null;
                    entry.Bitmap = bitmap;
                    return bitmap;
                }
            }
            catch (Exception)
            {
                bitmap?.Dispose();
                entry.Failed = true;
                return null;
            }
            finally
            {
                if (entered)
                {
                    _decoders.Release();
                }
            }
        }

        /// <summary>Removes finished requests from the shutdown wait set.</summary>
        /// <param name="entry">The observed shared request.</param>
        /// <returns>A task representing request bookkeeping.</returns>
        private async Task ObserveAsync(Entry entry)
        {
            await entry.Loading.ConfigureAwait(false);
            lock (_gate)
            {
                _pending.Remove(entry.Loading);
                if (entry.Retired)
                {
                    DisposeEntry(entry);
                }

                Trim();
            }
        }

        /// <summary>Releases a consumer and cancels unneeded pending work.</summary>
        /// <param name="entry">The request whose consumer detached.</param>
        private void Release(Entry entry)
        {
            lock (_gate)
            {
                if (_disposed || --entry.Users > 0)
                {
                    return;
                }

                entry.Stamp = ++_clock;
                if (!entry.Loading.IsCompleted || entry.Failed)
                {
                    _entries.Remove(entry.Key);
                    entry.Retired = true;
                    entry.Cancellation.Cancel();
                    if (entry.Loading.IsCompleted)
                    {
                        DisposeEntry(entry);
                    }
                }

                Trim();
            }
        }

        /// <summary>Evicts only unused completed entries so displayed images remain valid.</summary>
        private void Trim()
        {
            while (_entries.Count > Capacity)
            {
                var victim = _entries.Values.Where(item => item.Users == 0 && item.Loading.IsCompleted).MinBy(item => item.Stamp);
                if (victim is null)
                {
                    return;
                }

                _entries.Remove(victim.Key);
                DisposeEntry(victim);
            }
        }

        /// <summary>Releases completed resource ownership exactly once while the cache gate is held.</summary>
        /// <param name="entry">The unused or closing request to release.</param>
        private static void DisposeEntry(Entry entry)
        {
            if (!entry.Cleaned)
            {
                entry.Cleaned = true;
                entry.Bitmap?.Dispose();
                entry.Bitmap = null;
                entry.Cancellation.Dispose();
            }
        }

        /// <summary>Identifies only the cover resource and its account-routing context.</summary>
        /// <param name="Source">The indexed server or local source.</param>
        /// <param name="Endpoint">The independently authenticated endpoint.</param>
        /// <param name="Resource">The actual image descriptor.</param>
        /// <param name="Remote">The fallback entry identity when no image descriptor is known.</param>
        /// <param name="Revision">The fallback revision for a sparse online descriptor.</param>
        internal sealed record CoverIdentity(string Source, string? Endpoint, MediaResource? Resource, string? Remote, string? Revision);

        /// <summary>Pins a bounded navigation snapshot until cards adopt their own independent cover leases.</summary>
        internal sealed class PreloadBatch : IDisposable
        {
            /// <summary>Serializes lease adoption against cancellation or replacement of this batch.</summary>
            private readonly object _gate = new();
            /// <summary>Retains ready images and explicit unavailable placeholders by current routing identity.</summary>
            private readonly Dictionary<CoverIdentity, Lease?> _covers;
            /// <summary>Prevents adoption after this navigation's preparation ownership has ended.</summary>
            private bool _disposed;

            /// <summary>Owns the completed preparation results without acquiring any additional resources.</summary>
            /// <param name="covers">The unique attempted identities and their ready bitmap pins.</param>
            internal PreloadBatch(IEnumerable<(CoverIdentity Identity, Lease? Lease)> covers)
            {
                _covers = covers.ToDictionary(item => item.Identity, item => item.Lease);
            }

            /// <summary>Gets the bounded number of distinct attempted cover identities.</summary>
            public int Count => _covers.Count;

            /// <summary>Copies a prepared pin while preserving an explicit placeholder for unavailable artwork.</summary>
            /// <param name="track">The current card descriptor, including source and account routing.</param>
            /// <param name="lease">An independently owned bitmap pin, or null for a prepared placeholder.</param>
            /// <returns>Whether this active batch prepared the exact requested cover identity.</returns>
            public bool TryAcquire(MusicTrack track, out Lease? lease)
            {
                lock (_gate)
                {
                    lease = null;
                    if (_disposed || !_covers.TryGetValue(Identity(track), out var prepared))
                    {
                        return false;
                    }

                    lease = prepared?.Retain();
                    return true;
                }
            }

            /// <summary>Releases only the batch's pins, leaving adopted cards and other consumers valid.</summary>
            public void Dispose()
            {
                lock (_gate)
                {
                    if (_disposed)
                    {
                        return;
                    }

                    _disposed = true;
                    foreach (var lease in _covers.Values)
                    {
                        lease?.Dispose();
                    }
                }
            }
        }

        /// <summary>Retains one pending or completed thumbnail and its reference count.</summary>
        internal sealed class Entry
        {
            /// <summary>Creates shared resource ownership before asynchronous resolution begins.</summary>
            /// <param name="key">The resource identity.</param>
            /// <param name="cancellation">The request cancellation source.</param>
            public Entry(CoverIdentity key, CancellationTokenSource cancellation)
            {
                Key = key;
                Cancellation = cancellation;
            }
            /// <summary>Gets the resource identity.</summary>
            public CoverIdentity Key { get; }
            /// <summary>Gets the request cancellation owner.</summary>
            public CancellationTokenSource Cancellation { get; }
            /// <summary>Gets or sets the shared decoding task.</summary>
            public Task<Bitmap?> Loading { get; set; } = Task.FromResult<Bitmap?>(null);
            /// <summary>Gets or sets the decoded resource.</summary>
            public Bitmap? Bitmap { get; set; }
            /// <summary>Gets or sets the active consumer count.</summary>
            public int Users { get; set; }
            /// <summary>Gets or sets the last-use order.</summary>
            public long Stamp { get; set; }
            /// <summary>Gets or sets whether the last resolution failed.</summary>
            public bool Failed { get; set; }
            /// <summary>Gets or sets whether this canceled entry has left the cache.</summary>
            public bool Retired { get; set; }
            /// <summary>Gets or sets whether completed resource ownership has been released.</summary>
            public bool Cleaned { get; set; }
        }

        /// <summary>Pins one decoded bitmap until its displaying card detaches.</summary>
        internal sealed class Lease : IDisposable
        {
            /// <summary>Stores the shared owner until the lease is released.</summary>
            private CoverThumbnailCache? _owner;
            /// <summary>Stores the shared request reference.</summary>
            private readonly Entry _entry;
            /// <summary>Creates one active bitmap reference.</summary>
            /// <param name="owner">The cache receiving the eventual release.</param>
            /// <param name="entry">The shared request.</param>
            /// <param name="bitmap">The decoded image, when available.</param>
            public Lease(CoverThumbnailCache owner, Entry entry, Bitmap? bitmap)
            {
                _owner = owner;
                _entry = entry;
                Bitmap = bitmap;
            }
            /// <summary>Gets the shared thumbnail while this lease remains active.</summary>
            public Bitmap? Bitmap { get; }
            /// <summary>Gets the exact source-scoped cover descriptor pinned by this lease.</summary>
            public CoverIdentity Identity => _entry.Key;
            /// <summary>Creates another reference while this preparation pin remains owned.</summary>
            /// <returns>An independent bitmap lease, or null after release or cache shutdown.</returns>
            public Lease? Retain()
            {
                return Volatile.Read(ref _owner)?.Retain(_entry);
            }
            /// <summary>Releases this consumer exactly once.</summary>
            public void Dispose()
            {
                Interlocked.Exchange(ref _owner, null)?.Release(_entry);
            }
        }
    }
}

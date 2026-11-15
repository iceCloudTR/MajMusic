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
                return new Lease(this, entry, bitmap);
            }
            catch
            {
                Release(entry);
                throw;
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
            /// <summary>Releases this consumer exactly once.</summary>
            public void Dispose()
            {
                Interlocked.Exchange(ref _owner, null)?.Release(_entry);
            }
        }
    }
}

#nullable enable

using MajMusic.Core.Abstractions;
using MajMusic.Core.Models;
using MajMusic.Infrastructure.Metadata;

namespace MajMusic.Android.Audio
{
    /// <summary>Parses authorized Android document references and caches ordinary app-owned audio files.</summary>
    internal sealed class AndroidTrackMetadataProvider : ITrackMetadataProvider, IAsyncDisposable
    {
        /// <summary>Reads native media properties without opening the playing media owner.</summary>
        private readonly AndroidAudioMetadataReader _reader;
        /// <summary>Reuses properties for downloaded files by file length and timestamp.</summary>
        private readonly CachedTrackMetadataProvider _files;
        /// <summary>Serializes native reads for external document references.</summary>
        private readonly SemaphoreSlim _documents = new(1, 1);

        /// <summary>Creates a provider without reading external documents.</summary>
        /// <param name="reader">The Android native metadata reader.</param>
        /// <param name="cacheDirectory">The app-private file-metadata cache directory.</param>
        public AndroidTrackMetadataProvider(AndroidAudioMetadataReader reader, string cacheDirectory)
        {
            _reader = reader;
            _files = new CachedTrackMetadataProvider(reader, cacheDirectory);
        }

        /// <inheritdoc />
        public async Task<MusicTrack> EnrichAsync(MusicTrack track, CancellationToken cancellationToken = default)
        {
            if (!Uri.TryCreate(track.Audio.LocalPath, UriKind.Absolute, out var uri) || uri.Scheme != "content")
            {
                return await _files.EnrichAsync(track, cancellationToken).ConfigureAwait(false);
            }

            if (track.Duration is not null && track.AudioInfo is not null)
            {
                return track;
            }

            await _documents.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var metadata = await _reader.ReadAsync(uri.AbsoluteUri, cancellationToken).ConfigureAwait(false);
                return track with { Duration = metadata.Duration ?? track.Duration, AudioInfo = metadata.AudioInfo ?? track.AudioInfo };
            }
            finally
            {
                _documents.Release();
            }
        }

        /// <inheritdoc />
        public async ValueTask DisposeAsync()
        {
            await _documents.WaitAsync().ConfigureAwait(false);
            try
            {
                await _files.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                _documents.Release();
                _documents.Dispose();
            }
        }
    }
}

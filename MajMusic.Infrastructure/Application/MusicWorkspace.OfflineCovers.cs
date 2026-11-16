#nullable enable

using System.Text.Json;
using MajMusic.Core.Models;

namespace MajMusic.Infrastructure.Application
{
    /// <summary>Keeps explicit offline audio downloads accompanied by independently reusable artwork.</summary>
    public sealed partial class MusicWorkspace
    {
        /// <summary>Scopes unverified address aliases to their indexed source and configured session slot without changing persisted descriptors.</summary>
        /// <param name="track">The source-scoped entry that owns the resource.</param>
        /// <param name="resource">The original descriptor, including independent content digests and local paths.</param>
        /// <returns>A runtime-only alias namespace that cannot borrow another configured account's unknown content.</returns>
        private static MediaResource ScopeResourceForCache(MusicTrack track, MediaResource resource)
        {
            return resource with
            {
                CacheVersion = JsonSerializer.Serialize(new[]
                {
                    "MajMusic/cache/v1", track.SourceId, track.EndpointId ?? string.Empty,
                    resource.CacheVersion ?? track.Revision ?? string.Empty
                })
            };
        }

        /// <summary>Bounds optional artwork recovery without changing a successfully completed audio transfer into a failed download.</summary>
        /// <param name="track">The audio entry whose managed object has already been pinned.</param>
        /// <param name="cancellationToken">Cancels the complete user-requested download.</param>
        /// <returns>A task representing the finite cover pin or recovery attempt.</returns>
        private async Task PreserveOfflineCoverAsync(MusicTrack track, CancellationToken cancellationToken)
        {
            using var coverLifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            coverLifetime.CancelAfter(TimeSpan.FromSeconds(5));
            try
            {
                await ResolveCoverAsync(track, coverLifetime.Token).ConfigureAwait(false);
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested
                && exception is HttpRequestException or IOException or InvalidOperationException or JsonException or OperationCanceledException or UnauthorizedAccessException)
            {
                // The retained audio stays usable; a later cover read or explicit download retries the independent image.
            }

            cancellationToken.ThrowIfCancellationRequested();
        }
    }
}

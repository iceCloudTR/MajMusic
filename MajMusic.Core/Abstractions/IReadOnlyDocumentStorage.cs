#nullable enable

using MajMusic.Core.Models;

namespace MajMusic.Core.Abstractions
{
    /// <summary>Reads explicitly granted external document trees without copying music or exposing them to filesystem APIs.</summary>
    public interface IReadOnlyDocumentStorage
    {
        /// <summary>Checks whether this service handles a provider reference.</summary>
        /// <param name="reference">The original stored document or tree URI.</param>
        /// <returns>Whether the reference belongs to a supported document provider.</returns>
        bool CanHandle(string? reference);
        /// <summary>Indexes original music and maidata references recursively without copying audio.</summary>
        /// <param name="directory">The granted tree URI.</param>
        /// <param name="cancellationToken">Cancels enumeration and text reads.</param>
        /// <returns>The indexed original resource references and isolated read errors.</returns>
        Task<LocalLibraryScanResult> ScanAsync(string directory, CancellationToken cancellationToken = default);
        /// <summary>Validates read access and returns the original URI for a native content-aware player.</summary>
        /// <param name="reference">The original audio document URI.</param>
        /// <param name="cancellationToken">Cancels access validation.</param>
        /// <returns>The original audio URI without a private audio copy.</returns>
        Task<string> ResolveAudioAsync(string reference, CancellationToken cancellationToken = default);
        /// <summary>Resolves only artwork to a bounded private on-demand cache for ordinary image decoders.</summary>
        /// <param name="resource">The original cover URI and its scan-time version hint.</param>
        /// <param name="cancellationToken">Cancels the small artwork transfer.</param>
        /// <returns>The completed private artwork cache path.</returns>
        Task<string?> ResolveCoverAsync(MediaResource resource, CancellationToken cancellationToken = default);
        /// <summary>Reads the original display name without guessing it from an opaque provider identifier.</summary>
        /// <param name="reference">The selected document URI.</param>
        /// <param name="cancellationToken">Cancels lookup.</param>
        /// <returns>The exact provider reference and display name.</returns>
        Task<DocumentFileReference> GetFileReferenceAsync(string reference, CancellationToken cancellationToken = default);
        /// <summary>Opens a caller-owned read-only document stream.</summary>
        /// <param name="reference">The original document URI.</param>
        /// <param name="cancellationToken">Cancels opening.</param>
        /// <returns>The stream which the caller must dispose.</returns>
        Task<Stream> OpenReadAsync(string reference, CancellationToken cancellationToken = default);
        /// <summary>Enumerates conventional Maj collection JSON and optionally direct files of a selected index tree.</summary>
        /// <param name="directory">The granted tree URI.</param>
        /// <param name="includeDirectFiles">Whether all direct JSON documents are collection indexes.</param>
        /// <param name="cancellationToken">Cancels enumeration.</param>
        /// <returns>The original document references without modifying their provider.</returns>
        IAsyncEnumerable<DocumentFileReference> EnumerateCollectionFilesAsync(string directory, bool includeDirectFiles, CancellationToken cancellationToken = default);
    }
}

#nullable enable

namespace MajMusic.Core.Abstractions
{
    /// <summary>Accesses an explicitly granted media directory without interpreting document URIs as filesystem paths.</summary>
    public interface IMediaStorageAccess
    {
        /// <summary>Checks whether this adapter supports a selected root reference.</summary>
        /// <param name="root">The saved media-directory reference.</param>
        /// <returns>Whether the adapter owns this reference format.</returns>
        bool CanHandle(string root);
        /// <summary>Finds an existing item by traversing only children of the selected root.</summary>
        /// <param name="root">The granted media-directory reference.</param>
        /// <param name="relativePath">The nonescaping child path.</param>
        /// <param name="directory">Whether the requested item is a directory.</param>
        /// <param name="cancellationToken">Cancels directory enumeration.</param>
        /// <returns>The original item reference, or null when it is absent.</returns>
        Task<string?> FindAsync(string root, string relativePath, bool directory, CancellationToken cancellationToken = default);
        /// <summary>Opens an existing granted document without creating a media copy.</summary>
        /// <param name="reference">The original document reference.</param>
        /// <param name="cancellationToken">Cancels document opening.</param>
        /// <returns>The caller-owned read stream.</returns>
        Task<Stream> OpenReadAsync(string reference, CancellationToken cancellationToken = default);
        /// <summary>Publishes a completed private media object without truncating any existing managed media file.</summary>
        /// <param name="root">The explicitly granted writable media root.</param>
        /// <param name="relativePath">The immutable object's nonescaping destination path.</param>
        /// <param name="localSourcePath">The already validated private cache file, which remains unchanged.</param>
        /// <param name="cancellationToken">Cancels before the completed file is committed.</param>
        /// <returns>The completed media document reference.</returns>
        Task<string> PublishFileAsync(string root, string relativePath, string localSourcePath, CancellationToken cancellationToken = default);
        /// <summary>Deletes only one explicitly addressed media file, never an entire source tree.</summary>
        /// <param name="root">The writable granted root.</param>
        /// <param name="relativePath">The nonescaping immutable object path.</param>
        /// <param name="cancellationToken">Cancels before deletion.</param>
        /// <returns>A task representing deletion or an already absent object.</returns>
        Task DeleteFileAsync(string root, string relativePath, CancellationToken cancellationToken = default);
    }
}

#nullable enable

namespace MajMusic.Core.Abstractions
{
    /// <summary>Publishes only application-owned portable lists without granting writes to imported collection sources.</summary>
    public interface ISharedCollectionStorageAccess
    {
        /// <summary>Safely replaces one verified app-owned mirror while retaining the old document if publication fails.</summary>
        /// <param name="root">The explicitly writable granted media root.</param>
        /// <param name="relativePath">The reserved MaiCharts/MajMusic-hash.json destination.</param>
        /// <param name="localSourcePath">The complete private export containing its local playlist identity.</param>
        /// <param name="cancellationToken">Cancels before final commit.</param>
        /// <returns>The final complete mirror reference.</returns>
        Task<string> PublishCollectionAsync(string root, string relativePath, string localSourcePath, CancellationToken cancellationToken = default);
        /// <summary>Deletes only a reserved mirror whose embedded local identity matches its generated filename.</summary>
        /// <param name="root">The writable granted media root.</param>
        /// <param name="relativePath">The exact reserved mirror path.</param>
        /// <param name="cancellationToken">Cancels before deletion.</param>
        /// <returns>A task representing one owned mirror deletion.</returns>
        Task DeleteCollectionAsync(string root, string relativePath, CancellationToken cancellationToken = default);
    }
}

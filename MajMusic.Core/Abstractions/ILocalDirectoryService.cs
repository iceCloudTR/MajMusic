#nullable enable

using MajMusic.Core.Models;

namespace MajMusic.Core.Abstractions
{
    /// <summary>Scans explicit local sources without coupling audio discovery to collection discovery.</summary>
    public interface ILocalDirectoryService
    {
        /// <summary>Indexes audio or imports collection JSON according to the source's configured role.</summary>
        /// <param name="source">The explicitly selected read-only source.</param>
        /// <param name="cancellationToken">Cancels scanning and metadata publication.</param>
        /// <returns>The completed source counts and isolated errors.</returns>
        Task<DirectorySourceScanResult> ScanAsync(LocalDirectorySource source, CancellationToken cancellationToken = default);
    }
}

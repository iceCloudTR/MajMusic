#nullable enable

namespace MajMusic.Core.Abstractions
{
    /// <summary>Looks up public MajMusic releases independently of music sources.</summary>
    public interface IAppReleaseSource
    {
        /// <summary>Gets the latest published stable release.</summary>
        /// <param name="cancellationToken">Cancels the request.</param>
        /// <returns>The release version and page, or null when no stable release exists.</returns>
        Task<(Version Version, Uri Page)?> GetLatestAsync(CancellationToken cancellationToken = default);
    }
}

#nullable enable

using System.Security.Cryptography;
using System.Text;

namespace MajMusic.Core.Models
{
    /// <summary>Normalizes typed directory settings while migrating old untyped music roots once.</summary>
    public static class LocalDirectorySettings
    {
        /// <summary>Identifies the supported independent-directory configuration layout.</summary>
        public const int CurrentVersion = 1;

        /// <summary>Migrates legacy roots and canonicalizes distinct source and role pairs without filesystem access.</summary>
        /// <param name="settings">The settings snapshot to normalize.</param>
        /// <returns>A settings snapshot with stable explicit source identities.</returns>
        /// <exception cref="InvalidDataException">A source identity, path, kind, array or version is invalid.</exception>
        public static AppSettings Normalize(AppSettings settings)
        {
            ArgumentNullException.ThrowIfNull(settings);
            if (settings.LocalDirectorySources is null || settings.LocalMusicDirectories is null
                || settings.LocalDirectoryFormatVersion is < 0 or > CurrentVersion)
            {
                throw new InvalidDataException("Local directory settings are invalid or use an unsupported version.");
            }

            var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
            var knownPaths = new HashSet<string>(comparer);
            var knownIds = new HashSet<string>(StringComparer.Ordinal);
            var sources = new List<LocalDirectorySource>();
            foreach (var source in settings.LocalDirectorySources)
            {
                if (source is null || string.IsNullOrWhiteSpace(source.Id) || !Enum.IsDefined(source.Kind))
                {
                    throw new InvalidDataException("Each local directory needs an identity and a supported source type.");
                }

                var path = NormalizePath(source.Path);
                var key = ((int)source.Kind).ToString(System.Globalization.CultureInfo.InvariantCulture) + ":" + path;
                if (!knownPaths.Add(key))
                {
                    continue;
                }

                if (!knownIds.Add(source.Id))
                {
                    throw new InvalidDataException("Local directory identities must be unique.");
                }

                sources.Add(source with { Path = path });
            }

            var legacy = settings.LocalMusicDirectories.Select(NormalizePath).Distinct(comparer).ToArray();
            if (settings.LocalDirectoryFormatVersion == 0)
            {
                foreach (var path in legacy)
                {
                    var key = "0:" + path;
                    if (!knownPaths.Add(key))
                    {
                        continue;
                    }

                    var identityPath = OperatingSystem.IsWindows() ? path.ToUpperInvariant() : path;
                    var id = "directory:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(identityPath)));
                    if (!knownIds.Add(id))
                    {
                        throw new InvalidDataException("A migrated directory identity conflicts with an existing source.");
                    }

                    sources.Add(new LocalDirectorySource { Id = id, Path = path, Kind = LocalDirectoryKind.MusicLibrary });
                }
            }

            return settings with
            {
                LocalDirectorySources = sources.ToArray(),
                LocalMusicDirectories = legacy,
                LocalDirectoryFormatVersion = CurrentVersion
            };
        }

        /// <summary>Canonicalizes a fully qualified directory path without opening or creating it.</summary>
        /// <param name="path">The configured source path.</param>
        /// <returns>The canonical absolute path with a root-safe trailing-separator policy.</returns>
        /// <exception cref="InvalidDataException">The path is empty, relative or invalid.</exception>
        public static string NormalizePath(string path)
        {
            if (DocumentReference.IsContentUri(path))
            {
                return new Uri(path, UriKind.Absolute).AbsoluteUri;
            }

            if (string.IsNullOrWhiteSpace(path) || !System.IO.Path.IsPathFullyQualified(path))
            {
                throw new InvalidDataException("Local directory paths must be fully qualified.");
            }

            try
            {
                return System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(path));
            }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
            {
                throw new InvalidDataException("The configured directory path is invalid.", exception);
            }
        }
    }
}

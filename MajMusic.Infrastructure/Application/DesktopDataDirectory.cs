#nullable enable

using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using MajMusic.Core.Models;
using MajMusic.Infrastructure.Storage;
using Microsoft.Data.Sqlite;

namespace MajMusic.Infrastructure.Application
{
    /// <summary>Locates portable desktop data and copies a previous application-owned data set once.</summary>
    public static class DesktopDataDirectory
    {
        /// <summary>Serializes in-process bootstrap before a workspace opens its database.</summary>
        private static readonly object MigrationGate = new();
        /// <summary>Lists application-managed trees that may be copied without importing external music sources.</summary>
        private static readonly string[] ManagedDirectories = ["Cache", "Downloads", "AudioMetadata", "Library"];

        /// <summary>Gets the default data location without opening, creating or migrating files.</summary>
        /// <returns>The executable directory on desktop platforms, or platform application data on mobile.</returns>
        public static string GetDefaultDirectory()
        {
            return IsDesktop() ? Path.GetFullPath(AppContext.BaseDirectory) : GetLegacyDirectory();
        }

        /// <summary>Prepares the desktop executable directory before constructing application storage services.</summary>
        /// <returns>The portable desktop root or the existing mobile application-data root.</returns>
        /// <exception cref="IOException">Application data cannot be copied or published.</exception>
        /// <exception cref="InvalidDataException">Existing legacy preferences or database documents are invalid.</exception>
        public static string PrepareDefaultDirectory()
        {
            var directory = GetDefaultDirectory();
            return IsDesktop() ? Prepare(directory, GetLegacyDirectory()) : directory;
        }

        /// <summary>Copies application-owned legacy data only when the portable location has no existing data set.</summary>
        /// <param name="executableDirectory">The absolute directory containing the desktop executable.</param>
        /// <param name="legacyDirectory">The absolute previous MajMusic application-data directory.</param>
        /// <returns>The canonical portable data directory.</returns>
        /// <exception cref="ArgumentException">A directory is not an absolute path.</exception>
        /// <exception cref="InvalidOperationException">The portable and legacy roots overlap.</exception>
        /// <exception cref="IOException">Legacy files cannot be copied or portable files cannot be published.</exception>
        /// <exception cref="InvalidDataException">Legacy preferences or database documents are invalid.</exception>
        public static string Prepare(string executableDirectory, string legacyDirectory)
        {
            if (!Path.IsPathFullyQualified(executableDirectory) || !Path.IsPathFullyQualified(legacyDirectory))
            {
                throw new ArgumentException("Portable and legacy data directories must be absolute paths.");
            }

            var destination = Path.TrimEndingDirectorySeparator(Path.GetFullPath(executableDirectory));
            var source = Path.TrimEndingDirectorySeparator(Path.GetFullPath(legacyDirectory));
            if (PathEquals(destination, source))
            {
                return destination;
            }

            if (IsWithin(destination, source) || IsWithin(source, destination))
            {
                throw new InvalidOperationException("Portable and legacy data directories must not contain one another.");
            }

            lock (MigrationGate)
            {
                Directory.CreateDirectory(destination);
                if (!Directory.Exists(source) || HasApplicationData(destination) || !HasApplicationData(source))
                {
                    return destination;
                }

                var staging = Path.Combine(destination, ".majmusic-migration-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(staging);
                try
                {
                    foreach (var name in ManagedDirectories)
                    {
                        var from = Path.Combine(source, name);
                        if (Directory.Exists(from))
                        {
                            CopyManagedDirectory(from, Path.Combine(staging, name));
                        }
                    }

                    var settings = Path.Combine(source, "settings.json");
                    if (File.Exists(settings))
                    {
                        CopyFile(settings, Path.Combine(staging, "settings.json"));
                        RewriteSettings(Path.Combine(staging, "settings.json"), source, destination);
                    }

                    var database = Path.Combine(source, "library.db");
                    if (File.Exists(database))
                    {
                        var copiedDatabase = Path.Combine(staging, "library.db");
                        BackupDatabase(database, copiedDatabase);
                        RewriteDatabase(copiedDatabase, source, destination, staging);
                    }

                    PublishStaging(staging, destination);
                }
                finally
                {
                    var resolved = Path.GetFullPath(staging);
                    if (!IsWithin(resolved, destination)
                        || !Path.GetFileName(resolved).StartsWith(".majmusic-migration-", StringComparison.Ordinal))
                    {
                        throw new InvalidOperationException("Refusing to clean outside the portable migration staging directory.");
                    }

                    Directory.Delete(resolved, true);
                }
            }

            return destination;
        }

        /// <summary>Checks the platforms that store their data beside the executable.</summary>
        /// <returns>Whether this process targets Windows, Linux or macOS desktop.</returns>
        private static bool IsDesktop()
        {
            return OperatingSystem.IsWindows() || OperatingSystem.IsLinux() && !OperatingSystem.IsAndroid() || OperatingSystem.IsMacOS();
        }

        /// <summary>Locates only the previous application-owned root without creating it.</summary>
        /// <returns>The legacy MajMusic application-data root.</returns>
        private static string GetLegacyDirectory()
        {
            var parent = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrWhiteSpace(parent))
            {
                parent = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            }

            return Path.GetFullPath(Path.Combine(parent, "MajMusic"));
        }

        /// <summary>Checks for an existing application data set without inspecting external music roots.</summary>
        /// <param name="directory">The application root to inspect.</param>
        /// <returns>Whether settings, a library database or managed data already exist.</returns>
        private static bool HasApplicationData(string directory)
        {
            return File.Exists(Path.Combine(directory, "settings.json")) || File.Exists(Path.Combine(directory, "library.db"))
                || ManagedDirectories.Any(name => Directory.Exists(Path.Combine(directory, name)));
        }

        /// <summary>Publishes prepared entries and rolls back only this attempt's moves when publication fails.</summary>
        /// <param name="staging">The validated staging directory.</param>
        /// <param name="destination">The portable application root.</param>
        private static void PublishStaging(string staging, string destination)
        {
            var planned = ManagedDirectories.Concat(["library.db", "settings.json"])
                .Select(name => (From: Path.Combine(staging, name), To: Path.Combine(destination, name)))
                .Where(item => Directory.Exists(item.From) || File.Exists(item.From)).ToArray();
            foreach (var item in planned)
            {
                ValidatePublicationPaths(item.From, item.To, staging, destination);
                if (Directory.Exists(item.To) || File.Exists(item.To))
                {
                    throw new IOException("The portable destination already contains a conflicting application-data path.");
                }
            }

            var moved = new List<(string From, string To, bool Directory)>();
            try
            {
                foreach (var item in planned)
                {
                    ValidatePublicationPaths(item.From, item.To, staging, destination);
                    var directory = Directory.Exists(item.From);
                    if (directory)
                    {
                        Directory.Move(item.From, item.To);
                    }
                    else
                    {
                        File.Move(item.From, item.To, false);
                    }

                    moved.Add((item.From, item.To, directory));
                }
            }
            catch
            {
                for (var index = moved.Count - 1; index >= 0; index--)
                {
                    var item = moved[index];
                    ValidatePublicationPaths(item.From, item.To, staging, destination);
                    if (item.Directory)
                    {
                        Directory.Move(item.To, item.From);
                    }
                    else
                    {
                        File.Move(item.To, item.From, false);
                    }
                }

                throw;
            }
        }

        /// <summary>Confirms every recursive publication or rollback move remains within its named data roots.</summary>
        /// <param name="prepared">The generated staging entry.</param>
        /// <param name="published">The corresponding portable entry.</param>
        /// <param name="staging">The staging root owned by this attempt.</param>
        /// <param name="destination">The intended portable root.</param>
        private static void ValidatePublicationPaths(string prepared, string published, string staging, string destination)
        {
            if (!IsWithin(prepared, staging) || !IsWithin(published, destination)
                || !PathEquals(Path.GetDirectoryName(Path.GetFullPath(prepared))!, Path.GetFullPath(staging))
                || !PathEquals(Path.GetDirectoryName(Path.GetFullPath(published))!, Path.GetFullPath(destination)))
            {
                throw new InvalidOperationException("Refusing to move files outside the portable migration roots.");
            }
        }

        /// <summary>Copies one managed tree while refusing links that could lead into external music directories.</summary>
        /// <param name="source">The legacy application-owned tree.</param>
        /// <param name="destination">The generated staging tree.</param>
        private static void CopyManagedDirectory(string source, string destination)
        {
            if ((File.GetAttributes(source) & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException("Linked application-data directories cannot be migrated as managed files.");
            }

            Directory.CreateDirectory(destination);
            foreach (var file in Directory.EnumerateFiles(source))
            {
                CopyFile(file, Path.Combine(destination, Path.GetFileName(file)));
            }

            foreach (var directory in Directory.EnumerateDirectories(source))
            {
                CopyManagedDirectory(directory, Path.Combine(destination, Path.GetFileName(directory)));
            }
        }

        /// <summary>Copies an application-owned file while leaving the previous file's bytes and attributes intact.</summary>
        /// <param name="source">The original application-owned file.</param>
        /// <param name="destination">The generated staging file.</param>
        private static void CopyFile(string source, string destination)
        {
            if ((File.GetAttributes(source) & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException("Linked application-data files cannot be migrated as managed files.");
            }

            File.Copy(source, destination, false);
            File.SetAttributes(destination, File.GetAttributes(destination) & ~FileAttributes.ReadOnly);
        }

        /// <summary>Rebases only configured paths that belong to copied application-managed trees.</summary>
        /// <param name="path">The staged settings document.</param>
        /// <param name="source">The old application root.</param>
        /// <param name="destination">The final portable application root.</param>
        private static void RewriteSettings(string path, string source, string destination)
        {
            var store = new JsonSettingsStore(path);
            var settings = store.LoadAsync().GetAwaiter().GetResult();
            settings = settings with
            {
                LocalMusicDirectories = settings.LocalMusicDirectories.Select(item => Relocate(item, source, destination)).ToArray(),
                LocalDirectorySources = settings.LocalDirectorySources.Select(item => item with { Path = Relocate(item.Path, source, destination) }).ToArray()
            };
            store.SaveAsync(settings).GetAwaiter().GetResult();
        }

        /// <summary>Copies a consistent SQLite snapshot including transactions still present in its write-ahead log.</summary>
        /// <param name="source">The complete old database path.</param>
        /// <param name="destination">The complete staged database path.</param>
        private static void BackupDatabase(string source, string destination)
        {
            using var original = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = source, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
            using var copy = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = destination, Pooling = false }.ToString());
            original.Open();
            copy.Open();
            original.BackupDatabase(copy);
        }

        /// <summary>Rebases media descriptors without changing track identities, playlist ordering or unknown JSON fields.</summary>
        /// <param name="path">The staged SQLite snapshot.</param>
        /// <param name="source">The old application root.</param>
        /// <param name="destination">The final portable root.</param>
        /// <param name="staging">The generated migration staging root.</param>
        private static void RewriteDatabase(string path, string source, string destination, string staging)
        {
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
            connection.Open();
            using var transaction = connection.BeginTransaction();
            foreach (var table in new[] { "tracks", "playlists" })
            {
                using var exists = connection.CreateCommand();
                exists.Transaction = transaction;
                exists.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = $name;";
                exists.Parameters.AddWithValue("$name", table);
                if (Convert.ToInt32(exists.ExecuteScalar()) == 0)
                {
                    continue;
                }

                var documents = new List<(string Id, JsonObject Document)>();
                using (var select = connection.CreateCommand())
                {
                    select.Transaction = transaction;
                    select.CommandText = table == "tracks" ? "SELECT id, document FROM tracks;" : "SELECT id, document FROM playlists;";
                    using var reader = select.ExecuteReader();
                    while (reader.Read())
                    {
                        var document = JsonNode.Parse(reader.GetString(1)) as JsonObject
                            ?? throw new InvalidDataException("The legacy database contains an invalid metadata document.");
                        documents.Add((reader.GetString(0), document));
                    }
                }

                foreach (var (id, document) in documents)
                {
                    if (table == "tracks")
                    {
                        if (document["Audio"] is JsonObject audio)
                        {
                            var previous = audio["LocalPath"]?.GetValue<string>();
                            RewriteProperty(audio, "LocalPath", source, destination);
                            var updated = audio["LocalPath"]?.GetValue<string>();
                            if (previous is not null && updated is not null && !PathEquals(previous, updated))
                            {
                                CopyMetadataAlias(staging, previous, updated);
                            }
                        }

                        if (document["Cover"] is JsonObject cover)
                        {
                            RewriteProperty(cover, "LocalPath", source, destination);
                        }
                    }
                    else if (document["MajCollection"] is JsonObject collection)
                    {
                        RewriteProperty(collection, "SourceFilePath", source, destination);
                    }

                    using var update = connection.CreateCommand();
                    update.Transaction = transaction;
                    update.CommandText = table == "tracks" ? "UPDATE tracks SET document = $document WHERE id = $id;" : "UPDATE playlists SET document = $document WHERE id = $id;";
                    update.Parameters.AddWithValue("$document", document.ToJsonString());
                    update.Parameters.AddWithValue("$id", id);
                    update.ExecuteNonQuery();
                }
            }

            transaction.Commit();
        }

        /// <summary>Rebases one known filesystem field while leaving external song and index references untouched.</summary>
        /// <param name="document">The resource or collection object.</param>
        /// <param name="property">The filesystem property to update.</param>
        /// <param name="source">The old application root.</param>
        /// <param name="destination">The final portable root.</param>
        private static void RewriteProperty(JsonObject document, string property, string source, string destination)
        {
            if (document[property]?.GetValue<string>() is { } path)
            {
                document[property] = Relocate(path, source, destination);
            }
        }

        /// <summary>Preserves cached audio properties under the new path-derived lookup name.</summary>
        /// <param name="staging">The generated staging root.</param>
        /// <param name="previous">The previous local media path.</param>
        /// <param name="updated">The rebased portable local media path.</param>
        private static void CopyMetadataAlias(string staging, string previous, string updated)
        {
            var original = Path.Combine(staging, "AudioMetadata", MetadataKey(previous) + ".json");
            var relocated = Path.Combine(staging, "AudioMetadata", MetadataKey(updated) + ".json");
            if (File.Exists(original) && !File.Exists(relocated))
            {
                File.Copy(original, relocated, false);
            }
        }

        /// <summary>Matches the cached metadata provider's canonical path key.</summary>
        /// <param name="path">The complete media path.</param>
        /// <returns>The stable SHA-256 lookup filename stem.</returns>
        private static string MetadataKey(string path)
        {
            var normalized = Path.GetFullPath(path);
            return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(OperatingSystem.IsWindows() ? normalized.ToUpperInvariant() : normalized)));
        }

        /// <summary>Rebases only paths inside the explicitly copied managed subdirectories.</summary>
        /// <param name="path">The configured local path.</param>
        /// <param name="source">The previous application root.</param>
        /// <param name="destination">The portable application root.</param>
        /// <returns>The portable managed path or the unchanged external reference.</returns>
        private static string Relocate(string path, string source, string destination)
        {
            if (!Path.IsPathFullyQualified(path) || !IsWithin(path, source))
            {
                return path;
            }

            var relative = Path.GetRelativePath(source, path);
            var first = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0];
            return ManagedDirectories.Any(name => PathEquals(name, first)) ? Path.Combine(destination, relative) : path;
        }

        /// <summary>Checks normalized path equality using the current platform's filename rules.</summary>
        /// <param name="first">The first path or filename.</param>
        /// <param name="second">The second path or filename.</param>
        /// <returns>Whether the paths identify the same platform name.</returns>
        private static bool PathEquals(string first, string second)
        {
            return string.Equals(first, second, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        }

        /// <summary>Checks a resolved descendant path without permitting sibling-prefix matches.</summary>
        /// <param name="path">The candidate path.</param>
        /// <param name="parent">The intended root.</param>
        /// <returns>Whether the resolved candidate lies below the root.</returns>
        private static bool IsWithin(string path, string parent)
        {
            var resolved = Path.GetFullPath(parent);
            var prefix = Path.EndsInDirectorySeparator(resolved) ? resolved : resolved + Path.DirectorySeparatorChar;
            return Path.GetFullPath(path).StartsWith(prefix, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        }
    }
}

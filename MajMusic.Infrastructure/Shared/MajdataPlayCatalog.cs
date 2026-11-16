#nullable enable

using System.Security.Cryptography;
using System.Buffers;
using System.Text.Json;
using MajMusic.Core.Models;
using Microsoft.Data.Sqlite;

namespace MajMusic.Infrastructure.Shared
{
    /// <summary>Reads MajdataPlay's public chart catalog and verified media without changing its files or account data.</summary>
    public sealed partial class MajdataPlayCatalog
    {
        /// <summary>Lists the completed legacy audio names in MajdataPlay's discovery order.</summary>
        private static readonly string[] TrackNames = ["track.mp3", "track.opus", "track.ogg", "track.aac", "track.wav"];
        /// <summary>Lists legacy compressed and original cover names.</summary>
        private static readonly string[] CoverNames = ["bg.jpg", "bg.png", "bg_fullSize.jpg"];
        /// <summary>Retains the configured game root independently of MajMusic's own data directory.</summary>
        private readonly string _root;

        /// <summary>Creates a read-only view over a local game directory.</summary>
        /// <param name="rootDirectory">The MajdataPlay directory containing MaiCharts, Cache and Runtime.</param>
        /// <exception cref="ArgumentException">The root path is blank or invalid.</exception>
        public MajdataPlayCatalog(string rootDirectory)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
            _root = Path.GetFullPath(rootDirectory);
        }

        /// <summary>Gets the relative canonical object filename used by MajdataPlay.</summary>
        /// <param name="sha256">The complete SHA-256 digest in hexadecimal.</param>
        /// <returns>The extensionless path Cache/Objects/prefix/digest using platform separators.</returns>
        /// <exception cref="ArgumentException">The value is not a SHA-256 digest.</exception>
        public static string GetObjectRelativePath(string sha256)
        {
            var digest = NormalizeDigest(sha256) ?? throw new ArgumentException("A SHA-256 digest is required.", nameof(sha256));
            return Path.Combine("Cache", "Objects", digest[..2], digest);
        }

        /// <summary>Reads only public source metadata without opening or hashing any media object.</summary>
        /// <param name="endpoints">The configured source identities allowed to own returned charts.</param>
        /// <param name="cancellationToken">Cancels the short SQLite read.</param>
        /// <returns>Online descriptors whose local resources can be checked when their cards become visible.</returns>
        /// <exception cref="SqliteException">The existing runtime database cannot be read.</exception>
        /// <exception cref="OperationCanceledException">The operation is cancelled.</exception>
        public async Task<IReadOnlyList<MusicTrack>> ReadMetadataOnlyAsync(IReadOnlyList<EndpointConfiguration> endpoints, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(endpoints);
            cancellationToken.ThrowIfCancellationRequested();
            var database = Path.Combine(_root, "Runtime", "runtime.db");
            if (!File.Exists(database) || endpoints.Count == 0)
            {
                return Array.Empty<MusicTrack>();
            }

            var entries = await ReadMetadataAsync(database, endpoints, cancellationToken).ConfigureAwait(false);
            return entries.Select(entry => entry.Track).ToArray();
        }

        /// <summary>Reads chart metadata scoped to explicitly configured endpoints and attaches only verified shared files.</summary>
        /// <param name="endpoints">Existing MajMusic endpoint identities; no accounts or sessions are opened.</param>
        /// <param name="cancellationToken">Cancels catalog reads and file hashing.</param>
        /// <returns>Online entries retaining their server, endpoint, chart and revision identities.</returns>
        /// <exception cref="SqliteException">The existing runtime database cannot be read.</exception>
        /// <exception cref="OperationCanceledException">The operation is cancelled.</exception>
        public async Task<IReadOnlyList<MusicTrack>> ReadAsync(IReadOnlyList<EndpointConfiguration> endpoints, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(endpoints);
            cancellationToken.ThrowIfCancellationRequested();
            var database = Path.Combine(_root, "Runtime", "runtime.db");
            if (!File.Exists(database) || endpoints.Count == 0)
            {
                return Array.Empty<MusicTrack>();
            }

            // Close the read transaction before hashing large files, so a game using a rollback journal can keep writing.
            var entries = await ReadMetadataAsync(database, endpoints, cancellationToken).ConfigureAwait(false);
            var result = new List<MusicTrack>(entries.Count);
            foreach (var entry in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var track = await AttachResourcesAsync(entry.Track, entry.Endpoint, cancellationToken).ConfigureAwait(false);
                if (track.Cover?.LocalPath is null && entry.OriginalCoverSha256 is { } original)
                {
                    var resource = new MediaResource
                    {
                        RemoteUri = new Uri(ApiRoot(entry.Endpoint), $"maichart/{Uri.EscapeDataString(track.RemoteId!)}/image?fullimage=true"),
                        CacheVersion = track.Revision,
                        Sha256 = original,
                    };
                    var resolved = await FindResourceAsync(track, entry.Endpoint, resource, ["bg_fullSize.jpg"], cancellationToken).ConfigureAwait(false);
                    if (resolved is not null)
                    {
                        track = track with { Cover = resolved };
                    }
                }

                result.Add(track);
            }

            return result;
        }

        /// <summary>Looks up a known online entry in canonical objects and in its endpoint's legacy cache.</summary>
        /// <param name="track">The existing online entry, including its chart revision.</param>
        /// <param name="endpoint">The explicitly configured endpoint owning this entry.</param>
        /// <param name="cancellationToken">Cancels file reads and digest verification.</param>
        /// <returns>The entry with verified local resource paths; its online identities remain unchanged.</returns>
        /// <exception cref="OperationCanceledException">The operation is cancelled.</exception>
        public async Task<MusicTrack> AttachResourcesAsync(MusicTrack track, EndpointConfiguration endpoint, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(track);
            ArgumentNullException.ThrowIfNull(endpoint);
            cancellationToken.ThrowIfCancellationRequested();
            if (track.RemoteId is null || track.EndpointId != endpoint.Id)
            {
                return track;
            }

            var audio = await FindResourceAsync(track, endpoint, track.Audio, TrackNames, cancellationToken).ConfigureAwait(false);
            var cover = track.Cover is { } descriptor
                ? await FindResourceAsync(track, endpoint, descriptor, CoverNames, cancellationToken).ConfigureAwait(false) : null;
            var chart = track.Chart is { } chartDescriptor
                ? await FindResourceAsync(track, endpoint, chartDescriptor, ["maidata.txt"], cancellationToken).ConfigureAwait(false) : null;
            return audio is null && cover is null && chart is null ? track : track with { Audio = audio ?? track.Audio, Cover = cover ?? track.Cover, Chart = chart ?? track.Chart };
        }

        /// <summary>Copies only public chart rows into application-owned objects during one read-only SQLite snapshot.</summary>
        /// <param name="database">The existing runtime filename.</param>
        /// <param name="endpoints">Endpoint identities allowed to own the copied rows.</param>
        /// <param name="token">Cancels SQLite reads.</param>
        /// <returns>Entries whose file validation can run after the database connection closes.</returns>
        private static async Task<List<CatalogEntry>> ReadMetadataAsync(string database, IReadOnlyList<EndpointConfiguration> endpoints, CancellationToken token)
        {
            await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = database,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false,
                DefaultTimeout = 2,
            }.ToString());
            await connection.OpenAsync(token).ConfigureAwait(false);
            using var transaction = connection.BeginTransaction(deferred: true);
            var columns = await ReadColumnsAsync(connection, transaction, "OnlineChartCache", token).ConfigureAwait(false);
            var result = new List<CatalogEntry>();
            if (!columns.Contains("ChartId") || !columns.Contains("Detail") || !(columns.Contains("Server") || columns.Contains("Endpoint")))
            {
                return result;
            }

            var mappings = await ReadServerMappingsAsync(connection, transaction, token).ConfigureAwait(false);
            var owners = new Dictionary<string, List<EndpointConfiguration>>(StringComparer.Ordinal);
            foreach (var endpoint in endpoints)
            {
                AddOwner(owners, endpoint.BaseUri.AbsoluteUri, endpoint);
                if (!string.IsNullOrWhiteSpace(endpoint.ServerId))
                {
                    AddOwner(owners, endpoint.ServerId, endpoint);
                }

                foreach (var mapping in mappings)
                {
                    if (SameEndpoint(mapping.Key, endpoint.BaseUri))
                    {
                        AddOwner(owners, mapping.Value, endpoint);
                    }
                }
            }

            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            var scope = columns.Contains("Server") ? "Server" : "Endpoint";
            command.CommandText = $"SELECT ChartId, [{scope}], {SelectColumn(columns, "ChartHash")}, Detail, {SelectColumn(columns, "TrackSha256")}, {SelectColumn(columns, "ThumbnailSha256")}, {SelectColumn(columns, "CoverArtSha256")}, {SelectColumn(columns, "MaidataSha256")} FROM OnlineChartCache ORDER BY {(columns.Contains("Id") ? "Id" : "ChartId")}";
            await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
            while (await reader.ReadAsync(token).ConfigureAwait(false))
            {
                var server = ReadText(reader, 1);
                if (!owners.TryGetValue(server, out var matched))
                {
                    matched = endpoints.Where(endpoint => SameEndpoint(server, endpoint.BaseUri)).ToList();
                    if (matched.Count == 0)
                    {
                        continue;
                    }
                }

                try
                {
                    using var document = JsonDocument.Parse(ReadText(reader, 3));
                    var metadata = document.RootElement;
                    var remoteId = ReadText(reader, 0);
                    if (metadata.ValueKind != JsonValueKind.Object || string.IsNullOrWhiteSpace(remoteId))
                    {
                        continue;
                    }

                    var revision = Text(metadata, "hash");
                    if (string.IsNullOrEmpty(revision))
                    {
                        revision = ReadText(reader, 2);
                    }

                    foreach (var endpoint in matched)
                    {
                        var source = SameEndpoint(server, endpoint.BaseUri) ? endpoint.ServerId ?? endpoint.Id : server;
                        var route = $"maichart/{Uri.EscapeDataString(remoteId)}/";
                        var thumbnail = NormalizeDigest(ReadText(reader, 5));
                        var original = NormalizeDigest(ReadText(reader, 6));
                        var track = new MusicTrack
                        {
                            Id = $"majnet:{Uri.EscapeDataString(source)}:{Uri.EscapeDataString(endpoint.Id)}:{Uri.EscapeDataString(remoteId)}",
                            RemoteId = remoteId,
                            SourceId = source,
                            EndpointId = endpoint.Id,
                            SourceName = endpoint.Name,
                            Revision = revision,
                            SourceTimestamp = Text(metadata, "timestamp"),
                            MajChartHash = IsChartHash(revision) ? revision : null,
                            Title = Text(metadata, "title"),
                            Artist = Text(metadata, "artist"),
                            Description = Text(metadata, "description"),
                            Tags = Strings(metadata, "tags").Concat(Strings(metadata, "publicTags")).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                            Audio = new MediaResource { RemoteUri = new Uri(ApiRoot(endpoint), route + "track"), CacheVersion = revision, Sha256 = NormalizeDigest(ReadText(reader, 4)) },
                            Cover = new MediaResource { RemoteUri = new Uri(ApiRoot(endpoint), route + (thumbnail is null && original is not null ? "image?fullimage=true" : "image")), CacheVersion = revision, Sha256 = thumbnail ?? original },
                            Chart = new MediaResource { RemoteUri = new Uri(ApiRoot(endpoint), route + "chart"), CacheVersion = revision, Sha256 = NormalizeDigest(ReadText(reader, 7)) },
                        };
                        result.Add(new CatalogEntry(track, endpoint, original));
                    }
                }
                catch (JsonException)
                {
                    // A malformed chart row cannot prevent the remaining public catalog from being read.
                }
            }

            return result;
        }

        /// <summary>Reads a fixed public table's column names without running game migrations.</summary>
        /// <param name="connection">The read-only connection.</param>
        /// <param name="transaction">The current read snapshot.</param>
        /// <param name="table">One of this reader's fixed public table names.</param>
        /// <param name="token">Cancels the schema read.</param>
        /// <returns>The available column names.</returns>
        private static async Task<HashSet<string>> ReadColumnsAsync(SqliteConnection connection, SqliteTransaction transaction, string table, CancellationToken token)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = $"PRAGMA table_info([{table}])";
            var columns = new HashSet<string>(StringComparer.Ordinal);
            await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
            while (await reader.ReadAsync(token).ConfigureAwait(false))
            {
                columns.Add(reader.GetString(1));
            }

            return columns;
        }

        /// <summary>Reads only endpoint-to-server identities; no session or account tables are accessed.</summary>
        /// <param name="connection">The read-only connection.</param>
        /// <param name="transaction">The current read snapshot.</param>
        /// <param name="token">Cancels the mapping read.</param>
        /// <returns>Absolute endpoint URLs and their public server identifiers.</returns>
        private static async Task<Dictionary<string, string>> ReadServerMappingsAsync(SqliteConnection connection, SqliteTransaction transaction, CancellationToken token)
        {
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            var columns = await ReadColumnsAsync(connection, transaction, "OnlineServers", token).ConfigureAwait(false);
            if (columns.Contains("Endpoint") && columns.Contains("Server"))
            {
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = "SELECT Endpoint, Server FROM OnlineServers";
                await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
                while (await reader.ReadAsync(token).ConfigureAwait(false))
                {
                    result[ReadText(reader, 0)] = ReadText(reader, 1);
                }
            }

            return result;
        }

        /// <summary>Resolves canonical content first, then stable legacy files bound to the exact original chart bytes.</summary>
        /// <param name="track">The owning chart and revision.</param>
        /// <param name="endpoint">The configured legacy directory name.</param>
        /// <param name="resource">The resource's known content digest.</param>
        /// <param name="legacyNames">Completed filenames accepted for this resource.</param>
        /// <param name="token">Cancels resource hashing.</param>
        /// <returns>A verified descriptor, or null when no complete matching file exists.</returns>
        private async Task<MediaResource?> FindResourceAsync(MusicTrack track, EndpointConfiguration endpoint, MediaResource resource, string[] legacyNames, CancellationToken token)
        {
            var expected = NormalizeDigest(resource.Sha256);
            if (expected is not null)
            {
                var stored = await VerifyAsync(Path.Combine(_root, GetObjectRelativePath(expected)), expected, token).ConfigureAwait(false);
                if (stored is not null)
                {
                    return resource with { LocalPath = stored.Path, Sha256 = stored.Sha256, Md5 = stored.Md5, Length = stored.Length };
                }
            }

            var candidates = LegacyCandidates(endpoint, track.Revision);
            VerifiedFile? selected = null;
            foreach (var candidate in candidates)
            {
                token.ThrowIfCancellationRequested();
                var chart = await VerifyAsync(Path.Combine(candidate.Directory, "maidata.txt"), null, token).ConfigureAwait(false);
                if (chart is null || Convert.ToBase64String(Convert.FromHexString(chart.Md5)) != track.Revision ||
                    !Path.GetFileName(candidate.Directory).Equals(BitConverter.ToString(Convert.FromHexString(chart.Md5)), StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                VerifiedFile? resourceFile = null;
                foreach (var name in legacyNames)
                {
                    var path = Path.Combine(candidate.Directory, name);
                    var sidecar = path + ".sha256";
                    var digest = expected ?? await ReadSidecarAsync(sidecar, token).ConfigureAwait(false);
                    if (expected is null && File.Exists(sidecar) && digest is null)
                    {
                        continue;
                    }

                    var file = await VerifyAsync(path, digest, token).ConfigureAwait(false);
                    if (file is not null && (resource.Length is not > 0 || resource.Length == file.Length) &&
                        (NormalizeMd5(resource.Md5) is not { } md5 || md5 == file.Md5))
                    {
                        resourceFile = file;
                        break;
                    }
                }

                if (resourceFile is null)
                {
                    continue;
                }

                if (candidate.ExactEndpoint || expected is not null)
                {
                    return resource with { LocalPath = resourceFile.Path, Sha256 = resourceFile.Sha256, Md5 = resourceFile.Md5, Length = resourceFile.Length };
                }

                if (selected is not null && selected.Sha256 != resourceFile.Sha256)
                {
                    // A matching chart alone cannot choose between different anonymous servers' audio or artwork.
                    return null;
                }

                selected = resourceFile;
            }

            return selected is null ? null : resource with { LocalPath = selected.Path, Sha256 = selected.Sha256, Md5 = selected.Md5, Length = selected.Length };
        }

        /// <summary>Verifies a completed file without modifying or promoting any partial download.</summary>
        /// <param name="path">The candidate complete filename.</param>
        /// <param name="expected">The expected SHA-256, or null only when a legacy chart identity is verified separately.</param>
        /// <param name="token">Cancels hashing.</param>
        /// <returns>The verified path and length, or null when absent, unreadable or mismatched.</returns>
        private static async Task<VerifiedFile?> VerifyAsync(string path, string? expected, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (!IsRegularEntry(path, false))
            {
                return null;
            }

            try
            {
                var before = new FileInfo(path);
                var modified = before.LastWriteTimeUtc.Ticks;
                var originalLength = before.Length;
                await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
                var length = stream.Length;
                using var sha256 = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                using var md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
                var buffer = ArrayPool<byte>.Shared.Rent(81920);
                try
                {
                    int read;
                    long received = 0;
                    while ((read = await stream.ReadAsync(buffer.AsMemory(0, 81920), token).ConfigureAwait(false)) > 0)
                    {
                        received += read;
                        sha256.AppendData(buffer, 0, read);
                        md5.AppendData(buffer, 0, read);
                    }

                    var digest = Convert.ToHexStringLower(sha256.GetHashAndReset());
                    var md5Digest = Convert.ToHexStringLower(md5.GetHashAndReset());
                    before.Refresh();
                    return length > 0 && received == length && originalLength == length && stream.Length == length && before.Exists &&
                        before.Length == length && before.LastWriteTimeUtc.Ticks == modified && (before.Attributes & FileAttributes.ReparsePoint) == 0 &&
                        (expected is null || digest == expected) ? new VerifiedFile(path, digest, md5Digest, length) : null;
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }

        /// <summary>Prioritizes the configured endpoint name, then checks ordinary sibling endpoint folders for this chart hash.</summary>
        /// <param name="endpoint">The configured friendly name.</param>
        /// <param name="revision">The exact Base64 MD5 chart identity.</param>
        /// <returns>Only non-reparse chart directories beneath the selected legacy Net root.</returns>
        private IReadOnlyList<(string Directory, bool ExactEndpoint)> LegacyCandidates(EndpointConfiguration endpoint, string? revision)
        {
            var result = new List<(string Directory, bool ExactEndpoint)>();
            var cache = Path.Combine(_root, "Cache");
            var net = Path.Combine(cache, "Net");
            if (!IsChartHash(revision) || !IsRegularEntry(_root, true) || !IsRegularEntry(cache, true) || !IsRegularEntry(net, true))
            {
                return result;
            }

            var key = BitConverter.ToString(Convert.FromBase64String(revision!));
            var exact = LegacyDirectory(endpoint, revision);
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (exact is not null && IsRegularEntry(Path.GetDirectoryName(exact)!, true) && IsRegularEntry(exact, true))
            {
                result.Add((exact, true));
            }

            try
            {
                foreach (var directory in Directory.EnumerateDirectories(net, "*", new EnumerationOptions { AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = true }))
                {
                    var child = Path.Combine(directory, key);
                    if (!string.Equals(child, exact, comparison) && IsRegularEntry(directory, true) && IsRegularEntry(child, true))
                    {
                        result.Add((child, false));
                    }
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // An inaccessible sibling does not invalidate a verified explicitly named endpoint directory.
            }

            return result;
        }

        /// <summary>Rejects links, junctions and incorrect entry kinds before any shared resource is opened.</summary>
        /// <param name="path">The candidate directory or completed file.</param>
        /// <param name="directory">Whether a directory is required.</param>
        /// <returns>Whether the named entry is an ordinary file or directory.</returns>
        private static bool IsRegularEntry(string path, bool directory)
        {
            try
            {
                var attributes = File.GetAttributes(path);
                return (attributes & FileAttributes.ReparsePoint) == 0 && ((attributes & FileAttributes.Directory) != 0) == directory;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                return false;
            }
        }

        /// <summary>Reads a legacy Base64 SHA-256 sidecar while rejecting incomplete or malformed metadata.</summary>
        /// <param name="path">The completed resource's sidecar filename.</param>
        /// <param name="token">Cancels the read.</param>
        /// <returns>The digest in lowercase hexadecimal, or null when unavailable.</returns>
        private static async Task<string?> ReadSidecarAsync(string path, CancellationToken token)
        {
            if (!IsRegularEntry(path, false))
            {
                return null;
            }

            try
            {
                var info = new FileInfo(path);
                if (!info.Exists || info.Length > 256)
                {
                    return null;
                }

                var value = (await File.ReadAllTextAsync(path, token).ConfigureAwait(false)).Trim();
                var bytes = Convert.FromBase64String(value);
                return bytes.Length == 32 ? Convert.ToHexStringLower(bytes) : null;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or FormatException)
            {
                return null;
            }
        }

        /// <summary>Calculates the legacy uppercase hyphenated chart-hash directory without escaping the selected root.</summary>
        /// <param name="endpoint">The endpoint display name used by the game.</param>
        /// <param name="revision">The Base64 whole-file chart hash.</param>
        /// <returns>The legacy directory, or null when its scope or hash is invalid.</returns>
        private string? LegacyDirectory(EndpointConfiguration endpoint, string? revision)
        {
            if (!IsChartHash(revision) || string.IsNullOrWhiteSpace(endpoint.Name) || endpoint.Name.IndexOfAny(['/', '\\']) >= 0 || endpoint.Name is "." or "..")
            {
                return null;
            }

            try
            {
                var path = Path.GetFullPath(Path.Combine(_root, "Cache", "Net", endpoint.Name, BitConverter.ToString(Convert.FromBase64String(revision!))));
                var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
                return path.StartsWith(Path.TrimEndingDirectorySeparator(_root) + Path.DirectorySeparatorChar, comparison) ? path : null;
            }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
            {
                return null;
            }
        }

        /// <summary>Registers each endpoint only once for a public server key.</summary>
        /// <param name="owners">The owner index.</param>
        /// <param name="server">The public server identifier or endpoint URL.</param>
        /// <param name="endpoint">The configured session scope.</param>
        private static void AddOwner(Dictionary<string, List<EndpointConfiguration>> owners, string server, EndpointConfiguration endpoint)
        {
            if (!owners.TryGetValue(server, out var list))
            {
                list = [];
                owners.Add(server, list);
            }

            if (!list.Any(item => item.Id == endpoint.Id))
            {
                list.Add(endpoint);
            }
        }

        /// <summary>Compares endpoint URLs while preserving case-sensitive API paths.</summary>
        /// <param name="value">The stored endpoint or server key.</param>
        /// <param name="endpoint">The configured API root.</param>
        /// <returns>Whether both identify the same API address.</returns>
        private static bool SameEndpoint(string value, Uri endpoint)
        {
            return Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme.Equals(endpoint.Scheme, StringComparison.OrdinalIgnoreCase) &&
                uri.Authority.Equals(endpoint.Authority, StringComparison.OrdinalIgnoreCase) && uri.AbsolutePath.TrimEnd('/') == endpoint.AbsolutePath.TrimEnd('/') && uri.Query == endpoint.Query;
        }

        /// <summary>Normalizes an API root for relative chart routes.</summary>
        /// <param name="endpoint">The configured API address.</param>
        /// <returns>The API address with a trailing slash.</returns>
        private static Uri ApiRoot(EndpointConfiguration endpoint)
        {
            return new Uri(endpoint.BaseUri.AbsoluteUri.TrimEnd('/') + '/');
        }

        /// <summary>Checks the legacy Base64 MD5 chart identity before constructing a filesystem path.</summary>
        /// <param name="value">The chart identity.</param>
        /// <returns>Whether it contains exactly one MD5 digest.</returns>
        private static bool IsChartHash(string? value)
        {
            Span<byte> buffer = stackalloc byte[16];
            return value is not null && Convert.TryFromBase64String(value, buffer, out var count) && count == 16;
        }

        /// <summary>Normalizes a complete hexadecimal SHA-256 digest.</summary>
        /// <param name="value">The candidate digest.</param>
        /// <returns>A lowercase digest or null.</returns>
        private static string? NormalizeDigest(string? value)
        {
            return value is { Length: 64 } && value.All(char.IsAsciiHexDigit) ? value.ToLowerInvariant() : null;
        }

        /// <summary>Projects an optional known schema column without requiring a game migration.</summary>
        /// <param name="columns">The existing schema.</param>
        /// <param name="name">A fixed reader-owned column name.</param>
        /// <returns>The selected column or a null literal.</returns>
        private static string SelectColumn(HashSet<string> columns, string name)
        {
            return columns.Contains(name) ? $"[{name}]" : "NULL";
        }

        /// <summary>Reads optional database text without converting a null database value.</summary>
        /// <param name="reader">The current public chart row.</param>
        /// <param name="ordinal">The selected column index.</param>
        /// <returns>The text or an empty string.</returns>
        private static string ReadText(SqliteDataReader reader, int ordinal)
        {
            return reader.IsDBNull(ordinal) ? string.Empty : reader.GetString(ordinal);
        }

        /// <summary>Reads a public string field from chart metadata.</summary>
        /// <param name="element">The metadata object.</param>
        /// <param name="name">The camel-case field name.</param>
        /// <returns>The string or an empty default.</returns>
        private static string Text(JsonElement element, string name)
        {
            return element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : string.Empty;
        }

        /// <summary>Reads optional public song tags from a metadata array.</summary>
        /// <param name="element">The metadata object.</param>
        /// <param name="name">The array field name.</param>
        /// <returns>The nonempty string tags.</returns>
        private static IEnumerable<string> Strings(JsonElement element, string name)
        {
            if (element.TryGetProperty(name, out var array) && array.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in array.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 } value)
                    {
                        yield return value;
                    }
                }
            }
        }

        /// <summary>Retains copied public metadata and the optional original-cover fallback.</summary>
        /// <param name="Track">The online entry without unverified local paths.</param>
        /// <param name="Endpoint">The configured owner.</param>
        /// <param name="OriginalCoverSha256">The optional full-resolution cover digest.</param>
        private sealed record CatalogEntry(MusicTrack Track, EndpointConfiguration Endpoint, string? OriginalCoverSha256);
        /// <summary>Identifies one complete resource whose bytes match its expected digest.</summary>
        /// <param name="Path">The verified resource filename.</param>
        /// <param name="Sha256">The verified digest.</param>
        /// <param name="Md5">The hexadecimal MD5 digest used by MajdataPlay's resource mapping.</param>
        /// <param name="Length">The complete resource byte count.</param>
        private sealed record VerifiedFile(string Path, string Sha256, string Md5, long Length);
    }
}

#nullable enable

using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;
using MajMusic.Core.Models;

namespace MajMusic.Infrastructure.Collections
{
    /// <summary>Reads Maj's portable collection JSON and writes compatible JSON only to a caller-owned stream.</summary>
    /// <remarks>SongHashs identifies original maidata bytes, never music titles, audio hashes or filesystem paths.</remarks>
    public sealed class MajCollectionService
    {
        /// <summary>Identifies the built-in favorites view consistently across portable imports.</summary>
        public const string FavoritesPlaylistId = "local:favorites";

        /// <summary>Defines the portable favorites file name used by MajdataPlay.</summary>
        public const string FavoritesFileName = "MyFavorites.json";

        /// <summary>Limits a single local collection document while allowing large libraries.</summary>
        private const long MaximumDocumentBytes = 16L * 1024 * 1024;

        /// <summary>Writes readable standard PascalCase portable JSON without altering Unicode names.</summary>
        private static readonly JsonSerializerOptions ExportOptions = new()
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.Create(UnicodeRanges.All)
        };

        /// <summary>Reads one portable playlist or favorites file without modifying it.</summary>
        /// <param name="filePath">The explicitly selected collection file.</param>
        /// <param name="tracks">The indexed music entries used to resolve chart identities.</param>
        /// <param name="cancellationToken">Cancels reading and resolution.</param>
        /// <returns>The playlist with its original unknown fields and unresolved references retained.</returns>
        /// <exception cref="InvalidDataException">The collection JSON does not satisfy the portable format.</exception>
        /// <exception cref="IOException">The selected file cannot be read.</exception>
        public async Task<MusicPlaylist> ReadFileAsync(string filePath, IReadOnlyList<MusicTrack> tracks,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
            ArgumentNullException.ThrowIfNull(tracks);
            cancellationToken.ThrowIfCancellationRequested();
            var path = Path.GetFullPath(filePath);
            var file = new FileInfo(path);
            if (file.Length > MaximumDocumentBytes)
            {
                throw new InvalidDataException("The collection document is too large.");
            }

            if ((file.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException("Collection links are not followed outside the selected directory.");
            }

            try
            {
                await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                    8192, FileOptions.Asynchronous | FileOptions.SequentialScan);
                using var reader = new StreamReader(stream, new UTF8Encoding(false, true), true);
                var text = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
                using var document = JsonDocument.Parse(text);
                return Resolve(document.RootElement, path, tracks, cancellationToken);
            }
            catch (Exception exception) when (exception is JsonException or DecoderFallbackException)
            {
                throw new InvalidDataException("The collection document is not valid JSON text.", exception);
            }
        }

        /// <summary>Reads a selected song root's majcollection directory and optional root favorites export.</summary>
        /// <param name="directory">A song root or the majcollection directory itself.</param>
        /// <param name="tracks">The indexed music entries used for exact chart-hash resolution.</param>
        /// <param name="cancellationToken">Cancels enumeration and reading.</param>
        /// <returns>The independently loaded playlists and per-file issues.</returns>
        /// <exception cref="DirectoryNotFoundException">The selected root does not exist.</exception>
        /// <exception cref="IOException">The selected root cannot be enumerated.</exception>
        public async Task<MajCollectionImportResult> ReadDirectoryAsync(string directory, IReadOnlyList<MusicTrack> tracks,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(directory);
            ArgumentNullException.ThrowIfNull(tracks);
            cancellationToken.ThrowIfCancellationRequested();
            var root = Path.GetFullPath(directory);
            if (!Directory.Exists(root))
            {
                throw new DirectoryNotFoundException("The collection directory does not exist.");
            }

            var candidates = new List<string>();
            var issues = new List<MajCollectionImportIssue>();
            var isCollectionDirectory = string.Equals(Path.GetFileName(Path.TrimEndingDirectorySeparator(root)),
                "majcollection", StringComparison.OrdinalIgnoreCase);
            var collectionDirectory = isCollectionDirectory ? root : FindChildDirectory(root, "majcollection");
            if (collectionDirectory is not null)
            {
                try
                {
                    if ((File.GetAttributes(collectionDirectory) & FileAttributes.ReparsePoint) == 0)
                    {
                        foreach (var file in Directory.EnumerateFiles(collectionDirectory))
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            if (string.Equals(Path.GetExtension(file), ".json", StringComparison.OrdinalIgnoreCase))
                            {
                                candidates.Add(file);
                            }
                        }
                    }
                    else
                    {
                        issues.Add(Issue(collectionDirectory, "Collection directory links are not followed."));
                    }
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
                {
                    issues.Add(Issue(collectionDirectory, "The collection directory could not be read."));
                }
            }

            if (!isCollectionDirectory)
            {
                foreach (var file in Directory.EnumerateFiles(root))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (string.Equals(Path.GetFileName(file), FavoritesFileName, StringComparison.OrdinalIgnoreCase))
                    {
                        candidates.Add(file);
                    }
                }
            }

            var playlists = new List<MusicPlaylist>();
            foreach (var candidate in candidates.OrderBy(path => path, StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    playlists.Add(await ReadFileAsync(candidate, tracks, cancellationToken).ConfigureAwait(false));
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
                {
                    issues.Add(Issue(candidate, exception is InvalidDataException ? exception.Message : "The collection file could not be read."));
                }
            }

            return new MajCollectionImportResult { Playlists = playlists, Issues = issues };
        }

        /// <summary>Reads an explicitly selected index folder, including direct JSON files regardless of its name.</summary>
        /// <param name="directory">The read-only index folder or a Maj root containing majcollection.</param>
        /// <param name="tracks">The current music index used to resolve portable identities.</param>
        /// <param name="cancellationToken">Cancels enumeration and document reads.</param>
        /// <returns>All compatible direct and conventional Maj index documents with isolated failures.</returns>
        /// <exception cref="DirectoryNotFoundException">The selected directory does not exist.</exception>
        /// <exception cref="IOException">The selected root cannot be inspected.</exception>
        public async Task<MajCollectionImportResult> ReadIndexDirectoryAsync(string directory, IReadOnlyList<MusicTrack> tracks,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(directory);
            ArgumentNullException.ThrowIfNull(tracks);
            cancellationToken.ThrowIfCancellationRequested();
            var root = Path.GetFullPath(directory);
            if (!Directory.Exists(root))
            {
                throw new DirectoryNotFoundException("The collection directory does not exist.");
            }

            if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
            {
                return new MajCollectionImportResult { Issues = [Issue(root, "Collection directory links are not followed.")] };
            }

            var conventional = await ReadDirectoryAsync(root, tracks, cancellationToken).ConfigureAwait(false);
            var playlists = conventional.Playlists.ToList();
            var issues = conventional.Issues.ToList();
            var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
            var known = playlists.Select(item => item.MajCollection?.SourceFilePath).Where(path => path is not null)
                .Concat(conventional.Issues.Select(issue => issue.Path)).ToHashSet(comparer);
            foreach (var candidate in Directory.EnumerateFiles(root).OrderBy(path => path, StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!string.Equals(Path.GetExtension(candidate), ".json", StringComparison.OrdinalIgnoreCase) || known.Contains(candidate))
                {
                    continue;
                }

                try
                {
                    playlists.Add(await ReadFileAsync(candidate, tracks, cancellationToken).ConfigureAwait(false));
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
                {
                    issues.Add(Issue(candidate, exception is InvalidDataException ? exception.Message : "The collection file could not be read."));
                }
            }

            return new MajCollectionImportResult { Playlists = playlists, Issues = issues };
        }

        /// <summary>Writes standard Maj-compatible JSON to an explicitly supplied destination stream.</summary>
        /// <param name="destination">The caller-owned writable destination; this service never opens an output path.</param>
        /// <param name="playlist">The current ordered local playlist or favorites view.</param>
        /// <param name="tracks">Current metadata used to export portable chart identities.</param>
        /// <param name="cancellationToken">Cancels validation or output.</param>
        /// <returns>A task representing the completed write without closing the destination.</returns>
        /// <exception cref="InvalidDataException">A current playlist entry has no exportable Maj identity.</exception>
        /// <exception cref="IOException">The caller-owned destination cannot be written.</exception>
        public async Task WriteAsync(Stream destination, MusicPlaylist playlist, IReadOnlyList<MusicTrack> tracks,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(destination);
            ArgumentNullException.ThrowIfNull(playlist);
            ArgumentNullException.ThrowIfNull(tracks);
            cancellationToken.ThrowIfCancellationRequested();
            if (!destination.CanWrite)
            {
                throw new ArgumentException("The collection destination must be writable.", nameof(destination));
            }

            var current = tracks.GroupBy(track => track.Id, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
            var retained = playlist.MajCollection?.Entries.Where(entry => entry.TrackId is not null)
                .GroupBy(entry => entry.TrackId!, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.First().Hash, StringComparer.Ordinal)
                ?? new Dictionary<string, string>(StringComparer.Ordinal);
            var resolvedHashes = new Queue<string>();
            foreach (var trackId in playlist.TrackIds)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var hash = current.TryGetValue(trackId, out var track) ? GetChartHash(track) : null;
                if (hash is null && !retained.TryGetValue(trackId, out hash))
                {
                    throw new InvalidDataException("The playlist contains music without a Maj chart identity; no entries were silently omitted.");
                }

                resolvedHashes.Enqueue(hash);
            }

            var hashes = new List<string>();
            if (playlist.MajCollection is { } definition)
            {
                foreach (var entry in definition.Entries)
                {
                    if (entry.TrackId is null)
                    {
                        hashes.Add(entry.Hash);
                    }
                    else if (resolvedHashes.TryDequeue(out var hash))
                    {
                        hashes.Add(hash);
                    }
                }
            }

            while (resolvedHashes.TryDequeue(out var hash))
            {
                hashes.Add(hash);
            }

            var output = new Dictionary<string, object?>(StringComparer.Ordinal) { ["Name"] = playlist.Name };
            if (playlist.MajCollection is { } original)
            {
                foreach (var property in original.AdditionalProperties)
                {
                    if (!IsKnownProperty(property.Key))
                    {
                        output[property.Key] = property.Value;
                    }
                }
            }

            output["SongHashs"] = hashes;
            if (!playlist.IsCloud && IsPortableLocalPlaylistId(playlist.Id))
            {
                output["MajMusicPlaylistId"] = playlist.Id;
                output["MajMusicIsFavorite"] = playlist.IsFavorite;
            }
            var isPlayList = playlist.IsFavorite ? true : playlist.MajCollection?.IsPlayList;
            if (isPlayList.HasValue || playlist.MajCollection is null)
            {
                output["IsPlayList"] = isPlayList ?? true;
            }

            // Validate and serialize everything before touching a caller-owned output stream.
            var bytes = JsonSerializer.SerializeToUtf8Bytes(output, ExportOptions);
            cancellationToken.ThrowIfCancellationRequested();
            await destination.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>Validates and resolves one portable document without changing its unknown fields.</summary>
        /// <param name="document">The parsed portable object.</param>
        /// <param name="path">The original source path used only for local identity and favorites recognition.</param>
        /// <param name="tracks">The currently indexed entries.</param>
        /// <param name="cancellationToken">Cancels resolution.</param>
        /// <returns>The local playlist snapshot retaining every source reference.</returns>
        /// <exception cref="InvalidDataException">The portable fields have invalid types or omit required values.</exception>
        private static MusicPlaylist Resolve(JsonElement document, string path, IReadOnlyList<MusicTrack> tracks,
            CancellationToken cancellationToken)
        {
            if (document.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException("The collection must be a JSON object.");
            }

            var properties = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
            var seenNames = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in document.EnumerateObject())
            {
                if (!seenNames.Add(property.Name) || (IsKnownProperty(property.Name) && !properties.TryAdd(property.Name, property.Value)))
                {
                    throw new InvalidDataException("The collection contains ambiguous duplicate field names.");
                }
            }

            if (!properties.TryGetValue("Name", out var name) || name.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(name.GetString()) || !properties.TryGetValue("SongHashs", out var hashes)
                || hashes.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidDataException("The collection needs a name and a SongHashs array.");
            }

            bool? isPlayList = null;
            if (properties.TryGetValue("IsPlayList", out var flag))
            {
                if (flag.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                {
                    throw new InvalidDataException("IsPlayList must be a boolean when supplied.");
                }

                isPlayList = flag.GetBoolean();
            }

            var lookup = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var track in tracks.OrderBy(item => item.Audio.LocalPath ?? item.Id, StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (GetChartHash(track) is { } hash)
                {
                    lookup.TryAdd(hash, track.Id);
                }
            }

            var entries = new List<MajCollectionEntry>();
            var ids = new List<string>();
            foreach (var hash in hashes.EnumerateArray())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (hash.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(hash.GetString()))
                {
                    throw new InvalidDataException("SongHashs must contain nonempty string identities.");
                }

                var value = hash.GetString()!;
                lookup.TryGetValue(value, out var trackId);
                entries.Add(new MajCollectionEntry { Hash = value, TrackId = trackId });
                if (trackId is not null)
                {
                    ids.Add(trackId);
                }
            }

            var additional = document.EnumerateObject().Where(property => !IsKnownProperty(property.Name))
                .ToDictionary(property => property.Name, property => property.Value.Clone(), StringComparer.Ordinal);
            var favorite = string.Equals(Path.GetFileName(path), FavoritesFileName, StringComparison.OrdinalIgnoreCase);
            var normalizedPath = OperatingSystem.IsWindows() ? path.ToUpperInvariant() : path;
            var id = favorite ? FavoritesPlaylistId
                : "majcollection:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(normalizedPath)));
            if (!favorite && properties.TryGetValue("MajMusicPlaylistId", out var localId))
            {
                if (localId.ValueKind != JsonValueKind.String || !IsPortableLocalPlaylistId(localId.GetString()))
                {
                    throw new InvalidDataException("The MajMusic playlist identity is not a supported local identity.");
                }
                id = localId.GetString()!;
                favorite = id == FavoritesPlaylistId;
                if (properties.TryGetValue("MajMusicIsFavorite", out var localFavorite))
                {
                    if (localFavorite.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                    {
                        throw new InvalidDataException("The MajMusic favorite flag must be a boolean.");
                    }
                    favorite |= localFavorite.GetBoolean();
                }
            }
            return new MusicPlaylist
            {
                Id = id,
                Name = name.GetString()!,
                IsFavorite = favorite,
                TrackIds = ids,
                MajCollection = new MajCollectionDefinition
                {
                    SourceFileName = Path.GetFileName(path),
                    SourceFilePath = path,
                    IsPlayList = isPlayList,
                    Entries = entries,
                    AdditionalProperties = additional
                }
            };
        }

        /// <summary>Reads a portable identity from local metadata or an existing remote source revision.</summary>
        /// <param name="track">The library entry.</param>
        /// <returns>The Base64 MD5 identity, or null when unavailable.</returns>
        private static string? GetChartHash(MusicTrack track)
        {
            var hash = track.MajChartHash;
            if (string.IsNullOrWhiteSpace(hash) && track.RemoteId is not null)
            {
                hash = track.Revision;
            }

            if (hash is null)
            {
                return null;
            }

            Span<byte> digest = stackalloc byte[16];
            return Convert.TryFromBase64String(hash, digest, out var written) && written == digest.Length ? hash : null;
        }

        /// <summary>Finds an immediate standard collection directory without assuming filesystem casing.</summary>
        /// <param name="root">The selected song root.</param>
        /// <param name="name">The expected child name.</param>
        /// <returns>The matching child directory, or null.</returns>
        private static string? FindChildDirectory(string root, string name)
        {
            return Directory.EnumerateDirectories(root).FirstOrDefault(path => string.Equals(Path.GetFileName(path), name,
                StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>Identifies fields managed explicitly by the compatibility adapter.</summary>
        /// <param name="name">The JSON property name.</param>
        /// <returns>Whether the field belongs to the portable playlist core.</returns>
        private static bool IsKnownProperty(string name)
        {
            return name.Equals("Name", StringComparison.OrdinalIgnoreCase)
                || name.Equals("SongHashs", StringComparison.OrdinalIgnoreCase)
                || name.Equals("IsPlayList", StringComparison.OrdinalIgnoreCase)
                || name.Equals("MajMusicPlaylistId", StringComparison.OrdinalIgnoreCase)
                || name.Equals("MajMusicIsFavorite", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Checks supported local identities without allowing a portable file to impersonate a cloud collection.</summary>
        /// <param name="id">The original local playlist identity.</param>
        /// <returns>Whether this identity can be restored from an application-owned portable mirror.</returns>
        public static bool IsPortableLocalPlaylistId(string? id)
        {
            return id is { Length: > 0 and <= 512 } && id == id.Trim()
                && !id.StartsWith("cloud:", StringComparison.OrdinalIgnoreCase) && !id.Any(char.IsControl);
        }

        /// <summary>Gets the collision-resistant app-owned mirror filename while leaving MyFavorites and imported sources untouched.</summary>
        /// <param name="id">The exact local playlist identity.</param>
        /// <returns>The generated JSON filename recognized by MajdataPlay's ordinary collection scanner.</returns>
        public static string GetPortableFileName(string id)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(id);
            return "MajMusic-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(id))) + ".json";
        }

        /// <summary>Creates a report without exposing source contents.</summary>
        /// <param name="path">The source that failed.</param>
        /// <param name="message">The short failure description.</param>
        /// <returns>The report entry.</returns>
        private static MajCollectionImportIssue Issue(string path, string message)
        {
            return new MajCollectionImportIssue { Path = path, Message = message };
        }
    }
}

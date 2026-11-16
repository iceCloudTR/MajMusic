#nullable enable

using MajMusic.Core.Abstractions;
using MajMusic.Core.Models;
using MajMusic.Infrastructure.Collections;

namespace MajMusic.Infrastructure.Application
{
    /// <summary>Persists compatible lists locally while keeping source directories and imported JSON read-only.</summary>
    public sealed partial class MajCollectionExchange : IMajCollectionExchange
    {
        /// <summary>Provides local library and playlist persistence.</summary>
        private readonly IMusicWorkspace _workspace;
        /// <summary>Reads and writes the Maj JSON contract independently of storage ownership.</summary>
        private readonly MajCollectionService _service = new();
        /// <summary>Reads only explicitly granted provider documents on mobile hosts.</summary>
        private readonly IReadOnlyDocumentStorage? _documents;

        /// <summary>Creates a collection exchange without touching any source.</summary>
        /// <param name="workspace">The local application workspace.</param>
        /// <param name="documentStorage">The optional read-only mobile document provider.</param>
        public MajCollectionExchange(IMusicWorkspace workspace, IReadOnlyDocumentStorage? documentStorage = null)
        {
            ArgumentNullException.ThrowIfNull(workspace);
            _workspace = workspace;
            _documents = documentStorage;
        }

        /// <inheritdoc />
        public async Task<MajCollectionImportResult> ImportDirectoryAsync(string directory, bool preserveExisting = true, CancellationToken cancellationToken = default)
        {
            var tracks = await _workspace.SearchAsync(null, cancellationToken).ConfigureAwait(false);
            var result = DocumentReference.IsContentUri(directory)
                ? await ReadDocumentDirectoryAsync(directory, false, tracks, cancellationToken).ConfigureAwait(false)
                : await _service.ReadDirectoryAsync(directory, tracks, cancellationToken).ConfigureAwait(false);
            return await PersistImportedCollectionsAsync(result, tracks, preserveExisting, cancellationToken).ConfigureAwait(false);
        }

        /// <inheritdoc />
        public async Task<MajCollectionImportResult> ImportIndexDirectoryAsync(string directory, bool preserveExisting = true, CancellationToken cancellationToken = default)
        {
            var tracks = await _workspace.SearchAsync(null, cancellationToken).ConfigureAwait(false);
            var result = DocumentReference.IsContentUri(directory)
                ? await ReadDocumentDirectoryAsync(directory, true, tracks, cancellationToken).ConfigureAwait(false)
                : await _service.ReadIndexDirectoryAsync(directory, tracks, cancellationToken).ConfigureAwait(false);
            return await PersistImportedCollectionsAsync(result, tracks, preserveExisting, cancellationToken).ConfigureAwait(false);
        }

        /// <inheritdoc />
        public async Task<int> ReconcileUnavailableEntriesAsync(CancellationToken cancellationToken = default)
        {
            var tracks = await _workspace.SearchAsync(null, cancellationToken).ConfigureAwait(false);
            var existing = (await _workspace.GetPlaylistsAsync(cancellationToken).ConfigureAwait(false))
                .ToDictionary(playlist => playlist.Id, StringComparer.Ordinal);
            return await ReconcileUnavailableEntriesAsync(tracks, existing, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>Publishes imported lists while preserving existing local edits and previously unavailable references.</summary>
        /// <param name="result">The parsed source documents.</param>
        /// <param name="tracks">The current indexed music metadata.</param>
        /// <param name="preserveExisting">Whether existing locally edited lists retain their contents.</param>
        /// <param name="cancellationToken">Cancels metadata writes.</param>
        /// <returns>The original source read result.</returns>
        private async Task<MajCollectionImportResult> PersistImportedCollectionsAsync(MajCollectionImportResult result,
            IReadOnlyList<MusicTrack> tracks, bool preserveExisting, CancellationToken cancellationToken)
        {
            var existing = (await _workspace.GetPlaylistsAsync(cancellationToken).ConfigureAwait(false))
                .ToDictionary(playlist => playlist.Id, StringComparer.Ordinal);
            if (preserveExisting)
            {
                await ReconcileUnavailableEntriesAsync(tracks, existing, cancellationToken).ConfigureAwait(false);
            }

            foreach (var playlist in result.Playlists)
            {
                if (preserveExisting && _workspace is IPlaylistManagementWorkspace management
                    && await management.IsPlaylistDeletedAsync(playlist.Id, cancellationToken).ConfigureAwait(false))
                {
                    continue;
                }

                var retain = preserveExisting && existing.TryGetValue(playlist.Id, out var previous)
                    && !(previous.IsFavorite && previous.TrackIds.Count == 0 && previous.MajCollection is null);
                if (!retain)
                {
                    await _workspace.SavePlaylistAsync(playlist, cancellationToken).ConfigureAwait(false);
                }
            }

            return result;
        }

        /// <summary>Resolves only retained unknown references against already indexed music.</summary>
        /// <param name="tracks">The current music metadata without source JSON reads.</param>
        /// <param name="existing">The local lists, updated in place after each completed save.</param>
        /// <param name="cancellationToken">Cancels resolution or local persistence.</param>
        /// <returns>The number of lists changed by newly available music.</returns>
        private async Task<int> ReconcileUnavailableEntriesAsync(IReadOnlyList<MusicTrack> tracks,
            Dictionary<string, MusicPlaylist> existing, CancellationToken cancellationToken)
        {
            var byHash = CreateHashLookup(tracks);
            var byId = tracks.ToDictionary(track => track.Id, StringComparer.Ordinal);
            var changed = 0;
            foreach (var previous in existing.Values.ToArray())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var resolved = ResolveUnavailableEntries(previous, byHash, byId);
                if (!ReferenceEquals(resolved, previous))
                {
                    await _workspace.SavePlaylistAsync(resolved, cancellationToken).ConfigureAwait(false);
                    existing[previous.Id] = resolved;
                    changed++;
                }
            }

            return changed;
        }

        /// <inheritdoc />
        public async Task<MusicPlaylist> ImportFileAsync(string filePath, CancellationToken cancellationToken = default)
        {
            var tracks = await _workspace.SearchAsync(null, cancellationToken).ConfigureAwait(false);
            var playlist = DocumentReference.IsContentUri(filePath)
                ? await ReadDocumentAsync(await (_documents ?? throw new NotSupportedException("此宿主未提供文档列表访问。"))
                    .GetFileReferenceAsync(filePath, cancellationToken).ConfigureAwait(false), tracks, cancellationToken).ConfigureAwait(false)
                : await _service.ReadFileAsync(filePath, tracks, cancellationToken).ConfigureAwait(false);
            if (playlist.IsFavorite)
            {
                var previous = (await _workspace.GetPlaylistsAsync(cancellationToken).ConfigureAwait(false))
                    .FirstOrDefault(item => item.Id == playlist.Id);
                if (previous is not null && !(previous.TrackIds.Count == 0 && previous.MajCollection is null))
                {
                    var byId = tracks.ToDictionary(track => track.Id, StringComparer.Ordinal);
                    previous = ResolveUnavailableEntries(previous, CreateHashLookup(tracks), byId);
                    playlist = MergeFavorites(previous, playlist, byId);
                }
            }

            await _workspace.SavePlaylistAsync(playlist, cancellationToken).ConfigureAwait(false);
            return playlist;
        }

        /// <summary>Resolves only retained unavailable references without replaying removed or reordered known entries.</summary>
        /// <param name="playlist">The current locally edited list.</param>
        /// <param name="byHash">The current deterministic portable identity lookup.</param>
        /// <param name="byId">The current music metadata by local identity.</param>
        /// <returns>The list with newly available entries inserted, or the original unchanged object.</returns>
        private static MusicPlaylist ResolveUnavailableEntries(MusicPlaylist playlist, IReadOnlyDictionary<string, string> byHash,
            IReadOnlyDictionary<string, MusicTrack> byId)
        {
            if (playlist.IsCloud || playlist.MajCollection is not { } definition || !definition.Entries.Any(entry => entry.TrackId is null))
            {
                return playlist;
            }

            var entries = definition.Entries.ToArray();
            var nodes = new LinkedList<string>(playlist.TrackIds);
            var available = new Dictionary<string, Queue<LinkedListNode<string>>>(StringComparer.Ordinal);
            var favoriteNodes = new Dictionary<string, LinkedListNode<string>>(StringComparer.Ordinal);
            for (var node = nodes.First; node is not null; node = node.Next)
            {
                if (!available.TryGetValue(node.Value, out var occurrences))
                {
                    occurrences = new Queue<LinkedListNode<string>>();
                    available[node.Value] = occurrences;
                }

                occurrences.Enqueue(node);
                if (TrackHash(node.Value, byId, definition.Entries) is { } hash)
                {
                    favoriteNodes.TryAdd(hash, node);
                }
            }

            var anchors = new LinkedListNode<string>?[entries.Length];
            for (var index = 0; index < entries.Length; index++)
            {
                if (entries[index].TrackId is { } id && available.TryGetValue(id, out var occurrences)
                    && occurrences.TryDequeue(out var node))
                {
                    anchors[index] = node;
                }
            }

            var nextAnchors = new LinkedListNode<string>?[entries.Length];
            LinkedListNode<string>? next = null;
            for (var index = entries.Length - 1; index >= 0; index--)
            {
                nextAnchors[index] = next;
                next = anchors[index] ?? next;
            }

            LinkedListNode<string>? previous = null;
            var changed = false;
            for (var index = 0; index < entries.Length; index++)
            {
                var entry = entries[index];
                if (entry.TrackId is not null)
                {
                    previous = anchors[index] ?? previous;
                    continue;
                }

                if (!byHash.TryGetValue(entry.Hash, out var id))
                {
                    continue;
                }

                LinkedListNode<string> inserted;
                if (playlist.IsFavorite && favoriteNodes.TryGetValue(entry.Hash, out var favorite))
                {
                    inserted = favorite;
                    id = favorite.Value;
                }
                else
                {
                    inserted = previous is not null ? nodes.AddAfter(previous, id)
                        : nextAnchors[index] is { } following ? nodes.AddBefore(following, id) : nodes.AddLast(id);
                    favoriteNodes.TryAdd(entry.Hash, inserted);
                }

                entries[index] = entry with { TrackId = id };
                previous = inserted;
                changed = true;
            }

            return changed ? playlist with
            {
                TrackIds = nodes.ToArray(),
                MajCollection = definition with { Entries = entries },
                UpdatedAt = DateTimeOffset.UtcNow
            } : playlist;
        }

        /// <summary>Merges an explicit favorites import without dropping local members or unresolved portable references.</summary>
        /// <param name="previous">The existing local favorites, including local edits.</param>
        /// <param name="incoming">The explicitly imported favorites snapshot.</param>
        /// <param name="byId">The current music metadata by local identity.</param>
        /// <returns>Favorites using set membership and retaining the existing name and conflicting extension fields.</returns>
        private static MusicPlaylist MergeFavorites(MusicPlaylist previous, MusicPlaylist incoming,
            IReadOnlyDictionary<string, MusicTrack> byId)
        {
            var ids = new List<string>();
            var memberKeys = new HashSet<string>(StringComparer.Ordinal);
            var chosenByHash = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var playlist in new[] { previous, incoming })
            {
                foreach (var id in playlist.TrackIds)
                {
                    var hash = TrackHash(id, byId, playlist.MajCollection?.Entries);
                    var key = hash is not null ? "hash:" + hash : "id:" + id;
                    if (memberKeys.Add(key))
                    {
                        ids.Add(id);
                        if (hash is not null)
                        {
                            chosenByHash[hash] = id;
                        }
                    }
                }
            }

            var entries = new List<MajCollectionEntry>();
            var portableMembers = new HashSet<string>(StringComparer.Ordinal);
            foreach (var playlist in new[] { previous, incoming })
            {
                var currentIds = playlist.TrackIds.ToHashSet(StringComparer.Ordinal);
                foreach (var entry in playlist.MajCollection?.Entries ?? Array.Empty<MajCollectionEntry>())
                {
                    if ((entry.TrackId is null || currentIds.Contains(entry.TrackId)) && portableMembers.Add(entry.Hash))
                    {
                        entries.Add(entry with { TrackId = chosenByHash.GetValueOrDefault(entry.Hash) });
                    }
                }
            }

            foreach (var pair in chosenByHash)
            {
                if (portableMembers.Add(pair.Key))
                {
                    entries.Add(new MajCollectionEntry { Hash = pair.Key, TrackId = pair.Value });
                }
            }

            var retained = previous.MajCollection ?? incoming.MajCollection!;
            var additional = retained.AdditionalProperties.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
            foreach (var pair in incoming.MajCollection?.AdditionalProperties ?? new Dictionary<string, System.Text.Json.JsonElement>())
            {
                additional.TryAdd(pair.Key, pair.Value);
            }

            return previous with
            {
                IsFavorite = true,
                TrackIds = ids,
                MajCollection = retained with { Entries = entries, AdditionalProperties = additional },
                UpdatedAt = DateTimeOffset.UtcNow
            };
        }

        /// <summary>Builds the same deterministic exact-hash selection as the portable format reader.</summary>
        /// <param name="tracks">The currently indexed music entries.</param>
        /// <returns>A lookup from valid portable identity to a current music entry.</returns>
        private static IReadOnlyDictionary<string, string> CreateHashLookup(IReadOnlyList<MusicTrack> tracks)
        {
            var lookup = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var track in tracks.OrderBy(item => item.Audio.LocalPath ?? item.Id, StringComparer.Ordinal))
            {
                if (GetChartHash(track) is { } hash)
                {
                    lookup.TryAdd(hash, track.Id);
                }
            }

            return lookup;
        }

        /// <summary>Reads a member's current portable identity or its retained original reference.</summary>
        /// <param name="id">The member identity.</param>
        /// <param name="byId">The current music metadata.</param>
        /// <param name="retained">The original portable references, when available.</param>
        /// <returns>The portable identity, or null for non-portable local audio.</returns>
        private static string? TrackHash(string id, IReadOnlyDictionary<string, MusicTrack> byId, IReadOnlyList<MajCollectionEntry>? retained)
        {
            var current = byId.TryGetValue(id, out var track) ? GetChartHash(track) : null;
            return current ?? retained?.FirstOrDefault(entry => entry.TrackId == id)?.Hash;
        }

        /// <summary>Recognizes the exact Base64 MD5 identity used by Maj collection files.</summary>
        /// <param name="track">The indexed music metadata.</param>
        /// <returns>The valid chart identity, or null.</returns>
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

        /// <inheritdoc />
        /// <exception cref="InvalidOperationException">The destination is within a read-only source or passes through a link.</exception>
        /// <exception cref="IOException">The destination exists or cannot be written.</exception>
        public async Task ExportAsync(string filePath, MusicPlaylist playlist, CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
            ArgumentNullException.ThrowIfNull(playlist);
            var destination = Path.GetFullPath(filePath);
            var settings = await _workspace.LoadSettingsAsync(cancellationToken).ConfigureAwait(false);
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            IEnumerable<string> sourceRoots = settings.LocalMusicDirectories.Where(path => !DocumentReference.IsContentUri(path)).Concat(settings.LocalDirectorySources.Where(source => !DocumentReference.IsContentUri(source.Path)).Select(source =>
                source.Kind == LocalDirectoryKind.CollectionIndex
                    && string.Equals(Path.GetFileName(Path.TrimEndingDirectorySeparator(source.Path)), "majcollection", StringComparison.OrdinalIgnoreCase)
                    ? Path.GetDirectoryName(source.Path) ?? source.Path : source.Path));
            if (playlist.MajCollection?.SourceFilePath is { } sourcePath && !DocumentReference.IsContentUri(sourcePath))
            {
                var sourceDirectory = Path.GetDirectoryName(Path.GetFullPath(sourcePath))!;
                var sourceRoot = string.Equals(Path.GetFileName(sourceDirectory), "majcollection", StringComparison.OrdinalIgnoreCase)
                    ? Path.GetDirectoryName(sourceDirectory) ?? sourceDirectory : sourceDirectory;
                sourceRoots = sourceRoots.Append(sourceRoot);
            }

            foreach (var root in sourceRoots)
            {
                var normalized = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (string.Equals(destination, normalized, comparison)
                    || destination.StartsWith(normalized + Path.DirectorySeparatorChar, comparison))
                {
                    throw new InvalidOperationException("歌曲目录为只读来源，请将播放列表导出到其他位置。");
                }
            }

            if (File.Exists(destination))
            {
                throw new IOException("目标文件已存在，请选择新的文件名。");
            }

            var parent = Path.GetDirectoryName(destination)!;
            for (var ancestor = new DirectoryInfo(parent); ancestor is not null; ancestor = ancestor.Parent)
            {
                if (ancestor.Exists && (ancestor.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new InvalidOperationException("导出目录不能经过链接，请选择普通文件夹。");
                }
            }

            var tracks = await _workspace.SearchAsync(null, cancellationToken).ConfigureAwait(false);
            Directory.CreateDirectory(parent);
            var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous))
                {
                    await _service.WriteAsync(stream, playlist, tracks, cancellationToken).ConfigureAwait(false);
                }

                cancellationToken.ThrowIfCancellationRequested();
                File.Move(temporary, destination, false);
            }
            finally
            {
                File.Delete(temporary);
            }
        }
    }
}

#nullable enable

using MajMusic.Core.Models;

namespace MajMusic.Presentation.ViewModels
{
    /// <summary>Shows verified source mirrors once while retaining locally editable complete playlist membership.</summary>
    public sealed partial class MainViewModel
    {
        /// <summary>Retains the last persisted local snapshots for edit-time merge publication.</summary>
        private readonly Dictionary<string, MusicPlaylist> _storedLocalPlaylists = new(StringComparer.Ordinal);
        /// <summary>Retains complete local membership behind both ordinary and synchronized source presentations.</summary>
        private readonly Dictionary<string, MusicPlaylist> _localPlaylistSnapshots = new(StringComparer.Ordinal);
        /// <summary>Translates disappearing source presentations back to their preserved local identities.</summary>
        private readonly Dictionary<string, string> _playlistMirrorBackings = new(StringComparer.Ordinal);
        /// <summary>Suppresses removed members while an older source snapshot is still waiting for its upload refresh.</summary>
        private readonly Dictionary<string, HashSet<string>> _pendingMirrorRemovals = new(StringComparer.Ordinal);

        /// <summary>Combines only verified mirror identities without treating matching display names as ownership.</summary>
        /// <param name="locals">The persisted local snapshots.</param>
        /// <param name="clouds">The currently authorized source snapshots.</param>
        /// <param name="library">The indexed music used to identify portable aliases.</param>
        /// <returns>One source presentation for each mirror and all unrelated local or cloud lists.</returns>
        private IReadOnlyList<MusicPlaylist> ProjectPlaylistMirrors(IReadOnlyList<MusicPlaylist> locals,
            IReadOnlyList<MusicPlaylist> clouds, IReadOnlyDictionary<string, MusicTrack> library)
        {
            _storedLocalPlaylists.Clear();
            _localPlaylistSnapshots.Clear();
            _playlistMirrorBackings.Clear();
            foreach (var local in locals)
            {
                _storedLocalPlaylists[local.Id] = local;
                _localPlaylistSnapshots[local.Id] = local;
            }

            var mirrors = clouds.Where(cloud => cloud.IsCloud && cloud.LocalMirrorId is { } id && _storedLocalPlaylists.ContainsKey(id))
                .GroupBy(cloud => cloud.LocalMirrorId!, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
            foreach (var pair in mirrors)
            {
                ReconcilePlaylistMirrorRemovals(pair.Key, pair.Value, library);
                _localPlaylistSnapshots[pair.Key] = MergePlaylistMirrorMembers(_storedLocalPlaylists[pair.Key], pair.Value, library);
                foreach (var cloud in pair.Value)
                {
                    _playlistMirrorBackings[cloud.Id] = pair.Key;
                }
            }

            var projected = locals.Where(local => !mirrors.ContainsKey(local.Id)).ToList();
            foreach (var cloud in clouds)
            {
                if (_playlistMirrorBackings.TryGetValue(cloud.Id, out var localId))
                {
                    var local = _localPlaylistSnapshots[localId];
                    projected.Add(cloud with { Name = local.Name, TrackIds = local.TrackIds, MajCollection = local.MajCollection, IsFavorite = local.IsFavorite });
                }
                else
                {
                    projected.Add(cloud);
                }
            }

            return projected;
        }

        /// <summary>Preserves local songs, repeated playlist entries and unavailable references while adding unmatched cloud members.</summary>
        /// <param name="local">The complete persisted local list.</param>
        /// <param name="mirrors">Only the source snapshots bound to this local identity.</param>
        /// <param name="library">The indexed source-scoped songs.</param>
        /// <returns>A locally owned editable snapshot containing the logical collection's complete membership.</returns>
        private MusicPlaylist MergePlaylistMirrorMembers(MusicPlaylist local, IReadOnlyList<MusicPlaylist> mirrors,
            IReadOnlyDictionary<string, MusicTrack> library)
        {
            var ids = local.TrackIds.ToList();
            var knownIds = ids.ToHashSet(StringComparer.Ordinal);
            var hashes = ids.Where(library.ContainsKey).Select(id => TrackMajHash(library[id])).OfType<string>().ToHashSet(StringComparer.Ordinal);
            _pendingMirrorRemovals.TryGetValue(local.Id, out var removed);
            var currentMirrors = mirrors.Any(mirror => mirror.LocalMirrorHasPendingChanges) ? Array.Empty<MusicPlaylist>() : mirrors;
            foreach (var id in currentMirrors.SelectMany(mirror => mirror.TrackIds))
            {
                var hash = library.TryGetValue(id, out var track) ? TrackMajHash(track) : null;
                if (removed?.Contains(hash is null ? "id:" + id : "hash:" + hash) == true)
                {
                    continue;
                }

                if (!knownIds.Add(id))
                {
                    continue;
                }

                if (hash is null || hashes.Add(hash))
                {
                    ids.Add(id);
                }
            }

            var entries = (local.MajCollection?.Entries ?? []).ToList();
            var entryHashes = entries.Select(entry => entry.Hash).ToHashSet(StringComparer.Ordinal);
            foreach (var entry in currentMirrors.SelectMany(mirror => mirror.MajCollection?.Entries ?? []))
            {
                if (removed?.Contains("hash:" + entry.Hash) != true && entryHashes.Add(entry.Hash))
                {
                    var equivalent = ids.FirstOrDefault(id => library.TryGetValue(id, out var track) && TrackMajHash(track) == entry.Hash);
                    entries.Add(entry with { TrackId = equivalent ?? entry.TrackId });
                }
            }

            var definition = local.MajCollection;
            if (entries.Count > 0)
            {
                definition = (definition ?? new MajCollectionDefinition { IsPlayList = !local.IsFavorite }) with { Entries = entries };
            }

            return local with { TrackIds = ids, MajCollection = definition };
        }

        /// <summary>Releases a pending removal only after all current mirror snapshots confirm that it is absent.</summary>
        /// <param name="localId">The shared local backing identity.</param>
        /// <param name="mirrors">All current source presentations bound to the backing.</param>
        /// <param name="library">The indexed portable identities.</param>
        private void ReconcilePlaylistMirrorRemovals(string localId, IReadOnlyList<MusicPlaylist> mirrors, IReadOnlyDictionary<string, MusicTrack> library)
        {
            if (!_pendingMirrorRemovals.TryGetValue(localId, out var removed))
            {
                return;
            }

            var present = mirrors.SelectMany(mirror => mirror.TrackIds)
                .Select(id => library.TryGetValue(id, out var track) && TrackMajHash(track) is { } hash ? "hash:" + hash : "id:" + id)
                .Concat(mirrors.SelectMany(mirror => mirror.MajCollection?.Entries ?? []).Select(entry => "hash:" + entry.Hash))
                .ToHashSet(StringComparer.Ordinal);
            removed.RemoveWhere(key => !present.Contains(key));
            if (removed.Count == 0)
            {
                _pendingMirrorRemovals.Remove(localId);
            }
        }

        /// <summary>Records a successful local removal without accepting the older cloud member back during refresh.</summary>
        /// <param name="local">The edited logical local list.</param>
        /// <param name="track">The removed song or portable alias.</param>
        private void RememberPlaylistMirrorRemoval(MusicPlaylist local, MusicTrack track)
        {
            if (!_playlistMirrorBackings.Values.Contains(local.Id, StringComparer.Ordinal))
            {
                return;
            }

            if (!_pendingMirrorRemovals.TryGetValue(local.Id, out var removed))
            {
                removed = new HashSet<string>(StringComparer.Ordinal);
                _pendingMirrorRemovals[local.Id] = removed;
            }

            var hash = TrackMajHash(track) ?? local.MajCollection?.Entries.FirstOrDefault(entry => entry.TrackId == track.Id)?.Hash;
            removed.Add(hash is null ? "id:" + track.Id : "hash:" + hash);
        }

        /// <summary>Lets an explicit add action override an earlier removal awaiting synchronization.</summary>
        /// <param name="localId">The locally owned playlist identity.</param>
        /// <param name="track">The song explicitly being restored.</param>
        private void RestorePlaylistMirrorMember(string localId, MusicTrack track)
        {
            if (_pendingMirrorRemovals.TryGetValue(localId, out var removed))
            {
                removed.Remove("id:" + track.Id);
                if (TrackMajHash(track) is { } hash)
                {
                    removed.Remove("hash:" + hash);
                }

                if (removed.Count == 0)
                {
                    _pendingMirrorRemovals.Remove(localId);
                }
            }
        }

        /// <summary>Builds portable favorite membership from the complete logical local list.</summary>
        /// <param name="library">The current catalog used to identify chart aliases.</param>
        private void RefreshFavoriteMembership(IReadOnlyDictionary<string, MusicTrack> library)
        {
            _favoriteHashes.Clear();
            if (_favorites is not { } favorites)
            {
                return;
            }

            var ids = favorites.TrackIds.ToHashSet(StringComparer.Ordinal);
            foreach (var id in ids)
            {
                if (library.TryGetValue(id, out var track) && TrackMajHash(track) is { } hash)
                {
                    _favoriteHashes.Add(hash);
                }
            }

            foreach (var entry in favorites.MajCollection?.Entries ?? [])
            {
                if (entry.TrackId is null || ids.Contains(entry.TrackId))
                {
                    _favoriteHashes.Add(entry.Hash);
                }
            }
        }

        /// <summary>Checks one captured song against the complete logical favorite membership.</summary>
        /// <param name="track">The source-scoped song or portable alias.</param>
        /// <returns>Whether this song belongs to the effective local-backed favorites list.</returns>
        private bool IsTrackFavorite(MusicTrack track)
        {
            return _favorites is { } favorites && (favorites.TrackIds.Contains(track.Id, StringComparer.Ordinal)
                || TrackMajHash(track) is { } hash && _favoriteHashes.Contains(hash));
        }

        /// <summary>Resolves a current source presentation to its complete locally owned backing snapshot.</summary>
        /// <param name="playlist">The visible or locally owned list used by the command.</param>
        /// <returns>The editable local snapshot, or null for an unrelated or expired cloud list.</returns>
        private MusicPlaylist? ResolveLocalPlaylist(MusicPlaylist playlist)
        {
            if (!playlist.IsCloud)
            {
                return _localPlaylistSnapshots.GetValueOrDefault(playlist.Id);
            }

            var current = Playlists.FirstOrDefault(item => item.Id == playlist.Id && item.EndpointId == playlist.EndpointId && item.RemoteId == playlist.RemoteId);
            return current?.LocalMirrorId is { } id ? _localPlaylistSnapshots.GetValueOrDefault(id) : null;
        }

        /// <summary>Chooses a current source presentation for a backing list, preferring the selected endpoint.</summary>
        /// <param name="local">The preserved local backing list.</param>
        /// <returns>The matching visible source row or its ordinary local row.</returns>
        private MusicPlaylist VisiblePlaylistForLocal(MusicPlaylist local)
        {
            return Playlists.FirstOrDefault(item => item.LocalMirrorId == local.Id && item.EndpointId == SelectedEndpointProfile?.Configuration.Id)
                ?? Playlists.FirstOrDefault(item => item.LocalMirrorId == local.Id)
                ?? Playlists.FirstOrDefault(item => item.Id == local.Id) ?? local;
        }

        /// <summary>Checks whether an opened source or local row represents one backing identity.</summary>
        /// <param name="localId">The persisted local playlist identity.</param>
        /// <returns>Whether this logical collection is currently browsed.</returns>
        private bool IsLocalPlaylistOpen(string localId)
        {
            return IsPlaylistsMode && BrowsedPlaylist is { } visible && ResolveLocalPlaylist(visible)?.Id == localId;
        }

        /// <summary>Preserves cloud-only members in the local backing before a user-requested removal reads its persisted contents.</summary>
        /// <param name="local">The complete editable snapshot resolved for the user's action.</param>
        /// <returns>A task representing any required local merge publication.</returns>
        private Task EnsureLogicalPlaylistMembersPersistedAsync(MusicPlaylist local)
        {
            if (_storedLocalPlaylists.TryGetValue(local.Id, out var stored) && stored.TrackIds.SequenceEqual(local.TrackIds)
                && (stored.MajCollection?.Entries ?? []).SequenceEqual(local.MajCollection?.Entries ?? []))
            {
                return Task.CompletedTask;
            }

            return _workspace.SavePlaylistAsync(local, _lifetime.Token);
        }
    }
}

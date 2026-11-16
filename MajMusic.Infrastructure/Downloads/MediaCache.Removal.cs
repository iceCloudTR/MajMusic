#nullable enable

using MajMusic.Core.Models;

namespace MajMusic.Infrastructure.Downloads
{
    /// <summary>Forgets selected resource aliases while protecting imports, shared objects and concurrent resource users.</summary>
    public sealed partial class MediaCache
    {
        /// <summary>Checks canonical managed-object ownership without reading or changing the source file.</summary>
        /// <param name="path">The resource's optional local path.</param>
        /// <returns>Whether this exact path belongs to this cache's content-addressed objects.</returns>
        public bool IsManagedPath(string? path)
        {
            return GetManagedHash(path) is not null;
        }

        /// <summary>Gets this cache's canonical filename for a verified content digest without requiring an existing alias.</summary>
        /// <param name="hash">The complete SHA-256 digest.</param>
        /// <returns>The managed filename in this cache's selected media root.</returns>
        internal string GetManagedObjectPath(string hash) => GetObjectPath(hash);

        /// <summary>Removes selected aliases and deletes only unshared canonical objects after an atomic index commit.</summary>
        /// <param name="resources">The source-scoped resource descriptors selected for removal.</param>
        /// <param name="protectedPaths">Managed objects still referenced by other library entries, including imports.</param>
        /// <param name="cancellationToken">Cancels reservation, validation waits and index publication.</param>
        /// <returns>One detached-reference flag per resource and the number of deleted physical objects.</returns>
        public async Task<(bool[] Removed, int DeletedFiles)> RemoveResourcesAsync(IReadOnlyList<MediaResource> resources,
            IReadOnlySet<string> protectedPaths, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(resources);
            ArgumentNullException.ThrowIfNull(protectedPaths);
            var descriptors = resources.Select(resource =>
            {
                var eligible = resource.LocalPath is not { Length: > 0 } path || IsManagedPath(path);
                return (Resource: resource, UriKey: eligible ? GetUriKey(resource) : null,
                    Hash: eligible ? NormalizeHash(resource.Sha256) ?? GetManagedHash(resource.LocalPath) : null);
            }).ToArray();
            var keys = descriptors.SelectMany(item => new[] { item.UriKey, item.Hash is null ? null : "sha256:" + item.Hash })
                .OfType<string>().Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
            var reservations = new List<(string Key, ResourceTransfer Transfer, bool Entered)>();
            try
            {
                foreach (var key in keys)
                {
                    var reservation = ReserveTransfer(key, null);
                    reservations.Add((key, reservation.Transfer, false));
                    await reservation.Transfer.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
                    reservations[^1] = (key, reservation.Transfer, true);
                }

                while (true)
                {
                    await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
                    try
                    {
                        var index = await LoadIndexAsync(cancellationToken).ConfigureAwait(false);
                        var hashes = descriptors.SelectMany(item => new[] { item.Hash,
                            item.UriKey is null ? null : index.GetValueOrDefault(item.UriKey)?.ContentHash })
                            .OfType<string>().ToHashSet(StringComparer.Ordinal);
                        if (!hashes.Any(hash => _validationReaders.ContainsKey(hash)))
                        {
                            var candidate = new Dictionary<string, CacheEntry>(index, StringComparer.Ordinal);
                            var removed = new bool[resources.Count];
                            for (var position = 0; position < descriptors.Length; position++)
                            {
                                var item = descriptors[position];
                                if (item.Resource.LocalPath is { Length: > 0 } local && !IsManagedPath(local))
                                {
                                    continue;
                                }

                                removed[position] = item.UriKey is not null && candidate.Remove(item.UriKey)
                                    || item.Hash is not null && File.Exists(GetObjectPath(item.Hash));
                            }

                            var delete = new List<string>();
                            foreach (var hash in hashes)
                            {
                                var path = GetObjectPath(hash);
                                if (protectedPaths.Contains(path)
                                    || candidate.Any(pair => pair.Key.StartsWith("uri:", StringComparison.Ordinal) && pair.Value.ContentHash == hash))
                                {
                                    continue;
                                }

                                candidate.Remove("sha256:" + hash);
                                delete.Add(path);
                            }

                            await SaveIndexAsync(candidate, cancellationToken).ConfigureAwait(false);
                            var count = 0;
                            foreach (var path in delete)
                            {
                                if (File.Exists(path))
                                {
                                    File.Delete(path);
                                    count++;
                                }
                            }

                            return (removed, count);
                        }
                    }
                    finally
                    {
                        _gate.Release();
                    }

                    await Task.Delay(10, cancellationToken).ConfigureAwait(false);
                }
            }
            finally
            {
                for (var position = reservations.Count - 1; position >= 0; position--)
                {
                    var reservation = reservations[position];
                    ReleaseTransfer(reservation.Key, reservation.Transfer, null, reservation.Entered);
                }
            }
        }

        /// <summary>Recognizes only canonical object filenames resolved through the cache's own path mapper.</summary>
        /// <param name="path">The optional native local path.</param>
        /// <returns>The managed object hash, otherwise null for every external or document-provider path.</returns>
        private string? GetManagedHash(string? path)
        {
            if (string.IsNullOrWhiteSpace(path) || DocumentReference.IsContentUri(path))
            {
                return null;
            }

            try
            {
                var hash = NormalizeHash(Path.GetFileName(path));
                return hash is not null && string.Equals(Path.GetFullPath(path), GetObjectPath(hash),
                    OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) ? hash : null;
            }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
            {
                return null;
            }
        }
    }
}

#nullable enable

using System.Text.Json;
using System.Text.Json.Nodes;
using MajMusic.Core.Models;
using MajMusic.Infrastructure.Downloads;
using MajMusic.Infrastructure.Network;

namespace MajMusic.Infrastructure.Application
{
    /// <summary>Preserves unambiguous legacy cache resources while migrating only the generated portable copy.</summary>
    public static partial class DesktopDataDirectory
    {
        /// <summary>Promotes uniquely owned old address aliases to validated file identities without enabling runtime alias fallback.</summary>
        /// <param name="documents">All staged track documents, including independent configured source slots.</param>
        /// <param name="staging">The copied managed data awaiting publication.</param>
        /// <param name="destination">The eventual portable root used in stored paths.</param>
        private static void PromoteLegacyResourceAliases(IEnumerable<JsonObject> documents, string staging, string destination)
        {
            var cacheRoot = Path.Combine(staging, "Cache");
            if (!File.Exists(Path.Combine(cacheRoot, "index.json")))
            {
                return;
            }

            var candidates = new Dictionary<string, List<(string Owner, JsonObject Document, MediaResource Resource)>>(StringComparer.Ordinal);
            foreach (var track in documents)
            {
                foreach (var name in new[] { "Audio", "Cover" })
                {
                    if (track[name] is not JsonObject document || document.Deserialize<MediaResource>() is not { RemoteUri.IsAbsoluteUri: true } resource)
                    {
                        continue;
                    }

                    var alias = JsonSerializer.Serialize(new[] { resource.RemoteUri.AbsoluteUri, resource.CacheVersion ?? string.Empty });
                    var owner = JsonSerializer.Serialize(new[]
                    {
                        track["SourceId"]?.GetValue<string>() ?? "local", track["EndpointId"]?.GetValue<string>() ?? string.Empty,
                        resource.CacheVersion ?? track["Revision"]?.GetValue<string>() ?? string.Empty
                    });
                    if (!candidates.TryGetValue(alias, out var matches))
                    {
                        matches = [];
                        candidates.Add(alias, matches);
                    }

                    matches.Add((owner, document, resource));
                }
            }

            using var policy = new NetworkAccessPolicy();
            using var client = new HttpClient();
            using var cache = new MediaCache(cacheRoot, client, policy);
            foreach (var matches in candidates.Values)
            {
                if (matches.Select(item => item.Owner).Distinct(StringComparer.Ordinal).Skip(1).Any())
                {
                    continue;
                }

                foreach (var (_, document, resource) in matches)
                {
                    if (!string.IsNullOrEmpty(resource.LocalPath) || !string.IsNullOrEmpty(resource.Sha256))
                    {
                        continue;
                    }

                    var cached = cache.TryGetCachedAsync(resource).GetAwaiter().GetResult();
                    if (cached is null || !IsWithin(cached, cacheRoot))
                    {
                        continue;
                    }

                    document["LocalPath"] = Path.Combine(destination, Path.GetRelativePath(staging, cached));
                    document["Sha256"] = Path.GetFileName(cached);
                    document["Length"] = new FileInfo(cached).Length;
                }
            }
        }
    }
}

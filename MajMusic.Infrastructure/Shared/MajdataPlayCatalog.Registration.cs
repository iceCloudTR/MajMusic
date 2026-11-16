#nullable enable

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using MajMusic.Core.Models;
using Microsoft.Data.Sqlite;

namespace MajMusic.Infrastructure.Shared
{
    /// <summary>Registers verified shared resources in the game's public chart tables.</summary>
    public sealed partial class MajdataPlayCatalog
    {
        /// <summary>Defines the game's complete public chart schema without touching any other runtime tables.</summary>
        private const string ChartSchema = """
            CREATE TABLE IF NOT EXISTS OnlineChartCache (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                ChartId VARCHAR, Server VARCHAR, ChartHash VARCHAR, Detail VARCHAR, LevelHashes VARCHAR,
                IsResolved INTEGER,
                MaidataMd5 VARCHAR, MaidataSha256 VARCHAR, TrackMd5 VARCHAR, TrackSha256 VARCHAR,
                VideoMd5 VARCHAR, VideoSha256 VARCHAR, CoverArtMd5 VARCHAR, CoverArtSha256 VARCHAR,
                ThumbnailMd5 VARCHAR, ThumbnailSha256 VARCHAR,
                IsListed INTEGER, ListOrder BIGINT, IsDeletedOnServer INTEGER, UpdatedAtTicks BIGINT
            );
            CREATE UNIQUE INDEX IF NOT EXISTS IX_OnlineChartCache_ChartId_Server ON OnlineChartCache(ChartId, Server);
            CREATE TABLE IF NOT EXISTS OnlineServers (Endpoint VARCHAR PRIMARY KEY, Server VARCHAR, UpdatedAtTicks BIGINT);
            """;
        /// <summary>Requires the current game's public schema instead of attempting migrations owned by another process.</summary>
        private static readonly string[] RequiredChartColumns =
        [
            "Id", "ChartId", "Server", "ChartHash", "Detail", "LevelHashes", "IsResolved", "MaidataMd5", "MaidataSha256",
            "TrackMd5", "TrackSha256", "VideoMd5", "VideoSha256", "CoverArtMd5", "CoverArtSha256", "ThumbnailMd5",
            "ThumbnailSha256", "IsListed", "ListOrder", "IsDeletedOnServer", "UpdatedAtTicks",
        ];
        /// <summary>Omits an unavailable server timestamp instead of writing null into the game's nonnullable DateTime.</summary>
        private static readonly JsonSerializerOptions MetadataJsonOptions = new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

        /// <summary>Records resources so MajdataPlay can locate them offline and retain them during object pruning.</summary>
        /// <param name="track">An online entry enriched with genuine resource descriptors.</param>
        /// <param name="endpoint">The configured endpoint owning the entry.</param>
        /// <param name="cancellationToken">Cancels verification or the SQLite transaction.</param>
        /// <returns>True when registered; false when audio is not stored, identities are incomplete or a different revision already exists.</returns>
        /// <exception cref="InvalidOperationException">An existing public table has an unsupported schema.</exception>
        /// <exception cref="IOException">An existing shared object fails its content or length verification.</exception>
        /// <exception cref="SqliteException">The public chart registration cannot be committed.</exception>
        /// <exception cref="OperationCanceledException">The operation is cancelled.</exception>
        public async Task<bool> RegisterResourcesAsync(MusicTrack track, EndpointConfiguration endpoint, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(track);
            ArgumentNullException.ThrowIfNull(endpoint);
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(track.RemoteId) || track.EndpointId != endpoint.Id || !IsChartHash(track.Revision))
            {
                return false;
            }

            var info = ReadRegistrationInfo(track);
            var audioDescriptor = info?.IsComplete == true ? info.Resources[1] : info?.Resources[1] ?? track.Audio;
            if (audioDescriptor is null)
            {
                return false;
            }

            var audio = await ReadRegistrationDigestAsync(audioDescriptor, cancellationToken).ConfigureAwait(false);
            if (audio is not { IsStored: true })
            {
                return false;
            }

            var chartDescriptor = info?.IsComplete == true ? info.Resources[0] : info?.Resources[0] ?? track.Chart;
            var coverDescriptor = info?.IsComplete == true ? info.Resources[4] : info?.Resources[4] ?? track.Cover;
            var chart = chartDescriptor is { } chartResource
                ? await ReadRegistrationDigestAsync(chartResource, cancellationToken).ConfigureAwait(false) : null;
            var thumbnail = coverDescriptor is { } coverResource
                ? await ReadRegistrationDigestAsync(coverResource, cancellationToken).ConfigureAwait(false) : null;
            var video = info?.Resources[2] is { } videoResource
                ? await ReadRegistrationDigestAsync(videoResource, cancellationToken).ConfigureAwait(false) : null;
            var original = info?.Resources[3] is { } originalResource
                ? await ReadRegistrationDigestAsync(originalResource, cancellationToken).ConfigureAwait(false) : null;
            if (chart is not null && Convert.ToBase64String(Convert.FromHexString(chart.Md5)) != track.Revision)
            {
                return false;
            }

            var server = string.IsNullOrWhiteSpace(endpoint.ServerId) ? track.SourceId : endpoint.ServerId;
            if (string.IsNullOrWhiteSpace(server) || server == "local")
            {
                return false;
            }

            if (server == endpoint.Id)
            {
                server = endpoint.BaseUri.AbsoluteUri;
            }

            cancellationToken.ThrowIfCancellationRequested();
            Directory.CreateDirectory(Path.Combine(_root, "Runtime"));
            await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = Path.Combine(_root, "Runtime", "runtime.db"),
                Mode = SqliteOpenMode.ReadWriteCreate,
                Pooling = false,
                DefaultTimeout = 2,
            }.ToString());
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            using var transaction = connection.BeginTransaction();
            await EnsureRegistrationSchemaAsync(connection, transaction, cancellationToken).ConfigureAwait(false);

            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "SELECT Server FROM OnlineServers WHERE Endpoint = $endpoint";
            command.Parameters.AddWithValue("$endpoint", endpoint.BaseUri.AbsoluteUri);
            var mappedServer = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
            if (!string.IsNullOrWhiteSpace(mappedServer) && mappedServer != server)
            {
                return false;
            }

            command.CommandText = "SELECT ChartHash,Detail,LevelHashes FROM OnlineChartCache WHERE ChartId = $id AND Server = $server";
            command.Parameters.AddWithValue("$id", track.RemoteId);
            command.Parameters.AddWithValue("$server", server);
            string? existingRevision = null;
            var previousDetail = string.Empty;
            var previousLevelHashes = string.Empty;
            await using (var existing = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
            {
                if (await existing.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    existingRevision = ReadText(existing, 0);
                    previousDetail = ReadText(existing, 1);
                    previousLevelHashes = ReadText(existing, 2);
                }
            }
            if (existingRevision is not null && existingRevision != track.Revision)
            {
                // Content hashes do not express chronology. Preserve the game's revision when order is unproven.
                return false;
            }

            var incomingDetail = info?.Detail ?? JsonSerializer.Serialize(new
            {
                id = track.RemoteId,
                title = track.Title,
                artist = track.Artist,
                designer = string.Empty,
                description = track.Description,
                levels = Array.Empty<string>(),
                hash = track.Revision,
                timestamp = NormalizeTimestamp(track.SourceTimestamp),
                tags = track.Tags,
                publicTags = Array.Empty<string>(),
            }, MetadataJsonOptions);
            var detail = MergeRegistrationMetadata(previousDetail, incomingDetail);
            var levelHashes = MergeRegistrationLevelHashes(previousLevelHashes, info?.LevelHashes);
            var now = DateTime.UtcNow.Ticks;
            command.Parameters.AddWithValue("$revision", track.Revision);
            command.Parameters.AddWithValue("$detail", detail);
            command.Parameters.AddWithValue("$levels", levelHashes);
            command.Parameters.AddWithValue("$resolved", info?.IsComplete == true ? 1 : 0);
            command.Parameters.AddWithValue("$listed", HasRegistrationLevels(detail) ? 1 : 0);
            command.Parameters.AddWithValue("$now", now);
            command.Parameters.AddWithValue("$chartMd5", (object?)chart?.Md5 ?? DBNull.Value);
            command.Parameters.AddWithValue("$chartSha", (object?)chart?.Sha256 ?? DBNull.Value);
            command.Parameters.AddWithValue("$audioMd5", audio.Md5);
            command.Parameters.AddWithValue("$audioSha", audio.Sha256);
            command.Parameters.AddWithValue("$coverMd5", (object?)thumbnail?.Md5 ?? DBNull.Value);
            command.Parameters.AddWithValue("$coverSha", (object?)thumbnail?.Sha256 ?? DBNull.Value);
            command.Parameters.AddWithValue("$videoMd5", (object?)video?.Md5 ?? DBNull.Value);
            command.Parameters.AddWithValue("$videoSha", (object?)video?.Sha256 ?? DBNull.Value);
            command.Parameters.AddWithValue("$originalMd5", (object?)original?.Md5 ?? DBNull.Value);
            command.Parameters.AddWithValue("$originalSha", (object?)original?.Sha256 ?? DBNull.Value);
            command.CommandText = """
                INSERT INTO OnlineChartCache
                    (ChartId,Server,ChartHash,Detail,LevelHashes,IsResolved,MaidataMd5,MaidataSha256,TrackMd5,TrackSha256,
                     VideoMd5,VideoSha256,CoverArtMd5,CoverArtSha256,ThumbnailMd5,ThumbnailSha256,IsListed,ListOrder,IsDeletedOnServer,UpdatedAtTicks)
                VALUES
                    ($id,$server,$revision,$detail,$levels,$resolved,$chartMd5,$chartSha,$audioMd5,$audioSha,
                     $videoMd5,$videoSha,$originalMd5,$originalSha,$coverMd5,$coverSha,$listed,
                     (SELECT COALESCE(MAX(ListOrder),-1)+1 FROM OnlineChartCache WHERE Server=$server),0,$now)
                ON CONFLICT(ChartId,Server) DO UPDATE SET
                    Detail=excluded.Detail, LevelHashes=excluded.LevelHashes,
                    IsResolved=MAX(COALESCE(OnlineChartCache.IsResolved,0),excluded.IsResolved),
                    IsListed=CASE WHEN OnlineChartCache.IsDeletedOnServer=1 THEN OnlineChartCache.IsListed ELSE MAX(COALESCE(OnlineChartCache.IsListed,0),excluded.IsListed) END,
                    MaidataMd5=COALESCE(excluded.MaidataMd5,OnlineChartCache.MaidataMd5),
                    MaidataSha256=COALESCE(excluded.MaidataSha256,OnlineChartCache.MaidataSha256),
                    TrackMd5=excluded.TrackMd5, TrackSha256=excluded.TrackSha256,
                    VideoMd5=COALESCE(excluded.VideoMd5,OnlineChartCache.VideoMd5),
                    VideoSha256=COALESCE(excluded.VideoSha256,OnlineChartCache.VideoSha256),
                    CoverArtMd5=COALESCE(excluded.CoverArtMd5,OnlineChartCache.CoverArtMd5),
                    CoverArtSha256=COALESCE(excluded.CoverArtSha256,OnlineChartCache.CoverArtSha256),
                    ThumbnailMd5=COALESCE(excluded.ThumbnailMd5,OnlineChartCache.ThumbnailMd5),
                    ThumbnailSha256=COALESCE(excluded.ThumbnailSha256,OnlineChartCache.ThumbnailSha256),
                    UpdatedAtTicks=excluded.UpdatedAtTicks;
                INSERT INTO OnlineServers(Endpoint,Server,UpdatedAtTicks) VALUES($endpoint,$server,$now)
                ON CONFLICT(Endpoint) DO UPDATE SET Server=excluded.Server,UpdatedAtTicks=excluded.UpdatedAtTicks;
                """;
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            transaction.Commit();
            return true;
        }

        /// <summary>Creates absent public tables while refusing to migrate any existing game's schema.</summary>
        /// <param name="connection">The registration connection.</param>
        /// <param name="transaction">The atomic registration transaction.</param>
        /// <param name="token">Cancels schema inspection.</param>
        /// <returns>The schema validation and creation operation.</returns>
        private static async Task EnsureRegistrationSchemaAsync(SqliteConnection connection, SqliteTransaction transaction, CancellationToken token)
        {
            var chartColumns = await ReadColumnsAsync(connection, transaction, "OnlineChartCache", token).ConfigureAwait(false);
            var serverColumns = await ReadColumnsAsync(connection, transaction, "OnlineServers", token).ConfigureAwait(false);
            if (chartColumns.Count > 0 && RequiredChartColumns.Any(column => !chartColumns.Contains(column)) ||
                serverColumns.Count > 0 && new[] { "Endpoint", "Server", "UpdatedAtTicks" }.Any(column => !serverColumns.Contains(column)))
            {
                throw new InvalidOperationException("MajdataPlay's existing online cache schema is not supported.");
            }

            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = ChartSchema;
            await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }

        /// <summary>Reads a genuine detailed response while binding its metadata and resource identities to this revision.</summary>
        /// <param name="track">The source entry retaining its successful info response.</param>
        /// <returns>Preserved public metadata and validated resource descriptors, or null without an info response.</returns>
        /// <exception cref="InvalidDataException">The retained response is malformed or belongs to another chart revision.</exception>
        private static RegistrationInfo? ReadRegistrationInfo(MusicTrack track)
        {
            if (string.IsNullOrWhiteSpace(track.MajInfoJson))
            {
                return null;
            }

            try
            {
                using var document = JsonDocument.Parse(track.MajInfoJson);
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("metadata", out var metadata) ||
                    metadata.ValueKind != JsonValueKind.Object || Text(metadata, "id") != track.RemoteId || Text(metadata, "hash") != track.Revision)
                {
                    throw new InvalidDataException("The chart info does not match this source revision.");
                }

                var descriptors = new MediaResource?[5];
                var complete = root.TryGetProperty("resources", out var resources) && resources.ValueKind == JsonValueKind.Object;
                var names = new[] { "maidata", "track", "video", "coverArt", "thumbnail" };
                if (complete)
                {
                    for (var index = 0; index < names.Length; index++)
                    {
                        if (!resources.TryGetProperty(names[index], out var resource) || resource.ValueKind == JsonValueKind.Null)
                        {
                            continue;
                        }

                        if (resource.ValueKind != JsonValueKind.Object || NormalizeDigest(Text(resource, "sha256")) is not { } sha256 ||
                            NormalizeMd5(Text(resource, "md5")) is not { } md5)
                        {
                            complete = false;
                            continue;
                        }

                        descriptors[index] = new MediaResource
                        {
                            Sha256 = sha256,
                            Md5 = md5,
                            Length = resource.TryGetProperty("length", out var length) && length.TryGetInt64(out var count) && count > 0 ? count : null,
                        };
                    }
                }

                var levelHashes = string.Empty;
                if (root.TryGetProperty("levelHashes", out var levels) && levels.ValueKind == JsonValueKind.Array && levels.GetArrayLength() > 0)
                {
                    levelHashes = JsonSerializer.Serialize(ReadLevelHashSlots(levels));
                }

                return new RegistrationInfo(metadata.GetRawText(), levelHashes, complete, descriptors);
            }
            catch (JsonException exception)
            {
                throw new InvalidDataException("The retained chart info is not valid JSON.", exception);
            }
        }

        /// <summary>Fills missing metadata while preserving every existing nonempty field and unknown property.</summary>
        /// <param name="existing">The game's original public metadata JSON.</param>
        /// <param name="incoming">The genuine response or conservative fallback metadata.</param>
        /// <returns>Merged metadata without replacing existing nonempty game fields.</returns>
        private static string MergeRegistrationMetadata(string existing, string incoming)
        {
            if (string.IsNullOrWhiteSpace(existing))
            {
                return incoming;
            }

            try
            {
                if (JsonNode.Parse(existing) is not JsonObject previous || JsonNode.Parse(incoming) is not JsonObject current)
                {
                    return existing;
                }

                foreach (var field in current)
                {
                    if (!previous.TryGetPropertyValue(field.Key, out var value) || IsEmptyMetadataValue(value))
                    {
                        previous[field.Key] = field.Value?.DeepClone();
                    }
                }

                return previous.ToJsonString();
            }
            catch (JsonException)
            {
                return existing;
            }
        }

        /// <summary>Identifies genuinely absent metadata without treating false, zero or unknown object fields as empty.</summary>
        /// <param name="value">The existing JSON value.</param>
        /// <returns>Whether an incoming value can fill this field.</returns>
        private static bool IsEmptyMetadataValue(JsonNode? value)
        {
            return value is null || value is JsonValue scalar && scalar.TryGetValue<string>(out var text) && string.IsNullOrWhiteSpace(text) ||
                value is JsonArray array && array.All(IsEmptyMetadataValue);
        }

        /// <summary>Checks for real source-provided difficulty labels before listing a new chart row.</summary>
        /// <param name="metadata">The final public metadata JSON.</param>
        /// <returns>Whether at least one nonempty difficulty is present.</returns>
        private static bool HasRegistrationLevels(string metadata)
        {
            try
            {
                using var document = JsonDocument.Parse(metadata);
                return document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty("levels", out var levels) &&
                    levels.ValueKind == JsonValueKind.Array && levels.EnumerateArray().Any(level => level.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(level.GetString()));
            }
            catch (JsonException)
            {
                return false;
            }
        }

        /// <summary>Fills only missing normalized difficulty hashes in an existing same-revision row.</summary>
        /// <param name="existing">The game's previous serialized hashes.</param>
        /// <param name="incoming">The hashes supplied by the detailed response.</param>
        /// <returns>The existing data with missing valid hash slots filled.</returns>
        private static string MergeRegistrationLevelHashes(string existing, string? incoming)
        {
            if (string.IsNullOrEmpty(incoming))
            {
                return existing;
            }

            if (string.IsNullOrWhiteSpace(existing))
            {
                return incoming;
            }

            try
            {
                using var previous = JsonDocument.Parse(existing);
                using var current = JsonDocument.Parse(incoming);
                if (previous.RootElement.ValueKind != JsonValueKind.Array)
                {
                    return existing;
                }

                var hashes = ReadLevelHashSlots(previous.RootElement);
                var additions = ReadLevelHashSlots(current.RootElement);
                for (var index = 0; index < hashes.Length; index++)
                {
                    hashes[index] ??= additions[index];
                }

                return JsonSerializer.Serialize(hashes);
            }
            catch (JsonException)
            {
                return existing;
            }
        }

        /// <summary>Matches the game's seven-slot level-hash normalization and rejects nonlowercase digest text.</summary>
        /// <param name="levels">The source hash array.</param>
        /// <returns>Seven nullable SHA-256 identities indexed by difficulty.</returns>
        private static string?[] ReadLevelHashSlots(JsonElement levels)
        {
            var result = new string?[7];
            var index = 0;
            foreach (var level in levels.EnumerateArray())
            {
                if (index == result.Length)
                {
                    break;
                }

                var text = level.ValueKind == JsonValueKind.String ? level.GetString() : null;
                result[index++] = NormalizeDigest(text) is { } digest && text == digest ? digest : null;
            }

            return result;
        }

        /// <summary>Validates stored bytes or accepts a complete server-provided descriptor for an object not downloaded yet.</summary>
        /// <param name="resource">The genuine metadata descriptor.</param>
        /// <param name="token">Cancels local integrity verification.</param>
        /// <returns>Verified or fully described content, or null when no complete digest is known.</returns>
        private async Task<RegistrationDigest?> ReadRegistrationDigestAsync(MediaResource resource, CancellationToken token)
        {
            var sha256 = NormalizeDigest(resource.Sha256);
            if (sha256 is null)
            {
                return null;
            }

            var expectedMd5 = NormalizeMd5(resource.Md5);
            var path = Path.Combine(_root, GetObjectRelativePath(sha256));
            var file = await VerifyAsync(path, sha256, token).ConfigureAwait(false);
            if (file is not null)
            {
                if (expectedMd5 is not null && expectedMd5 != file.Md5 || resource.Length is { } length && length != file.Length)
                {
                    throw new IOException("A shared media object does not match its resource descriptor.");
                }

                return new RegistrationDigest(file.Md5, file.Sha256, true);
            }

            if (File.Exists(path))
            {
                throw new IOException("A shared media object failed integrity verification.");
            }

            return expectedMd5 is not null ? new RegistrationDigest(expectedMd5, sha256, false) : null;
        }

        /// <summary>Normalizes the server's hexadecimal or Base64 MD5 representation to the game's hexadecimal format.</summary>
        /// <param name="value">The server-provided MD5 digest.</param>
        /// <returns>A lowercase hexadecimal MD5 digest, or null.</returns>
        private static string? NormalizeMd5(string? value)
        {
            if (value is { Length: 32 } && value.All(char.IsAsciiHexDigit))
            {
                return value.ToLowerInvariant();
            }

            Span<byte> bytes = stackalloc byte[16];
            return value is not null && Convert.TryFromBase64String(value, bytes, out var count) && count == 16
                ? Convert.ToHexStringLower(bytes) : null;
        }

        /// <summary>Keeps a valid server time and leaves missing metadata at the game's default instead of inventing a time.</summary>
        /// <param name="value">The timestamp supplied by the source.</param>
        /// <returns>An ISO timestamp or null when unavailable.</returns>
        private static string? NormalizeTimestamp(string? value)
        {
            return DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var timestamp)
                ? timestamp.ToString("O", CultureInfo.InvariantCulture) : null;
        }

        /// <summary>Retains genuine content identities and whether their local object has been verified.</summary>
        /// <param name="Md5">The hexadecimal MD5 digest used by the game.</param>
        /// <param name="Sha256">The lowercase SHA-256 object identity.</param>
        /// <param name="IsStored">Whether the matching object already exists in the selected root.</param>
        private sealed record RegistrationDigest(string Md5, string Sha256, bool IsStored);
        /// <summary>Retains verified chart metadata and the complete optional resource set from one detailed response.</summary>
        /// <param name="Detail">The original public metadata object.</param>
        /// <param name="LevelHashes">The game's normalized serialized difficulty hashes.</param>
        /// <param name="IsComplete">Whether every resource entry was present, missing or validly described.</param>
        /// <param name="Resources">Maidata, audio, video, original cover and thumbnail descriptors, in game order.</param>
        private sealed record RegistrationInfo(string Detail, string LevelHashes, bool IsComplete, MediaResource?[] Resources);
    }
}

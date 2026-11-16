#nullable enable

using Microsoft.Data.Sqlite;

namespace MajMusic.Infrastructure.Shared
{
    /// <summary>Protects immutable objects still referenced by other game chart owners.</summary>
    public sealed partial class MajdataPlayCatalog
    {
        /// <summary>Reads all public resource references except the owners explicitly selected for removal.</summary>
        /// <param name="removalOwners">The exact server and chart identities being removed together.</param>
        /// <param name="cancellationToken">Cancels the short read-only snapshot.</param>
        /// <returns>The validated SHA-256 digests that must remain stored.</returns>
        /// <exception cref="IOException">An existing database cannot be accessed.</exception>
        /// <exception cref="UnauthorizedAccessException">Database access is denied.</exception>
        /// <exception cref="SqliteException">The existing public references cannot be read.</exception>
        /// <exception cref="OperationCanceledException">The operation is cancelled.</exception>
        public async Task<IReadOnlySet<string>> GetProtectedResourceHashesAsync(IReadOnlySet<(string Server, string ChartId)> removalOwners, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(removalOwners);
            cancellationToken.ThrowIfCancellationRequested();
            var hashes = new HashSet<string>(StringComparer.Ordinal);
            var path = Path.Combine(_root, "Runtime", "runtime.db");
            try
            {
                File.GetAttributes(path);
            }
            catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
            {
                return hashes;
            }

            await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false, DefaultTimeout = 2 }.ToString());
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            using var transaction = connection.BeginTransaction(deferred: true);
            var columns = await ReadColumnsAsync(connection, transaction, "OnlineChartCache", cancellationToken).ConfigureAwait(false);
            if (columns.Count == 0)
            {
                return hashes;
            }

            var scope = columns.Contains("Server") ? "Server" : columns.Contains("Endpoint") ? "Endpoint" : null;
            var mappings = await ReadServerMappingsAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            var resources = new[] { "TrackSha256", "CoverArtSha256", "ThumbnailSha256", "MaidataSha256", "VideoSha256" };
            command.CommandText = $"SELECT {SelectColumn(columns, "ChartId")}, {(scope is null ? "NULL" : $"[{scope}]")}, {string.Join(",", resources.Select(name => SelectColumn(columns, name)))} FROM OnlineChartCache";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var server = ReadText(reader, 1);
                var known = scope == "Server" || scope == "Endpoint" && mappings.TryGetValue(server, out server!);
                if (known && removalOwners.Contains((server, ReadText(reader, 0))))
                {
                    continue;
                }

                for (var index = 2; index < 7; index++)
                {
                    if (NormalizeDigest(ReadText(reader, index)) is { } hash)
                    {
                        hashes.Add(hash);
                    }
                }
            }

            return hashes;
        }
    }
}

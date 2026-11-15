#nullable enable

using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace MajMusic.Infrastructure.Storage
{
    /// <summary>Persists application-owned collection bindings independently of cloud account credentials.</summary>
    internal sealed class CloudCollectionSyncStore : IDisposable
    {
        /// <summary>Stores the explicit application-owned database path.</summary>
        private readonly string _path;
        /// <summary>Serializes schema initialization and small local binding transactions.</summary>
        private readonly SemaphoreSlim _gate = new(1, 1);
        /// <summary>Records successful initialization of this dedicated database.</summary>
        private bool _initialized;

        /// <summary>Creates an idle store without creating a directory or database.</summary>
        /// <param name="path">The application-owned binding database path.</param>
        public CloudCollectionSyncStore(string path)
        {
            _path = Path.GetFullPath(path);
        }

        /// <summary>Reads the bindings belonging to exactly one authenticated source/account scope.</summary>
        /// <param name="scope">The opaque source/account scope digest.</param>
        /// <param name="token">Cancels the local read.</param>
        /// <returns>The persisted owned bindings for this scope.</returns>
        public async Task<IReadOnlyList<CloudCollectionBinding>> ReadAsync(string scope, CancellationToken token)
        {
            await _gate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                await using var connection = await OpenAsync(token).ConfigureAwait(false);
                await using var command = connection.CreateCommand();
                command.CommandText = "SELECT document FROM bindings WHERE scope = $scope ORDER BY playlist;";
                command.Parameters.AddWithValue("$scope", scope);
                var bindings = new List<CloudCollectionBinding>();
                await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
                while (await reader.ReadAsync(token).ConfigureAwait(false))
                {
                    var binding = JsonSerializer.Deserialize<CloudCollectionBinding>(reader.GetString(0))
                        ?? throw new InvalidDataException("A cloud collection binding is invalid.");
                    if (binding.Scope != scope || !Guid.TryParseExact(binding.Marker, "N", out _))
                    {
                        throw new InvalidDataException("A cloud collection binding has an invalid owner marker.");
                    }

                    bindings.Add(binding);
                }

                return bindings;
            }
            finally
            {
                _gate.Release();
            }
        }

        /// <summary>Atomically saves a binding or its durable pending-creation journal.</summary>
        /// <param name="binding">The application-owned binding.</param>
        /// <param name="token">Cancels the local write before publication.</param>
        /// <returns>A task representing the completed local publication.</returns>
        public async Task SaveAsync(CloudCollectionBinding binding, CancellationToken token)
        {
            await _gate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                await using var connection = await OpenAsync(token).ConfigureAwait(false);
                await using var command = connection.CreateCommand();
                command.CommandText = """
                    INSERT INTO bindings (scope, playlist, document) VALUES ($scope, $playlist, $document)
                    ON CONFLICT(scope, playlist) DO UPDATE SET document = excluded.document;
                    """;
                command.Parameters.AddWithValue("$scope", binding.Scope);
                command.Parameters.AddWithValue("$playlist", binding.PlaylistId);
                command.Parameters.AddWithValue("$document", JsonSerializer.Serialize(binding));
                await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            }
            finally
            {
                _gate.Release();
            }
        }

        /// <summary>Removes a binding after its verified application-owned remote collection has been removed.</summary>
        /// <param name="binding">The completed deletion binding.</param>
        /// <param name="token">Cancels the local write.</param>
        /// <returns>A task representing the completed removal.</returns>
        public async Task DeleteAsync(CloudCollectionBinding binding, CancellationToken token)
        {
            await _gate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                await using var connection = await OpenAsync(token).ConfigureAwait(false);
                await using var command = connection.CreateCommand();
                command.CommandText = "DELETE FROM bindings WHERE scope = $scope AND playlist = $playlist;";
                command.Parameters.AddWithValue("$scope", binding.Scope);
                command.Parameters.AddWithValue("$playlist", binding.PlaylistId);
                await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            }
            finally
            {
                _gate.Release();
            }
        }

        /// <summary>Opens a nonpooled connection and initializes only this application-owned schema.</summary>
        /// <param name="token">Cancels opening or schema creation.</param>
        /// <returns>The open connection owned by the caller.</returns>
        private async Task<SqliteConnection> OpenAsync(CancellationToken token)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _path, Pooling = false }.ToString());
            try
            {
                await connection.OpenAsync(token).ConfigureAwait(false);
                if (!_initialized)
                {
                    await using var command = connection.CreateCommand();
                    command.CommandText = """
                        CREATE TABLE IF NOT EXISTS bindings (
                            scope TEXT NOT NULL,
                            playlist TEXT NOT NULL,
                            document TEXT NOT NULL,
                            PRIMARY KEY (scope, playlist)
                        );
                        """;
                    await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                    _initialized = true;
                }

                return connection;
            }
            catch
            {
                await connection.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }

        /// <inheritdoc />
        public void Dispose()
        {
            _gate.Dispose();
        }
    }

    /// <summary>Retains only opaque ownership and applied payload identity for one local playlist mirror.</summary>
    internal sealed record CloudCollectionBinding
    {
        /// <summary>Gets the source, account and canonical principal scope digest.</summary>
        public required string Scope { get; init; }
        /// <summary>Gets the locally persisted playlist identity.</summary>
        public required string PlaylistId { get; init; }
        /// <summary>Gets the durable random marker assigned before the first creation request.</summary>
        public required string Marker { get; init; }
        /// <summary>Gets the remote identity recovered from the account's owned collection inventory.</summary>
        public string? RemoteId { get; init; }
        /// <summary>Gets whether a creation may have reached the server and requires complete inventory reconciliation before a retry.</summary>
        public bool CreationPending { get; init; }
        /// <summary>Gets the fingerprint of the last successfully applied complete local projection.</summary>
        public string? AppliedFingerprint { get; init; }
    }
}

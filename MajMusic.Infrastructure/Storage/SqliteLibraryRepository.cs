#nullable enable

using System.Text.Json;
using MajMusic.Core.Abstractions;
using MajMusic.Core.Models;
using Microsoft.Data.Sqlite;

namespace MajMusic.Infrastructure.Storage
{
    /// <summary>Persists music metadata and playlists in a local SQLite database.</summary>
    public sealed class SqliteLibraryRepository : ILibraryRepository
    {
        /// <summary>Stores the absolute database file path.</summary>
        private readonly string _databasePath;

        /// <summary>Serializes initial schema creation for this repository.</summary>
        private readonly SemaphoreSlim _initializationGate = new(1, 1);

        /// <summary>Indicates that the schema was successfully initialized.</summary>
        private bool _initialized;

        /// <summary>Creates a repository without opening or changing the database.</summary>
        /// <param name="databasePath">The SQLite database file path.</param>
        /// <exception cref="ArgumentException">The path is empty or invalid.</exception>
        public SqliteLibraryRepository(string databasePath)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
            _databasePath = Path.GetFullPath(databasePath);
        }

        /// <summary>Creates the database schema if necessary, without replacing existing data.</summary>
        /// <param name="cancellationToken">Cancels initialization.</param>
        /// <returns>A task representing schema initialization.</returns>
        /// <exception cref="InvalidDataException">The database uses a newer unsupported schema.</exception>
        /// <exception cref="SqliteException">The database cannot be opened or updated.</exception>
        public async Task InitializeAsync(CancellationToken cancellationToken = default)
        {
            await _initializationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (_initialized)
                {
                    return;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(_databasePath)!);
                await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
                await using var versionCommand = connection.CreateCommand();
                versionCommand.CommandText = "PRAGMA user_version;";
                var version = Convert.ToInt32(await versionCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));
                if (version > 1)
                {
                    throw new InvalidDataException("The music database was created by a newer version of MajMusic.");
                }

                await using var transaction = connection.BeginTransaction();
                await using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = """
                    CREATE TABLE IF NOT EXISTS tracks (
                        id TEXT PRIMARY KEY NOT NULL,
                        title TEXT NOT NULL,
                        artist TEXT NOT NULL,
                        description TEXT NOT NULL,
                        tags TEXT NOT NULL,
                        document TEXT NOT NULL
                    );
                    CREATE TABLE IF NOT EXISTS playlists (
                        id TEXT PRIMARY KEY NOT NULL,
                        name TEXT NOT NULL,
                        document TEXT NOT NULL
                    );
                    PRAGMA user_version = 1;
                    """;
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                _initialized = true;
            }
            finally
            {
                _initializationGate.Release();
            }
        }

        /// <inheritdoc />
        public async Task<IReadOnlyList<MusicTrack>> SearchAsync(string? query, CancellationToken cancellationToken = default)
        {
            await InitializeAsync(cancellationToken).ConfigureAwait(false);
            await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT document FROM tracks
                WHERE $all = 1 OR title LIKE $query ESCAPE '\' COLLATE NOCASE
                    OR artist LIKE $query ESCAPE '\' COLLATE NOCASE
                    OR description LIKE $query ESCAPE '\' COLLATE NOCASE
                    OR tags LIKE $query ESCAPE '\' COLLATE NOCASE
                ORDER BY title COLLATE NOCASE, id;
                """;
            var keyword = query?.Trim() ?? string.Empty;
            command.Parameters.AddWithValue("$all", keyword.Length == 0 ? 1 : 0);
            command.Parameters.AddWithValue("$query", $"%{EscapeLike(keyword)}%");
            var tracks = new List<MusicTrack>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                tracks.Add(Deserialize<MusicTrack>(reader.GetString(0)));
            }

            return tracks;
        }

        /// <summary>Reads current metadata using its exact stable identity.</summary>
        /// <param name="id">The source-scoped identity.</param>
        /// <param name="cancellationToken">Cancels reading.</param>
        /// <returns>The entry, or null if absent.</returns>
        public async Task<MusicTrack?> FindTrackAsync(string id, CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(id);
            await InitializeAsync(cancellationToken).ConfigureAwait(false);
            await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT document FROM tracks WHERE id = $id;";
            command.Parameters.AddWithValue("$id", id);
            var document = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            return document is string json ? Deserialize<MusicTrack>(json) : null;
        }

        /// <inheritdoc />
        public async Task UpsertTracksAsync(IReadOnlyList<MusicTrack> tracks, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(tracks);
            foreach (var track in tracks)
            {
                ArgumentNullException.ThrowIfNull(track);
                ArgumentException.ThrowIfNullOrWhiteSpace(track.Id);
                ArgumentNullException.ThrowIfNull(track.Title);
                ArgumentNullException.ThrowIfNull(track.Audio);
            }

            await InitializeAsync(cancellationToken).ConfigureAwait(false);
            await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = connection.BeginTransaction();
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO tracks (id, title, artist, description, tags, document)
                VALUES ($id, $title, $artist, $description, $tags, $document)
                ON CONFLICT(id) DO UPDATE SET title = excluded.title, artist = excluded.artist,
                    description = excluded.description, tags = excluded.tags, document = excluded.document;
                """;
            var id = command.Parameters.Add("$id", SqliteType.Text);
            var title = command.Parameters.Add("$title", SqliteType.Text);
            var artist = command.Parameters.Add("$artist", SqliteType.Text);
            var description = command.Parameters.Add("$description", SqliteType.Text);
            var tags = command.Parameters.Add("$tags", SqliteType.Text);
            var document = command.Parameters.Add("$document", SqliteType.Text);
            foreach (var track in tracks)
            {
                cancellationToken.ThrowIfCancellationRequested();
                id.Value = track.Id;
                title.Value = track.Title;
                artist.Value = track.Artist;
                description.Value = track.Description;
                tags.Value = string.Join('\n', track.Tags);
                document.Value = JsonSerializer.Serialize(track);
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        /// <inheritdoc />
        public async Task<IReadOnlyList<MusicPlaylist>> GetPlaylistsAsync(CancellationToken cancellationToken = default)
        {
            await InitializeAsync(cancellationToken).ConfigureAwait(false);
            await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT document FROM playlists ORDER BY name COLLATE NOCASE, id;";
            var playlists = new List<MusicPlaylist>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                playlists.Add(Deserialize<MusicPlaylist>(reader.GetString(0)));
            }

            return playlists;
        }

        /// <inheritdoc />
        public async Task SavePlaylistAsync(MusicPlaylist playlist, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(playlist);
            ArgumentException.ThrowIfNullOrWhiteSpace(playlist.Id);
            ArgumentException.ThrowIfNullOrWhiteSpace(playlist.Name);
            ArgumentNullException.ThrowIfNull(playlist.TrackIds);
            await InitializeAsync(cancellationToken).ConfigureAwait(false);
            await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO playlists (id, name, document) VALUES ($id, $name, $document)
                ON CONFLICT(id) DO UPDATE SET name = excluded.name, document = excluded.document;
                """;
            command.Parameters.AddWithValue("$id", playlist.Id);
            command.Parameters.AddWithValue("$name", playlist.Name);
            command.Parameters.AddWithValue("$document", JsonSerializer.Serialize(playlist));
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        /// <inheritdoc />
        public async Task DeletePlaylistAsync(string id, CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(id);
            await InitializeAsync(cancellationToken).ConfigureAwait(false);
            await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM playlists WHERE id = $id;";
            command.Parameters.AddWithValue("$id", id);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        /// <summary>Opens a nonpooled connection so closed repositories do not retain file handles.</summary>
        /// <param name="cancellationToken">Cancels opening.</param>
        /// <returns>The open connection owned by the caller.</returns>
        private async Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken)
        {
            var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = _databasePath,
                Pooling = false
            }.ToString());
            try
            {
                await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
                return connection;
            }
            catch
            {
                await connection.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }

        /// <summary>Escapes the SQLite pattern syntax to implement literal keyword search.</summary>
        /// <param name="value">The user-supplied keyword.</param>
        /// <returns>The escaped pattern fragment.</returns>
        private static string EscapeLike(string value)
        {
            return value.Replace("\\", "\\\\", StringComparison.Ordinal)
                .Replace("%", "\\%", StringComparison.Ordinal)
                .Replace("_", "\\_", StringComparison.Ordinal);
        }

        /// <summary>Reads a stored document and refuses corrupt null records.</summary>
        /// <typeparam name="T">The stored entity type.</typeparam>
        /// <param name="document">The serialized entity.</param>
        /// <returns>The restored entity.</returns>
        /// <exception cref="InvalidDataException">The stored record is empty.</exception>
        private static T Deserialize<T>(string document)
        {
            return JsonSerializer.Deserialize<T>(document)
                ?? throw new InvalidDataException("The music database contains an empty metadata record.");
        }
    }
}

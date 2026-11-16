#nullable enable

using System.Text.Json;
using System.Text.Json.Serialization;
using MajMusic.Core.Abstractions;
using MajMusic.Core.Models;

namespace MajMusic.Infrastructure.Storage
{
    /// <summary>Loads and atomically saves non-secret preferences without contacting any source.</summary>
    public sealed class JsonSettingsStore : ISettingsStore
    {
        /// <summary>Defines the human-readable settings format and rejects misspelled or secret fields.</summary>
        private static readonly JsonSerializerOptions SerializerOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
        };

        /// <summary>Stores the absolute settings path.</summary>
        private readonly string _path;

        /// <summary>Prevents simultaneous writes through this store.</summary>
        private readonly SemaphoreSlim _writeGate = new(1, 1);

        /// <summary>Creates a store without creating a file.</summary>
        /// <param name="path">The settings file path.</param>
        /// <exception cref="ArgumentException">The path is empty or invalid.</exception>
        public JsonSettingsStore(string path)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(path);
            _path = Path.GetFullPath(path);
        }

        /// <summary>Loads preferences, or returns offline defaults when the file does not exist.</summary>
        /// <param name="cancellationToken">Cancels reading.</param>
        /// <returns>The saved preferences or offline defaults with no endpoints.</returns>
        /// <exception cref="InvalidDataException">The file is corrupt or has unsupported settings.</exception>
        /// <exception cref="IOException">The existing file cannot be read.</exception>
        public async Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read,
                    4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
                var settings = await JsonSerializer.DeserializeAsync<AppSettings>(stream, SerializerOptions, cancellationToken)
                    .ConfigureAwait(false) ?? throw new InvalidDataException("The settings document is empty.");
                settings = LocalDirectorySettings.Normalize(settings);
                Validate(settings);
                return settings;
            }
            catch (FileNotFoundException)
            {
                return LocalDirectorySettings.Normalize(new AppSettings());
            }
            catch (DirectoryNotFoundException)
            {
                return LocalDirectorySettings.Normalize(new AppSettings());
            }
            catch (JsonException exception)
            {
                throw new InvalidDataException("The settings file is invalid. It has been left unchanged.", exception);
            }
        }

        /// <summary>Writes validated settings to a temporary sibling and atomically replaces the destination.</summary>
        /// <param name="settings">The preferences to save; credentials are not part of this model.</param>
        /// <param name="cancellationToken">Cancels before publication of the complete file.</param>
        /// <returns>A task representing the completed replacement.</returns>
        /// <exception cref="InvalidDataException">The settings contain invalid endpoint or cache values.</exception>
        /// <exception cref="IOException">The file cannot be saved.</exception>
        public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(settings);
            settings = LocalDirectorySettings.Normalize(settings);
            Validate(settings);
            await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            var temporaryPath = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                await using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                    4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
                {
                    await JsonSerializer.SerializeAsync(stream, settings, SerializerOptions, cancellationToken).ConfigureAwait(false);
                    await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                }

                cancellationToken.ThrowIfCancellationRequested();
                File.Move(temporaryPath, _path, true);
            }
            finally
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }

                _writeGate.Release();
            }
        }

        /// <summary>Rejects invalid settings without repairing or silently replacing user configuration.</summary>
        /// <param name="settings">The preferences to validate.</param>
        /// <exception cref="InvalidDataException">A preference or endpoint is invalid.</exception>
        internal static void Validate(AppSettings settings)
        {
            foreach (var root in new[] { settings.MajdataPlayDirectory, settings.MediaStorageDirectory })
            {
                if (root is not null && (root.Length > 16384 || root.Contains('\0')
                    || !DocumentReference.IsContentUri(root) && !Path.IsPathFullyQualified(root)))
                {
                    throw new InvalidDataException("Media storage requires an absolute directory or granted document-tree reference.");
                }
            }
            if (!Enum.IsDefined(settings.Appearance))
            {
                throw new InvalidDataException("The appearance preference must be light, dark or system.");
            }

            if (!Enum.IsDefined(settings.CardLayout))
            {
                throw new InvalidDataException("The card layout must be spacious, compact or small.");
            }

            if (settings.AudioOutput is null || settings.AudioOutput.Volume is < 0 or > 100
                || settings.AudioOutput.DeviceId is { Length: > 4096 } || settings.AudioOutput.DeviceId?.Contains('\0') == true)
            {
                throw new InvalidDataException("Audio volume must be between zero and one hundred and the output device identity must be valid.");
            }

            settings = LocalDirectorySettings.Normalize(settings);
            if (settings.CacheLimitBytes < 0 || settings.Endpoints is null || settings.LocalMusicDirectories is null)
            {
                throw new InvalidDataException("Cache size must be nonnegative and endpoints must be an array.");
            }

            foreach (var directory in settings.LocalMusicDirectories)
            {
                if (string.IsNullOrWhiteSpace(directory) || !Path.IsPathFullyQualified(directory))
                {
                    throw new InvalidDataException("Local music directories must be fully qualified paths.");
                }
            }

            var identities = new HashSet<string>(StringComparer.Ordinal);
            foreach (var endpoint in settings.Endpoints)
            {
                if (endpoint is null || string.IsNullOrWhiteSpace(endpoint.Id) || string.IsNullOrWhiteSpace(endpoint.Name)
                    || endpoint.BaseUri is null || !endpoint.BaseUri.IsAbsoluteUri
                    || endpoint.BaseUri.Scheme != Uri.UriSchemeHttps || endpoint.BaseUri.UserInfo.Length != 0
                    || endpoint.BaseUri.Query.Length != 0 || endpoint.BaseUri.Fragment.Length != 0
                    || !identities.Add(endpoint.Id))
                {
                    throw new InvalidDataException("Each endpoint needs a unique identity, a name, and a credential-free HTTPS API root.");
                }

                if (endpoint.Accounts is null)
                {
                    throw new InvalidDataException("Each endpoint's saved accounts must be an array.");
                }

                var accountIds = new HashSet<string>(StringComparer.Ordinal);
                var usernames = new HashSet<string>(StringComparer.Ordinal);
                var automaticAccounts = 0;
                foreach (var account in endpoint.Accounts)
                {
                    if (account is null || string.IsNullOrWhiteSpace(account.Id) || string.IsNullOrWhiteSpace(account.Username)
                        || !accountIds.Add(account.Id) || !usernames.Add(account.Username)
                        || account.AutoLogin && !account.RememberPassword)
                    {
                        throw new InvalidDataException("Saved accounts need unique identities and names; automatic login requires a remembered password.");
                    }

                    if (account.AutoLogin)
                    {
                        automaticAccounts++;
                    }
                }

                if (automaticAccounts > 1 || endpoint.SelectedAccountId is not null && !accountIds.Contains(endpoint.SelectedAccountId))
                {
                    throw new InvalidDataException("Select an existing saved account and at most one automatic-login account per endpoint.");
                }
            }

            if (settings.ActiveEndpointId is not null && !identities.Contains(settings.ActiveEndpointId))
            {
                throw new InvalidDataException("The active endpoint is not present in the endpoint list.");
            }
        }
    }
}

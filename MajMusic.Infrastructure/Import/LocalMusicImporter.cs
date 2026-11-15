#nullable enable

using System.Security.Cryptography;
using MajMusic.Core.Models;

namespace MajMusic.Infrastructure.Import
{
    /// <summary>Copies local audio and optional cover files into a content-addressed music library.</summary>
    public sealed class LocalMusicImporter
    {
        /// <summary>Defines supported audio file extensions in directory import priority order.</summary>
        private static readonly string[] AudioExtensions = [".mp3", ".ogg", ".opus", ".wav", ".aac", ".flac"];

        /// <summary>Stores the absolute managed library directory.</summary>
        private readonly string _libraryRoot;

        /// <summary>Creates an importer without touching the source or destination.</summary>
        /// <param name="libraryRoot">The destination for managed audio and cover files.</param>
        /// <exception cref="ArgumentException">The destination path is empty or invalid.</exception>
        public LocalMusicImporter(string libraryRoot)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(libraryRoot);
            _libraryRoot = Path.GetFullPath(libraryRoot);
        }

        /// <summary>Imports a standard song directory, reading only title and artist metadata when available.</summary>
        /// <param name="sourceDirectory">The directory containing a supported track and optional cover.</param>
        /// <param name="cancellationToken">Cancels copying and prevents publishing an incomplete file.</param>
        /// <returns>The local music entry; its files are managed copies and the source remains unchanged.</returns>
        /// <exception cref="DirectoryNotFoundException">The source directory does not exist.</exception>
        /// <exception cref="FileNotFoundException">No supported track file exists.</exception>
        /// <exception cref="IOException">Metadata or media cannot be read or copied.</exception>
        public async Task<MusicTrack> ImportDirectoryAsync(string sourceDirectory, CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(sourceDirectory);
            var directory = Path.GetFullPath(sourceDirectory);
            cancellationToken.ThrowIfCancellationRequested();
            var files = Directory.GetFiles(directory);
            var audioPath = FindNamedFile(files, "track", AudioExtensions)
                ?? throw new FileNotFoundException("The selected directory has no supported track audio file.");
            var coverPath = FindNamedFile(files, "bg", [".png", ".jpg", ".jpeg"]);
            var metadataPath = files.FirstOrDefault(path => string.Equals(Path.GetFileName(path), "maidata.txt", StringComparison.OrdinalIgnoreCase));
            var title = new DirectoryInfo(directory).Name;
            var artist = string.Empty;
            if (metadataPath is not null)
            {
                using var reader = new StreamReader(metadataPath, detectEncodingFromByteOrderMarks: true);
                while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
                {
                    var separator = line.IndexOf('=');
                    if (separator < 0)
                    {
                        continue;
                    }

                    var key = line.AsSpan(0, separator).Trim();
                    if (key.Equals("&title", StringComparison.OrdinalIgnoreCase))
                    {
                        var value = line[(separator + 1)..].Trim();
                        if (value.Length > 0)
                        {
                            title = value;
                        }
                    }
                    else if (key.Equals("&artist", StringComparison.OrdinalIgnoreCase))
                    {
                        artist = line[(separator + 1)..].Trim();
                    }
                }
            }

            var audio = await CopyResourceAsync(audioPath, cancellationToken).ConfigureAwait(false);
            var cover = coverPath is null ? null : await CopyResourceAsync(coverPath, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return CreateTrack(audio, cover, title, artist);
        }

        /// <summary>Imports one supported audio file with its filename as the initial title.</summary>
        /// <param name="audioPath">The audio file to copy into the managed library.</param>
        /// <param name="cancellationToken">Cancels copying.</param>
        /// <returns>The imported local music entry.</returns>
        /// <exception cref="NotSupportedException">The file extension is unsupported.</exception>
        /// <exception cref="IOException">The source file cannot be read or copied.</exception>
        public async Task<MusicTrack> ImportAudioFileAsync(string audioPath, CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(audioPath);
            var fullPath = Path.GetFullPath(audioPath);
            if (!AudioExtensions.Contains(Path.GetExtension(fullPath), StringComparer.OrdinalIgnoreCase))
            {
                throw new NotSupportedException("The selected file is not a supported audio format.");
            }

            var audio = await CopyResourceAsync(fullPath, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return CreateTrack(audio, null, Path.GetFileNameWithoutExtension(fullPath), string.Empty);
        }

        /// <summary>Finds a known basename using case-insensitive matching for portable imports.</summary>
        /// <param name="files">The files directly inside the source directory.</param>
        /// <param name="basename">The expected name without its extension.</param>
        /// <param name="extensions">Supported extensions in preference order.</param>
        /// <returns>The matched file, or null.</returns>
        private static string? FindNamedFile(string[] files, string basename, string[] extensions)
        {
            foreach (var extension in extensions)
            {
                var match = files.FirstOrDefault(path => string.Equals(Path.GetFileName(path), basename + extension, StringComparison.OrdinalIgnoreCase));
                if (match is not null)
                {
                    return match;
                }
            }

            return null;
        }

        /// <summary>Builds a stable local entry from the audio content identity.</summary>
        /// <param name="audio">The managed audio descriptor.</param>
        /// <param name="cover">The optional managed cover descriptor.</param>
        /// <param name="title">The user-visible title.</param>
        /// <param name="artist">The credited artist.</param>
        /// <returns>The local music entry.</returns>
        private static MusicTrack CreateTrack(MediaResource audio, MediaResource? cover, string title, string artist)
        {
            return new MusicTrack
            {
                Id = "local:" + audio.Sha256,
                Title = title,
                Artist = artist,
                Audio = audio,
                Cover = cover
            };
        }

        /// <summary>Copies and hashes in one pass, publishing the destination only after successful completion.</summary>
        /// <param name="sourcePath">The source file, which is never modified.</param>
        /// <param name="cancellationToken">Cancels copying before publication.</param>
        /// <returns>The copied resource including its actual digest and byte length.</returns>
        /// <exception cref="InvalidDataException">The source file is empty.</exception>
        private async Task<MediaResource> CopyResourceAsync(string sourcePath, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Directory.CreateDirectory(_libraryRoot);
            var temporaryPath = Path.Combine(_libraryRoot, ".import-" + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                string digest;
                long length = 0;
                await using (var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                    81920, FileOptions.Asynchronous | FileOptions.SequentialScan))
                await using (var target = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                    81920, FileOptions.Asynchronous | FileOptions.SequentialScan))
                using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
                {
                    var buffer = new byte[81920];
                    int count;
                    while ((count = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                    {
                        hash.AppendData(buffer, 0, count);
                        await target.WriteAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
                        length += count;
                    }

                    await target.FlushAsync(cancellationToken).ConfigureAwait(false);
                    digest = Convert.ToHexStringLower(hash.GetHashAndReset());
                }

                if (length == 0)
                {
                    throw new InvalidDataException("An empty media file cannot be imported.");
                }

                cancellationToken.ThrowIfCancellationRequested();
                var directory = Path.Combine(_libraryRoot, digest[..2]);
                Directory.CreateDirectory(directory);
                var destination = Path.Combine(directory, digest + Path.GetExtension(sourcePath).ToLowerInvariant());
                try
                {
                    File.Move(temporaryPath, destination);
                }
                catch (IOException) when (File.Exists(destination))
                {
                    await using var existing = new FileStream(destination, FileMode.Open, FileAccess.Read, FileShare.Read,
                        81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
                    var existingDigest = Convert.ToHexStringLower(await SHA256.HashDataAsync(existing, cancellationToken).ConfigureAwait(false));
                    if (existing.Length != length || existingDigest != digest)
                    {
                        throw new InvalidDataException("The managed library contains a damaged content-addressed file.");
                    }
                }

                return new MediaResource { LocalPath = destination, Sha256 = digest, Length = length };
            }
            finally
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
        }
    }
}

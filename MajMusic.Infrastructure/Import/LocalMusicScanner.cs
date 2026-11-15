#nullable enable

using System.Security;
using System.Security.Cryptography;
using System.Text;
using MajMusic.Core.Models;

namespace MajMusic.Infrastructure.Import
{
    /// <summary>Recursively indexes local song directories without copying or changing any source file.</summary>
    public sealed class LocalMusicScanner
    {
        /// <summary>Lists supported track filenames in preference order.</summary>
        private static readonly string[] AudioExtensions = [".mp3", ".ogg", ".opus", ".wav", ".aac", ".flac"];

        /// <summary>Compares paths according to the platform's ordinary case behavior.</summary>
        private static readonly StringComparer PathComparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

        /// <summary>Reads a local directory tree and references original song files.</summary>
        /// <param name="directory">The root selected by the user.</param>
        /// <param name="cancellationToken">Cancels enumeration or metadata reads.</param>
        /// <returns>The complete result without publishing any database changes.</returns>
        /// <exception cref="ArgumentException">The root path is empty or invalid.</exception>
        /// <exception cref="DirectoryNotFoundException">The selected root does not exist.</exception>
        public Task<LocalLibraryScanResult> ScanAsync(string directory, CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(directory);
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
            cancellationToken.ThrowIfCancellationRequested();
            if (!Directory.Exists(root))
            {
                throw new DirectoryNotFoundException("The selected music directory does not exist.");
            }

            return Task.Run(() => ScanCoreAsync(root, cancellationToken), cancellationToken);
        }

        /// <summary>Enumerates one directory at a time without following reparse points.</summary>
        /// <param name="root">The normalized selected root.</param>
        /// <param name="cancellationToken">Cancels the operation.</param>
        /// <returns>The accumulated music references and skipped items.</returns>
        private static async Task<LocalLibraryScanResult> ScanCoreAsync(string root, CancellationToken cancellationToken)
        {
            var pending = new Stack<string>();
            var visited = new HashSet<string>(PathComparer);
            var tracks = new Dictionary<string, MusicTrack>(StringComparer.Ordinal);
            var issues = new List<LocalLibraryScanIssue>();
            var directoryCount = 0;
            pending.Push(root);
            while (pending.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var directory = pending.Pop();
                if (!visited.Add(directory))
                {
                    continue;
                }

                string[] entries;
                try
                {
                    if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                    {
                        AddIssue(issues, directory, "Symbolic links and junctions are not followed.");
                        continue;
                    }

                    directoryCount++;
                    entries = Directory.GetFileSystemEntries(directory);
                }
                catch (Exception exception) when (IsReadFailure(exception))
                {
                    AddIssue(issues, directory, "The directory could not be read.");
                    continue;
                }

                Array.Sort(entries, PathComparer);
                var files = new List<string>();
                foreach (var entry in entries)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        var attributes = File.GetAttributes(entry);
                        if ((attributes & FileAttributes.ReparsePoint) != 0)
                        {
                            AddIssue(issues, entry, "Symbolic links and junctions are not followed.");
                        }
                        else if ((attributes & FileAttributes.Directory) != 0)
                        {
                            pending.Push(entry);
                        }
                        else
                        {
                            files.Add(entry);
                        }
                    }
                    catch (Exception exception) when (IsReadFailure(exception))
                    {
                        AddIssue(issues, entry, "The item could not be inspected.");
                    }
                }

                var audio = FindNamedFile(files, "track", AudioExtensions);
                if (audio is null)
                {
                    continue;
                }

                try
                {
                    var track = await ReadTrackAsync(directory, files, audio, cancellationToken).ConfigureAwait(false);
                    tracks[track.Id] = track;
                }
                catch (Exception exception) when (IsReadFailure(exception) || exception is DecoderFallbackException or InvalidDataException)
                {
                    AddIssue(issues, directory, "The song metadata or audio file could not be read.");
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            return new LocalLibraryScanResult
            {
                RootDirectory = root,
                ScannedDirectoryCount = directoryCount,
                Tracks = Array.AsReadOnly(tracks.Values.OrderBy(track => track.Audio.LocalPath, PathComparer).ToArray()),
                Issues = issues.AsReadOnly()
            };
        }

        /// <summary>Reads only text metadata and file attributes, leaving audio bytes untouched.</summary>
        /// <param name="directory">The containing song directory.</param>
        /// <param name="files">Its direct ordinary files.</param>
        /// <param name="audioPath">The selected original audio path.</param>
        /// <param name="cancellationToken">Cancels metadata reading.</param>
        /// <returns>The original-file music reference.</returns>
        /// <exception cref="InvalidDataException">The original audio is empty.</exception>
        private static async Task<MusicTrack> ReadTrackAsync(string directory, List<string> files, string audioPath, CancellationToken cancellationToken)
        {
            var title = new DirectoryInfo(directory).Name;
            var artist = string.Empty;
            string? chartHash = null;
            var metadataPath = files.FirstOrDefault(file => string.Equals(Path.GetFileName(file), "maidata.txt", StringComparison.OrdinalIgnoreCase));
            if (metadataPath is not null)
            {
                var metadata = await ReadMetadataAsync(metadataPath, cancellationToken).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(metadata.Title))
                {
                    title = metadata.Title;
                }

                artist = metadata.Artist ?? string.Empty;
                chartHash = metadata.Hash;
            }

            var audio = new FileInfo(audioPath);
            if (audio.Length == 0)
            {
                throw new InvalidDataException("The original audio file is empty.");
            }

            var coverPath = FindNamedFile(files, "bg", [".png", ".jpg", ".jpeg"]);
            var normalizedAudioPath = Path.GetFullPath(audioPath);
            var identityPath = OperatingSystem.IsWindows() ? normalizedAudioPath.ToUpperInvariant() : normalizedAudioPath;
            return new MusicTrack
            {
                Id = "local-path:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(identityPath))),
                Title = title,
                Artist = artist,
                MajChartHash = chartHash,
                SourceId = "local/reference",
                Revision = $"{audio.Length}:{audio.LastWriteTimeUtc.Ticks}",
                Audio = new MediaResource { LocalPath = normalizedAudioPath, Length = audio.Length },
                Cover = coverPath is null ? null : new MediaResource { LocalPath = Path.GetFullPath(coverPath) }
            };
        }

        /// <summary>Reads canonical title and artist fields without parsing gameplay notes.</summary>
        /// <param name="metadataPath">The original metadata file.</param>
        /// <param name="cancellationToken">Cancels text reading.</param>
        /// <returns>The text title and artist together with the original metadata bytes' MD5 Base64 identity.</returns>
        private static async Task<(string? Title, string? Artist, string Hash)> ReadMetadataAsync(string metadataPath, CancellationToken cancellationToken)
        {
            string? title = null;
            string? artist = null;
            await using var stream = new FileStream(metadataPath, FileMode.Open, FileAccess.Read, FileShare.Read,
                81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var digest = await MD5.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
            stream.Position = 0;
            using var reader = new StreamReader(stream, new UTF8Encoding(false, true), true);
            while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
            {
                var separator = line.IndexOf('=');
                if (separator < 0)
                {
                    continue;
                }

                var key = line.AsSpan(0, separator).Trim();
                if (key.StartsWith("&inote_", StringComparison.OrdinalIgnoreCase))
                {
                    break;
                }

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

                if (title is not null && artist is not null)
                {
                    break;
                }
            }

            return (title, artist, Convert.ToBase64String(digest));
        }

        /// <summary>Finds a portable standard filename using extension priority.</summary>
        /// <param name="files">The direct file paths.</param>
        /// <param name="basename">The expected basename.</param>
        /// <param name="extensions">The supported extensions in order.</param>
        /// <returns>The original matched path, or null.</returns>
        private static string? FindNamedFile(List<string> files, string basename, string[] extensions)
        {
            foreach (var extension in extensions)
            {
                var path = files.FirstOrDefault(file => string.Equals(Path.GetFileName(file), basename + extension, StringComparison.OrdinalIgnoreCase));
                if (path is not null)
                {
                    return path;
                }
            }

            return null;
        }

        /// <summary>Classifies expected source-tree failures that should not abort other songs.</summary>
        /// <param name="exception">The source read failure.</param>
        /// <returns>Whether the source path can be skipped safely.</returns>
        private static bool IsReadFailure(Exception exception)
        {
            return exception is IOException or UnauthorizedAccessException or SecurityException;
        }

        /// <summary>Records a skipped source path without modifying that path.</summary>
        /// <param name="issues">The accumulated scan issues.</param>
        /// <param name="path">The original skipped path.</param>
        /// <param name="message">The short readable reason.</param>
        private static void AddIssue(List<LocalLibraryScanIssue> issues, string path, string message)
        {
            issues.Add(new LocalLibraryScanIssue { Path = path, Message = message });
        }
    }
}

#nullable enable

using System.Text.Json;
using System.Text.Json.Nodes;
using global::Android.Content;
using global::Android.OS;

namespace MajMusic.Android.Storage
{
    /// <summary>Chooses and prepares Music's Android data root beside the public MajdataPlay directory.</summary>
    internal static class AndroidDataDirectory
    {
        /// <summary>Names the public application directory under the shared Documents folder.</summary>
        private const string PublicDirectoryName = "MajMusic";

        /// <summary>Creates the directories used by the Music data and media layout.</summary>
        private static readonly string[] LayoutDirectories =
        [
            "Cache",
            "Cache/Temp",
            "Runtime",
            "MaiCharts",
            "Skins",
            "Logs",
            "RecordOutputs",
            "AudioMetadata",
            "DocumentCovers"
        ];

        /// <summary>Resolves the public Documents root when direct shared-storage access is already available.</summary>
        /// <param name="context">The process-owned Android application context.</param>
        /// <returns>The public Documents/MajMusic root, or the application-private fallback.</returns>
        /// <exception cref="InvalidOperationException">The application-private files directory is unavailable.</exception>
        public static string Resolve(Context context)
        {
            ArgumentNullException.ThrowIfNull(context);
            var fallback = context.FilesDir?.AbsolutePath
                ?? throw new InvalidOperationException("Application storage is unavailable.");
            var preferred = GetPublicDirectory();
            if (preferred is null || OperatingSystem.IsAndroidVersionAtLeast(30) && !global::Android.OS.Environment.IsExternalStorageManager)
            {
                EnsureLayout(fallback);
                return fallback;
            }

            try
            {
                Prepare(preferred, fallback);
                global::Android.Util.Log.Info("MajMusic.Storage", $"Using public data root: {preferred}");
                return preferred;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or global::Java.Lang.SecurityException)
            {
                EnsureLayout(fallback);
                global::Android.Util.Log.Warn("MajMusic.Storage", $"Public data root unavailable: {exception.GetType().Name}");
                return fallback;
            }
        }

        /// <summary>Prepares one writable root and copies missing legacy application data into it.</summary>
        /// <param name="preferred">The desired public root.</param>
        /// <param name="legacy">The previous application-private root.</param>
        /// <exception cref="ArgumentException">A supplied root is not fully qualified.</exception>
        /// <exception cref="IOException">The root cannot be created or legacy data cannot be copied.</exception>
        internal static void Prepare(string preferred, string legacy)
        {
            var destination = RequireAbsoluteDirectory(preferred, nameof(preferred));
            var source = RequireAbsoluteDirectory(legacy, nameof(legacy));
            if (PathEquals(destination, source))
            {
                EnsureLayout(destination);
                return;
            }

            Directory.CreateDirectory(destination);
            MigrateMissingData(source, destination);
            EnsureLayout(destination);
        }

        /// <summary>Returns the public Documents/MajMusic path when Android exposes a public Documents directory.</summary>
        /// <returns>The public path, or null when Android cannot provide one.</returns>
        private static string? GetPublicDirectory()
        {
#pragma warning disable CS0618
            using var documents = global::Android.OS.Environment.GetExternalStoragePublicDirectory(global::Android.OS.Environment.DirectoryDocuments);
#pragma warning restore CS0618
            return documents?.AbsolutePath is { Length: > 0 } path ? Path.Combine(path, PublicDirectoryName) : null;
        }

        /// <summary>Creates the stable top-level directories shared by the Music and MajdataPlay layouts.</summary>
        /// <param name="directory">The writable application root.</param>
        private static void EnsureLayout(string directory)
        {
            Directory.CreateDirectory(directory);
            foreach (var name in LayoutDirectories)
            {
                Directory.CreateDirectory(Path.Combine(directory, name));
            }
        }

        /// <summary>Copies legacy entries through a staging directory without overwriting an existing public entry.</summary>
        /// <param name="source">The private application root.</param>
        /// <param name="destination">The public application root.</param>
        private static void MigrateMissingData(string source, string destination)
        {
            if (!Directory.Exists(source) || !Directory.EnumerateFileSystemEntries(source).Any())
            {
                return;
            }

            var staging = Path.Combine(destination, ".majmusic-migration-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(staging);
            try
            {
                CopyDirectory(source, staging);
                foreach (var entry in Directory.EnumerateFileSystemEntries(staging))
                {
                    var name = Path.GetFileName(entry);
                    if (string.IsNullOrEmpty(name))
                    {
                        continue;
                    }

                    var target = Path.Combine(destination, name);
                    if (Directory.Exists(entry))
                    {
                        if (Directory.Exists(target))
                        {
                            CopyMissingDirectory(entry, target);
                        }
                        else if (!File.Exists(target))
                        {
                            Directory.Move(entry, target);
                        }
                    }
                    else if (!Directory.Exists(target) && !File.Exists(target))
                    {
                        File.Move(entry, target);
                    }
                }

                RewriteLegacySettings(Path.Combine(destination, "settings.json"), source, destination);
            }
            finally
            {
                if (Directory.Exists(staging))
                {
                    Directory.Delete(staging, true);
                }
            }
        }

        /// <summary>Rewrites saved paths that referred to the old private root after public migration.</summary>
        /// <param name="settingsPath">The migrated settings document.</param>
        /// <param name="legacy">The old private application root.</param>
        /// <param name="destination">The new public application root.</param>
        private static void RewriteLegacySettings(string settingsPath, string legacy, string destination)
        {
            if (!File.Exists(settingsPath))
            {
                return;
            }

            var document = JsonNode.Parse(File.ReadAllText(settingsPath)) as JsonObject;
            if (document is null)
            {
                return;
            }

            var changed = RewritePathValues(document, legacy, destination);
            if (changed)
            {
                File.WriteAllText(settingsPath, document.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            }
        }

        /// <summary>Rewrites every JSON string that identifies a path inside the old application root.</summary>
        /// <param name="node">The JSON node being inspected.</param>
        /// <param name="legacy">The old private application root.</param>
        /// <param name="destination">The new public application root.</param>
        /// <returns>Whether at least one string value changed.</returns>
        private static bool RewritePathValues(JsonNode node, string legacy, string destination)
        {
            var changed = false;
            if (node is JsonObject objectNode)
            {
                foreach (var property in objectNode.ToArray())
                {
                    if (property.Value is null)
                    {
                        continue;
                    }

                    if (TryRelocateValue(property.Value, legacy, destination, out var replacement))
                    {
                        objectNode[property.Key] = replacement;
                        changed = true;
                    }
                    else if (RewritePathValues(property.Value, legacy, destination))
                    {
                        changed = true;
                    }
                }
            }
            else if (node is JsonArray arrayNode)
            {
                for (var index = 0; index < arrayNode.Count; index++)
                {
                    if (arrayNode[index] is not { } item)
                    {
                        continue;
                    }

                    if (TryRelocateValue(item, legacy, destination, out var replacement))
                    {
                        arrayNode[index] = replacement;
                        changed = true;
                    }
                    else if (RewritePathValues(item, legacy, destination))
                    {
                        changed = true;
                    }
                }
            }

            return changed;
        }

        /// <summary>Builds a replacement JSON string node when one value contains a legacy path.</summary>
        /// <param name="node">The JSON value being checked.</param>
        /// <param name="legacy">The old private application root.</param>
        /// <param name="destination">The new public application root.</param>
        /// <param name="replacement">The relocated JSON string node.</param>
        /// <returns>Whether the value was a relocatable string.</returns>
        private static bool TryRelocateValue(JsonNode node, string legacy, string destination, out JsonNode? replacement)
        {
            replacement = null;
            if (node is not JsonValue valueNode || !valueNode.TryGetValue<string>(out var value)
                || TryRelocatePath(value, legacy, destination) is not { } relocated)
            {
                return false;
            }

            replacement = JsonValue.Create(relocated);
            return true;
        }

        /// <summary>Relocates one absolute path only when it remains inside the old application root.</summary>
        /// <param name="value">The JSON string value.</param>
        /// <param name="legacy">The old private application root.</param>
        /// <param name="destination">The new public application root.</param>
        /// <returns>The relocated path, or null when the value is unrelated.</returns>
        private static string? TryRelocatePath(string value, string legacy, string destination)
        {
            if (string.IsNullOrWhiteSpace(value) || !Path.IsPathFullyQualified(value))
            {
                return null;
            }

            var path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(value));
            if (!PathEquals(path, legacy) && !path.StartsWith(legacy + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                && !path.StartsWith(legacy + Path.AltDirectorySeparatorChar, StringComparison.Ordinal))
            {
                return null;
            }

            var relative = Path.GetRelativePath(legacy, path);
            return relative == "." ? destination : Path.Combine(destination, relative);
        }

        /// <summary>Copies a private application tree into a fresh migration staging directory.</summary>
        /// <param name="source">The source tree.</param>
        /// <param name="destination">The empty staging tree.</param>
        private static void CopyDirectory(string source, string destination)
        {
            Directory.CreateDirectory(destination);
            foreach (var entry in Directory.EnumerateFileSystemEntries(source))
            {
                var name = Path.GetFileName(entry);
                if (string.IsNullOrEmpty(name) || name.StartsWith(".", StringComparison.Ordinal))
                {
                    continue;
                }

                var target = Path.Combine(destination, name);
                if (Directory.Exists(entry))
                {
                    if ((File.GetAttributes(entry) & FileAttributes.ReparsePoint) == 0)
                    {
                        CopyDirectory(entry, target);
                    }
                }
                else if ((File.GetAttributes(entry) & FileAttributes.ReparsePoint) == 0)
                {
                    File.Copy(entry, target);
                }
            }
        }

        /// <summary>Copies only missing files from one staged directory into an existing public directory.</summary>
        /// <param name="source">The staged source directory.</param>
        /// <param name="destination">The existing public directory.</param>
        private static void CopyMissingDirectory(string source, string destination)
        {
            Directory.CreateDirectory(destination);
            foreach (var entry in Directory.EnumerateFileSystemEntries(source))
            {
                var name = Path.GetFileName(entry);
                if (string.IsNullOrEmpty(name))
                {
                    continue;
                }

                var target = Path.Combine(destination, name);
                if (Directory.Exists(entry))
                {
                    if (Directory.Exists(target))
                    {
                        CopyMissingDirectory(entry, target);
                    }
                    else if (!File.Exists(target))
                    {
                        Directory.Move(entry, target);
                    }
                }
                else if (!Directory.Exists(target) && !File.Exists(target))
                {
                    File.Move(entry, target);
                }
            }
        }

        /// <summary>Validates and canonicalizes one application root.</summary>
        /// <param name="directory">The candidate root.</param>
        /// <param name="parameterName">The argument name used in validation.</param>
        /// <returns>The full directory path without a trailing separator.</returns>
        private static string RequireAbsoluteDirectory(string directory, string parameterName)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(directory, parameterName);
            if (!Path.IsPathFullyQualified(directory))
            {
                throw new ArgumentException("The Android data root must be an absolute path.", parameterName);
            }

            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        }

        /// <summary>Compares two canonical filesystem roots using the current platform's case rules.</summary>
        /// <param name="first">The first root.</param>
        /// <param name="second">The second root.</param>
        /// <returns>Whether the roots identify the same directory.</returns>
        private static bool PathEquals(string first, string second)
        {
            return string.Equals(first, second, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        }
    }
}
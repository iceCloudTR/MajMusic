#nullable enable

using System.Text.Json;

namespace MajMusic.Infrastructure.Preview
{
    /// <summary>Opens the renderer shipped with the app or a previously verified development copy.</summary>
    public sealed class MajViewRuntime
    {
        /// <summary>Names the runtime folder in the desktop app and Android assets.</summary>
        public const string PackageDirectory = "MajView";

        private static readonly (string Revision, bool EmbeddedViewport, RuntimeFile[] Files) Manifest = ReadManifest();
        private readonly Func<string, Stream> _open;

        internal static IReadOnlyList<RuntimeFile> Files => Manifest.Files;
        internal static bool EmbeddedViewport => Manifest.EmbeddedViewport;

        /// <summary>Connects the package resources to the loopback preview.</summary>
        /// <param name="dataDirectory">The workspace directory containing a possible older installation of this revision.</param>
        /// <param name="openAsset">Opens a packaged asset when the platform stores it inside its application archive.</param>
        public MajViewRuntime(string dataDirectory, Func<string, Stream>? openAsset = null)
        {
            if (openAsset is not null)
            {
                _open = openAsset;
                return;
            }

            var bundled = Path.Combine(AppContext.BaseDirectory, PackageDirectory);
            var directory = Directory.Exists(bundled) ? bundled : Path.Combine(dataDirectory, "Runtime", PackageDirectory, Manifest.Revision);
            _open = name => MajViewPreviewSession.OpenFile(Path.Combine(directory, name));
        }

        /// <summary>Confirms the renderer is available before the song resources are resolved.</summary>
        /// <param name="cancellationToken">Cancels before opening the preview.</param>
        internal void EnsureAvailable(CancellationToken cancellationToken)
        {
            foreach (var file in Files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    using var stream = _open(file.Name);
                }
                catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
                {
                    throw new InvalidOperationException("MajView 预览组件缺失。", exception);
                }
            }
        }

        internal Stream Open(string fileName) => _open(fileName);

        private static (string Revision, bool EmbeddedViewport, RuntimeFile[] Files) ReadManifest()
        {
            using var stream = typeof(MajViewRuntime).Assembly.GetManifestResourceStream("MajMusic.MajView.runtime.json")!;
            using var document = JsonDocument.Parse(stream);
            var root = document.RootElement;
            var files = root.GetProperty("files").EnumerateArray()
                .Select(file => new RuntimeFile(file.GetProperty("name").GetString()!, file.GetProperty("length").GetInt64()))
                .ToArray();
            var embeddedViewport = root.TryGetProperty("embeddedViewport", out var viewport) && viewport.GetBoolean();
            return (root.GetProperty("revision").GetString()!, embeddedViewport, files);
        }

        internal sealed record RuntimeFile(string Name, long Length)
        {
            public string ContentType => Path.GetExtension(Name) switch
            {
                ".js" => "application/javascript",
                ".wasm" => "application/wasm",
                _ => "application/octet-stream"
            };
        }
    }
}

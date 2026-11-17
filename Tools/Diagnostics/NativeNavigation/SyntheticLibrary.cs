#nullable enable

using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using MajMusic.Core.Abstractions;
using MajMusic.Core.Models;
using MajMusic.Infrastructure.Application;
using MajMusic.Infrastructure.Storage;

namespace MajMusic.Diagnostics.NativeNavigation
{
    /// <summary>Creates private generated media and metadata without using the player's configuration, credentials or network.</summary>
    internal sealed class SyntheticLibrary : IDisposable
    {
        /// <summary>Owns an HTTP facade that rejects every request.</summary>
        private readonly HttpClient _http;
        /// <summary>Creates a diagnostic-only workspace.</summary>
        /// <param name="root">The newly created diagnostic data directory.</param>
        private SyntheticLibrary(string root)
        {
            _http = new HttpClient(Transport, false);
            Workspace = new MusicWorkspace(root, _http, _http, credentialStore: Credentials);
        }
        /// <summary>Gets the private production workspace under measurement.</summary>
        public MusicWorkspace Workspace { get; }
        /// <summary>Gets the no-network boundary and its attempted request count.</summary>
        public RejectedTransport Transport { get; } = new();
        /// <summary>Gets the no-vault boundary and its attempted access count.</summary>
        public RejectedCredentials Credentials { get; } = new();
        /// <summary>Gets the number of generated searchable songs.</summary>
        public int TrackCount => 600;
        /// <summary>Gets the generated cover files available to the diagnostic recommendation feed.</summary>
        public IReadOnlyList<string> Covers { get; private set; } = Array.Empty<string>();

        /// <summary>Seeds complete synthetic catalog metadata, covers and private playlists before timing begins.</summary>
        /// <param name="root">The uniquely owned generated-data directory.</param>
        /// <param name="token">Cancels fixture file and private database operations.</param>
        /// <returns>The owned synthetic workspace and boundaries.</returns>
        /// <param name="separateArtwork">Uses different artwork for the first 25 tracks of each tested mode.</param>
        public static async Task<SyntheticLibrary> CreateAsync(string root, CancellationToken token, bool separateArtwork = false)
        {
            Directory.CreateDirectory(root);
            var fixture = new SyntheticLibrary(root);
            try
            {
                var covers = new List<string>();
                for (var index = 0; index < (separateArtwork ? 100 : 8); index++)
                {
                    using var image = new RenderTargetBitmap(new PixelSize(256, 256));
                    var sample = new Avalonia.Controls.Border { Width = 256, Height = 256, Background = new SolidColorBrush(Color.FromRgb((byte)((60 + index * 20) & 255), (byte)((80 + index * 13) & 255), (byte)((160 - index * 9) & 255))) };
                    sample.Measure(new Size(256, 256));
                    sample.Arrange(new Rect(0, 0, 256, 256));
                    image.Render(sample);
                    var path = Path.Combine(root, "generated-cover-" + index + ".png");
                    image.Save(path, PngBitmapEncoderOptions.Default);
                    covers.Add(path);
                }
                fixture.Covers = covers;
                var settings = await fixture.Workspace.LoadSettingsAsync(token);
                await fixture.Workspace.SaveSettingsAsync(settings with
                {
                    OnlineEnabled = false,
                    Endpoints = Enumerable.Range(0, 4).Select(index => new EndpointConfiguration { Id = "generated-source-" + index, Name = "Generated source " + index,
                        BaseUri = new Uri("https://source-" + index + ".example.test/api/") }).ToArray(),
                    LocalDirectorySources = Enumerable.Range(0, 4).Select(index => new LocalDirectorySource { Id = "generated-directory-" + index,
                        Name = "Generated library " + index, Path = root, Kind = LocalDirectoryKind.MusicLibrary }).ToArray()
                }, token);
                var tracks = Enumerable.Range(0, fixture.TrackCount).Select(index => new MusicTrack
                {
                    Id = "generated-" + index, Title = "Generated song " + index.ToString("0000") + " · Native navigation fixture",
                    Artist = "Generated artist " + index % 7, SourceId = "local/native-diagnostics",
                    Audio = new MediaResource(), Cover = new MediaResource { LocalPath = covers[index % covers.Count] },
                    Duration = TimeSpan.FromSeconds(180 + index % 90), AudioInfo = new AudioStreamInfo { BitrateBitsPerSecond = 320000, SampleRateHz = 44100 }
                }).ToArray();
                await new SqliteLibraryRepository(Path.Combine(root, "library.db")).UpsertTracksAsync(tracks, token);
                for (var index = 0; index < 20; index++)
                {
                    await fixture.Workspace.SavePlaylistAsync(new MusicPlaylist { Id = "generated-list-" + index, Name = "Generated playlist " + index,
                        TrackIds = tracks.Skip(separateArtwork ? 225 + index * 15 : index * 15).Take(40).Select(track => track.Id).ToArray() }, token);
                }
                return fixture;
            }
            catch
            {
                fixture.Dispose();
                throw;
            }
        }
        /// <inheritdoc />
        public void Dispose()
        {
            Workspace.Dispose();
            _http.Dispose();
            Transport.Dispose();
        }
    }

    /// <summary>Fails instead of contacting any configured source.</summary>
    internal sealed class RejectedTransport : HttpMessageHandler
    {
        /// <summary>Gets the number of attempted requests, which must remain zero.</summary>
        public int RequestCount { get; private set; }
        /// <inheritdoc />
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            throw new InvalidOperationException("Native synthetic navigation must never contact a source.");
        }
    }

    /// <summary>Fails instead of accessing any user's operating-system credential store.</summary>
    internal sealed class RejectedCredentials : ICredentialStore
    {
        /// <summary>Gets the attempted credential-operation count, which must remain zero.</summary>
        public int Calls { get; private set; }
        /// <inheritdoc />
        public Task<string?> ReadAsync(string key, CancellationToken cancellationToken = default)
        {
            Calls++;
            throw new InvalidOperationException("Native synthetic navigation must never read credentials.");
        }
        /// <inheritdoc />
        public Task WriteAsync(string key, string secret, CancellationToken cancellationToken = default)
        {
            Calls++;
            throw new InvalidOperationException("Native synthetic navigation must never save credentials.");
        }
        /// <inheritdoc />
        public Task DeleteAsync(string key, CancellationToken cancellationToken = default)
        {
            Calls++;
            throw new InvalidOperationException("Native synthetic navigation must never delete credentials.");
        }
    }
}

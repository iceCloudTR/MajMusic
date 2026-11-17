#nullable enable

using MajMusic.Core.Abstractions;
using MajMusic.Core.Models;
using MajMusic.Infrastructure.Application;

namespace MajMusic.Diagnostics.NativeNavigation
{
    /// <summary>Adds a populated recommendation feed in-process without allowing the workspace to contact a source.</summary>
    internal sealed class RapidNavigationWorkspace(SyntheticLibrary fixture) : IMusicWorkspace, IOnlineMusicWorkspace
    {
        private MusicWorkspace Local => fixture.Workspace;

        public int RecommendationReads { get; private set; }

        public bool IsOnline => true;

        public string DataDirectory => Local.DataDirectory;

        public async Task<AppSettings> LoadSettingsAsync(CancellationToken cancellationToken = default)
        {
            var settings = await Local.LoadSettingsAsync(cancellationToken);
            return settings with { OnlineEnabled = true, ActiveEndpointId = settings.Endpoints.First().Id };
        }

        public IReadOnlyList<EndpointSession> GetEndpointSessions() =>
            [new EndpointSession { EndpointId = "generated-source-0", IsLoggedIn = true, Username = "generated-listener" }];

        public async Task<IReadOnlyList<MusicTrack>> GetRecommendationsAsync(EndpointConfiguration endpoint, int page = 0,
            int pageSize = 50, CancellationToken cancellationToken = default)
        {
            RecommendationReads++;
            await Task.Delay(10, cancellationToken);
            return Enumerable.Range(page * pageSize, Math.Clamp(150 - page * pageSize, 0, pageSize)).Select(index => new MusicTrack
            {
                Id = "recommendation-" + index,
                Title = "Generated recommendation " + index.ToString("0000"),
                Artist = "Generated artist",
                EndpointId = endpoint.Id,
                SourceId = endpoint.Id,
                RemoteId = index.ToString(),
                Revision = "synthetic",
                Cover = new MediaResource { LocalPath = fixture.Covers[50 + index % 50] },
                Audio = new MediaResource(),
                Duration = TimeSpan.FromSeconds(180 + index),
                AudioInfo = new AudioStreamInfo { BitrateBitsPerSecond = 320000, SampleRateHz = 44100 }
            }).ToArray();
        }

        public Task<IReadOnlyList<MusicTrack>> SearchAsync(string? query, CancellationToken cancellationToken = default) => Local.SearchAsync(query, cancellationToken);
        public Task<IReadOnlyList<MusicPlaylist>> GetPlaylistsAsync(CancellationToken cancellationToken = default) => Local.GetPlaylistsAsync(cancellationToken);
        public Task<IReadOnlyList<MusicPlaylist>> GetCloudPlaylistsAsync(EndpointConfiguration endpoint, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<MusicPlaylist>>([]);
        public Task<string?> ResolveCoverAsync(MusicTrack track, CancellationToken cancellationToken = default) => Task.FromResult(track.Cover?.LocalPath);
        public Task<MusicTrack> EnrichTrackMetadataAsync(MusicTrack track, CancellationToken cancellationToken = default) => Task.FromResult(track);
        public void SetOnlineEnabled(bool enabled) { }
        public Task SaveSettingsAsync(AppSettings settings, CancellationToken cancellationToken = default) => throw Unexpected();
        public Task<MusicTrack> ImportDirectoryAsync(string directory, CancellationToken cancellationToken = default) => throw Unexpected();
        public Task<LocalLibraryScanResult> IndexDirectoryAsync(string directory, CancellationToken cancellationToken = default) => throw Unexpected();
        public Task SavePlaylistAsync(MusicPlaylist playlist, CancellationToken cancellationToken = default) => throw Unexpected();
        public Task<int> SynchronizeAsync(EndpointConfiguration endpoint, CancellationToken cancellationToken = default) => throw Unexpected();
        public Task<string> ResolveAudioAsync(MusicTrack track, bool keepOffline, CancellationToken cancellationToken = default) => throw Unexpected();
        public Task ClearCacheAsync(CancellationToken cancellationToken = default) => throw Unexpected();
        public Task LoginAsync(EndpointConfiguration endpoint, string username, string password, CancellationToken cancellationToken = default) => throw Unexpected();
        public Task UseGuestAsync(EndpointConfiguration endpoint, CancellationToken cancellationToken = default) => throw Unexpected();
        public Task UpdateTrackMetadataAsync(MusicTrack track, CancellationToken cancellationToken = default) => throw Unexpected();

        private static InvalidOperationException Unexpected() => new("Rapid navigation must not mutate music, accounts or preferences.");
    }
}

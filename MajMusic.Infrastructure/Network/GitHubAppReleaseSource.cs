#nullable enable

using System.Net;
using MajMusic.Core.Abstractions;

namespace MajMusic.Infrastructure.Network
{
    /// <summary>Looks up the stable version through the official GitHub latest-release link.</summary>
    public sealed class GitHubAppReleaseSource : IAppReleaseSource
    {
        private const string ReleasesUrl = "https://github.com/iceCloudTR/MajMusic/releases";
        private static readonly Uri Releases = new(ReleasesUrl);
        private static readonly Uri LatestRelease = new(ReleasesUrl + "/latest");
        private static readonly Uri ReleaseTags = new(ReleasesUrl + "/tag/");
        private static readonly HttpClient PublicClient = new(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false })
        {
            Timeout = TimeSpan.FromSeconds(10)
        };

        private readonly HttpClient _client;

        /// <summary>Creates an anonymous release source with a dedicated transport.</summary>
        public GitHubAppReleaseSource() : this(PublicClient)
        {
        }

        /// <summary>Creates a release source with an independent test transport.</summary>
        /// <param name="client">The caller-owned HTTP client.</param>
        internal GitHubAppReleaseSource(HttpClient client)
        {
            _client = client;
        }

        /// <inheritdoc />
        public async Task<(Version Version, Uri Page)?> GetLatestAsync(CancellationToken cancellationToken = default)
        {
            using var request = new HttpRequestMessage(HttpMethod.Head, LatestRelease);
            request.Headers.UserAgent.ParseAdd("MajMusic");

            using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return null;
            }

            if (response.StatusCode is not (HttpStatusCode.MovedPermanently or HttpStatusCode.Found
                or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect))
            {
                response.EnsureSuccessStatusCode();
                throw new InvalidDataException("GitHub did not redirect to a MajMusic release.");
            }

            if (response.Headers.Location is not { } location || !Uri.TryCreate(LatestRelease, location, out var page))
            {
                throw new InvalidDataException("GitHub did not identify the latest MajMusic release.");
            }

            if (page == Releases)
            {
                return null;
            }

            if (!ReleaseTags.IsBaseOf(page))
            {
                throw new InvalidDataException("GitHub redirected outside the MajMusic release tags.");
            }

            var tag = page.AbsolutePath[ReleaseTags.AbsolutePath.Length..];
            var number = tag.StartsWith('v') ? tag[1..] : tag;
            if (!Version.TryParse(number, out var version) || version.Build < 0 || version.Revision >= 0)
            {
                throw new InvalidDataException("The latest MajMusic release has no stable three-part version tag.");
            }

            return (version, new Uri(ReleaseTags, Uri.EscapeDataString(tag)));
        }
    }
}

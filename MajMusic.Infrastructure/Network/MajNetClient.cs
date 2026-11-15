#nullable enable

using MajMusic.Core.Models;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MajMusic.Infrastructure.Network
{
    /// <summary>Adapts explicitly configured MajNet music endpoints without starting network activity implicitly.</summary>
    public sealed class MajNetClient : IDisposable
    {
        /// <summary>Describes this configured source without containing credentials.</summary>
        private readonly EndpointConfiguration _endpoint;

        /// <summary>Preserves path prefixes while resolving relative API routes.</summary>
        private readonly Uri _apiRoot;

        /// <summary>Gates every outgoing request.</summary>
        private readonly NetworkAccessPolicy _policy;

        /// <summary>Contains cookies for this client instance only.</summary>
        private readonly CookieContainer _cookies = new();

        /// <summary>Transports API requests without automatic cookie or redirect behavior.</summary>
        private readonly HttpClient _client;

        /// <summary>Tracks whether this instance owns the API transport.</summary>
        private readonly bool _ownsClient;

        /// <summary>Serializes discovery of server identity.</summary>
        private readonly SemaphoreSlim _identityGate = new(1, 1);

        /// <summary>Caches the discovered server identity within this client.</summary>
        private RemoteServer? _server;

        /// <summary>Gets whether this client has completed an explicit login.</summary>
        public bool IsLoggedIn { get; private set; }

        /// <summary>Creates a configured, initially idle endpoint adapter.</summary>
        /// <param name="endpoint">The user-configured API endpoint.</param>
        /// <param name="policy">The shared online/offline gate.</param>
        /// <param name="client">An optional isolated transport for tests, without redirects or default credentials.</param>
        /// <exception cref="ArgumentException">The endpoint address or identity is invalid.</exception>
        public MajNetClient(EndpointConfiguration endpoint, NetworkAccessPolicy policy, HttpClient? client = null)
        {
            ArgumentNullException.ThrowIfNull(endpoint);
            ArgumentNullException.ThrowIfNull(policy);
            ArgumentException.ThrowIfNullOrWhiteSpace(endpoint.Id);
            ValidateAddress(endpoint.BaseUri);
            if (endpoint.BaseUri.Query.Length != 0 || endpoint.BaseUri.Fragment.Length != 0)
            {
                throw new ArgumentException("An API root must not contain a query or fragment.", nameof(endpoint));
            }

            _endpoint = endpoint;
            _apiRoot = new Uri(endpoint.BaseUri.AbsoluteUri.TrimEnd('/') + "/");
            _policy = policy;
            _ownsClient = client is null;
            _client = client ?? new HttpClient(CreateAnonymousTransport());
        }

        /// <summary>Reads server identity, falling back to the configured identity on an older server's 404.</summary>
        /// <param name="cancellationToken">Cancels discovery.</param>
        /// <returns>The stable server identity and display name.</returns>
        /// <exception cref="InvalidOperationException">Online activity is disabled.</exception>
        /// <exception cref="HttpRequestException">The request fails.</exception>
        public async Task<RemoteServer> GetServerAsync(CancellationToken cancellationToken = default)
        {
            if (_server is not null)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return _server;
            }

            await _identityGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (_server is not null)
                {
                    return _server;
                }

                using var response = await SendAsync(HttpMethod.Get, "info", null, cancellationToken).ConfigureAwait(false);
                if (response.StatusCode == HttpStatusCode.NotFound)
                {
                    _server = new RemoteServer { Id = _endpoint.Id, Name = _endpoint.Name };
                }
                else
                {
                    EnsureSuccess(response);
                    using var document = await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
                    var id = GetText(document.RootElement, "id");
                    var name = GetText(document.RootElement, "name");
                    _server = new RemoteServer
                    {
                        Id = string.IsNullOrWhiteSpace(id) ? _endpoint.Id : id,
                        Name = string.IsNullOrWhiteSpace(name) ? _endpoint.Name : name
                    };
                }

                return _server;
            }
            finally
            {
                _identityGate.Release();
            }
        }

        /// <summary>Reads music metadata without downloading audio, covers or per-item information.</summary>
        /// <param name="cancellationToken">Cancels the operation.</param>
        /// <returns>The source's music metadata with lazy resource routes.</returns>
        /// <exception cref="InvalidOperationException">Online activity is disabled.</exception>
        /// <exception cref="HttpRequestException">The source rejects or fails the request.</exception>
        /// <exception cref="JsonException">The source response is invalid.</exception>
        public async Task<IReadOnlyList<MusicTrack>> GetLibraryAsync(CancellationToken cancellationToken = default)
        {
            var server = await GetServerAsync(cancellationToken).ConfigureAwait(false);
            using var response = await SendAsync(HttpMethod.Get, "maichart/list", null, cancellationToken).ConfigureAwait(false);
            EnsureSuccess(response);
            using var document = await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
            return ParseTracks(document.RootElement, server.Id);
        }

        /// <summary>Resolves one music entry's audio and cover file descriptors.</summary>
        /// <param name="remoteId">The source resource identifier.</param>
        /// <param name="cancellationToken">Cancels the request.</param>
        /// <returns>The entry, or null when absent or explicitly missing an audio resource.</returns>
        /// <exception cref="ArgumentException">The identifier is invalid.</exception>
        /// <exception cref="InvalidOperationException">Online activity is disabled.</exception>
        /// <exception cref="HttpRequestException">The request fails.</exception>
        public async Task<MusicTrack?> GetTrackAsync(string remoteId, CancellationToken cancellationToken = default)
        {
            var routeId = EscapeIdentifier(remoteId);
            var server = await GetServerAsync(cancellationToken).ConfigureAwait(false);
            using var response = await SendAsync(HttpMethod.Get, $"maichart/{routeId}/info", null, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return null;
            }

            EnsureSuccess(response);
            using var document = await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
            var root = document.RootElement;
            if (!root.TryGetProperty("metadata", out var metadata) || metadata.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("resources", out var resources) || resources.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var track = ParseTrack(metadata, server.Id);
            var audio = ReadResource(resources, "track", track.Audio.RemoteUri!);
            if (audio is null)
            {
                return null;
            }

            var cover = ReadResource(resources, "thumbnail", new Uri(_apiRoot, $"maichart/{routeId}/image")) ??
                ReadResource(resources, "coverArt", new Uri(_apiRoot, $"maichart/{routeId}/image?fullimage=true"));
            return track with
            {
                Audio = audio with { CacheVersion = track.Revision },
                Cover = cover is null ? null : cover with { CacheVersion = track.Revision },
                Duration = track.Duration ?? ReadDuration(root) ?? ReadDuration(resources.GetProperty("track")),
                AudioInfo = MergeAudioInfo(track.AudioInfo, ReadAudioInfo(resources.GetProperty("track"), audio.Length))
            };
        }

        /// <summary>Performs an explicit login using MajNet's existing multipart password-digest protocol.</summary>
        /// <param name="username">The account name.</param>
        /// <param name="password">The password, used only for this request and never persisted.</param>
        /// <param name="cancellationToken">Cancels login.</param>
        /// <returns>A task representing the completed login.</returns>
        /// <exception cref="ArgumentException">Credentials are empty.</exception>
        /// <exception cref="HttpRequestException">The server refuses login.</exception>
        /// <exception cref="InvalidOperationException">Online activity is disabled.</exception>
        public async Task LoginAsync(string username, string password, CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(username);
            ArgumentException.ThrowIfNullOrWhiteSpace(password);
            var passwordBytes = Encoding.UTF8.GetBytes(password);
            string digest;
            try
            {
                // This is wire-protocol compatibility, not a password-storage mechanism.
                digest = Convert.ToHexString(MD5.HashData(passwordBytes)).ToLowerInvariant();
            }
            finally
            {
                CryptographicOperations.ZeroMemory(passwordBytes);
            }

            using var content = new MultipartFormDataContent
            {
                { new StringContent(username), "username" },
                { new StringContent(digest), "password" }
            };
            using var response = await SendAsync(HttpMethod.Post, "account/login", content, cancellationToken).ConfigureAwait(false);
            var status = await ReadLoginStatusAsync(response, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.Forbidden && status.Code is 0 or 60001)
            {
                IsLoggedIn = true;
                return;
            }

            if (status.IsFailure || status.Code is { } code && code is not (0 or 114514 or 60001))
            {
                ClearSession();
                throw new HttpRequestException("登录失败，请检查账号状态或凭据。", null, response.StatusCode);
            }

            if (!response.IsSuccessStatusCode)
            {
                ClearSession();
            }

            EnsureSuccess(response);
            IsLoggedIn = true;
        }

        /// <summary>Ends the remote session and clears all cookies retained by this endpoint.</summary>
        /// <param name="cancellationToken">Cancels the remote request.</param>
        /// <returns>A task representing the completed logout.</returns>
        /// <exception cref="HttpRequestException">The server rejects logout.</exception>
        /// <exception cref="InvalidOperationException">Online activity is disabled.</exception>
        public async Task LogoutAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                using var response = await SendAsync(HttpMethod.Post, "account/logout", null, cancellationToken).ConfigureAwait(false);
                EnsureSuccess(response);
            }
            finally
            {
                ClearSession();
            }
        }

        /// <summary>Reads one finite recommendation snapshot page for a logged-in user.</summary>
        /// <param name="page">The zero-based page number.</param>
        /// <param name="pageSize">The number of entries requested, between one and one hundred.</param>
        /// <param name="cancellationToken">Cancels the request.</param>
        /// <returns>The entries in the current recommendation page.</returns>
        /// <exception cref="InvalidOperationException">Guest mode or offline mode prevents the request.</exception>
        /// <exception cref="ArgumentOutOfRangeException">Pagination arguments are invalid.</exception>
        /// <exception cref="HttpRequestException">The request fails.</exception>
        public async Task<IReadOnlyList<MusicTrack>> GetRecommendationsAsync(int page, int pageSize = 50, CancellationToken cancellationToken = default)
        {
            RequireLogin();
            ArgumentOutOfRangeException.ThrowIfNegative(page);
            if (pageSize is < 1 or > 100)
            {
                throw new ArgumentOutOfRangeException(nameof(pageSize));
            }

            var server = await GetServerAsync(cancellationToken).ConfigureAwait(false);
            var route = string.Create(CultureInfo.InvariantCulture, $"recommend/feed?source=play-feed&page={page}&pageSize={pageSize}");
            using var response = await SendAsync(HttpMethod.Get, route, null, cancellationToken).ConfigureAwait(false);
            EnsureSuccess(response);
            using var document = await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
            return ParseTracks(document.RootElement, server.Id);
        }

        /// <summary>Reads cloud collections without assuming write or multi-list synchronization support.</summary>
        /// <param name="cancellationToken">Cancels the request.</param>
        /// <returns>The source's read-only collection descriptors.</returns>
        /// <exception cref="InvalidOperationException">Guest mode or offline mode prevents the request.</exception>
        /// <exception cref="HttpRequestException">The request fails.</exception>
        public async Task<IReadOnlyList<RemotePlaylist>> GetCloudPlaylistsAsync(CancellationToken cancellationToken = default)
        {
            RequireLogin();
            using var response = await SendAsync(HttpMethod.Get, "account/favorite/collection/list", null, cancellationToken).ConfigureAwait(false);
            EnsureSuccess(response);
            using var document = await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
            var playlists = new List<RemotePlaylist>();
            var fallbackNames = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var item in document.RootElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                {
                    throw new JsonException("The configured source returned an invalid cloud collection.");
                }

                var id = GetText(item, "id");
                var name = GetText(item, "name");
                if (string.IsNullOrWhiteSpace(id))
                {
                    var occurrence = fallbackNames.GetValueOrDefault(name);
                    fallbackNames[name] = occurrence + 1;
                    id = $"name:{name}:{occurrence.ToString(CultureInfo.InvariantCulture)}";
                }

                playlists.Add(new RemotePlaylist
                {
                    Id = id,
                    Name = string.IsNullOrWhiteSpace(name) ? "未命名收藏夹" : name,
                    Description = GetText(item, "description"),
                    SourceHashes = GetStrings(item, "songHashs"),
                    IsPlayList = item.TryGetProperty("isPlayList", out var flag) && flag.ValueKind is JsonValueKind.True or JsonValueKind.False
                        ? flag.GetBoolean() : null
                });
            }

            return playlists;
        }

        /// <summary>Creates a media transport sharing this in-memory session only with the API origin.</summary>
        /// <param name="transport">An optional anonymous test transport with no automatic cookies or redirects.</param>
        /// <returns>A client owned and disposed by the caller.</returns>
        public HttpClient CreateMediaClient(HttpMessageHandler? transport = null)
        {
            return new HttpClient(new EndpointSessionHandler(_apiRoot, _cookies, _policy, transport ?? CreateAnonymousTransport()));
        }

        /// <summary>Creates an isolated media client around a caller-owned test transport.</summary>
        /// <param name="transport">The caller-owned transport that must not add automatic cookies or redirects.</param>
        /// <returns>A client owned by the caller without ownership of the supplied transport.</returns>
        internal HttpClient CreateMediaClient(HttpClient transport)
        {
            return CreateMediaClient(new BorrowedClientHandler(transport));
        }

        /// <summary>Releases owned transports without logging out or issuing network requests.</summary>
        public void Dispose()
        {
            if (_ownsClient)
            {
                _client.Dispose();
            }

            _identityGate.Dispose();
        }

        /// <summary>Restricts remote connections to HTTPS, with HTTP permitted only for loopback development.</summary>
        /// <param name="address">The address to validate.</param>
        /// <exception cref="ArgumentException">The address is unsafe or contains embedded credentials.</exception>
        internal static void ValidateAddress(Uri address)
        {
            ArgumentNullException.ThrowIfNull(address);
            if (!address.IsAbsoluteUri || address.UserInfo.Length > 0 ||
                (address.Scheme != Uri.UriSchemeHttps && !(address.Scheme == Uri.UriSchemeHttp && address.IsLoopback)))
            {
                throw new ArgumentException("Use an absolute HTTPS address without embedded credentials; HTTP is limited to loopback development.", nameof(address));
            }
        }

        /// <summary>Creates an unauthenticated transport whose cookie and redirect policy is controlled here.</summary>
        /// <returns>The isolated transport.</returns>
        private static HttpMessageHandler CreateAnonymousTransport()
        {
            return new HttpClientHandler { UseCookies = false, AllowAutoRedirect = false };
        }

        /// <summary>Sends one API request within the current online generation.</summary>
        /// <param name="method">The HTTP method.</param>
        /// <param name="route">The relative API route.</param>
        /// <param name="content">The optional body.</param>
        /// <param name="cancellationToken">Cancels the request.</param>
        /// <returns>The response whose content has been buffered within the online lifetime.</returns>
        /// <exception cref="InvalidOperationException">Online activity is disabled.</exception>
        /// <exception cref="HttpRequestException">The request fails.</exception>
        private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string route, HttpContent? content, CancellationToken cancellationToken)
        {
            using var lifetime = _policy.CreateRequestCancellation(cancellationToken);
            var address = new Uri(_apiRoot, route);
            using var request = new HttpRequestMessage(method, address) { Content = content };
            var cookie = _cookies.GetCookieHeader(address);
            if (cookie.Length > 0)
            {
                request.Headers.TryAddWithoutValidation("Cookie", cookie);
            }

            var response = await _client.SendAsync(request, HttpCompletionOption.ResponseContentRead, lifetime.Token).ConfigureAwait(false);
            if (response.Headers.TryGetValues("Set-Cookie", out var cookies))
            {
                foreach (var value in cookies)
                {
                    try
                    {
                        _cookies.SetCookies(address, value);
                    }
                    catch (CookieException)
                    {
                        // Ignore malformed server cookies rather than broadening the credential scope.
                    }
                }
            }

            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                ClearSession();
            }

            return response;
        }

        /// <summary>Rejects failed responses without including server bodies, credentials or query strings.</summary>
        /// <param name="response">The response to validate.</param>
        /// <exception cref="HttpRequestException">The response is not successful.</exception>
        private static void EnsureSuccess(HttpResponseMessage response)
        {
            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException($"The configured source returned HTTP {(int)response.StatusCode}.", null, response.StatusCode);
            }
        }

        /// <summary>Parses a buffered API JSON response.</summary>
        /// <param name="response">The buffered response.</param>
        /// <param name="cancellationToken">Cancels parsing.</param>
        /// <returns>The JSON document owned by the caller.</returns>
        /// <exception cref="JsonException">The response contains invalid JSON.</exception>
        private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response, CancellationToken cancellationToken)
        {
            using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        /// <summary>Maps a metadata array without requesting individual files.</summary>
        /// <param name="items">The array of remote metadata objects.</param>
        /// <param name="serverId">The stable source identity.</param>
        /// <returns>The mapped music entries.</returns>
        private IReadOnlyList<MusicTrack> ParseTracks(JsonElement items, string serverId)
        {
            if (items.ValueKind != JsonValueKind.Array)
            {
                throw new JsonException("The configured source did not return a metadata array.");
            }

            var tracks = new List<MusicTrack>();
            foreach (var item in items.EnumerateArray())
            {
                if (!string.IsNullOrWhiteSpace(GetText(item, "id")))
                {
                    tracks.Add(ParseTrack(item, serverId));
                }
            }

            return tracks;
        }

        /// <summary>Maps source metadata to a music entry and lazy audio/cover routes.</summary>
        /// <param name="metadata">The metadata object.</param>
        /// <param name="serverId">The stable source identity.</param>
        /// <returns>The library entry without gameplay or video fields.</returns>
        private MusicTrack ParseTrack(JsonElement metadata, string serverId)
        {
            var id = GetText(metadata, "id");
            var routeId = EscapeIdentifier(id);
            return new MusicTrack
            {
                Id = $"majnet:{Uri.EscapeDataString(serverId)}:{Uri.EscapeDataString(_endpoint.Id)}:{routeId}",
                Title = GetText(metadata, "title"),
                Artist = GetText(metadata, "artist"),
                Description = GetText(metadata, "description"),
                Tags = GetStrings(metadata, "tags").Concat(GetStrings(metadata, "publicTags")).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                SourceId = serverId,
                EndpointId = _endpoint.Id,
                SourceName = _endpoint.Name,
                RemoteId = id,
                Revision = GetText(metadata, "hash"),
                Duration = ReadDuration(metadata),
                AudioInfo = metadata.TryGetProperty("audioInfo", out var info) ? ReadAudioInfo(info, null) : ReadAudioInfo(metadata, null),
                Audio = new MediaResource { RemoteUri = new Uri(_apiRoot, $"maichart/{routeId}/track"), CacheVersion = GetText(metadata, "hash") },
                Cover = new MediaResource { RemoteUri = new Uri(_apiRoot, $"maichart/{routeId}/image"), CacheVersion = GetText(metadata, "hash") }
            };
        }

        /// <summary>Reads optional duration extensions without mistaking chart length or file size for audio time.</summary>
        /// <param name="element">The metadata or audio descriptor.</param>
        /// <returns>A positive audio duration when supplied by the endpoint.</returns>
        private static TimeSpan? ReadDuration(JsonElement element)
        {
            if (element.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var seconds = ReadPositiveNumber(element, "durationSeconds");
            var milliseconds = ReadPositiveNumber(element, "durationMs") ?? ReadPositiveNumber(element, "durationMilliseconds");
            if (seconds is null && milliseconds is not null)
            {
                seconds = milliseconds / 1000;
            }

            if (seconds is null && element.TryGetProperty("duration", out var duration))
            {
                if (duration.ValueKind == JsonValueKind.Number && duration.TryGetDouble(out var number))
                {
                    seconds = number;
                }
                else if (duration.ValueKind == JsonValueKind.String
                    && TimeSpan.TryParse(duration.GetString(), CultureInfo.InvariantCulture, out var parsed) && parsed > TimeSpan.Zero)
                {
                    return parsed;
                }
            }

            return seconds is > 0 and < 922337203685 ? TimeSpan.FromSeconds(seconds.Value) : null;
        }

        /// <summary>Reads optional stream-property extensions with explicit units.</summary>
        /// <param name="element">The source object.</param>
        /// <param name="fileSize">The known complete audio file size.</param>
        /// <returns>The supplied audio properties, or null when none are present.</returns>
        private static AudioStreamInfo? ReadAudioInfo(JsonElement element, long? fileSize)
        {
            if (element.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var bitrate = ReadPositiveNumber(element, "bitrateBitsPerSecond") ?? ReadPositiveNumber(element, "bitrate");
            var rate = ReadPositiveNumber(element, "sampleRateHz") ?? ReadPositiveNumber(element, "sampleRate");
            var channels = ReadPositiveNumber(element, "channels");
            var codec = GetText(element, "codecName");
            if (codec.Length == 0)
            {
                codec = GetText(element, "codec");
            }

            if (bitrate is null && rate is null && channels is null && codec.Length == 0 && fileSize is null)
            {
                return null;
            }

            return new AudioStreamInfo
            {
                CodecName = codec.Length == 0 ? null : codec,
                BitrateBitsPerSecond = bitrate is > 0 and < long.MaxValue ? (long)bitrate : null,
                SampleRateHz = rate is > 0 and <= int.MaxValue ? (int)rate : null,
                Channels = channels is > 0 and <= int.MaxValue ? (int)channels : null,
                FileSizeBytes = fileSize
            };
        }

        /// <summary>Combines metadata properties with a more specific file descriptor.</summary>
        /// <param name="metadata">The metadata's stream properties.</param>
        /// <param name="resource">The resource descriptor's stream properties.</param>
        /// <returns>The combined known values.</returns>
        private static AudioStreamInfo? MergeAudioInfo(AudioStreamInfo? metadata, AudioStreamInfo? resource)
        {
            return resource is null ? metadata : resource with
            {
                CodecName = resource.CodecName ?? metadata?.CodecName,
                BitrateBitsPerSecond = resource.BitrateBitsPerSecond ?? metadata?.BitrateBitsPerSecond,
                SampleRateHz = resource.SampleRateHz ?? metadata?.SampleRateHz,
                Channels = resource.Channels ?? metadata?.Channels
            };
        }

        /// <summary>Reads one finite positive JSON number.</summary>
        /// <param name="element">The containing object.</param>
        /// <param name="name">The property name.</param>
        /// <returns>The value when present and positive.</returns>
        private static double? ReadPositiveNumber(JsonElement element, string name)
        {
            return element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
                && value.TryGetDouble(out var number) && double.IsFinite(number) && number > 0 ? number : null;
        }

        /// <summary>Reads a login status body when the server provides JSON, without retaining its text.</summary>
        /// <param name="response">The buffered login response.</param>
        /// <param name="cancellationToken">Cancels reading.</param>
        /// <returns>The optional server code and explicit rejection flag.</returns>
        private static async Task<(int? Code, bool IsFailure)> ReadLoginStatusAsync(HttpResponseMessage response, CancellationToken cancellationToken)
        {
            var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            if (bytes.Length == 0)
            {
                return (null, false);
            }

            try
            {
                using var document = JsonDocument.Parse(bytes);
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                {
                    return (null, false);
                }

                int? code = root.TryGetProperty("code", out var value) && value.ValueKind == JsonValueKind.Number
                    && value.TryGetInt32(out var parsed) ? parsed : null;
                var rejected = new[] { "success", "isSuccess", "isSuccessfully" }
                    .Any(name => root.TryGetProperty(name, out var flag) && flag.ValueKind == JsonValueKind.False);
                return (code, rejected);
            }
            catch (JsonException)
            {
                return (null, false);
            }
        }

        /// <summary>Clears cookies and account state without making a request.</summary>
        private void ClearSession()
        {
            IsLoggedIn = false;
            foreach (Cookie cookie in _cookies.GetAllCookies())
            {
                cookie.Expired = true;
            }
        }

        /// <summary>Reads an optional file descriptor, validating any supplied download address.</summary>
        /// <param name="resources">The resource object.</param>
        /// <param name="name">The audio or cover property.</param>
        /// <param name="fallback">The route used when the descriptor omits its URL.</param>
        /// <returns>The descriptor, or null when the resource is absent.</returns>
        private MediaResource? ReadResource(JsonElement resources, string name, Uri fallback)
        {
            if (!resources.TryGetProperty(name, out var resource) || resource.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var url = GetText(resource, "url");
            var address = string.IsNullOrWhiteSpace(url) ? fallback : new Uri(_apiRoot, url);
            ValidateAddress(address);
            var sha256 = GetText(resource, "sha256");
            return new MediaResource
            {
                RemoteUri = address,
                Sha256 = string.IsNullOrWhiteSpace(sha256) ? null : sha256.ToLowerInvariant(),
                Length = resource.TryGetProperty("length", out var length) && length.ValueKind == JsonValueKind.Number && length.TryGetInt64(out var bytes) && bytes >= 0 ? bytes : null
            };
        }

        /// <summary>Reads a string property without coercing unexpected JSON types.</summary>
        /// <param name="element">The containing object.</param>
        /// <param name="name">The property name.</param>
        /// <returns>The string or an empty default.</returns>
        private static string GetText(JsonElement element, string name)
        {
            return element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : string.Empty;
        }

        /// <summary>Reads a string array while ignoring null and malformed items.</summary>
        /// <param name="element">The containing object.</param>
        /// <param name="name">The property name.</param>
        /// <returns>The nonempty string values.</returns>
        private static string[] GetStrings(JsonElement element, string name)
        {
            if (!element.TryGetProperty(name, out var array) || array.ValueKind != JsonValueKind.Array)
            {
                return Array.Empty<string>();
            }

            return array.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String)
                .Select(item => item.GetString() ?? string.Empty).Where(item => item.Length > 0).ToArray();
        }

        /// <summary>Escapes a nonempty source identity as one path segment.</summary>
        /// <param name="id">The remote identity.</param>
        /// <returns>The escaped path segment.</returns>
        /// <exception cref="ArgumentException">The identity is empty or a relative-path marker.</exception>
        private static string EscapeIdentifier(string id)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(id);
            if (id is "." or "..")
            {
                throw new ArgumentException("A remote identity must identify one resource.", nameof(id));
            }

            return Uri.EscapeDataString(id);
        }

        /// <summary>Stops account-only actions before any request is constructed in Guest mode.</summary>
        /// <exception cref="InvalidOperationException">The current client is a Guest.</exception>
        private void RequireLogin()
        {
            if (!IsLoggedIn)
            {
                throw new InvalidOperationException("This action requires an explicit account login.");
            }
        }
    }
}

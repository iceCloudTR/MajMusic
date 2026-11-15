#nullable enable

using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;

namespace MajMusic.Infrastructure.Network
{
    /// <summary>Implements authenticated collection operations using the chart-ID protocol introduced in November 2026.</summary>
    public sealed partial class MajNetClient
    {
        /// <summary>Reads the canonical account identity of the current session without assuming the login input was a username.</summary>
        /// <param name="cancellationToken">Cancels the profile request.</param>
        /// <returns>The canonical username associated with the current session cookie.</returns>
        /// <exception cref="InvalidOperationException">The client is a Guest or online requests are disabled.</exception>
        /// <exception cref="HttpRequestException">The profile request fails.</exception>
        /// <exception cref="JsonException">The profile does not identify a canonical username.</exception>
        public async Task<string> GetCurrentUsernameAsync(CancellationToken cancellationToken = default)
        {
            RequireCollectionLogin(cancellationToken);
            using var response = await SendAsync(HttpMethod.Get, "account/profile", null, cancellationToken).ConfigureAwait(false);
            EnsureSuccess(response);
            using var document = await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
            return RequiredCollectionText(document.RootElement, "username");
        }

        /// <summary>Reads one page of collections created by a canonical account identity.</summary>
        /// <param name="username">The canonical creator username returned by the profile API.</param>
        /// <param name="page">The zero-based collection page.</param>
        /// <param name="pageSize">The requested page size between one and one hundred.</param>
        /// <param name="cancellationToken">Cancels the request.</param>
        /// <returns>The collection metadata in server order.</returns>
        /// <exception cref="ArgumentException">The creator identity is empty.</exception>
        /// <exception cref="ArgumentOutOfRangeException">The page or page size is invalid.</exception>
        /// <exception cref="InvalidOperationException">Guest or offline mode prevents the read.</exception>
        /// <exception cref="HttpRequestException">The server rejects the request.</exception>
        /// <exception cref="JsonException">The response is not a valid collection array.</exception>
        public async Task<IReadOnlyList<RemoteCollection>> GetCollectionPageAsync(string username, int page = 0, int pageSize = 100,
            CancellationToken cancellationToken = default)
        {
            RequireCollectionLogin(cancellationToken);
            ArgumentException.ThrowIfNullOrWhiteSpace(username);
            ArgumentOutOfRangeException.ThrowIfNegative(page);
            if (pageSize is < 1 or > 100)
            {
                throw new ArgumentOutOfRangeException(nameof(pageSize));
            }

            var route = string.Create(CultureInfo.InvariantCulture,
                $"collection/list?page={page}&pageSize={pageSize}&createdBy={Uri.EscapeDataString(username)}");
            using var response = await SendAsync(HttpMethod.Get, route, null, cancellationToken).ConfigureAwait(false);
            EnsureSuccess(response);
            using var document = await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                throw new JsonException("The configured source did not return a collection array.");
            }

            return document.RootElement.EnumerateArray().Select(item => ParseCollection(item, false)).ToArray();
        }

        /// <summary>Reads one collection's creator, description, visibility and complete ordered chart-ID list.</summary>
        /// <param name="id">The server's opaque collection identity.</param>
        /// <param name="cancellationToken">Cancels the request.</param>
        /// <returns>The collection details, or null when the server returns not found.</returns>
        /// <exception cref="ArgumentException">The collection identity is invalid.</exception>
        /// <exception cref="InvalidOperationException">Guest or offline mode prevents the read.</exception>
        /// <exception cref="HttpRequestException">The server rejects the request.</exception>
        /// <exception cref="JsonException">The response does not contain valid collection details.</exception>
        public async Task<RemoteCollection?> GetCollectionSongListAsync(string id, CancellationToken cancellationToken = default)
        {
            RequireCollectionLogin(cancellationToken);
            var routeId = EscapeIdentifier(id);
            using var response = await SendAsync(HttpMethod.Get, $"collection/{routeId}/songlist", null, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return null;
            }

            EnsureSuccess(response);
            using var document = await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
            return ParseCollection(document.RootElement, true);
        }

        /// <summary>Creates a collection without assuming an undocumented response-body identity format.</summary>
        /// <param name="name">The nonempty collection name.</param>
        /// <param name="description">The description, including any caller-owned synchronization marker.</param>
        /// <param name="visibility">Zero for private or one for public.</param>
        /// <param name="cancellationToken">Cancels the write.</param>
        /// <returns>A task representing an accepted creation; callers discover its identity through their owned collection pages.</returns>
        /// <exception cref="ArgumentException">The name is empty.</exception>
        /// <exception cref="ArgumentNullException">The description is null.</exception>
        /// <exception cref="ArgumentOutOfRangeException">Visibility is neither private nor public.</exception>
        /// <exception cref="InvalidOperationException">Guest or offline mode prevents the write.</exception>
        /// <exception cref="HttpRequestException">The server rejects creation.</exception>
        public async Task CreateCollectionAsync(string name, string description, int visibility = 0, CancellationToken cancellationToken = default)
        {
            RequireCollectionLogin(cancellationToken);
            ArgumentException.ThrowIfNullOrWhiteSpace(name);
            ArgumentNullException.ThrowIfNull(description);
            ValidateCollectionVisibility(visibility);
            using var content = CollectionJson(new { name = name.Trim(), description, visibility });
            using var response = await SendAsync(HttpMethod.Post, "collection/create", content, cancellationToken).ConfigureAwait(false);
            EnsureSuccess(response);
        }

        /// <summary>Replaces an owned collection's complete ordered chart-ID list and supplied metadata using PUT modify.</summary>
        /// <param name="id">The server's opaque collection identity.</param>
        /// <param name="name">The nonempty collection name.</param>
        /// <param name="description">The replacement description, or null to leave it unchanged.</param>
        /// <param name="chartIds">The complete ordered chart IDs; an empty list clears the collection.</param>
        /// <param name="cancellationToken">Cancels the write.</param>
        /// <returns>A task representing an accepted replacement.</returns>
        /// <exception cref="ArgumentException">The collection identity, name or a chart identity is empty.</exception>
        /// <exception cref="ArgumentNullException">The chart list is null.</exception>
        /// <exception cref="InvalidOperationException">Guest or offline mode prevents the write.</exception>
        /// <exception cref="HttpRequestException">The server rejects modification.</exception>
        public async Task UpdateCollectionAsync(string id, string name, string? description, IReadOnlyList<string> chartIds,
            CancellationToken cancellationToken = default)
        {
            RequireCollectionLogin(cancellationToken);
            var routeId = EscapeIdentifier(id);
            ArgumentException.ThrowIfNullOrWhiteSpace(name);
            ArgumentNullException.ThrowIfNull(chartIds);
            var items = chartIds.ToArray();
            if (items.Any(string.IsNullOrWhiteSpace))
            {
                throw new ArgumentException("A collection item must identify a chart.", nameof(chartIds));
            }

            var body = new Dictionary<string, object> { ["name"] = name.Trim(), ["items"] = items };
            if (description is not null)
            {
                body["description"] = description;
            }

            using var content = CollectionJson(body);
            using var response = await SendAsync(HttpMethod.Put, $"collection/{routeId}/modify", content, cancellationToken).ConfigureAwait(false);
            EnsureSuccess(response);
        }

        /// <summary>Subscribes or unsubscribes a collection so the account's favorite collection list reflects that choice.</summary>
        /// <param name="id">The collection identity, rather than any individual chart identity.</param>
        /// <param name="favorite">Whether to add or remove the collection subscription.</param>
        /// <param name="cancellationToken">Cancels the write.</param>
        /// <returns>A task representing the accepted subscription change.</returns>
        /// <exception cref="ArgumentException">The collection identity is empty.</exception>
        /// <exception cref="InvalidOperationException">Guest or offline mode prevents the write.</exception>
        /// <exception cref="HttpRequestException">The server rejects the subscription change.</exception>
        public async Task SetCollectionFavoriteAsync(string id, bool favorite, CancellationToken cancellationToken = default)
        {
            RequireCollectionLogin(cancellationToken);
            ArgumentException.ThrowIfNullOrWhiteSpace(id);
            using var content = CollectionJson(new[] { id });
            using var response = await SendAsync(favorite ? HttpMethod.Post : HttpMethod.Delete,
                "account/favorite/collection/diff", content, cancellationToken).ConfigureAwait(false);
            EnsureSuccess(response);
        }

        /// <summary>Deletes one caller-verified owned collection through the server's destroy route.</summary>
        /// <param name="id">The collection identity whose ownership the caller already verified.</param>
        /// <param name="cancellationToken">Cancels the write.</param>
        /// <returns>A task representing accepted deletion.</returns>
        /// <exception cref="ArgumentException">The collection identity is invalid.</exception>
        /// <exception cref="InvalidOperationException">Guest or offline mode prevents the write.</exception>
        /// <exception cref="HttpRequestException">The server rejects deletion.</exception>
        public async Task DeleteCollectionAsync(string id, CancellationToken cancellationToken = default)
        {
            RequireCollectionLogin(cancellationToken);
            var routeId = EscapeIdentifier(id);
            using var response = await SendAsync(HttpMethod.Post, $"collection/{routeId}/destroy", null, cancellationToken).ConfigureAwait(false);
            EnsureSuccess(response);
        }

        /// <summary>Rejects account-only work before creating an HTTP request.</summary>
        /// <param name="cancellationToken">The caller's operation lifetime.</param>
        /// <exception cref="InvalidOperationException">The session is a Guest.</exception>
        /// <exception cref="OperationCanceledException">The operation was already canceled.</exception>
        private void RequireCollectionLogin(CancellationToken cancellationToken)
        {
            RequireLogin();
            cancellationToken.ThrowIfCancellationRequested();
        }

        /// <summary>Creates the JSON content used by authenticated collection mutations.</summary>
        /// <param name="body">The exact documented request shape.</param>
        /// <returns>UTF-8 JSON content owned by the requesting method.</returns>
        private static StringContent CollectionJson(object body)
        {
            return new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        }

        /// <summary>Rejects unknown collection visibility values before writing.</summary>
        /// <param name="visibility">Zero for private or one for public.</param>
        /// <exception cref="ArgumentOutOfRangeException">The value is not defined by the collection protocol.</exception>
        private static void ValidateCollectionVisibility(int visibility)
        {
            if (visibility is not (0 or 1))
            {
                throw new ArgumentOutOfRangeException(nameof(visibility));
            }
        }

        /// <summary>Reads typed collection metadata without substituting synthetic identities or chart hashes.</summary>
        /// <param name="item">The server's collection object.</param>
        /// <param name="requireItems">Whether this is a songlist response requiring ordered chart IDs.</param>
        /// <returns>The parsed collection descriptor.</returns>
        /// <exception cref="JsonException">A required identity or chart-ID list is malformed.</exception>
        private static RemoteCollection ParseCollection(JsonElement item, bool requireItems)
        {
            var id = RequiredCollectionText(item, "id");
            var name = RequiredCollectionText(item, "name");
            var creator = RequiredCollectionText(item, "createdBy");
            var items = Array.Empty<string>();
            if (item.TryGetProperty("items", out var values))
            {
                if (values.ValueKind != JsonValueKind.Array || values.EnumerateArray().Any(value =>
                    value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString())))
                {
                    throw new JsonException("The collection did not contain a valid chart-ID array.");
                }

                items = values.EnumerateArray().Select(value => value.GetString()!).ToArray();
            }
            else if (requireItems)
            {
                throw new JsonException("The collection songlist did not contain chart IDs.");
            }

            int? visibility = null;
            if (item.TryGetProperty("visibility", out var visibilityValue))
            {
                if (visibilityValue.ValueKind != JsonValueKind.Number || !visibilityValue.TryGetInt32(out var parsed) || parsed is not (0 or 1))
                {
                    throw new JsonException("The collection did not contain a valid visibility value.");
                }

                visibility = parsed;
            }

            var count = items.Length;
            if (item.TryGetProperty("count", out var countValue)
                && (countValue.ValueKind != JsonValueKind.Number || !countValue.TryGetInt32(out count) || count < 0))
            {
                throw new JsonException("The collection did not contain a valid item count.");
            }

            string? description = null;
            if (item.TryGetProperty("description", out var text) && text.ValueKind != JsonValueKind.Null)
            {
                if (text.ValueKind != JsonValueKind.String)
                {
                    throw new JsonException("The collection did not contain a valid description.");
                }

                description = text.GetString();
            }

            return new RemoteCollection { Id = id, Name = name, CreatedBy = creator, Description = description, Count = count, Visibility = visibility, Items = items };
        }

        /// <summary>Reads a required server identity without coercing another JSON value kind.</summary>
        /// <param name="item">The containing profile or collection object.</param>
        /// <param name="name">The required property name.</param>
        /// <returns>The nonempty server-provided text.</returns>
        /// <exception cref="JsonException">The object or required property is malformed.</exception>
        private static string RequiredCollectionText(JsonElement item, string name)
        {
            if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty(name, out var value)
                || value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
            {
                throw new JsonException("The configured source did not identify the profile or collection.");
            }

            return value.GetString()!;
        }
    }
}

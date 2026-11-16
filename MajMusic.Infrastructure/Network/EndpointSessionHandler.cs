#nullable enable

using System.Net;
using MajMusic.Core.Network;

namespace MajMusic.Infrastructure.Network
{
    /// <summary>Downloads media while restricting session cookies to the configured API origin.</summary>
    internal sealed class EndpointSessionHandler : DelegatingHandler
    {
        /// <summary>Identifies the only origin eligible to receive this session.</summary>
        private readonly Uri _apiRoot;

        /// <summary>Stores this endpoint's in-memory cookies.</summary>
        private readonly CookieContainer _cookies;

        /// <summary>Applies the user's online choice to every redirect hop.</summary>
        private readonly NetworkAccessPolicy _policy;
        /// <summary>Identifies only production-owned media requests without rewriting a supplied test transport's headers.</summary>
        private readonly string? _userAgent;

        /// <summary>Constructs a redirect-aware isolated media transport.</summary>
        /// <param name="apiRoot">The configured API root.</param>
        /// <param name="cookies">The endpoint's isolated cookie container.</param>
        /// <param name="policy">The shared online gate.</param>
        /// <param name="innerHandler">An anonymous transport without automatic redirects or cookies.</param>
        /// <param name="userAgent">The application identity for owned transports, or null for a caller-supplied transport.</param>
        public EndpointSessionHandler(Uri apiRoot, CookieContainer cookies, NetworkAccessPolicy policy, HttpMessageHandler innerHandler, string? userAgent = null)
            : base(innerHandler)
        {
            _apiRoot = apiRoot;
            _cookies = cookies;
            _policy = policy;
            _userAgent = userAgent;
        }

        /// <summary>Sends a clean media GET and follows validated redirects without forwarding credentials.</summary>
        /// <param name="request">The requested media resource.</param>
        /// <param name="cancellationToken">Cancels the transfer.</param>
        /// <returns>The final media response.</returns>
        /// <exception cref="HttpRequestException">A redirect is invalid or exceeds the supported limit.</exception>
        /// <exception cref="InvalidOperationException">Online activity is disabled.</exception>
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var address = request.RequestUri ?? throw new ArgumentException("A media address is required.", nameof(request));
            using var lifetime = _policy.CreateRequestCancellation(cancellationToken);
            for (var redirect = 0; redirect <= 5; redirect++)
            {
                MajNetClient.ValidateAddress(address);
                using var cleanRequest = new HttpRequestMessage(HttpMethod.Get, address);
                if (_userAgent is not null)
                {
                    cleanRequest.Headers.UserAgent.ParseAdd(_userAgent);
                }

                if (IsSameOrigin(address, _apiRoot))
                {
                    var cookie = _cookies.GetCookieHeader(address);
                    if (cookie.Length > 0)
                    {
                        cleanRequest.Headers.TryAddWithoutValidation("Cookie", cookie);
                    }
                }

                var response = await base.SendAsync(cleanRequest, lifetime.Token).ConfigureAwait(false);
                try
                {
                    SourceResponseGuard.ThrowIfBrowserVerificationRequired(response);
                }
                catch (BrowserVerificationRequiredException)
                {
                    response.Dispose();
                    throw;
                }

                if (response.StatusCode is not (HttpStatusCode.MovedPermanently or HttpStatusCode.Found or HttpStatusCode.SeeOther or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect))
                {
                    return response;
                }

                var location = response.Headers.Location;
                response.Dispose();
                if (location is null)
                {
                    throw new HttpRequestException("The media redirect has no destination.");
                }

                address = location.IsAbsoluteUri ? location : new Uri(address, location);
            }

            throw new HttpRequestException("The media redirect limit was exceeded.");
        }

        /// <summary>Compares origins without treating a shared hostname as a shared credential scope.</summary>
        /// <param name="first">The first address.</param>
        /// <param name="second">The second address.</param>
        /// <returns>Whether scheme, hostname and effective port match.</returns>
        internal static bool IsSameOrigin(Uri first, Uri second)
        {
            return first.Scheme == second.Scheme && first.IdnHost == second.IdnHost && first.Port == second.Port;
        }
    }
}

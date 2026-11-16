#nullable enable

using System.Net;

namespace MajMusic.Infrastructure.Network
{
    /// <summary>Creates native source transports while leaving account cookies and redirects under the application's explicit policy.</summary>
    public static class SourceHttpTransportFactory
    {
        /// <summary>Provides the configured MajdataPlay-compatible identity for owned API and media requests.</summary>
        public const string UserAgent = "MajdataPlay/2.1.0";

        /// <summary>Creates an anonymous native HTTP handler without automatic account cookies, redirects or default credentials.</summary>
        /// <returns>A caller-owned WinHTTP handler on Windows, otherwise the platform's managed HTTP handler.</returns>
        public static HttpMessageHandler CreateAnonymousHandler()
        {
            if (OperatingSystem.IsWindows())
            {
                return new WinHttpHandler
                {
                    CookieUsePolicy = CookieUsePolicy.IgnoreCookies,
                    AutomaticRedirection = false,
                    ServerCredentials = null,
                    DefaultProxyCredentials = null,
                    WindowsProxyUsePolicy = WindowsProxyUsePolicy.UseCustomProxy,
                    Proxy = HttpClient.DefaultProxy
                };
            }

            return new HttpClientHandler { UseCookies = false, AllowAutoRedirect = false, UseDefaultCredentials = false };
        }

        /// <summary>Creates an owned source client with the same explicit cookie and redirect boundaries and application user agent.</summary>
        /// <returns>The caller-owned anonymous client.</returns>
        public static HttpClient CreateAnonymousClient()
        {
            var client = new HttpClient(CreateAnonymousHandler());
            client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
            return client;
        }
    }
}

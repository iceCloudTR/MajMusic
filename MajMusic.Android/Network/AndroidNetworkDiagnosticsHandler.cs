#nullable enable

using System.Diagnostics;
using global::Android.Util;

namespace MajMusic.Android.Network
{
    /// <summary>Records bounded native transport results without logging request data, account identities or session values.</summary>
    internal sealed class AndroidNetworkDiagnosticsHandler : DelegatingHandler
    {
        /// <summary>Wraps the native transport without changing its request or cookie policy.</summary>
        /// <param name="transport">The exclusively owned native message handler.</param>
        public AndroidNetworkDiagnosticsHandler(HttpMessageHandler transport) : base(transport)
        {
        }

        /// <summary>Logs only the operation category, status and presence of session headers.</summary>
        /// <param name="request">The original request, whose content and header values are never logged.</param>
        /// <param name="cancellationToken">Cancels the native request.</param>
        /// <returns>The unchanged native response.</returns>
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var operation = request.RequestUri?.AbsolutePath.EndsWith("/account/login", StringComparison.OrdinalIgnoreCase) == true
                ? "Login" : request.RequestUri?.AbsolutePath.Contains("/account/", StringComparison.OrdinalIgnoreCase) == true
                    ? "Account" : "Media";
            var started = Stopwatch.GetTimestamp();
            try
            {
                var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
                var outcome = $"{operation}: status={(int)response.StatusCode}, sentSession={request.Headers.Contains("Cookie")}, receivedSession={response.Headers.Contains("Set-Cookie")}, elapsedMs={(long)Stopwatch.GetElapsedTime(started).TotalMilliseconds}";
                if (operation == "Media")
                {
                    Log.Debug("MajMusic.Network", outcome);
                }
                else
                {
                    Log.Info("MajMusic.Network", outcome);
                }
                return response;
            }
            catch (Exception exception)
            {
                Log.Warn("MajMusic.Network", $"{operation}: failed={exception.GetType().Name}");
                throw;
            }
        }
    }
}

#nullable enable

using MajMusic.Core.Network;

namespace MajMusic.Infrastructure.Network
{
    /// <summary>Recognizes explicit middleware challenges without treating ordinary HTTP permission errors as expired credentials.</summary>
    internal static class SourceResponseGuard
    {
        /// <summary>Rejects a Cloudflare challenge before an API parser or media decoder consumes its HTML response.</summary>
        /// <param name="response">The source response whose headers identify the expected resource or challenge.</param>
        /// <exception cref="BrowserVerificationRequiredException">The response explicitly declares a browser challenge.</exception>
        public static void ThrowIfBrowserVerificationRequired(HttpResponseMessage response)
        {
            if (response.Headers.TryGetValues("cf-mitigated", out var values)
                && values.Any(value => string.Equals(value.Trim(), "challenge", StringComparison.OrdinalIgnoreCase)))
            {
                throw new BrowserVerificationRequiredException(response.StatusCode);
            }
        }
    }
}

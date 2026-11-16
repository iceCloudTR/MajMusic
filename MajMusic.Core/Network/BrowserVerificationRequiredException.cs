#nullable enable

using System.Net;

namespace MajMusic.Core.Network
{
    /// <summary>Identifies a source middleware challenge independently of account credentials or permission.</summary>
    public sealed class BrowserVerificationRequiredException : HttpRequestException
    {
        /// <summary>Creates a safe challenge failure without retaining response bodies or authorization values.</summary>
        /// <param name="statusCode">The actual HTTP status returned by the challenged source.</param>
        public BrowserVerificationRequiredException(HttpStatusCode statusCode)
            : base("The configured source requires browser verification.", null, statusCode)
        {
        }
    }
}

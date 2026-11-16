#nullable enable

using System.Net;

namespace MajMusic.Core.Network
{
    /// <summary>Identifies an explicit login whose account name or password was rejected by the source.</summary>
    public sealed class LoginRejectedException : HttpRequestException
    {
        /// <summary>Creates a safe login failure without retaining credentials or the source response body.</summary>
        public LoginRejectedException()
            : base("The configured source rejected the account name or password.", null, HttpStatusCode.Unauthorized)
        {
        }
    }
}

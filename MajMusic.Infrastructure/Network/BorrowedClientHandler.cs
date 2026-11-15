#nullable enable

namespace MajMusic.Infrastructure.Network
{
    /// <summary>Forwards isolated test requests without taking ownership of their caller-supplied client.</summary>
    internal sealed class BorrowedClientHandler : HttpMessageHandler
    {
        /// <summary>Stores the caller-owned transport.</summary>
        private readonly HttpClient _client;

        /// <summary>Creates a forwarding handler.</summary>
        /// <param name="client">The caller-owned client.</param>
        public BorrowedClientHandler(HttpClient client)
        {
            _client = client;
        }

        /// <summary>Forwards one already sanitized request.</summary>
        /// <param name="request">The request supplied by the endpoint handler.</param>
        /// <param name="cancellationToken">Cancels the transport.</param>
        /// <returns>The response owned by the caller.</returns>
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        }
    }
}

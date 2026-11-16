#nullable enable

using System.Net;
using System.Net.Sockets;
using System.Text;
using MajMusic.Core.Models;
using MajMusic.Infrastructure.Network;

namespace MajMusic.Tests
{
    /// <summary>Exercises the production native source handler's policy and real loopback cookie, redirect and cancellation boundaries.</summary>
    internal static class SourceTransportTests
    {
        /// <summary>Checks the selected platform handler and its real HTTP behavior without any external source or account.</summary>
        /// <returns>A task representing completed transport policy and loopback regressions.</returns>
        public static async Task RunAsync()
        {
            ValidateFactoryPolicy();
            await ValidateLoopbackAsync();
        }

        /// <summary>Checks that native Windows transport changes neither certificate validation nor account isolation policy.</summary>
        private static void ValidateFactoryPolicy()
        {
            using var handler = SourceHttpTransportFactory.CreateAnonymousHandler();
            if (OperatingSystem.IsWindows())
            {
                TestAssert.True(handler is WinHttpHandler, "Windows source requests must use the verified native WinHTTP transport.");
                var windows = (WinHttpHandler)handler;
                TestAssert.True(windows.CookieUsePolicy == CookieUsePolicy.IgnoreCookies && !windows.AutomaticRedirection
                    && windows.ServerCredentials is null && windows.DefaultProxyCredentials is null
                    && windows.ServerCertificateValidationCallback is null
                    && windows.WindowsProxyUsePolicy == WindowsProxyUsePolicy.UseCustomProxy
                    && ReferenceEquals(windows.Proxy, HttpClient.DefaultProxy),
                    "The native factory must preserve explicit cookie/redirect handling, normal certificate checks and the existing default proxy selection.");
            }
            else
            {
                TestAssert.True(handler is HttpClientHandler, "Other platforms retain their existing platform HTTP handler.");
                var managed = (HttpClientHandler)handler;
                TestAssert.True(!managed.UseCookies && !managed.AllowAutoRedirect && !managed.UseDefaultCredentials
                    && managed.ServerCertificateCustomValidationCallback is null,
                    "The non-Windows fallback must keep the same anonymous transport boundaries without overriding certificate validation.");
            }

            using var client = SourceHttpTransportFactory.CreateAnonymousClient();
            TestAssert.Equal("MajdataPlay/2.1.0", client.DefaultRequestHeaders.UserAgent.ToString(),
                "Owned clients use the configured MajdataPlay-compatible identity.");
        }

        /// <summary>Uses actual sockets to prove that ignored native cookies do not prevent application-owned session headers.</summary>
        /// <returns>A task representing explicit cookie forwarding, redirect isolation and native cancellation.</returns>
        private static async Task ValidateLoopbackAsync()
        {
            var previousProxy = HttpClient.DefaultProxy;
            try
            {
                HttpClient.DefaultProxy = new WebProxy();
                await ValidateIsolatedLoopbackAsync();
            }
            finally
            {
                HttpClient.DefaultProxy = previousProxy;
            }
        }

        /// <summary>Exercises production-owned API and media clients with a scoped direct loopback-only default proxy.</summary>
        /// <returns>A task representing real native requests without dependence on machine proxy bypass lists.</returns>
        private static async Task ValidateIsolatedLoopbackAsync()
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            await using var foreign = new LoopbackSource();
            await using var origin = new LoopbackSource { RedirectTarget = new Uri(foreign.BaseUri, "image") };
            using var http = SourceHttpTransportFactory.CreateAnonymousClient();
            using (var nativeCookie = await http.GetAsync(new Uri(origin.BaseUri, "set-cookie"), timeout.Token))
            {
                nativeCookie.EnsureSuccessStatusCode();
            }

            using (var check = await http.GetAsync(new Uri(origin.BaseUri, "cookie-check"), timeout.Token))
            {
                check.EnsureSuccessStatusCode();
            }

            TestAssert.True(!origin.Observations.Single(item => item.Path == "/api/cookie-check").HasCookie,
                "The native handler must not create its own shared cookie session from Set-Cookie.");
            using var policy = new NetworkAccessPolicy();
            policy.SetEnabled(true);
            var endpoint = new EndpointConfiguration { Id = "loopback", Name = "Generated source", BaseUri = origin.BaseUri };
            using var provider = new MajNetClient(endpoint, policy);
            await provider.LoginAsync("GeneratedUser", "Generated fixture password", timeout.Token);
            TestAssert.Equal("GeneratedUser", await provider.GetCurrentUsernameAsync(timeout.Token),
                "WinHTTP IgnoreCookies must still permit the explicit same-origin Cookie header added by the source client.");
            var profile = origin.Observations.Single(item => item.Path == "/api/account/profile");
            TestAssert.True(profile.HasCookie && profile.UserAgent == "MajdataPlay/2.1.0",
                "Owned API requests retain the explicit session header and configured MajdataPlay-compatible identity at the native boundary.");
            using (var redirect = await http.GetAsync(new Uri(origin.BaseUri, "redirect"), timeout.Token))
            {
                TestAssert.Equal(HttpStatusCode.Found, redirect.StatusCode, "The underlying native handler must not follow a redirect independently.");
            }

            TestAssert.Equal(0, foreign.Observations.Count, "A raw native request cannot contact a redirected origin automatically.");
            using var media = provider.CreateMediaClient();
            using (var sameOrigin = await media.GetAsync(new Uri(origin.BaseUri, "image"), timeout.Token))
            {
                sameOrigin.EnsureSuccessStatusCode();
            }

            var sameOriginMedia = origin.Observations.Single(item => item.Path == "/api/image");
            TestAssert.True(sameOriginMedia.HasCookie && sameOriginMedia.UserAgent == "MajdataPlay/2.1.0",
                "Owned same-origin media receives the isolated cookie and configured MajdataPlay-compatible identity even though native cookie storage is disabled.");
            using (var redirected = await media.GetAsync(new Uri(origin.BaseUri, "redirect"), timeout.Token))
            {
                redirected.EnsureSuccessStatusCode();
            }

            TestAssert.True(foreign.Observations.Count == 1 && !foreign.Observations.Single().HasCookie
                && foreign.Observations.Single().UserAgent == "MajdataPlay/2.1.0",
                "A manually followed cross-port redirect keeps only the non-secret application identity, never the source account cookie.");
            using var supplied = new HttpClient(SourceHttpTransportFactory.CreateAnonymousHandler());
            supplied.DefaultRequestHeaders.UserAgent.ParseAdd("GeneratedCaller/1.0");
            using var borrowed = new MajNetClient(endpoint, policy, supplied);
            await borrowed.GetServerAsync(timeout.Token);
            TestAssert.Equal("GeneratedCaller/1.0", origin.Observations.Single(item => item.Path == "/api/info").UserAgent,
                "An injected API transport must retain the caller's supplied headers.");
            using (var borrowedMedia = borrowed.CreateMediaClient(SourceHttpTransportFactory.CreateAnonymousHandler()))
            using (var image = await borrowedMedia.GetAsync(new Uri(origin.BaseUri, "borrowed-image"), timeout.Token))
            {
                image.EnsureSuccessStatusCode();
            }

            TestAssert.Equal(string.Empty, origin.Observations.Single(item => item.Path == "/api/borrowed-image").UserAgent,
                "An injected media transport cannot acquire a production product header through the borrowed-handler path.");
            using var cancel = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
            var pending = media.GetAsync(new Uri(origin.BaseUri, "wait"), cancel.Token);
            try
            {
                await origin.WaitStarted.Task.WaitAsync(timeout.Token);
                cancel.Cancel();
                await TestAssert.ThrowsAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(3)));
                TestAssert.True(provider.IsLoggedIn, "Canceling a native media operation cannot discard the independent API session.");
            }
            finally
            {
                origin.ReleaseWait.TrySetResult();
            }
        }

        /// <summary>Retains only non-sensitive request facts observed by the generated loopback listener.</summary>
        /// <param name="Path">The generated request path.</param>
        /// <param name="HasCookie">Whether the generated session header was present.</param>
        /// <param name="UserAgent">The non-secret generated application identity actually received by the listener.</param>
        private sealed record Observation(string Path, bool HasCookie, string UserAgent);

        /// <summary>Provides a minimal real HTTP/1.1 source without URL ACLs, external traffic or native account data.</summary>
        private sealed class LoopbackSource : IAsyncDisposable
        {
            /// <summary>Owns only a dynamically assigned loopback listener.</summary>
            private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
            /// <summary>Cancels listener and held request lifetimes.</summary>
            private readonly CancellationTokenSource _lifetime = new();
            /// <summary>Protects request observations and the bounded fixture connection set.</summary>
            private readonly object _gate = new();
            /// <summary>Retains each accepted connection until fixture shutdown observes its completion.</summary>
            private readonly List<Task> _connections = new();
            /// <summary>Stores only safe facts from generated requests.</summary>
            private readonly List<Observation> _observations = new();
            /// <summary>Owns the single asynchronous accept loop.</summary>
            private readonly Task _accept;

            /// <summary>Starts an isolated ephemeral listener without modifying machine networking.</summary>
            public LoopbackSource()
            {
                _listener.Start();
                BaseUri = new Uri("http://127.0.0.1:" + ((IPEndPoint)_listener.LocalEndpoint).Port + "/api/");
                _accept = AcceptAsync();
            }

            /// <summary>Gets the explicitly generated loopback API root.</summary>
            public Uri BaseUri { get; }
            /// <summary>Gets or sets the generated redirect destination.</summary>
            public Uri? RedirectTarget { get; init; }
            /// <summary>Signals arrival of the actual request whose native cancellation is exercised.</summary>
            public TaskCompletionSource WaitStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            /// <summary>Releases the held generated response after the client cancels.</summary>
            public TaskCompletionSource ReleaseWait { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            /// <summary>Gets a safe immutable copy of observed requests.</summary>
            public IReadOnlyList<Observation> Observations
            {
                get
                {
                    lock (_gate)
                    {
                        return _observations.ToArray();
                    }
                }
            }

            /// <summary>Accepts independent HTTP connections until fixture shutdown.</summary>
            /// <returns>A task representing the fixture listener lifetime.</returns>
            private async Task AcceptAsync()
            {
                try
                {
                    while (!_lifetime.IsCancellationRequested)
                    {
                        var connection = await _listener.AcceptTcpClientAsync(_lifetime.Token);
                        var handled = HandleAsync(connection);
                        lock (_gate)
                        {
                            _connections.Add(handled);
                        }
                    }
                }
                catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
                {
                    // Fixture shutdown ended the pending accept.
                }
            }

            /// <summary>Reads one generated text request and responds with a complete connection-closing HTTP message.</summary>
            /// <param name="client">The accepted loopback connection owned by this handler.</param>
            /// <returns>A task representing request parsing and the optional held response.</returns>
            private async Task HandleAsync(TcpClient client)
            {
                using (client)
                {
                    try
                    {
                        using var stream = client.GetStream();
                        using var reader = new StreamReader(stream, Encoding.UTF8, false, 1024, true);
                        var request = await reader.ReadLineAsync(_lifetime.Token);
                        if (request is null)
                        {
                            return;
                        }

                        var path = request.Split(' ')[1];
                        var length = 0;
                        var hasCookie = false;
                        var userAgent = string.Empty;
                        while (await reader.ReadLineAsync(_lifetime.Token) is { Length: > 0 } line)
                        {
                            if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                            {
                                length = int.Parse(line[15..].Trim(), System.Globalization.CultureInfo.InvariantCulture);
                            }

                            hasCookie |= line.StartsWith("Cookie:", StringComparison.OrdinalIgnoreCase);
                            if (line.StartsWith("User-Agent:", StringComparison.OrdinalIgnoreCase))
                            {
                                userAgent = line[11..].Trim();
                            }
                        }

                        if (length > 0)
                        {
                            var body = new char[length];
                            await reader.ReadBlockAsync(body.AsMemory(), _lifetime.Token);
                        }

                        lock (_gate)
                        {
                            _observations.Add(new Observation(path, hasCookie, userAgent));
                        }

                        if (path == "/api/wait")
                        {
                            WaitStarted.TrySetResult();
                            await ReleaseWait.Task.WaitAsync(_lifetime.Token);
                        }

                        var headers = path == "/api/account/login" ? "Set-Cookie: generated-session=fixture; Path=/\r\n"
                            : path == "/api/set-cookie" ? "Set-Cookie: native-session=ignored; Path=/\r\n"
                            : path == "/api/redirect" ? "Location: " + RedirectTarget!.AbsoluteUri + "\r\n" : string.Empty;
                        var status = path == "/api/redirect" ? "302 Found" : "200 OK";
                        var bodyText = path == "/api/account/login" ? "{\"code\":0}"
                            : path == "/api/account/profile" ? "{\"username\":\"GeneratedUser\"}"
                            : path == "/api/info" ? "{\"id\":\"generated-server\",\"name\":\"Generated source\"}" : "generated";
                        var responseBody = Encoding.UTF8.GetBytes(bodyText);
                        var responseHeader = Encoding.ASCII.GetBytes("HTTP/1.1 " + status + "\r\n" + headers
                            + "Content-Type: application/json\r\nContent-Length: " + responseBody.Length
                            + "\r\nConnection: close\r\n\r\n");
                        await stream.WriteAsync(responseHeader, _lifetime.Token);
                        await stream.WriteAsync(responseBody, _lifetime.Token);
                    }
                    catch (IOException)
                    {
                        // Native cancellation can close the generated client before its held reply is written.
                    }
                    catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
                    {
                        // Fixture shutdown cancels a held response.
                    }
                }
            }

            /// <summary>Releases held replies, stops listening and observes every accepted connection.</summary>
            /// <returns>A task representing complete loopback cleanup.</returns>
            public async ValueTask DisposeAsync()
            {
                ReleaseWait.TrySetResult();
                await _lifetime.CancelAsync();
                await _accept;
                _listener.Stop();
                Task[] connections;
                lock (_gate)
                {
                    connections = _connections.ToArray();
                }

                await Task.WhenAll(connections);
                _lifetime.Dispose();
            }
        }
    }
}

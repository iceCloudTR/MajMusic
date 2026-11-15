#nullable enable

namespace MajMusic.Infrastructure.Network
{
    /// <summary>Gates explicit online activity and cancels requests when offline mode is restored.</summary>
    public sealed class NetworkAccessPolicy : IDisposable
    {
        /// <summary>Protects the online generation and its cancellation source.</summary>
        private readonly object _gate = new();

        /// <summary>Cancels all requests belonging to the current online generation.</summary>
        private CancellationTokenSource _generation = new();

        /// <summary>Records the user's explicit online choice.</summary>
        private bool _enabled;

        /// <summary>Records whether the policy has been disposed.</summary>
        private bool _disposed;

        /// <summary>Gets whether configured online sources are currently enabled.</summary>
        public bool IsEnabled
        {
            get
            {
                lock (_gate)
                {
                    return _enabled && !_disposed;
                }
            }
        }

        /// <summary>Enables explicit requests or cancels the current generation when going offline.</summary>
        /// <param name="enabled">Whether online requests are allowed.</param>
        /// <exception cref="ObjectDisposedException">The policy has already been disposed.</exception>
        public void SetEnabled(bool enabled)
        {
            CancellationTokenSource? previous = null;
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (_enabled == enabled)
                {
                    return;
                }

                _enabled = enabled;
                if (!enabled)
                {
                    previous = _generation;
                    _generation = new CancellationTokenSource();
                }
            }

            previous?.Cancel();
            previous?.Dispose();
        }

        /// <summary>Creates a request lifetime linked to both offline mode and the caller.</summary>
        /// <param name="cancellationToken">The caller's cancellation token.</param>
        /// <returns>A linked source that the caller must dispose.</returns>
        /// <exception cref="InvalidOperationException">Online activity is disabled.</exception>
        /// <exception cref="ObjectDisposedException">The policy has already been disposed.</exception>
        public CancellationTokenSource CreateRequestCancellation(CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (!_enabled)
                {
                    throw new InvalidOperationException("Online sources are disabled.");
                }

                return CancellationTokenSource.CreateLinkedTokenSource(_generation.Token, cancellationToken);
            }
        }

        /// <summary>Cancels outstanding network activity and releases cancellation resources.</summary>
        public void Dispose()
        {
            CancellationTokenSource previous;
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                _enabled = false;
                previous = _generation;
            }

            previous.Cancel();
            previous.Dispose();
        }
    }
}

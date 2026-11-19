#nullable enable

using System.Globalization;
using System.Net;
using System.Text.Json;
using MajMusic.Core.Models;

namespace MajMusic.Infrastructure.Preview
{
    internal sealed partial class MajViewPreviewSession
    {
        private readonly object _transportGate = new();
        private ChartIntent? _intent;
        private TaskCompletionSource _changed = NewSignal();
        private static readonly JsonSerializerOptions Wire = new(JsonSerializerDefaults.Web);
        private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

        public event Action<ChartFrame>? FrameReceived;

        public void SetIntent(ChartIntent intent)
        {
            lock (_transportGate)
            {
                _intent = intent;
                _changed.TrySetResult();
                _changed = NewSignal();
            }
        }

        private async Task HandleTransportAsync(HttpListenerContext context, string route, CancellationToken token)
        {
            var request = context.Request;
            var response = context.Response;
            var query = request.QueryString;
            var generation = int.TryParse(query["g"], out var g) ? g : -1;
            var revision = int.TryParse(query["ack"], out var ack) ? ack : -1;
            ChartIntent? intent;
            Task changed;
            lock (_transportGate)
            {
                intent = _intent;
                changed = _changed.Task;
            }
            if (intent is null || intent.Generation != generation)
            {
                response.StatusCode = 410;
                return;
            }
            _lifetime.CancelAfter(TimeSpan.FromMinutes(3));
            if (request.HttpMethod == "POST" && route is "notes" or "waveform" or "failed")
            {
                if (route == "failed")
                {
                    FrameReceived?.Invoke(new(generation, revision, 0, 0, false, Failed: true));
                }
                else
                {
                    var maxBytes = route == "notes" ? 4 * 1024 * 1024 : 8192;
                    if (request.ContentLength64 <= 0 || request.ContentLength64 > maxBytes)
                    {
                        response.StatusCode = 413;
                        return;
                    }
                    try
                    {
                        if (route == "notes")
                        {
                            var times = await JsonSerializer.DeserializeAsync<int[][]>(request.InputStream, cancellationToken: token).ConfigureAwait(false);
                            if (!double.TryParse(query["len"], NumberStyles.Float, CultureInfo.InvariantCulture, out var duration)
                                || ChartNoteTimeline.Create(times, duration) is not { } notes)
                            {
                                response.StatusCode = 400;
                                return;
                            }
                            FrameReceived?.Invoke(new(generation, revision, 0, duration, false, Notes: notes));
                        }
                        else
                        {
                            var peaks = await JsonSerializer.DeserializeAsync<int[]>(request.InputStream, cancellationToken: token).ConfigureAwait(false);
                            if (peaks is not { Length: 128 } || Array.Exists(peaks, peak => peak is < 0 or > 255))
                            {
                                response.StatusCode = 400;
                                return;
                            }
                            FrameReceived?.Invoke(new(generation, revision, 0, 0, false, Waveform: peaks));
                        }
                    }
                    catch (JsonException)
                    {
                        response.StatusCode = 400;
                        return;
                    }
                }
                response.StatusCode = 204;
                return;
            }
            if (request.HttpMethod != "GET" || route != "transport")
            {
                response.StatusCode = 405;
                return;
            }
            if (revision == intent.Revision)
            {
                var playing = query["play"] == "1";
                if (double.TryParse(query["time"], NumberStyles.Float, CultureInfo.InvariantCulture, out var position)
                    && double.TryParse(query["len"], NumberStyles.Float, CultureInfo.InvariantCulture, out var length)
                    && length is > 0 and <= 604800 && double.IsFinite(position))
                {
                    FrameReceived?.Invoke(new(generation, revision, Math.Clamp(position, 0, length), length, playing));
                }
                // Paused pages sleep until a native command. While playing, return the audio clock at 6 Hz.
                try
                {
                    await changed.WaitAsync(playing ? TimeSpan.FromMilliseconds(160) : TimeSpan.FromSeconds(5), token).ConfigureAwait(false);
                }
                catch (TimeoutException) { }
                lock (_transportGate)
                { intent = _intent!; }
                if (intent.Generation != generation)
                {
                    response.StatusCode = 410;
                    return;
                }
                if (revision == intent.Revision)
                {
                    response.StatusCode = 204;
                    return;
                }
            }
            response.ContentType = "application/json";
            await JsonSerializer.SerializeAsync(response.OutputStream, intent, Wire, token).ConfigureAwait(false);
        }
    }
}

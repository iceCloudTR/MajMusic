#nullable enable

using System.Net;
using System.Text.Json;
using MajMusic.Core.Models;

namespace MajMusic.Infrastructure.Network
{
    public sealed partial class MajNetClient
    {
        /// <summary>Reads preview-only resource descriptors through this endpoint's existing account session.</summary>
        /// <param name="remoteId">The chart's identifier within the source.</param>
        /// <param name="includeVideo">Whether to resolve the optional background descriptor.</param>
        /// <param name="cancellationToken">Cancels the API request.</param>
        /// <returns>The current revision and its playable resources, or the legacy direct routes.</returns>
        internal async Task<(string? Revision, PreviewResource Chart, PreviewResource? Video, bool DirectRoutes)> GetChartPreviewResourcesAsync(
            string remoteId, bool includeVideo, CancellationToken cancellationToken)
        {
            var path = $"maichart/{EscapeIdentifier(remoteId)}";
            var chartRoute = new Uri(_apiRoot, $"{path}/chart");
            var videoRoute = new Uri(_apiRoot, $"{path}/video");
            using var response = await SendAsync(HttpMethod.Get, $"{path}/info", null, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed or HttpStatusCode.NotImplemented)
            {
                return (null, new PreviewResource(new MediaResource { RemoteUri = chartRoute }, chartRoute),
                    includeVideo ? new PreviewResource(new MediaResource { RemoteUri = videoRoute }, videoRoute) : null, true);
            }

            EnsureSuccess(response);
            using var document = await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
            var root = document.RootElement;
            if (!root.TryGetProperty("metadata", out var metadata) || metadata.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException("接入点没有返回有效的谱面详情。");
            }

            var revision = GetText(metadata, "hash");
            var hasResources = root.TryGetProperty("resources", out var resources) && resources.ValueKind == JsonValueKind.Object;
            var chart = (hasResources ? ReadResource(resources, "maidata", chartRoute) : null)
                ?? new MediaResource { RemoteUri = chartRoute };
            var video = includeVideo
                ? hasResources && resources.TryGetProperty("video", out _)
                    ? ReadResource(resources, "video", videoRoute)
                    : new MediaResource { RemoteUri = videoRoute }
                : null;
            return (string.IsNullOrWhiteSpace(revision) ? null : revision,
                new PreviewResource(chart with { RemoteUri = chartRoute }, chart.RemoteUri!),
                video is null ? null : new PreviewResource(video with { RemoteUri = videoRoute }, video.RemoteUri!), false);
        }

        /// <summary>Separates a cache's stable identity from the server's possibly temporary transfer URL.</summary>
        internal sealed record PreviewResource(MediaResource Descriptor, Uri RequestAddress);
    }
}

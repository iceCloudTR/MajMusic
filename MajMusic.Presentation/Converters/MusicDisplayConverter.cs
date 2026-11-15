#nullable enable

using System.Globalization;
using Avalonia.Data.Converters;
using MajMusic.Core.Models;

namespace MajMusic.Presentation.Converters
{
    /// <summary>Formats library and playlist metadata without doing I/O from a binding.</summary>
    public sealed class MusicDisplayConverter : IValueConverter
    {
        /// <inheritdoc />
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is MusicTrack track)
            {
                return (parameter as string) switch
                {
                    "duration" => track.Duration is { TotalSeconds: > 0 } duration
                        ? duration.TotalHours >= 1 ? $"{(long)duration.TotalHours}:{duration.Minutes:00}:{duration.Seconds:00}"
                            : $"{(long)duration.TotalMinutes:00}:{duration.Seconds:00}" : "--:--",
                    "audio" => AudioLabel(track),
                    "source" => track.RemoteId is null ? "本地" : track.SourceName ?? "在线谱面",
                    _ => string.Empty
                };
            }

            if (value is MusicPlaylist playlist)
            {
                var label = playlist.IsCloud ? "云端" + (playlist.SourceName is { Length: > 0 } source ? " · " + source : string.Empty) : playlist.ContentKind switch
                {
                    PlaylistContentKind.Local => "纯本地",
                    PlaylistContentKind.Online => "纯在线",
                    PlaylistContentKind.Mixed => "混合",
                    PlaylistContentKind.Unresolved => "待匹配",
                    _ => "空列表"
                };
                return playlist.UnresolvedCount > 0 ? $"{label} · {playlist.UnresolvedCount} 首待匹配" : label;
            }

            if (value is LocalDirectorySource directory)
            {
                return directory.Kind == LocalDirectoryKind.MusicLibrary ? "曲库" : "收藏夹索引";
            }

            return string.Empty;
        }

        /// <inheritdoc />
        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            throw new NotSupportedException();
        }

        /// <summary>Formats known encoding properties or the online-chart fallback.</summary>
        /// <param name="track">The immutable indexed entry.</param>
        /// <returns>A compact bitrate and sample-rate label.</returns>
        private static string AudioLabel(MusicTrack track)
        {
            var parts = new List<string>();
            if (track.AudioInfo?.BitrateBitsPerSecond is { } bitrate)
            {
                parts.Add($"{(track.AudioInfo.IsBitrateEstimated ? "≈" : "")}{bitrate / 1000d:0.#} kbps");
            }

            if (track.AudioInfo?.SampleRateHz is { } rate)
            {
                parts.Add($"{rate / 1000d:0.##} kHz");
            }

            return parts.Count > 0 ? string.Join(" · ", parts) : track.RemoteId is not null ? "在线谱面" : "—";
        }
    }
}

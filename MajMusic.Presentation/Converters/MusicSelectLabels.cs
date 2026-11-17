#nullable enable

using MajMusic.Core.Models;
using MajMusic.Presentation.ViewModels;

namespace MajMusic.Presentation.Converters
{
    /// <summary>Provides typed selection labels to Arc without runtime property lookup.</summary>
    public static class MusicSelectLabels
    {
        /// <summary>Gets the visible label of a light, dark or system appearance choice.</summary>
        public static Func<object?, string?> Appearance { get; } = static item => (item as AppearanceOption)?.Name;
        /// <summary>Gets the localized name of a spacious, compact or small card layout.</summary>
        public static Func<object?, string?> CardLayout { get; } = static item => (item as CardLayoutOption)?.Name;
        /// <summary>Gets the visible range used by an explicitly requested local-music cleanup.</summary>
        public static Func<object?, string?> LocalMediaCleanup { get; } = static item => (item as LocalMediaCleanupOption)?.Name;
        /// <summary>Gets the display label of an independent endpoint and its current account.</summary>
        public static Func<object?, string?> Endpoint { get; } = static item => (item as EndpointProfile)?.DisplayName;
        /// <summary>Gets the display label of a remembered account or Guest.</summary>
        public static Func<object?, string?> Account { get; } = static item => (item as SavedAccountOption)?.DisplayName;
        /// <summary>Gets the display label of a configured directory role.</summary>
        public static Func<object?, string?> DirectoryKind { get; } = static item => (item as DirectoryKindOption)?.Name;
        /// <summary>Gets the display label of an audio output device.</summary>
        public static Func<object?, string?> OutputDevice { get; } = static item => (item as AudioOutputDevice)?.Name;
        /// <summary>Gets a playlist name and its local, mixed or cloud classification.</summary>
        public static Func<object?, string?> Playlist { get; } = static item => item is MusicPlaylist playlist
            ? playlist.Name + " · " + (playlist.IsCloud ? playlist.SourceName ?? "云端" : playlist.ContentKind switch
            {
                PlaylistContentKind.Local => "本地",
                PlaylistContentKind.Online => "在线",
                PlaylistContentKind.Mixed => "混合",
                PlaylistContentKind.Unresolved => "待匹配",
                _ => "空列表"
            }) : null;
    }
}

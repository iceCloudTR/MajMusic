#nullable enable

using LibVLCSharp.Shared;
using MajMusic.Core.Playback;

namespace MajMusic.Audio.Desktop
{
    /// <summary>Loads the desktop audio engine without preventing the local library from opening.</summary>
    public static class DesktopPlaybackBackendFactory
    {
        /// <summary>Creates a local-file audio backend or an explicitly unavailable backend with a useful reason.</summary>
        /// <param name="nativeLibraryDirectory">An optional explicit directory containing the platform LibVLC library.</param>
        /// <returns>The available backend, or a backend describing the missing native dependency.</returns>
        public static IPlaybackBackend Create(string? nativeLibraryDirectory = null)
        {
            try
            {
                return new LibVlcPlaybackBackend(nativeLibraryDirectory);
            }
            catch (Exception exception) when (exception is VLCException or DllNotFoundException or BadImageFormatException
                or FileNotFoundException or DirectoryNotFoundException or TypeInitializationException)
            {
                var instruction = OperatingSystem.IsWindows()
                    ? "Restore the bundled VideoLAN.LibVLC.Windows runtime for this application architecture."
                    : OperatingSystem.IsMacOS()
                        ? "Install the matching VLC/LibVLC runtime for macOS."
                        : "Install libvlc and its audio codec plugins for this Linux distribution.";
                return new UnavailablePlaybackBackend("The desktop audio engine could not be loaded. " + instruction);
            }
        }
    }
}

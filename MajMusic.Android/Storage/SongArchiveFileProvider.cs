#nullable enable

using AndroidX.Core.Content;
using Java.Interop;

namespace MajMusic.Android.Storage
{
    /// <summary>Serves ZIPs from the narrow cache subtree declared in the Android manifest.</summary>
    [JniTypeSignature("com/icecloud/majmusic/SongArchiveFileProvider", GenerateJavaPeer = true)]
    public sealed class SongArchiveFileProvider : FileProvider
    {
    }
}

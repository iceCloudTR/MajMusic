#nullable enable

namespace MajMusic.Core.Models
{
    /// <summary>Describes the known music origins in a playlist independently of where the list is stored.</summary>
    public enum PlaylistContentKind
    {
        /// <summary>Indicates that the playlist has no members or unavailable references.</summary>
        Empty,

        /// <summary>Indicates that all resolved members originated from local files.</summary>
        Local,

        /// <summary>Indicates that all resolved members originated from online sources.</summary>
        Online,

        /// <summary>Indicates that resolved members include both local files and online sources.</summary>
        Mixed,

        /// <summary>Indicates that the playlist contains references but none are currently resolved.</summary>
        Unresolved
    }
}

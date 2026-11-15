#nullable enable

namespace MajMusic.Core.Models
{
    /// <summary>Chooses a persistent application appearance independently of the UI toolkit.</summary>
    public enum AppearancePreference
    {
        /// <summary>Uses the application's graphite appearance.</summary>
        Dark = 0,
        /// <summary>Uses the application's light appearance.</summary>
        Light = 1,
        /// <summary>Follows the operating system's appearance and subsequent changes.</summary>
        System = 2
    }
}

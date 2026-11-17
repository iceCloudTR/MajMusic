#nullable enable

using Avalonia.Controls;

namespace MajMusic.Presentation.Views
{
    /// <summary>Shares the application version and release actions between settings layouts.</summary>
    public partial class AppUpdateSettings : UserControl
    {
        /// <summary>Initializes the update settings without contacting GitHub.</summary>
        public AppUpdateSettings()
        {
            InitializeComponent();
        }
    }
}

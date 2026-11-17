#nullable enable

using Avalonia.Controls;

namespace MajMusic.Presentation.Views
{
    /// <summary>Uses simultaneous artwork, discussion and composer columns while the original song card moves into its left slot.</summary>
    public partial class DesktopSongDetailsView : UserControl
    {
        /// <summary>Creates the desktop panes without opening another player or submitting source interactions.</summary>
        public DesktopSongDetailsView()
        {
            InitializeComponent();
        }
    }
}

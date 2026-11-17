#nullable enable

using Avalonia.Controls;
using Avalonia.Interactivity;
using MajMusic.Presentation.ViewModels;

namespace MajMusic.Presentation.Views
{
    /// <summary>Opens the selected song in the official MajView renderer.</summary>
    public partial class ChartPreviewPane : UserControl
    {
        /// <summary>Initializes the shared desktop and mobile preview entry.</summary>
        public ChartPreviewPane()
        {
            InitializeComponent();
        }

        private async void OnOpenMajView(object? sender, RoutedEventArgs args)
        {
            if (DataContext is MainViewModel owner && TopLevel.GetTopLevel(this) is { } top)
            {
                await owner.OpenMajViewAsync(address => top.Launcher.LaunchUriAsync(address));
            }
        }
    }
}

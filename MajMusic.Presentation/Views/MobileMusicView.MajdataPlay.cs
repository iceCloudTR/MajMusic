#nullable enable

using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace MajMusic.Presentation.Views
{
    /// <summary>Uses the current host's writable media picker without retaining an obsolete mobile activity.</summary>
    public partial class MobileMusicView
    {
        /// <summary>Opens the shared media picker from the touch directory page.</summary>
        /// <param name="sender">The media directory browse action.</param>
        /// <param name="args">The explicit touch or keyboard activation.</param>
        private async void OnMediaStorageBrowseClicked(object? sender, RoutedEventArgs args)
        {
            if (this.FindAncestorOfType<MainView>() is { } host)
            {
                await host.BrowseMediaStorageAsync();
            }
        }
    }
}

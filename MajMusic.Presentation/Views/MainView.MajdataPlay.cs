#nullable enable

using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using MajMusic.Presentation.Storage;
using MajMusic.Presentation.ViewModels;

namespace MajMusic.Presentation.Views
{
    /// <summary>Connects a separately authorized shared-media picker to the directory editor.</summary>
    public partial class MainView
    {
        /// <summary>Gets or sets the host's writable shared-tree resolver independently of ordinary read-only music imports.</summary>
        public Func<IStorageFolder, Task<string>>? SharedDirectoryReferenceResolver { get; set; }

        /// <summary>Opens a writable media-directory picker for the desktop directory editor.</summary>
        /// <param name="sender">The media directory browse button.</param>
        /// <param name="args">The explicit user click.</param>
        private async void OnMediaStorageBrowseClicked(object? sender, RoutedEventArgs args)
        {
            await BrowseMediaStorageAsync();
        }

        /// <summary>Chooses and retains a media root only after the user explicitly opens the native directory picker.</summary>
        /// <returns>A task representing the chosen writable tree, or canceled selection.</returns>
        internal async Task BrowseMediaStorageAsync()
        {
            if (DataContext is not MainViewModel owner || TopLevel.GetTopLevel(this) is not { } topLevel)
            {
                return;
            }
            try
            {
                var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
                {
                    Title = owner.UseMajdataPlayStorage ? "选择 MajdataPlay 目录" : "选择媒体储存目录",
                    AllowMultiple = false
                });
                if (folders.Count > 0)
                {
                    using var folder = folders[0];
                    var reference = await StoragePickerBridge.GetDirectoryReferenceAsync(folder, SharedDirectoryReferenceResolver);
                    owner.SetPickedMediaStorage(reference);
                }
            }
            catch (Exception exception)
            {
                owner.ReportError(exception);
            }
        }
    }
}

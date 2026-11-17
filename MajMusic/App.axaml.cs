using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

using MajMusic.Core.Playback;
using MajMusic.Audio.Desktop;
using MajMusic.Infrastructure.Application;
using MajMusic.Infrastructure.Metadata;
using MajMusic.Infrastructure.Network;
using MajMusic.Preview;
using MajMusic.Presentation.Theme;
using MajMusic.Presentation.ViewModels;
using MajMusic.Presentation.Views;

#nullable enable

namespace MajMusic
{
    /// <summary>Composes the desktop services without contacting optional music sources.</summary>
    public partial class App : Application
    {
        /// <summary>Loads application resources.</summary>
        public override void Initialize()
        {
            AvaloniaXamlLoader.Load(this);
        }

        /// <summary>Connects the shared view to local-first application services.</summary>
        public override void OnFrameworkInitializationCompleted()
        {
            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                var workspace = new MusicWorkspace();
                ChartPreviewPane.CreateEmbeddedView = (address, fail) => new DesktopMajView(address, fail, workspace.DataDirectory);
                try
                {
                    var preferences = workspace.LoadSettingsAsync().GetAwaiter().GetResult();
                    MajMusicTheme.ApplyAppearance(preferences.Appearance);
                }
                catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException)
                {
                    System.Diagnostics.Trace.TraceWarning("Startup appearance could not be restored: {0}", exception.GetType().Name);
                }

                var playback = new PlaybackService(DesktopPlaybackBackendFactory.Create(), resolveAudioPath:
                    (track, cancellationToken) => workspace.ResolveAudioAsync(track, false, cancellationToken));
                var metadata = new CachedTrackMetadataProvider(new LocalAudioMetadataReader(),
                    Path.Combine(workspace.DataDirectory, "AudioMetadata"));
                var collections = new MajCollectionExchange(workspace);
                var directories = new LocalDirectoryService(workspace, collections);
                var viewModel = new MainViewModel(workspace, playback, collections: collections, metadata: metadata, directories: directories,
                    releases: new GitHubAppReleaseSource());
                var mobilePreview = Array.Exists(desktop.Args ?? [], argument => argument == "--mobile-preview");
                desktop.MainWindow = new MainWindow(viewModel, workspace, mobilePreview);
            }

            base.OnFrameworkInitializationCompleted();
        }
    }
}

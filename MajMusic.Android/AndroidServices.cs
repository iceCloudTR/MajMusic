#nullable enable

using System.Net;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using MajMusic.Android.Audio;
using MajMusic.Android.Security;
using MajMusic.Android.Services;
using MajMusic.Core.Models;
using MajMusic.Core.Playback;
using MajMusic.Infrastructure.Application;
using MajMusic.Infrastructure.Metadata;
using MajMusic.Infrastructure.Network;
using MajMusic.Presentation.Storage;
using MajMusic.Presentation.Theme;
using MajMusic.Presentation.ViewModels;
using MajMusic.Presentation.Views;
using Xamarin.Android.Net;

namespace MajMusic.Android
{
    /// <summary>Owns one local-first workspace and media session across Android activity recreation.</summary>
    internal sealed class AndroidServices
    {
        /// <summary>Keeps the endpoint API transport alive for the application session.</summary>
        private readonly HttpClient _apiClient;
        /// <summary>Keeps media transfers independently configurable from account API requests.</summary>
        private readonly HttpClient _resourceClient;
        /// <summary>Persists the application-owned catalog, settings and offline media.</summary>
        private readonly MusicWorkspace _workspace;
        /// <summary>Owns the shared queue and native playback independently of visible views.</summary>
        private readonly PlaybackService _playback;
        /// <summary>Shares current music actions across replacement activity views.</summary>
        private readonly MainViewModel _viewModel;
        /// <summary>Locates the current storage picker without retaining destroyed activity views.</summary>
        private WeakReference<Control>? _currentView;
        /// <summary>Coalesces application initialization across activity recreation.</summary>
        private Task? _initialization;

        /// <summary>Creates the Android composition without enabling network access.</summary>
        /// <param name="context">The process-owned Android application context.</param>
        public AndroidServices(global::Android.Content.Context context)
        {
            var directory = context.FilesDir?.AbsolutePath ?? throw new InvalidOperationException("Application storage is unavailable.");
            _apiClient = CreateClient(TimeSpan.FromSeconds(100));
            _resourceClient = CreateClient(TimeSpan.FromMinutes(5));
            var documents = new AvaloniaDocumentStorage(CurrentStorageProvider, Path.Combine(directory, "DocumentCovers"));
            _workspace = new MusicWorkspace(directory, _apiClient, _resourceClient,
                credentialStore: new AndroidCredentialStore(context), documentStorage: documents);
            try
            {
                var settings = _workspace.LoadSettingsAsync().GetAwaiter().GetResult();
                MajMusicTheme.ApplyAppearance(settings.Appearance);
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                System.Diagnostics.Trace.TraceWarning("Android appearance could not be restored: {0}", exception.GetType().Name);
            }

            _playback = new PlaybackService(new AndroidMediaPlayerBackend(context),
                resolveAudioPath: (track, token) => _workspace.ResolveAudioAsync(track, false, token));
            var metadata = new AndroidTrackMetadataProvider(new AndroidAudioMetadataReader(context),
                Path.Combine(directory, "AudioMetadata"));
            var collections = new MajCollectionExchange(_workspace, documentStorage: documents);
            var directories = new LocalDirectoryService(_workspace, collections);
            _viewModel = new MainViewModel(_workspace, _playback, collections: collections, metadata: metadata, directories: directories);
            PlaybackMediaService.Connect(_playback);
        }

        /// <summary>Creates a fresh responsive view while retaining the existing catalog and playback session.</summary>
        /// <returns>The view belonging to the new Android activity.</returns>
        public Control CreateMainView()
        {
            var view = new MainView { DataContext = _viewModel, DirectoryReferenceResolver = RetainDirectoryReferenceAsync };
            _currentView = new WeakReference<Control>(view);
            view.Loaded += OnViewLoaded;
            return view;
        }

        /// <summary>Starts indexing and saved preferences after the Android document provider is available.</summary>
        /// <param name="sender">The newly attached activity view.</param>
        /// <param name="args">The native view-load event.</param>
        private async void OnViewLoaded(object? sender, RoutedEventArgs args)
        {
            try
            {
                _initialization ??= _viewModel.InitializeAsync();
                await _initialization;
            }
            catch (Exception exception)
            {
                _viewModel.ReportError(exception);
            }
        }

        /// <summary>Finds the picker associated with the currently attached activity.</summary>
        /// <returns>The current native storage provider, or null between activity views.</returns>
        private IStorageProvider? CurrentStorageProvider()
        {
            return _currentView?.TryGetTarget(out var view) == true ? TopLevel.GetTopLevel(view)?.StorageProvider : null;
        }

        /// <summary>Retains only read access to the selected source tree without requiring a writable provider.</summary>
        /// <param name="folder">The directory explicitly authorized through the native picker.</param>
        /// <returns>The original provider reference retained across activity and process restarts.</returns>
        /// <exception cref="NotSupportedException">The picker did not return an accessible local directory or content tree.</exception>
        private static async Task<string> RetainDirectoryReferenceAsync(IStorageFolder folder)
        {
            if (folder.TryGetLocalPath() is { } localPath)
            {
                return localPath;
            }

            var reference = folder.Path.AbsoluteUri;
            if (!DocumentReference.IsContentUri(reference))
            {
                throw new NotSupportedException("无法保留所选目录的读取权限。");
            }

            await Task.Run(() =>
            {
                using var uri = global::Android.Net.Uri.Parse(reference)
                    ?? throw new InvalidDataException("The selected directory reference is invalid.");
                var resolver = global::Android.App.Application.Context.ContentResolver
                    ?? throw new InvalidOperationException("Android document permissions are unavailable.");
                resolver.TakePersistableUriPermission(uri, global::Android.Content.ActivityFlags.GrantReadUriPermission);
            }).ConfigureAwait(false);
            return reference;
        }

        /// <summary>Creates an Android-native transport whose account cookies and redirects remain application-owned.</summary>
        /// <param name="timeout">The complete-request timeout used by this client.</param>
        /// <returns>The owned native HTTP client with the configured compatible user agent.</returns>
        private static HttpClient CreateClient(TimeSpan timeout)
        {
            var handler = new AndroidMessageHandler
            {
                UseCookies = false,
                AllowAutoRedirect = false,
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
            };
            var client = new HttpClient(handler) { Timeout = timeout };
            client.DefaultRequestHeaders.UserAgent.ParseAdd(SourceHttpTransportFactory.UserAgent);
            return client;
        }
    }
}

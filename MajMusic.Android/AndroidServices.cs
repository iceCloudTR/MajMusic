#nullable enable

using System.Net;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using MajMusic.Android.Audio;
using MajMusic.Android.Network;
using MajMusic.Android.Security;
using MajMusic.Android.Services;
using MajMusic.Android.Storage;
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
            var mediaStorage = new AvaloniaMediaStorageAccess(CurrentStorageProvider, RenameMediaDocumentAsync);
            _workspace = new MusicWorkspace(directory, _apiClient, _resourceClient,
                credentialStore: new AndroidCredentialStore(context), documentStorage: documents, mediaStorageAccess: mediaStorage);
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
            _viewModel.EnsureFileAccessAsync = AndroidStorageAccess.Instance.EnsureAccessAsync;
            _viewModel.RequestAllFilesAccessAsync = AndroidStorageAccess.Instance.RequestAllFilesAccessAsync;
            _viewModel.ResolveFileSystemPath = AndroidStorageAccess.Instance.ResolveFileSystemPath;
            PlaybackMediaService.Connect(_playback);
        }

        /// <summary>Creates a fresh responsive view while retaining the existing catalog and playback session.</summary>
        /// <returns>The view belonging to the new Android activity.</returns>
        public Control CreateMainView()
        {
            var view = new MainView
            {
                DataContext = _viewModel,
                DirectoryReferenceResolver = RetainDirectoryReferenceAsync,
                MediaDirectoryReferenceResolver = RetainMediaDirectoryReferenceAsync
            };
            TopLevel.SetAutoSafeAreaPadding(view, false);
            _currentView = new WeakReference<Control>(view);
            view.Loaded += OnViewLoaded;
            view.LayoutUpdated += OnMobileLayoutReady;
            return view;
        }

        /// <summary>Releases the active embedded chart preview independently of the music transport.</summary>
        internal void CloseChartPreview()
        {
            _viewModel.CloseMajView();
        }

        /// <summary>Connects the single page-owned safe area after the real mobile viewport and native rendering scale are available.</summary>
        /// <param name="sender">The current activity's shared root view.</param>
        /// <param name="args">The completed layout notification.</param>
        private static void OnMobileLayoutReady(object? sender, EventArgs args)
        {
            if (sender is not MainView view || TopLevel.GetTopLevel(view) is not { } topLevel
                || view.GetVisualDescendants().OfType<MobileMusicView>().FirstOrDefault() is not { } mobile
                || mobile.FindControl<TKCode.Arc.Mobile.Page>("MobilePage") is not { } page
                || topLevel.ClientSize.Width < 200 || topLevel.ClientSize.Height < 200
                || Math.Abs(view.Bounds.Width - topLevel.ClientSize.Width) > 1
                || Math.Abs(view.Bounds.Height - topLevel.ClientSize.Height) > 1
                || mobile.Bounds.Width < 200 || mobile.Bounds.Height < 200 || page.Bounds.Height < 200
                || Math.Abs(topLevel.RenderScaling - (global::Android.App.Application.Context.Resources?.DisplayMetrics?.Density ?? 1)) > .01)
            {
                return;
            }

            view.LayoutUpdated -= OnMobileLayoutReady;
            PageSafeAreaOwner.Attach(topLevel, view, page);
        }

        /// <summary>Starts indexing and saved preferences after the Android document provider is available.</summary>
        /// <param name="sender">The newly attached activity view.</param>
        /// <param name="args">The native view-load event.</param>
        private async void OnViewLoaded(object? sender, RoutedEventArgs args)
        {
            if (sender is MainView view)
            {
                view.LayoutUpdated -= OnMobileLayoutReady;
                view.LayoutUpdated += OnMobileLayoutReady;
            }

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
            var converted = AndroidStorageAccess.Instance.ResolveFileSystemPath(folder.Path.AbsoluteUri);
            if (!DocumentReference.IsContentUri(converted) && Path.IsPathFullyQualified(converted))
            {
                return converted;
            }
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

        /// <summary>Retains read and write access only for a media root explicitly selected through the media-directory picker.</summary>
        /// <param name="folder">The user-selected media root, independent of ordinary read-only library grants.</param>
        /// <returns>The persisted writable document-tree URI or ordinary accessible path.</returns>
        private static async Task<string> RetainMediaDirectoryReferenceAsync(IStorageFolder folder)
        {
            var reference = folder.Path.AbsoluteUri;
            var converted = AndroidStorageAccess.Instance.ResolveFileSystemPath(reference);
            if (!DocumentReference.IsContentUri(converted) && Path.IsPathFullyQualified(converted))
            {
                return converted;
            }
            if (!DocumentReference.IsContentUri(reference))
            {
                return folder.TryGetLocalPath() ?? throw new NotSupportedException("无法保留所选媒体目录的读写权限。");
            }

            await Task.Run(() =>
            {
                using var uri = global::Android.Net.Uri.Parse(reference)
                    ?? throw new InvalidDataException("The selected media tree reference is invalid.");
                var resolver = global::Android.App.Application.Context.ContentResolver
                    ?? throw new InvalidOperationException("Android document permissions are unavailable.");
                resolver.TakePersistableUriPermission(uri,
                    global::Android.Content.ActivityFlags.GrantReadUriPermission | global::Android.Content.ActivityFlags.GrantWriteUriPermission);
            }).ConfigureAwait(false);
            return reference;
        }

        /// <summary>Commits one completed temporary media object using its provider's same-parent rename operation.</summary>
        /// <param name="reference">The caller-owned completed temporary document.</param>
        /// <param name="name">The immutable final SHA-256 filename.</param>
        /// <param name="cancellationToken">Cancels before the native commit begins.</param>
        /// <returns>The renamed completed document URI.</returns>
        private static Task<string> RenameMediaDocumentAsync(string reference, string name, CancellationToken cancellationToken)
        {
            return Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var uri = global::Android.Net.Uri.Parse(reference)
                    ?? throw new InvalidDataException("The temporary media document reference is invalid.");
                var resolver = global::Android.App.Application.Context.ContentResolver
                    ?? throw new InvalidOperationException("Android document permissions are unavailable.");
                using var renamed = global::Android.Provider.DocumentsContract.RenameDocument(resolver, uri, name)
                    ?? throw new IOException("无法提交完整的媒体文件。");
                return renamed.ToString() ?? throw new IOException("文件提交未返回有效地址。");
            }, cancellationToken);
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
            var client = new HttpClient(new AndroidNetworkDiagnosticsHandler(handler)) { Timeout = timeout };
            client.DefaultRequestHeaders.UserAgent.ParseAdd(SourceHttpTransportFactory.UserAgent);
            return client;
        }
    }
}

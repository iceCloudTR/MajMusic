#nullable enable

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MajMusic.Core.Abstractions;
using MajMusic.Core.Models;
using MajMusic.Core.Playback;
using MajMusic.Infrastructure.Application;
using MajMusic.Infrastructure.Storage;
using MajMusic.Presentation.Theme;
using MajMusic.Presentation.ViewModels;
using MajMusic.Presentation.Views;
using Microsoft.Data.Sqlite;
using SkiaSharp;
using TKCode.Arc;
using TKCode.Arc.Motion;
using ArcSelect = TKCode.Arc.Select;

namespace MajMusic.Tests
{
    /// <summary>Checks independent appearance persistence and actual light, dark and system UI projection.</summary>
    internal static class AppearanceTests
    {
        /// <summary>Runs preference round trips, merged saves and real desktop and phone rendering.</summary>
        /// <returns>A task representing the completed appearance regressions.</returns>
        public static async Task RunAsync()
        {
            var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "MajMusic-appearance-" + Guid.NewGuid().ToString("N")));
            Directory.CreateDirectory(root);
            try
            {
                await VerifyPersistenceAsync(Path.Combine(root, "persistence"));
                await VerifyMergedChoicesAsync(Path.Combine(root, "merged"));
                await VerifyRenderingAsync(Path.Combine(root, "render"));
            }
            finally
            {
                SqliteConnection.ClearAllPools();
                var temporary = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                if (!root.StartsWith(temporary, StringComparison.OrdinalIgnoreCase)
                    || !Path.GetFileName(root).StartsWith("MajMusic-appearance-", StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("Refusing to remove files outside the generated appearance fixture.");
                }

                Directory.Delete(root, true);
            }
        }

        /// <summary>Restores legacy defaults and saves one appearance without replacing independent preferences.</summary>
        /// <param name="root">The explicit generated application-data directory.</param>
        /// <returns>A task representing validated local appearance persistence.</returns>
        private static async Task VerifyPersistenceAsync(string root)
        {
            Directory.CreateDirectory(root);
            var path = Path.Combine(root, "settings.json");
            await File.WriteAllTextAsync(path, "{\"onlineEnabled\":false}");
            using var transport = new NoNetworkTransport();
            using var client = new HttpClient(transport);
            using (var workspace = new MusicWorkspace(root, client, client))
            {
                var legacy = await workspace.LoadSettingsAsync();
                TestAssert.True(legacy.Appearance == AppearancePreference.Dark && !legacy.OnlineEnabled,
                    "An existing settings file without appearance must retain dark mode and offline permission.");
                var endpoint = new EndpointConfiguration
                {
                    Id = "source",
                    Name = "Generated source",
                    BaseUri = new Uri("https://appearance.example.invalid/api/"),
                    Accounts = [new SavedEndpointAccount { Id = "account", Username = "Generated account", UseCloudCollections = true }],
                    SelectedAccountId = "account"
                };
                await workspace.SaveSettingsAsync(legacy with { Endpoints = [endpoint], ActiveEndpointId = endpoint.Id, CacheLimitBytes = 123456789 });
                await Task.WhenAll(workspace.UpdateAppearanceAsync(AppearancePreference.Light),
                    workspace.UpdateAudioOutputAsync(new AudioOutputPreferences { Volume = 37, Muted = true }));
                var saved = await workspace.LoadSettingsAsync();
                TestAssert.True(saved.Appearance == AppearancePreference.Light && saved.AudioOutput is { Volume: 37, Muted: true }
                    && saved.CacheLimitBytes == 123456789 && saved.Endpoints.Single().Accounts.Single().UseCloudCollections
                    && saved.Endpoints.Single().SelectedAccountId == "account" && !saved.OnlineEnabled,
                    "An appearance write and a concurrent audio write must preserve each other, saved identities and offline permission.");
                await TestAssert.ThrowsAsync<ArgumentOutOfRangeException>(() => workspace.UpdateAppearanceAsync((AppearancePreference)777));
                using var canceled = new CancellationTokenSource();
                canceled.Cancel();
                await TestAssert.ThrowsAsync<OperationCanceledException>(() => workspace.UpdateAppearanceAsync(AppearancePreference.System, canceled.Token));
                TestAssert.Equal(AppearancePreference.Light, (await workspace.LoadSettingsAsync()).Appearance,
                    "Rejecting an invalid or canceled appearance write cannot replace the valid saved choice.");
            }

            using (var reopened = new MusicWorkspace(root, client, client))
            {
                await using var view = new MainViewModel(reopened, new PlaybackService(new UnavailablePlaybackBackend()), static action => action());
                await view.InitializeAsync();
                TestAssert.Equal(AppearancePreference.Light, view.ThemePreference, "Restarted view models must restore the saved appearance.");
                view.ThemePreference = AppearancePreference.Dark;
                view.ThemePreference = AppearancePreference.Light;
                view.ThemePreference = AppearancePreference.System;
                TestAssert.True(view.ThemePreference == AppearancePreference.System && view.IsIdle,
                    "Rapid appearance edits must apply immediately without occupying the global user-operation state.");
                await view.DisposeAsync();
                TestAssert.Equal(AppearancePreference.System, (await reopened.LoadSettingsAsync()).Appearance,
                    "Closing immediately after an appearance edit must drain its final local preference save.");
            }

            var invalid = Path.Combine(root, "invalid.json");
            const string invalidDocument = "{\"appearance\":777}";
            await File.WriteAllTextAsync(invalid, invalidDocument);
            await TestAssert.ThrowsAsync<InvalidDataException>(() => new JsonSettingsStore(invalid).LoadAsync());
            TestAssert.Equal(invalidDocument, await File.ReadAllTextAsync(invalid), "Unsupported appearance settings must remain untouched.");
            TestAssert.Equal(0, transport.Requests, "Changing or restoring appearance cannot contact a configured source.");
        }

        /// <summary>Coalesces choices made during a held save and preserves an explicit startup edit.</summary>
        /// <param name="root">The explicit generated application-data directory.</param>
        /// <returns>A task representing merged preference ownership.</returns>
        private static async Task VerifyMergedChoicesAsync(string root)
        {
            using var transport = new NoNetworkTransport();
            using var client = new HttpClient(transport);
            var store = new GateStore(Path.Combine(root, "settings.json"));
            using var workspace = new MusicWorkspace(root, client, client, store);
            await using var view = new MainViewModel(workspace, new PlaybackService(new UnavailablePlaybackBackend()), static action => action());
            view.ThemePreference = AppearancePreference.Light;
            await view.InitializeAsync();
            await view.AppearanceSettingsUpdate;
            TestAssert.Equal(AppearancePreference.Light, (await workspace.LoadSettingsAsync()).Appearance,
                "An explicit appearance choice made before startup completes must survive preference restoration.");
            store.HoldNext();
            view.ThemePreference = AppearancePreference.Dark;
            await store.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
            var audio = workspace.UpdateAudioOutputAsync(new AudioOutputPreferences { Volume = 61 });
            try
            {
                view.ThemePreference = AppearancePreference.Light;
                view.ThemePreference = AppearancePreference.System;
            }
            finally
            {
                store.Release.TrySetResult();
            }

            await Task.WhenAll(view.AppearanceSettingsUpdate, audio).WaitAsync(TimeSpan.FromSeconds(3));
            var saved = await workspace.LoadSettingsAsync();
            TestAssert.True(saved.Appearance == AppearancePreference.System && saved.AudioOutput.Volume == 61 && !saved.OnlineEnabled,
                "The final merged appearance choice cannot restore an older choice or replace a concurrent audio edit.");
            TestAssert.Equal(0, transport.Requests, "Merged appearance persistence must remain entirely local.");
        }

        /// <summary>Exercises both real appearance pickers and checks actual painted light and dark resources.</summary>
        /// <param name="root">The explicit generated library and settings directory.</param>
        /// <returns>A task representing actual theme projection and captured frames.</returns>
        private static async Task VerifyRenderingAsync(string root)
        {
            await using var session = HeadlessUnitTestSession.StartNew(typeof(AppearanceTestApplication));
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
            await session.Dispatch<bool>(async () =>
            {
                Directory.CreateDirectory(root);
                var output = Path.Combine(Environment.CurrentDirectory, ".work");
                Directory.CreateDirectory(output);
                using var transport = new NoNetworkTransport();
                using var client = new HttpClient(transport);
                using var workspace = new MusicWorkspace(root, client, client);
                var cover = Path.Combine(root, "generated-cover.png");
                using (var image = new SKBitmap(128, 128))
                {
                    image.Erase(new SKColor(181, 121, 81));
                    using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
                    await File.WriteAllBytesAsync(cover, encoded.ToArray(), timeout.Token);
                }

                var repository = new SqliteLibraryRepository(Path.Combine(root, "library.db"));
                await repository.UpsertTracksAsync([new MusicTrack
                {
                    Id = "appearance-song", Title = "A quieter kind of light", Artist = "Generated artist",
                    Audio = new MediaResource(), Cover = new MediaResource { LocalPath = cover }, Duration = TimeSpan.FromMinutes(3),
                    AudioInfo = new AudioStreamInfo { BitrateBitsPerSecond = 320000, SampleRateHz = 44100 }
                }], timeout.Token);
                await using var model = new MainViewModel(workspace, new PlaybackService(new HeadlessTests.TimelineBackend()));
                await model.InitializeAsync();
                model.ShowLibraryModeCommand.Execute(null);
                await model.HomeUpdate;
                var view = new MainView { DataContext = model };
                var window = new Window { Width = 1100, Height = 760, Content = view };
                try
                {
                    window.Show();
                    Layout(window);
                    AssertPalette(view, false);
                    Capture(window, Path.Combine(output, "ui-appearance-dark.png"), Color.Parse("#141416"));
                    view.NavigateToSection(4);
                    Layout(window);
                    var picker = view.FindControl<ArcSelect>("AppearancePicker")!;
                    Choose(picker, model, AppearancePreference.Light);
                    TestAssert.True(model.ThemePreference == AppearancePreference.Light && ArcTheme.State.Appearance == ArcAppearance.Light
                        && Application.Current!.RequestedThemeVariant == ThemeVariant.Light,
                        "Changing the real desktop appearance picker must update both Arc tokens and Avalonia's theme immediately.");
                    Layout(window);
                    AssertPalette(view, true);
                    Capture(window, Path.Combine(output, "ui-appearance-light.png"), Color.Parse("#F5F5F7"));
                    await model.AppearanceSettingsUpdate;
                    TestAssert.Equal(AppearancePreference.Light, (await workspace.LoadSettingsAsync()).Appearance,
                        "The rendered light choice must also reach the existing local settings file.");
                    view.NavigateToSection(0);
                    Layout(window);
                    Capture(window, Path.Combine(output, "ui-appearance-library-light.png"), Color.Parse("#F5F5F7"));
                    view.NavigateToSection(4);
                    Layout(window);

                    window.Width = 390;
                    window.Height = 844;
                    Layout(window);
                    var mobile = view.GetVisualDescendants().OfType<MobileMusicView>().Single();
                    var mobilePicker = mobile.GetVisualDescendants().OfType<ArcSelect>().Single(control => control.Name == "MobileAppearancePicker");
                    TestAssert.True(mobilePicker.Bounds.Height >= 44, "The phone appearance picker must retain a 44-DIP touch target.");
                    Choose(mobilePicker, model, AppearancePreference.Dark);
                    Layout(window);
                    AssertPalette(view, false);
                    Capture(window, Path.Combine(output, "ui-appearance-phone-dark.png"), Color.Parse("#141416"));
                    Choose(mobilePicker, model, AppearancePreference.Light);
                    Layout(window);
                    AssertPalette(view, true);
                    Capture(window, Path.Combine(output, "ui-appearance-phone-light.png"), Color.Parse("#F5F5F7"));
                    Choose(mobilePicker, model, AppearancePreference.System);
                    Layout(window);
                    var expectedDark = Application.Current!.PlatformSettings!.GetColorValues().ThemeVariant == PlatformThemeVariant.Dark;
                    TestAssert.True(ArcTheme.State.Appearance == ArcAppearance.System && ArcThemeManager.Current!.IsDark == expectedDark,
                        "Following the system must resolve through the host's actual platform appearance without becoming a fixed light or dark preference.");
                    await model.AppearanceSettingsUpdate;
                    TestAssert.Equal(AppearancePreference.System, (await workspace.LoadSettingsAsync()).Appearance,
                        "The phone and desktop pickers must save the same persistent system choice.");
                    TestAssert.Equal(0, transport.Requests, "Theme rendering and responsive view exchange cannot issue network requests.");
                }
                finally
                {
                    window.Close();
                }

                return true;
            }, timeout.Token);
        }

        /// <summary>Selects an actual bound appearance option while preserving the control's two-way binding.</summary>
        /// <param name="picker">The desktop or mobile selector.</param>
        /// <param name="model">The current shared preference owner.</param>
        /// <param name="preference">The requested appearance.</param>
        private static void Choose(ArcSelect picker, MainViewModel model, AppearancePreference preference)
        {
            picker.SetCurrentValue(SelectingItemsControl.SelectedItemProperty, model.AppearanceOptions.Single(option => option.Value == preference));
        }

        /// <summary>Checks neutral surfaces, readable text and distinct selection and transfer colors.</summary>
        /// <param name="view">The actual rendered music host.</param>
        /// <param name="light">Whether the light palette is expected.</param>
        private static void AssertPalette(MainView view, bool light)
        {
            var background = Brush(view, "MusicCanvasBrush");
            var panel = Brush(view, "MusicPanelBrush");
            var text = Brush(view, "MusicTextBrush");
            var muted = Brush(view, "MusicMutedBrush");
            var accent = Brush(view, "MusicAccentBrush");
            var accentText = Brush(view, "MusicAccentTextBrush");
            var selection = Brush(view, "MusicSelectionBrush");
            var download = Brush(view, "MusicDownloadBrush");
            TestAssert.Equal(Color.Parse(light ? "#F5F5F7" : "#141416"), background, "The actual theme dictionaries must publish neutral light or graphite backgrounds.");
            TestAssert.Equal(Color.Parse(light ? "#5B5BD6" : "#8B8CF8"), accent, "Both appearances must use the same restrained indigo accent family.");
            TestAssert.True(Contrast(text, panel) >= 10 && Contrast(muted, panel) >= 4.5 && Contrast(accentText, accent) >= 4.5,
                "Primary, secondary and accent-label colors must remain readable in both appearances.");
            TestAssert.True(selection != download && selection != accent && download != panel && download != accent,
                "Download fill, selected surfaces and selected borders must retain independent visual meanings.");
        }

        /// <summary>Reads a live theme brush from the actual shared view's resource chain.</summary>
        /// <param name="view">The rendered host.</param>
        /// <param name="key">The theme brush identity.</param>
        /// <returns>The resolved solid color.</returns>
        private static Color Brush(Control view, string key)
        {
            return view.TryFindResource(key, view.ActualThemeVariant, out var resource) && resource is ISolidColorBrush brush
                ? brush.Color : throw new InvalidOperationException("The appearance resource is unavailable: " + key);
        }

        /// <summary>Calculates the relative luminance ratio of two opaque UI colors.</summary>
        /// <param name="first">The foreground color.</param>
        /// <param name="second">The background color.</param>
        /// <returns>The contrast ratio between one and twenty-one.</returns>
        private static double Contrast(Color first, Color second)
        {
            var left = Luminance(first);
            var right = Luminance(second);
            return (Math.Max(left, right) + .05) / (Math.Min(left, right) + .05);
        }

        /// <summary>Converts an sRGB color into relative linear luminance.</summary>
        /// <param name="color">The opaque sRGB color.</param>
        /// <returns>The relative luminance between zero and one.</returns>
        private static double Luminance(Color color)
        {
            static double Linear(byte channel)
            {
                var value = channel / 255d;
                return value <= .04045 ? value / 12.92 : Math.Pow((value + .055) / 1.055, 2.4);
            }

            return .2126 * Linear(color.R) + .7152 * Linear(color.G) + .0722 * Linear(color.B);
        }

        /// <summary>Renders a real frame and verifies that the new palette reaches painted pixels.</summary>
        /// <param name="window">The actual rendering window.</param>
        /// <param name="path">The ignored diagnostic image destination.</param>
        /// <param name="expectedCanvas">The expected color of the exposed music canvas between outer panels.</param>
        private static void Capture(Window window, string path, Color expectedCanvas)
        {
            Layout(window);
            using var bitmap = new RenderTargetBitmap(new PixelSize((int)window.Width, (int)window.Height));
            bitmap.Render(window);
            bitmap.Save(path, PngBitmapEncoderOptions.Default);
            using var image = SKBitmap.Decode(path);
            var pixel = image.GetPixel(1, image.Height / 2);
            TestAssert.True(pixel.Red == expectedCanvas.R && pixel.Green == expectedCanvas.G && pixel.Blue == expectedCanvas.B,
                "The chosen appearance must reach the actual rendered canvas, rather than changing only the selector or view-model value.");
        }

        /// <summary>Completes layout and real rendering after synchronous theme changes.</summary>
        /// <param name="window">The current rendering window.</param>
        private static void Layout(Window window)
        {
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
        }

        /// <summary>Holds one local save to expose rapid-edit ownership without relying on timing thresholds.</summary>
        private sealed class GateStore : ISettingsStore
        {
            /// <summary>Owns the real atomic JSON settings fixture.</summary>
            private readonly JsonSettingsStore _inner;
            /// <summary>Marks the next save for explicit release.</summary>
            private int _hold;
            /// <summary>Creates the local write gate.</summary>
            /// <param name="path">The generated settings destination.</param>
            public GateStore(string path)
            {
                _inner = new JsonSettingsStore(path);
            }
            /// <summary>Signals that the chosen local save is pending.</summary>
            public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            /// <summary>Releases the chosen local save.</summary>
            public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            /// <summary>Arms the next local save.</summary>
            public void HoldNext()
            {
                Interlocked.Exchange(ref _hold, 1);
            }
            /// <inheritdoc />
            public Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default)
            {
                return _inner.LoadAsync(cancellationToken);
            }
            /// <inheritdoc />
            public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
            {
                if (Interlocked.Exchange(ref _hold, 0) == 1)
                {
                    Started.TrySetResult();
                    await Release.Task.WaitAsync(cancellationToken);
                }

                await _inner.SaveAsync(settings, cancellationToken);
            }
        }

        /// <summary>Rejects external traffic during appearance persistence and rendering.</summary>
        private sealed class NoNetworkTransport : HttpMessageHandler
        {
            /// <summary>Gets the number of attempted remote requests.</summary>
            public int Requests { get; private set; }
            /// <inheritdoc />
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                Requests++;
                throw new InvalidOperationException("Appearance fixtures cannot contact a server.");
            }
        }
    }

    /// <summary>Loads actual Arc palettes while keeping appearance pixel checks deterministic.</summary>
    public sealed class AppearanceTestApplication : Application
    {
        /// <summary>Retains native virtualization as in the production desktop host.</summary>
        public override void Initialize()
        {
            Styles.Add(new FluentTheme());
        }

        /// <summary>Builds the actual shared Arc theme without disabling appearance projection.</summary>
        /// <returns>The configured in-memory Skia application.</returns>
        public static AppBuilder BuildAvaloniaApp()
        {
            return AppBuilder.Configure<AppearanceTestApplication>().UseSkia()
                .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
                .UseMajMusicArc(ArcMotionLevel.Off);
        }
    }
}

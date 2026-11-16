#nullable enable

using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using MajMusic.Core.Models;
using MajMusic.Core.Playback;
using MajMusic.Presentation.ViewModels;
using SkiaSharp;

namespace MajMusic.Tests
{
    /// <summary>Checks obsolete catalog collection and bounded selected artwork without shrinking the visible cache or changing card quality.</summary>
    internal static class PresentationRetentionTests
    {
        /// <summary>Runs weak-reference catalog regressions and real high-resolution image ownership checks.</summary>
        /// <returns>A task representing the complete presentation-retention suite.</returns>
        public static async Task RunAsync()
        {
            await using var session = HeadlessUnitTestSession.StartNew(typeof(HeadlessTestApplication));
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(40));
            await session.Dispatch<bool>(async () =>
            {
                await ValidateCatalogGenerationReleaseAsync();
                var root = Path.Combine(Path.GetTempPath(), "MajMusic-presentation-retention-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(root);
                try
                {
                    var workspace = new LibraryExperienceTests.ExperienceWorkspace();
                    await using var model = new MainViewModel(workspace, new PlaybackService(new UnavailablePlaybackBackend()), static action => action());
                    var releasedCover = await ValidateSelectedArtworkAsync(root, workspace, model);
                    await Task.Yield();
                    await Task.Run(static () => { });
                    CollectReleasedObjects();
                    TestAssert.True(model.Cover is null && !releasedCover.IsAlive,
                        "Clearing selection must release its final owned artwork after the producing async frame returns, while its view model remains alive.");
                    GC.KeepAlive(model);
                    ValidateUnknownHeaderFallback(root);
                }
                finally
                {
                    var parent = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                    if (!Path.GetFullPath(root).StartsWith(parent, StringComparison.OrdinalIgnoreCase)
                        || !Path.GetFileName(root).StartsWith("MajMusic-presentation-retention-", StringComparison.Ordinal))
                    {
                        throw new InvalidOperationException("Refusing to clean outside the generated presentation-retention fixture.");
                    }

                    Directory.Delete(root, true);
                }

                return true;
            }, timeout.Token);
        }

        /// <summary>Reuses stable rows while proving that each obsolete whole-catalog lazy collection can actually be collected.</summary>
        /// <returns>A task representing descriptor replacement and real GC ownership assertions.</returns>
        private static async Task ValidateCatalogGenerationReleaseAsync()
        {
            var workspace = new LibraryExperienceTests.ExperienceWorkspace();
            workspace.Library.AddRange(Enumerable.Range(0, 6000).Select(index => new MusicTrack
            {
                Id = "retention-" + index.ToString("D5"),
                Title = "Song " + index.ToString("D5"),
                Audio = new MediaResource()
            }));
            await using var model = new MainViewModel(workspace, new PlaybackService(new UnavailablePlaybackBackend()), static action => action());
            await model.InitializeAsync();
            model.ShowLibraryModeCommand.Execute(null);
            await model.HomeUpdate;
            var stableRow = model.BrowseRows[0];
            var stableCard = model.CardTracks[0];
            var obsolete = new List<WeakReference>();
            for (var generation = 0; generation < 8; generation++)
            {
                obsolete.Add(CaptureCatalog(model));
                var changed = 100 + generation * model.BrowseColumns;
                workspace.Library[changed] = workspace.Library[changed] with { Artist = "Updated generation " + generation };
                model.ShowLibraryModeCommand.Execute(null);
                await model.HomeUpdate;
                TestAssert.True(ReferenceEquals(stableRow, model.BrowseRows[0]) && ReferenceEquals(stableCard, model.CardTracks[0]),
                    "Refreshing another row must keep the unchanged row and its existing song presentation stable.");
            }

            CollectReleasedObjects();
            TestAssert.True(obsolete.All(reference => !reference.IsAlive),
                "Unchanged virtual rows cannot retain any obsolete full-catalog lazy collection after a newer snapshot is published.");
            TestAssert.True(model.CardTracks.Count == 6000 && model.MaterializedCardCount == 1 && ReferenceEquals(stableRow.Tracks[0], stableCard),
                "Catalog collection cannot require eager card construction or change the current row's song identity.");
        }

        /// <summary>Checks bounded 4K and narrow artwork, obsolete late requests, selected-cover release and unchanged source files.</summary>
        /// <param name="root">The generated image directory.</param>
        /// <param name="workspace">The isolated controlled artwork resolver.</param>
        /// <param name="model">The view model retained by the caller throughout the final release assertion.</param>
        /// <returns>A task yielding a nonowning reference checked only after this producer's async frame has exited.</returns>
        private static async Task<WeakReference> ValidateSelectedArtworkAsync(string root, LibraryExperienceTests.ExperienceWorkspace workspace, MainViewModel model)
        {
            var square = WriteCover(root, "square.png", 4096, 4096);
            var narrow = WriteCover(root, "narrow.png", 128, 8192);
            var signatures = new[] { Signature(square), Signature(narrow) };
            var delayed = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
            workspace.ResolveCoverOverride = (track, _) => track.Id == "late" ? delayed.Task : Task.FromResult(track.Cover?.LocalPath);
            model.SelectedTrack = Track("large", square);
            await model.LoadCoverAsync();
            TestAssert.Equal(new PixelSize(512, 512), SelectedCoverSize(model),
                "The 120/144-DIP inspector cannot retain a full 4K decoded original behind its already bounded transition image.");
            using (var card = CoverThumbnailDecoder.Decode(square))
            {
                TestAssert.Equal(new PixelSize(256, 256), card!.PixelSize, "Ordinary song cards must preserve their existing 256-pixel thumbnail resolution.");
            }

            var original = CaptureCover(model);
            model.SelectedTrack = Track("late", square);
            var late = model.LoadCoverAsync();
            model.SelectedTrack = Track("current", narrow);
            await model.LoadCoverAsync();
            delayed.TrySetResult(square);
            await late;
            TestAssert.Equal(new PixelSize(8, 512), SelectedCoverSize(model),
                "The latest narrow cover must retain its aspect ratio and long-edge bound after a canceled old request returns.");
            CollectReleasedObjects();
            TestAssert.True(!original.IsAlive, "The view model cannot retain an obsolete selected bitmap after another selection commits.");
            for (var index = 0; index < 8; index++)
            {
                model.SelectedTrack = Track("rapid-" + index, index % 2 == 0 ? square : narrow);
                await model.LoadCoverAsync();
                var size = SelectedCoverSize(model);
                TestAssert.True(size.Width <= 512 && size.Height <= 512,
                    "Repeated selection changes must retain only bounded selected-cover pixels.");
            }

            var last = CaptureCover(model);
            model.SelectedTrack = null;
            await model.LoadCoverAsync();
            TestAssert.True(model.Cover is null, "Clearing selection must immediately clear the visible selected artwork.");
            TestAssert.True(signatures.SequenceEqual([Signature(square), Signature(narrow)]),
                "Inspector decoding cannot rewrite or copy over the original music-directory artwork.");
            return last;
        }

        /// <summary>Preserves formats accepted by Avalonia but absent from the lightweight thumbnail header reader.</summary>
        /// <param name="root">The generated image directory.</param>
        private static void ValidateUnknownHeaderFallback(string root)
        {
            var png = WriteCover(root, "icon-source.png", 32, 32);
            var bytes = File.ReadAllBytes(png);
            var path = Path.Combine(root, "fallback.ico");
            using (var stream = File.Create(path))
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write((ushort)0);
                writer.Write((ushort)1);
                writer.Write((ushort)1);
                writer.Write((byte)32);
                writer.Write((byte)32);
                writer.Write((byte)0);
                writer.Write((byte)0);
                writer.Write((ushort)1);
                writer.Write((ushort)32);
                writer.Write(bytes.Length);
                writer.Write(22);
                writer.Write(bytes);
            }

            using var baseline = new Bitmap(path);
            using var bounded = CoverThumbnailDecoder.DecodeSelected(path);
            TestAssert.Equal(baseline.PixelSize, bounded.PixelSize,
                "A supported ICO cover with an unrecognized fast header must still display through the ordinary decoder fallback.");
        }

        /// <summary>Creates a weak catalog reference without extending its lifetime through a caller's async state machine.</summary>
        /// <param name="model">The current catalog owner.</param>
        /// <returns>A nonowning reference to the old lazy collection.</returns>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static WeakReference CaptureCatalog(MainViewModel model)
        {
            return new WeakReference(model.CardTracks);
        }

        /// <summary>Captures selected artwork without a strong bitmap local surviving across later awaits.</summary>
        /// <param name="model">The current selected-artwork owner.</param>
        /// <returns>A nonowning reference to the selected image.</returns>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static WeakReference CaptureCover(MainViewModel model)
        {
            return new WeakReference(model.Cover);
        }

        /// <summary>Reads pixel bounds without letting the caller's async frame retain a temporary strong bitmap reference.</summary>
        /// <param name="model">The selected-artwork owner.</param>
        /// <returns>Only the value-type pixel bounds.</returns>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static PixelSize SelectedCoverSize(MainViewModel model)
        {
            return model.Cover?.PixelSize ?? default;
        }

        /// <summary>Runs actual collection after all tested publications and their awaited resource operations finish.</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void CollectReleasedObjects()
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }

        /// <summary>Creates one generated selected-song descriptor.</summary>
        /// <param name="id">The stable selection identity.</param>
        /// <param name="path">The read-only generated artwork path.</param>
        /// <returns>The fixture song.</returns>
        private static MusicTrack Track(string id, string path)
        {
            return new MusicTrack { Id = id, Title = id, Audio = new MediaResource(), Cover = new MediaResource { LocalPath = path } };
        }

        /// <summary>Creates a compressed solid image while disposing its full-size generation surface before decoding is tested.</summary>
        /// <param name="root">The generated image directory.</param>
        /// <param name="name">The generated filename.</param>
        /// <param name="width">The source pixel width.</param>
        /// <param name="height">The source pixel height.</param>
        /// <returns>The generated complete path.</returns>
        private static string WriteCover(string root, string name, int width, int height)
        {
            using var pixels = new SKBitmap(width, height);
            pixels.Erase(SKColors.CornflowerBlue);
            using var encoded = pixels.Encode(SKEncodedImageFormat.Png, 100);
            var path = Path.Combine(root, name);
            File.WriteAllBytes(path, encoded.ToArray());
            return path;
        }

        /// <summary>Detects source-byte or timestamp mutation during selected-cover decoding.</summary>
        /// <param name="path">The read-only fixture image.</param>
        /// <returns>The stable source signature.</returns>
        private static string Signature(string path)
        {
            return new FileInfo(path).LastWriteTimeUtc.Ticks + "|" + Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));
        }
    }
}

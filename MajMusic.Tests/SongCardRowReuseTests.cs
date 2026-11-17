#nullable enable

using System.Collections;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MajMusic.Core.Models;
using MajMusic.Core.Playback;
using MajMusic.Presentation.Theme;
using MajMusic.Presentation.ViewModels;
using MajMusic.Presentation.Views;
using SkiaSharp;

namespace MajMusic.Tests
{
    /// <summary>Checks real song-control identity through row rebinding and native virtualized list resets.</summary>
    internal static partial class SongCardRowReuseTests
    {
        /// <summary>Runs isolated real-template reuse and lifecycle checks without audio or network requests.</summary>
        /// <returns>A task representing the rendered regressions.</returns>
        public static Task RunAsync() => HeadlessProcessIsolation.RunAsync("song-card-row-reuse", RunHeadlessAsync);

        /// <summary>Creates generated covers and exercises recycled controls on the actual headless dispatcher.</summary>
        /// <returns>A task representing the complete reuse fixture.</returns>
        private static async Task RunHeadlessAsync()
        {
            await using var session = HeadlessUnitTestSession.StartNew(typeof(HeadlessFullMotionTestApplication));
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(35));
            await session.Dispatch<bool>(async () =>
            {
                var directory = Path.Combine(Path.GetTempPath(), "MajMusic-row-reuse-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(directory);
                try
                {
                    var red = WriteRowCover(directory, "red.png", SKColors.IndianRed);
                    var green = WriteRowCover(directory, "green.png", SKColors.SeaGreen);
                    await ValidateRowAndNativeResetAsync(red, green);
                    await ValidateBrowserReuseAsync(red, green);
                }
                finally
                {
                    Directory.Delete(directory, true);
                }
                return true;
            }, timeout.Token);
        }

        /// <summary>Reuses the same child trees while changing visible data, canceling a play gesture and clearing spare columns.</summary>
        /// <param name="red">The first generated local cover.</param>
        /// <param name="green">The replacement generated local cover.</param>
        /// <returns>A task representing actual row and native-reset assertions.</returns>
        private static async Task ValidateRowAndNativeResetAsync(string red, string green)
        {
            var workspace = new CardDownloadPresentationTests.DownloadWorkspace();
            for (var index = 0; index < 40; index++)
            {
                workspace.Inner.Library.Add(new MusicTrack
                {
                    Id = "row-reuse-" + index,
                    Title = "Row song " + index,
                    Artist = "Generated artist",
                    SourceId = "row-fixture",
                    Duration = TimeSpan.FromMinutes(3),
                    AudioInfo = new AudioStreamInfo { BitrateBitsPerSecond = 320000, SampleRateHz = 44100 },
                    Audio = new MediaResource { LocalPath = "generated-row-audio.mp3" },
                    Cover = new MediaResource { LocalPath = index < 10 ? red : green }
                });
            }
            await using var model = new MainViewModel(workspace, new PlaybackService(new UnavailablePlaybackBackend()));
            await model.InitializeAsync();
            model.ShowLibraryModeCommand.Execute(null);
            await model.HomeUpdate;
            var first = new CountingRowCards(model.CardTracks, 0, 3);
            var second = new CountingRowCards(model.CardTracks, 10, 3);
            var tail = new CountingRowCards(model.CardTracks, 20, 3);
            var inactive = new CountingRowCards(model.CardTracks, 30, 3);
            var slot = new SongCardRow(first, 3);
            var row = new SongCardRowView { Columns = 3, DataContext = slot, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top };
            var host = new Border { Background = Brushes.Black, Child = row };
            var window = new Window { Width = 660, Height = 400, Content = host };
            try
            {
                TestAssert.True(first.Requested.Count == 0 && row.Children.Count == 0,
                    "An unattached stable slot must not materialize its lazy songs merely because its data context was assigned.");
                window.Show();
                Pump(window);
                await UntilAsync(window, () => first.Materialized.All(card => card.Cover is not null), "The first visible row must acquire its real covers.");
                var controls = row.Children.OfType<SongCard>().ToArray();
                TestAssert.True(controls.Length == 3 && model.MaterializedCardCount == 3 && first.Requested.SetEquals([0, 1, 2]),
                    "A row must materialize only its three indexed songs, without walking the complete catalog.");
                TestAssert.Equal(SKColors.IndianRed, RowCoverPixel(window, controls[0]), "The first row must actually paint its original artwork.");

                var gesture = controls[0].FindControl<CoverPlayGesture>("CardQuickPlay")!;
                var accidentalPlays = 0;
                gesture.PlayRequested += (_, _) => accidentalPlays++;
                gesture.Focus();
                window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
                window.KeyReleaseQwerty(PhysicalKey.Enter, RawInputModifiers.None);
                await UntilAsync(window, () => gesture.Progress > 0 && !gesture.TransitionUpdate.IsCompleted,
                    "The recycle fixture must interrupt a real quick-play animation.");
                slot.Update(second, 3);
                Pump(window);
                await UntilAsync(window, () => second.Materialized.Count == 3 && second.Materialized.All(card => card.Cover is not null),
                    "A rebound row must load its replacement artwork without constructing another song tree.");
                await FramesAsync(window, 700);
                TestAssert.True(accidentalPlays == 0 && gesture.Progress == 0 && gesture.TransitionUpdate.IsCompleted,
                    "A quick-play action belonging to the recycled row must never play the replacement song.");
                TestAssert.True(ReferenceEquals(row.DataContext, slot) && row.Children.Cast<SongCard>().SequenceEqual(controls)
                    && first.Materialized.All(card => card.Cover is null)
                    && controls[0].GetVisualDescendants().OfType<TextBlock>().Any(text => text.Text == second[0].Track.Title),
                    "Rebinding must retain the original controls while releasing old covers and updating the rendered title.");
                TestAssert.Equal(SKColors.SeaGreen, RowCoverPixel(window, controls[0]), "The reused control must actually paint the replacement cover.");

                row.DataContext = null;
                Pump(window);
                TestAssert.True(row.Children.Count == 3 && row.Children.Cast<SongCard>().All(card => card.DataContext is null && !card.IsVisible && !card.IsBrowsingActive)
                    && second.Materialized.All(card => card.Cover is null),
                    "Clearing recycled row data must retain only hidden static controls and release all old media interests.");
                var tailSlot = new SongCardRow(tail, 1);
                row.DataContext = tailSlot;
                Pump(window);
                TestAssert.True(tail.Requested.SetEquals([0]) && row.Children.Cast<SongCard>().Count(card => card.IsVisible) == 1
                    && row.Children.Cast<SongCard>().Skip(1).All(card => card.DataContext is null && !card.IsBrowsingActive),
                    "Shrinking a row must materialize only its visible column and must not expose or activate retained spare controls.");
                TestAssert.True(Math.Abs(row.Bounds.Height - controls[0].DesiredSize.Height) < 1,
                    "Hidden spare columns must not increase the UniformGrid's single-row height.");
                tailSlot.Update(tail, 3);
                Pump(window);
                TestAssert.True(row.Columns == 3 && row.Children.Cast<SongCard>().SequenceEqual(controls) && tail.Requested.SetEquals([0, 1, 2]),
                    "Growing the row again must reuse its three original children instead of constructing new cards.");
                row.IsBrowsingActive = false;
                var materialized = model.MaterializedCardCount;
                tailSlot.Update(inactive, 3);
                Pump(window);
                TestAssert.True(inactive.Requested.Count == 0 && model.MaterializedCardCount == materialized
                    && row.Children.Cast<SongCard>().All(card => card.DataContext is null && !card.IsVisible && !card.IsBrowsingActive)
                    && row.DesiredSize.Height > 0,
                    "An inactive slot update must clear retired cards and retain an estimated extent without indexing its new lazy slice.");
                window.Content = null;
                tailSlot.Update(second, 3);
                tailSlot.Update(inactive, 2);
                row.IsBrowsingActive = true;
                TestAssert.True(inactive.Requested.Count == 0 && model.MaterializedCardCount == materialized,
                    "Detached slot changes must not materialize either a superseded or the newest lazy source.");
                window.Content = host;
                Pump(window);
                TestAssert.True(row.Columns == 2 && inactive.Requested.SetEquals([0, 1])
                    && row.Children.Cast<SongCard>().Take(2).All(card => card.IsVisible && card.IsBrowsingActive)
                    && controls[2].DataContext is null && !controls[2].IsVisible,
                    "Reattachment must bind only the latest slot slice and column count, without reviving a retired catalog.");
                row.DataContext = null;

                var items = new ResetRowItems();
                items.Replace([new SongCardRow(first, 3), new SongCardRow(second, 3), new SongCardRow(tail, 3)]);
                var list = new BrowseCardListBox { ItemsSource = items, AutoScrollToSelectedItem = false };
                window.Styles.Add(new Style(selector => selector.OfType<ListBoxItem>())
                {
                    Setters = { new Setter(Control.HeightProperty, double.NaN), new Setter(ListBoxItem.PaddingProperty, default(Thickness)) }
                });
                window.Content = list;
                Pump(window);
                var before = list.GetVisualDescendants().OfType<SongCard>().ToHashSet();
                TestAssert.True(before.Count > 0, "The native virtualizing panel must realize real row controls before its reset.");
                items.Replace([new SongCardRow(tail, 3), new SongCardRow(first, 3), new SongCardRow(second, 3)]);
                Pump(window);
                var after = list.GetVisualDescendants().OfType<SongCard>().Where(card => card.IsEffectivelyVisible).ToArray();
                TestAssert.True(after.Length > 0 && after.All(before.Contains)
                    && list.GetRealizedContainers().OfType<ListBoxItem>().All(container => container.Content is SongCardRowView),
                    "A real native ItemsReset must retain the original SongCard trees through ClearItemContainer and the separate song-row recycle key.");
                TestAssert.True(after[0].DataContext is SongCardViewModel updated && updated.Track.Id == tail[0].Track.Id,
                    "Native reset must rebind the retained controls to the new ordered source instead of showing their previous songs.");
            }
            finally
            {
                row.DataContext = null;
                window.Content = null;
                window.Close();
            }
        }

        /// <summary>Creates only generated opaque image data for artwork-rebinding checks.</summary>
        /// <param name="directory">The isolated fixture directory.</param>
        /// <param name="name">The generated PNG filename.</param>
        /// <param name="color">The diagnostic cover color.</param>
        /// <returns>The generated local cover path.</returns>
        private static string WriteRowCover(string directory, string name, SKColor color)
        {
            var path = Path.Combine(directory, name);
            using var image = new SKBitmap(64, 64);
            image.Erase(color);
            using var data = image.Encode(SKEncodedImageFormat.Png, 100);
            using var file = File.Create(path);
            data.SaveTo(file);
            return path;
        }

        /// <summary>Reads actual painted artwork away from the cover-corner play affordance.</summary>
        /// <param name="window">The rendered viewport.</param>
        /// <param name="card">The original or rebound song control.</param>
        /// <returns>The diagnostic opaque cover pixel.</returns>
        private static SKColor RowCoverPixel(Window window, SongCard card)
        {
            using var frame = window.CaptureRenderedFrame()!;
            using var stream = new MemoryStream();
            frame.Save(stream, PngBitmapEncoderOptions.Default);
            using var pixels = SKBitmap.Decode(stream.ToArray());
            var point = card.TranslatePoint(new Point(12, 12), window)!.Value;
            return pixels.GetPixel((int)Math.Round(point.X * window.RenderScaling), (int)Math.Round(point.Y * window.RenderScaling));
        }

        /// <summary>Waits for observed asynchronous resources, without using elapsed time as a performance assertion.</summary>
        /// <param name="window">The current real view.</param>
        /// <param name="condition">The completed resource or UI state.</param>
        /// <param name="message">The failure context for a stalled operation.</param>
        /// <returns>A task representing bounded observation.</returns>
        private static async Task UntilAsync(Window window, Func<bool> condition, string message)
        {
            var elapsed = Stopwatch.StartNew();
            while (!condition() && elapsed.Elapsed < TimeSpan.FromSeconds(4))
            {
                await Task.Delay(10);
                Pump(window);
            }
            TestAssert.True(condition(), message);
        }

        /// <summary>Lets obsolete native play callbacks pass their original finite gesture lifetime.</summary>
        /// <param name="window">The rendered viewport.</param>
        /// <param name="milliseconds">The display interval to observe.</param>
        /// <returns>A task representing real native frames.</returns>
        private static async Task FramesAsync(Window window, int milliseconds)
        {
            var elapsed = Stopwatch.StartNew();
            while (elapsed.ElapsedMilliseconds < milliseconds)
            {
                await Task.Delay(12);
                Pump(window);
            }
        }

        /// <summary>Commits actual bindings, layout and Skia rendering.</summary>
        /// <param name="window">The current real viewport.</param>
        private static void Pump(Window window)
        {
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
        }

        /// <summary>Counts indexed row access without enumerating or materializing another catalog entry.</summary>
        private sealed class CountingRowCards(IReadOnlyList<SongCardViewModel> source, int start, int count) : IReadOnlyList<SongCardViewModel>
        {
            /// <summary>Gets the indexed positions requested from this bounded row.</summary>
            public HashSet<int> Requested { get; } = new();
            /// <summary>Gets the materialized current-row cards independently of repeated index reads.</summary>
            public List<SongCardViewModel> Materialized { get; } = new();
            /// <inheritdoc />
            public int Count => count;
            /// <inheritdoc />
            public SongCardViewModel this[int index]
            {
                get
                {
                    if (index < 0 || index >= count)
                    {
                        throw new ArgumentOutOfRangeException(nameof(index));
                    }
                    var card = source[start + index];
                    if (Requested.Add(index))
                    {
                        Materialized.Add(card);
                    }
                    return card;
                }
            }
            /// <inheritdoc />
            public IEnumerator<SongCardViewModel> GetEnumerator()
            {
                for (var index = 0; index < count; index++)
                {
                    yield return this[index];
                }
            }
            /// <inheritdoc />
            IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
        }

        /// <summary>Replaces native list data with the same single Reset event produced by catalog snapshots.</summary>
        private sealed class ResetRowItems : ObservableCollection<object>
        {
            /// <summary>Publishes a genuinely reordered set of source rows to the native virtualizing panel.</summary>
            /// <param name="rows">The complete next row sequence.</param>
            public void Replace(IReadOnlyList<object> rows)
            {
                Items.Clear();
                foreach (var row in rows)
                {
                    Items.Add(row);
                }
                OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
                OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
                OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
            }
        }
    }
}

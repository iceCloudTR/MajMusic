#nullable enable

using System.Collections.Specialized;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.VisualTree;
using MajMusic.Core.Models;
using MajMusic.Core.Playback;
using MajMusic.Presentation.ViewModels;
using MajMusic.Presentation.Views;
using TKCode.Arc.Motion;

namespace MajMusic.Tests
{
    /// <summary>Checks actual virtualized browser recycling independently of song-view-model reuse.</summary>
    internal static partial class SongCardRowReuseTests
    {
        /// <summary>Switches disjoint playlists through a real browser and checks recycled children, input and responsive grouping.</summary>
        /// <param name="redCover">A generated artwork file for the first playlist.</param>
        /// <param name="greenCover">A generated artwork file for the second playlist.</param>
        /// <returns>A task representing the complete browser recycling checks.</returns>
        private static async Task ValidateBrowserReuseAsync(string redCover, string greenCover)
        {
            var workspace = new CardDownloadPresentationTests.DownloadWorkspace();
            for (var index = 0; index < 100; index++)
            {
                workspace.Inner.Library.Add(new MusicTrack
                {
                    Id = "row-browser-" + index.ToString("D3"),
                    Title = "Recycled browser song " + index.ToString("D3"),
                    Artist = "Generated artist",
                    Audio = new MediaResource(),
                    Cover = new MediaResource { LocalPath = index < 50 ? redCover : greenCover },
                    Duration = TimeSpan.FromMinutes(3),
                    AudioInfo = new AudioStreamInfo { BitrateBitsPerSecond = 320000, SampleRateHz = 44100 }
                });
            }

            foreach (var group in new[] { 0, 1 })
            {
                var id = "row-browser-playlist-" + group;
                workspace.Inner.LocalPlaylists[id] = new MusicPlaylist
                {
                    Id = id,
                    Name = "Recycled playlist " + group,
                    TrackIds = workspace.Inner.Library.Skip(group * 50).Take(50).Select(track => track.Id).ToArray()
                };
            }

            await using var model = new MainViewModel(workspace, new PlaybackService(new UnavailablePlaybackBackend()));
            await model.InitializeAsync();
            await model.OpenPlaylistFromMenuAsync(model.Playlists.Single(item => item.Id == "row-browser-playlist-0"));
            var view = new MainView { DataContext = model };
            var window = new Window { Content = view, Width = 1100, Height = 800 };
            ArcMotion.SetLevel(window, ArcMotionLevel.Off);
            var resets = 0;
            NotifyCollectionChangedEventHandler observeReset = (_, change) =>
            {
                if (change.Action == NotifyCollectionChangedAction.Reset)
                {
                    resets++;
                }
            };
            try
            {
                window.Show();
                Pump(window);
                await UntilAsync(window, () => !model.IsBrowsePlaceholderVisible && model.CardTracks.Count == 50
                    && BrowserCards(view.GetVisualDescendants().OfType<BrowseTracksView>().Single()).Any(card => ((SongCardViewModel)card.DataContext!).Cover is not null),
                    "The first real playlist must realize its local artwork before recording control references.");
                var browser = view.GetVisualDescendants().OfType<BrowseTracksView>().Single();
                var list = browser.ActiveList;
                var original = new HashSet<SongCard>(BrowserCards(browser), ReferenceEqualityComparer.Instance);
                TestAssert.True(original.Count > 0 && list.GetVisualDescendants().OfType<VirtualizingStackPanel>().Any(),
                    "The fixture must use actual realized song controls under the native virtualizing panel.");
                model.BrowseRows.CollectionChanged += observeReset;
                await model.OpenPlaylistFromMenuAsync(model.Playlists.Single(item => item.Id == "row-browser-playlist-1"));
                await UntilAsync(window, () => !model.IsBrowsePlaceholderVisible && model.CardTracks.Count == 50
                    && BrowserCards(browser).Any(card => ((SongCardViewModel)card.DataContext!).Track.Id == "row-browser-050"),
                    "The second disjoint playlist must replace the actual visible catalog.");
                var replacement = BrowserCards(browser);
                TestAssert.True(resets == 0 && replacement.Count > 0 && replacement.All(original.Contains),
                    $"A real catalog change must update stable row slots without a native Reset: resets={resets}, original={original.Count}, replacement={replacement.Count}, reused={replacement.Count(original.Contains)}.");
                TestAssert.True(replacement.All(card => int.Parse(((SongCardViewModel)card.DataContext!).Track.Id[12..]) >= 50),
                    "Every reused visible card must own the replacement playlist rather than its previous row.");

                list.Focus();
                window.KeyPressQwerty(PhysicalKey.End, RawInputModifiers.None);
                window.KeyReleaseQwerty(PhysicalKey.End, RawInputModifiers.None);
                Pump(window);
                TestAssert.Equal("row-browser-099", model.SelectedBrowseTrack?.Id,
                    "End navigation must still select the last song after row-container recycling.");
                var looseColumns = model.BrowseColumns;
                model.SelectedCardLayout = SongCardLayout.Small;
                Pump(window);
                await UntilAsync(window, () => model.BrowseColumns > looseColumns,
                    "Desktop Small must regroup the real virtualized rows into more columns.");
                ValidateBrowserRows(browser, model.BrowseColumns, SongCardLayout.Small);

                var originalSource = list.ItemsSource;
                try
                {
                    list.ItemsSource = new object[] { model.BrowseRows[0], new RecommendationFooterRow(model) };
                    Pump(window);
                    list.ScrollIntoView(list.Items[1]!);
                    Pump(window);
                    var musicContainer = (ListBoxItem)list.ContainerFromIndex(0)!;
                    var footerContainer = (ListBoxItem)list.ContainerFromIndex(1)!;
                    TestAssert.True(musicContainer.Content is SongCardRowView && footerContainer.Content is RecommendationFooterRow
                        && !footerContainer.GetVisualDescendants().OfType<SongCard>().Any(),
                        "Music rows must keep their fixed song panel while pagination uses a separate ordinary container and template.");
                }
                finally
                {
                    list.ItemsSource = originalSource;
                    Pump(window);
                }

                window.Width = 390;
                window.Height = 844;
                Pump(window);
                await UntilAsync(window, () => view.IsMobileLayout && model.BrowseColumns == 2,
                    "A phone Small browser must retain its two-column grouping.");
                var mobileBrowser = view.GetVisualDescendants().OfType<BrowseTracksView>().Single();
                ValidateBrowserRows(mobileBrowser, 2, SongCardLayout.Small);
                model.SelectedCardLayout = SongCardLayout.Loose;
                Pump(window);
                await UntilAsync(window, () => model.BrowseColumns == 1,
                    "A phone Loose browser must return to one real song per row.");
                ValidateBrowserRows(mobileBrowser, 1, SongCardLayout.Loose);
                TestAssert.True(model.SelectedBrowseTrack?.Id == "row-browser-099",
                    "Responsive row regrouping must preserve the song selected through keyboard navigation.");
            }
            finally
            {
                model.BrowseRows.CollectionChanged -= observeReset;
                window.Close();
            }
        }

        /// <summary>Collects attached active child controls without materializing any offscreen song view models.</summary>
        /// <param name="browser">The currently rendered production browser.</param>
        /// <returns>The real visible child-control prefix.</returns>
        private static IReadOnlyList<SongCard> BrowserCards(BrowseTracksView browser)
        {
            return browser.ActiveList.GetVisualDescendants().OfType<SongCard>()
                .Where(card => card.IsVisible && card.IsBrowsingActive && card.DataContext is SongCardViewModel).ToArray();
        }

        /// <summary>Checks responsive row geometry against actual active children rather than a backing collection count.</summary>
        /// <param name="browser">The current desktop or phone browser.</param>
        /// <param name="columns">The expected number of equal-width song columns.</param>
        /// <param name="layout">The expected density applied to each existing child.</param>
        private static void ValidateBrowserRows(BrowseTracksView browser, int columns, SongCardLayout layout)
        {
            var rows = browser.ActiveList.GetVisualDescendants().OfType<SongCardRowView>()
                .Where(row => row.IsEffectivelyVisible && row.DataContext is SongCardRow).ToArray();
            TestAssert.True(rows.Length > 0, "Responsive grouping must retain actual realized music rows.");
            foreach (var row in rows)
            {
                var context = (SongCardRow)row.DataContext!;
                var children = row.Children.OfType<SongCard>().Where(card => card.IsVisible && card.DataContext is SongCardViewModel).ToArray();
                TestAssert.True(row.Columns == columns && row.Rows == 1 && children.Length == context.Tracks.Count
                    && children.Length <= columns && children.All(card => card.CardLayout == layout && card.IsBrowsingActive),
                    "Each recycled row must expose only its current bounded child prefix with the requested responsive density.");
            }
        }
    }
}

#nullable enable

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ArcSelect = TKCode.Arc.Select;
using ArcTextarea = TKCode.Arc.Textarea;

namespace MajMusic.Tests
{
    /// <summary>Checks global reserved scroll tracks using real viewer, list, editor and popup templates.</summary>
    internal static class ScrollGutterLayoutTests
    {
        /// <summary>Checks compact and desktop geometry, virtualization, wheel input and actual thumb dragging.</summary>
        /// <returns>A task representing the isolated real-template regressions.</returns>
        public static async Task RunAsync()
        {
            await using var session = HeadlessUnitTestSession.StartNew(typeof(HeadlessTestApplication));
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await session.Dispatch<bool>(async () =>
            {
                var plain = new ScrollViewer
                {
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    Content = new Border { Height = 1000, Background = Brushes.DarkSlateBlue }
                };
                var bothAxes = new ScrollViewer
                {
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    Content = new Border { Width = 1000, Height = 1000, Background = Brushes.DarkSlateGray }
                };
                var list = new ListBox { ItemsSource = Enumerable.Range(0, 400).Select(index => "Virtual row " + index).ToArray() };
                ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Disabled);
                ScrollViewer.SetVerticalScrollBarVisibility(list, ScrollBarVisibility.Auto);
                var text = string.Join("\n", Enumerable.Range(0, 60).Select(index => "A complete right-hand text edge " + index));
                var editor = new TextBox { Text = text, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap };
                ScrollViewer.SetHorizontalScrollBarVisibility(editor, ScrollBarVisibility.Disabled);
                ScrollViewer.SetVerticalScrollBarVisibility(editor, ScrollBarVisibility.Auto);
                var textarea = new ArcTextarea { Text = text, FrameHeight = 130, FrameMinHeight = 130, IsResizable = false };
                var select = new ArcSelect
                {
                    ItemsSource = Enumerable.Range(0, 60).Select(index => "Popup row " + index).ToArray(),
                    MaxDropDownHeight = 180,
                    SelectedIndex = 0
                };
                var grid = new Grid
                {
                    RowDefinitions = RowDefinitions.Parse("130,130,130,130,150,44"),
                    Margin = new Thickness(10),
                    RowSpacing = 8,
                    Children = { plain, bothAxes, list, editor, textarea, select }
                };
                for (var index = 0; index < grid.Children.Count; index++)
                {
                    Grid.SetRow(grid.Children[index], index);
                }
                // Deliberately has no MainView or local MusicStyles include: the policy must be application-wide.
                var window = new Window { Content = grid, Width = 720, Height = 900 };
                try
                {
                    window.Show();
                    foreach (var width in new[] { 720d, 390d })
                    {
                        window.Width = width;
                        window.SetRenderScaling(width == 390 ? 1.25 : 1);
                        Pump(window);
                        AssertGutter(plain, "Ordinary viewer");
                        AssertGutter(bothAxes, "Two-axis viewer", true);
                        AssertGutter(OwnedViewer(list), "Virtualized list");
                        AssertGutter(OwnedViewer(editor), "Multiline TextBox");
                        AssertGutter(OwnedViewer(textarea), "Arc multiline editor");
                        TestAssert.True(!ScrollViewer.GetAllowAutoHide(list) && !ScrollViewer.GetAllowAutoHide(editor),
                            "Templated scroll owners must pass the same non-overlay preference to their internal viewers.");
                        TestAssert.True(list.GetVisualDescendants().OfType<VirtualizingStackPanel>().Any()
                            && list.GetVisualDescendants().OfType<ListBoxItem>().Count() < list.Items.Count,
                            "Reserving a scrollbar track must preserve native list virtualization.");

                        var geometry = ViewportBounds(plain);
                        var bar = VerticalBar(plain);
                        var barBounds = BoundsIn(bar, plain);
                        window.MouseMove(Center(bar, window));
                        Pump(window);
                        TestAssert.True(ViewportBounds(plain) == geometry && BoundsIn(bar, plain) == barBounds,
                            "Hover must neither widen the reserved track into content nor move the viewport.");
                        plain.Offset = default;
                        window.MouseWheel(Center(plain, window), new Vector(0, -3), RawInputModifiers.None);
                        await WaitUntilAsync(() => plain.Offset.Y > 0, window, timeout.Token);
                        var beforeDrag = plain.Offset.Y;
                        var thumb = bar.GetVisualDescendants().OfType<Thumb>().Single();
                        var start = Center(thumb, window);
                        var end = start + new Vector(0, 30);
                        window.MouseMove(start);
                        window.MouseDown(start, MouseButton.Left);
                        Pump(window);
                        TestAssert.True(ViewportBounds(plain) == geometry && BoundsIn(bar, plain) == barBounds,
                            "A held scrollbar must stay entirely inside its existing reserved lane.");
                        window.MouseMove(end, RawInputModifiers.LeftMouseButton);
                        window.MouseUp(end, MouseButton.Left);
                        Pump(window);
                        TestAssert.True(plain.Offset.Y > beforeDrag && ViewportBounds(plain) == geometry,
                            "Dragging the real thumb must still scroll content without changing its viewport.");

                        select.IsOpen = true;
                        Pump(window);
                        var popup = select.GetVisualDescendants().OfType<Popup>().Single();
                        TestAssert.True(popup.IsOpen && popup.Child is not null, "The real Arc select popup must open.");
                        var popupViewer = popup.Child!.GetVisualDescendants().OfType<ScrollViewer>().Single();
                        TestAssert.True(!popupViewer.AllowAutoHide,
                            "A separate popup root must inherit the global scroll policy without a MainView ancestor.");
                        // Arc intentionally hides this menu's bars. Exercise its actual template with an available bar too.
                        popupViewer.SetCurrentValue(ScrollViewer.VerticalScrollBarVisibilityProperty, ScrollBarVisibility.Auto);
                        Pump(window);
                        AssertGutter(popupViewer, "Arc select popup");
                        popupViewer.Offset = new Vector(0, 40);
                        Pump(window);
                        TestAssert.True(popupViewer.Offset.Y > 0, "The popup's actual scroll presenter must remain functional.");
                        select.IsOpen = false;
                        Pump(window);
                    }
                }
                finally
                {
                    select.IsOpen = false;
                    window.Close();
                }
                return true;
            }, timeout.Token);
        }

        /// <summary>Finds the native viewer created by a real list or editor template.</summary>
        /// <param name="owner">The templated control.</param>
        /// <returns>Its scroll viewer.</returns>
        private static ScrollViewer OwnedViewer(Control owner)
        {
            return owner.GetVisualDescendants().OfType<ScrollViewer>().First(viewer => ReferenceEquals(viewer.TemplatedParent, owner));
        }

        /// <summary>Checks the content viewport against the scrollbar's real layout and painted hit area.</summary>
        /// <param name="viewer">The actual native viewer.</param>
        /// <param name="label">The diagnostic control label.</param>
        /// <param name="bothAxes">Whether a horizontal reserved track is required too.</param>
        private static void AssertGutter(ScrollViewer viewer, string label, bool bothAxes = false)
        {
            var viewport = ViewportBounds(viewer);
            var vertical = VerticalBar(viewer);
            var barBounds = BoundsIn(vertical, viewer);
            TestAssert.True(!viewer.AllowAutoHide && vertical.IsVisible && vertical.Bounds.Width > 0 && viewport.Width > 0,
                label + " must expose a real reserved vertical scroll track: autoHide=" + viewer.AllowAutoHide
                + "; visible=" + vertical.IsVisible + "; bar=" + barBounds + "; viewport=" + viewport);
            TestAssert.True(viewport.Right <= barBounds.Left + 0.01,
                label + " must end content before the scrollbar begins: viewport=" + viewport + "; bar=" + barBounds);
            var presenter = viewer.GetVisualDescendants().OfType<ScrollContentPresenter>().Single(control => ReferenceEquals(control.TemplatedParent, viewer));
            TestAssert.True(Grid.GetColumnSpan(presenter) == 1 && Grid.GetRowSpan(presenter) == 1,
                label + " must not span its content underneath either scrollbar track.");
            if (bothAxes)
            {
                var horizontal = viewer.GetVisualDescendants().OfType<ScrollBar>()
                    .Single(control => ReferenceEquals(control.TemplatedParent, viewer) && control.Orientation == Avalonia.Layout.Orientation.Horizontal);
                TestAssert.True(horizontal.IsVisible && viewport.Bottom <= BoundsIn(horizontal, viewer).Top + 0.01,
                    label + " must reserve the real horizontal track as well.");
            }
        }

        /// <summary>Finds the actual vertical bar belonging to this viewer.</summary>
        /// <param name="viewer">The real viewer.</param>
        /// <returns>The template's vertical scrollbar.</returns>
        private static ScrollBar VerticalBar(ScrollViewer viewer)
        {
            return viewer.GetVisualDescendants().OfType<ScrollBar>()
                .Single(control => ReferenceEquals(control.TemplatedParent, viewer) && control.Orientation == Avalonia.Layout.Orientation.Vertical);
        }

        /// <summary>Measures the real content presenter in its owning viewer's coordinate system.</summary>
        /// <param name="viewer">The real viewer.</param>
        /// <returns>The viewport's actual arranged rectangle.</returns>
        private static Rect ViewportBounds(ScrollViewer viewer)
        {
            var presenter = viewer.GetVisualDescendants().OfType<ScrollContentPresenter>().Single(control => ReferenceEquals(control.TemplatedParent, viewer));
            return BoundsIn(presenter, viewer);
        }

        /// <summary>Converts actual arranged bounds to a stable common coordinate system.</summary>
        /// <param name="control">The measured control.</param>
        /// <param name="relativeTo">The coordinate owner.</param>
        /// <returns>The actual rectangle.</returns>
        private static Rect BoundsIn(Control control, Visual relativeTo)
        {
            return new Rect(control.TranslatePoint(default, relativeTo)!.Value, control.Bounds.Size);
        }

        /// <summary>Finds a real control's pointer target within the input window.</summary>
        /// <param name="control">The pointer target.</param>
        /// <param name="window">The input window.</param>
        /// <returns>The target center.</returns>
        private static Point Center(Control control, Window window)
        {
            return control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;
        }

        /// <summary>Waits for real input to update the viewer while keeping its renderer active.</summary>
        /// <param name="condition">The observed input result.</param>
        /// <param name="window">The input window.</param>
        /// <param name="token">Cancels a missing result.</param>
        /// <returns>A task representing the observed result.</returns>
        private static async Task WaitUntilAsync(Func<bool> condition, Window window, CancellationToken token)
        {
            while (!condition())
            {
                await Task.Delay(10, token);
                Pump(window);
            }
        }

        /// <summary>Completes binding, layout and compositor input geometry for the actual templates.</summary>
        /// <param name="window">The rendered input window.</param>
        private static void Pump(Window window)
        {
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
        }
    }
}

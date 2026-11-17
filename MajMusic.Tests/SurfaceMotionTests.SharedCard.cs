#nullable enable

using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.VisualTree;
using MajMusic.Core.Playback;
using MajMusic.Presentation.Theme;
using MajMusic.Presentation.ViewModels;
using MajMusic.Presentation.Views;
using TKCode.Arc.Motion;

namespace MajMusic.Tests
{
    /// <summary>Checks that desktop expansion flies one real information card between its existing layout slots.</summary>
    internal static partial class SurfaceMotionTests
    {
        /// <summary>Exercises uninterrupted, reversed, disabled and detached flights through the actual bound desktop view.</summary>
        /// <param name="clock">The installed deterministic Arc frame clock.</param>
        /// <returns>A task representing real-control ownership and painted-geometry assertions.</returns>
        private static async Task ValidateSharedSongCardFlightAsync(ManualFrameClock clock)
        {
            var workspace = new SocialPresentationTests.SocialWorkspace();
            await using var model = new MainViewModel(workspace, new PlaybackService(new UnavailablePlaybackBackend()));
            await model.InitializeAsync();
            model.ShowLibraryModeCommand.Execute(null);
            await model.HomeUpdate;
            model.SelectedTrack = workspace.Library[0];
            var main = new MainView { DataContext = model };
            var window = new Window { Content = main, Width = 1100, Height = 800 };
            try
            {
                window.Show();
                Pump(window);
                var source = main.FindControl<ContentControl>("DesktopSidebarDetailsSlot")!;
                var destination = main.FindControl<DesktopSongDetailsView>("DesktopSongDetails")!
                    .FindControl<ContentControl>("DesktopSongSummarySlot")!;
                var flight = main.FindControl<SharedSongCardFlight>("DesktopSongCardFlight")!;
                var card = main.FindControl<Border>("LibraryDetails")!;
                var original = BoundsIn(card, window);
                var originalWidth = card.Width;
                var originalHeight = card.Height;
                var originalTransform = card.RenderTransform;
                TestAssert.True(ReferenceEquals(source.Content, card), "The real sidebar must initially own the only information card.");

                model.OpenExpandedSongDetailsCommand.Execute(null);
                var firstOpening = flight.MoveUpdate;
                var firstParent = (card.Parent as Control)?.Name;
                Pump(window);
                var opening = flight.MoveUpdate;
                var targetBounds = BoundsIn(destination, window);
                TestAssert.True(!opening.IsCompleted && ReferenceEquals(card.Parent, flight)
                    && source.Content is null && destination.Content is null && card.Bounds.Size == original.Size,
                    $"Opening details must immediately transfer the original, unchanged-size card to the live flight layer. " +
                    $"completed={opening.IsCompleted}, beforePumpCompleted={firstOpening.IsCompleted}, beforePumpParent={firstParent}, parent={card.Parent?.GetType().Name}/{(card.Parent as Control)?.Name}, " +
                    $"sourceOwns={ReferenceEquals(source.Content, card)}, destinationOwns={ReferenceEquals(destination.Content, card)}, " +
                    $"original={original}, card={BoundsIn(card, window)}, source={source.Bounds}, destination={destination.Bounds}, " +
                    $"flight={flight.Bounds}, visible={flight.IsEffectivelyVisible}, level={ArcMotion.GetEffectiveLevel(flight)}.");
                TestAssert.True(targetBounds.Width > original.Width + 20 && targetBounds.Height > original.Height + 20,
                    "The real expanded layout must provide a wider, taller left slot instead of forcing the original sidebar size.");
                clock.Advance(TimeSpan.FromMilliseconds(180), TimeSpan.FromMilliseconds(5));
                Pump(window);
                var middle = BoundsIn(card, window);
                var matrix = card.RenderTransform?.Value ?? Matrix.Identity;
                TestAssert.True(!opening.IsCompleted && middle.X < original.X - 40 && middle.X > destination.TranslatePoint(default, window)!.Value.X
                    && Math.Abs(middle.Y - original.Y) < .1
                    && middle.Width > original.Width && middle.Width < targetBounds.Width
                    && middle.Height > original.Height && middle.Height < targetBounds.Height
                    && matrix.M11 == 1 && matrix.M22 == 1 && matrix.M12 == 0 && matrix.M21 == 0,
                    "The real mid-flight card must move left at its original Y while laying out an intermediate width and height, without rotating or scaling text.");
                TestAssert.Equal(1, main.GetVisualDescendants().OfType<Border>().Count(control => control.Name == "LibraryDetails"),
                    "The flight must move the existing card rather than introduce a duplicate interactive tree.");

                model.CloseExpandedSongDetailsCommand.Execute(null);
                Pump(window);
                var reversal = flight.MoveUpdate;
                var reversedStart = BoundsIn(card, window);
                TestAssert.True(opening.IsCompleted && !await opening && !reversal.IsCompleted
                    && Math.Abs(reversedStart.X - middle.X) < .1 && Math.Abs(reversedStart.Y - middle.Y) < .1
                    && Math.Abs(reversedStart.Width - middle.Width) < .1 && Math.Abs(reversedStart.Height - middle.Height) < .1,
                    "A fast reversal must retire the old move and begin at the exact currently painted position and size.");
                clock.Advance(TimeSpan.FromMilliseconds(400), TimeSpan.FromMilliseconds(5));
                Pump(window);
                TestAssert.True(await reversal.WaitAsync(TimeSpan.FromSeconds(3)) && ReferenceEquals(source.Content, card)
                    && ReferenceEquals(card.Parent, source) && destination.Content is null && flight.Children.Count == 0
                    && card.Width.Equals(originalWidth) && card.Height.Equals(originalHeight) && ReferenceEquals(card.RenderTransform, originalTransform)
                    && BoundsIn(card, window) == original,
                    "The reversed flight must return the same card and restore its original layout expressions and resting transform.");

                model.OpenExpandedSongDetailsCommand.Execute(null);
                Pump(window);
                var completeOpening = flight.MoveUpdate;
                clock.Advance(TimeSpan.FromMilliseconds(400), TimeSpan.FromMilliseconds(5));
                Pump(window);
                TestAssert.True(await completeOpening.WaitAsync(TimeSpan.FromSeconds(3)) && ReferenceEquals(destination.Content, card)
                    && ReferenceEquals(card.Parent, destination) && source.Content is null && flight.Children.Count == 0
                    && BoundsIn(card, window) == BoundsIn(destination, window) && Math.Abs(BoundsIn(card, window).Y - original.Y) < .1,
                    "A complete expansion must land the original card flush with the wider, taller left slot without leaving an overlay child.");

                ArcMotion.SetLevel(flight, ArcMotionLevel.Off);
                model.CloseExpandedSongDetailsCommand.Execute(null);
                Pump(window);
                TestAssert.True(flight.MoveUpdate.IsCompleted && await flight.MoveUpdate && ReferenceEquals(source.Content, card)
                    && ReferenceEquals(card.Parent, source) && destination.Content is null && flight.Children.Count == 0
                    && BoundsIn(card, window) == original,
                    "Motion Off must place the real card directly in its requested slot without retaining a driver or orphan.");

                ArcMotion.SetLevel(flight, ArcMotionLevel.Full);
                model.OpenExpandedSongDetailsCommand.Execute(null);
                Pump(window);
                var detached = flight.MoveUpdate;
                TestAssert.True(!detached.IsCompleted && ReferenceEquals(card.Parent, flight),
                    "The detach fixture must interrupt a real in-progress card flight.");
                window.Content = null;
                Pump(window);
                TestAssert.True(!await detached.WaitAsync(TimeSpan.FromSeconds(3)) && ReferenceEquals(destination.Content, card)
                    && ReferenceEquals(card.Parent, destination) && source.Content is null && flight.Children.Count == 0
                    && ReferenceEquals(card.RenderTransform, originalTransform),
                    "Detachment must supersede motion, restore the resting transform and return the original card to exactly one slot.");
            }
            finally
            {
                window.Close();
            }
        }
    }
}

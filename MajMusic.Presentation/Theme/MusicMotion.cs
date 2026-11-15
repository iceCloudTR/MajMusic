#nullable enable

using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Styling;
using Avalonia.VisualTree;
using TKCode.Arc.Motion;

namespace MajMusic.Presentation.Theme
{
    /// <summary>Reveals changed music pages and search controls without moving their layout or retaining old pages.</summary>
    public sealed class MusicMotion : AvaloniaObject
    {
        /// <summary>Enables a short fade when a control becomes visible or its presented content changes.</summary>
        public static readonly AttachedProperty<bool> RevealChangesProperty =
            AvaloniaProperty.RegisterAttached<MusicMotion, Control, bool>("RevealChanges");

        /// <summary>Retains one cancellable reveal per opted-in control.</summary>
        private static readonly AttachedProperty<RevealState?> StateProperty =
            AvaloniaProperty.RegisterAttached<MusicMotion, Control, RevealState?>("State");

        /// <summary>Registers lifetime-bound animation observers for opted-in controls.</summary>
        static MusicMotion()
        {
            RevealChangesProperty.Changed.AddClassHandler<Control>((control, change) =>
            {
                control.GetValue(StateProperty)?.Dispose();
                control.SetValue(StateProperty, change.NewValue is true ? new RevealState(control) : null);
            });
        }

        /// <summary>Prevents construction of the attached behavior.</summary>
        private MusicMotion()
        {
        }

        /// <summary>Gets whether page, content and visibility changes reveal this control.</summary>
        /// <param name="control">The presentation host or search field.</param>
        /// <returns>Whether change reveals are enabled.</returns>
        public static bool GetRevealChanges(Control control)
        {
            return control.GetValue(RevealChangesProperty);
        }

        /// <summary>Enables or removes change reveals on a presentation host or search field.</summary>
        /// <param name="control">The control to animate.</param>
        /// <param name="value">Whether change reveals are enabled.</param>
        public static void SetRevealChanges(Control control, bool value)
        {
            control.SetValue(RevealChangesProperty, value);
        }

        /// <summary>Owns and cancels a control's current reveal when superseded, hidden or detached.</summary>
        private sealed class RevealState : IDisposable
        {
            /// <summary>Identifies the presentation host whose bindings and base opacity remain untouched.</summary>
            private readonly Control _control;
            /// <summary>Cancels only the currently running reveal.</summary>
            private CancellationTokenSource? _pending;

            /// <summary>Observes the control for its attached lifetime.</summary>
            /// <param name="control">The opted-in control.</param>
            public RevealState(Control control)
            {
                _control = control;
                control.PropertyChanged += OnChanged;
                control.DetachedFromVisualTree += OnDetached;
            }

            /// <summary>Stops motion and removes all observers.</summary>
            public void Dispose()
            {
                _control.PropertyChanged -= OnChanged;
                _control.DetachedFromVisualTree -= OnDetached;
                _pending?.Cancel();
            }

            /// <summary>Cancels motion as soon as the active presentation leaves the visual tree.</summary>
            /// <param name="sender">The detached control.</param>
            /// <param name="e">The completed detachment.</param>
            private void OnDetached(object? sender, VisualTreeAttachmentEventArgs e)
            {
                _pending?.Cancel();
            }

            /// <summary>Reveals only an explicitly changed page, content host or visible search field.</summary>
            /// <param name="sender">The observed control.</param>
            /// <param name="change">The changed content or visibility property.</param>
            private async void OnChanged(object? sender, AvaloniaPropertyChangedEventArgs change)
            {
                if (change.Property != Visual.IsVisibleProperty && change.Property != TabControl.SelectedContentProperty
                    && change.Property != ContentControl.ContentProperty)
                {
                    return;
                }

                _pending?.Cancel();
                if (!_control.IsEffectivelyVisible || TopLevel.GetTopLevel(_control) is null
                    || ArcMotion.GetEffectiveLevel(_control) == ArcMotionLevel.Off)
                {
                    return;
                }

                using var cancellation = new CancellationTokenSource();
                _pending = cancellation;
                var baseOpacity = _control.GetBaseValue(Visual.OpacityProperty).GetValueOrDefault(1);
                var animation = new Animation
                {
                    Duration = TimeSpan.FromMilliseconds(180),
                    Easing = new CubicEaseOut(),
                    FillMode = FillMode.None,
                    Children =
                    {
                        new KeyFrame { Cue = new Cue(0), Setters = { new Setter(Visual.OpacityProperty, .35) } },
                        new KeyFrame { Cue = new Cue(1), Setters = { new Setter(Visual.OpacityProperty, baseOpacity) } }
                    }
                };
                try
                {
                    await animation.RunAsync(_control, cancellation.Token);
                }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
                {
                }
                finally
                {
                    if (ReferenceEquals(_pending, cancellation))
                    {
                        _pending = null;
                    }
                }
            }
        }
    }
}

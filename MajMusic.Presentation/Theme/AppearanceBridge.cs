#nullable enable

using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
using MajMusic.Presentation.ViewModels;

namespace MajMusic.Presentation.Theme
{
    /// <summary>Applies an attached workspace's appearance through the UI theme runtime.</summary>
    public sealed class AppearanceBridge : AvaloniaObject
    {
        /// <summary>Enables appearance projection for a shared music host.</summary>
        public static readonly AttachedProperty<bool> FollowPreferenceProperty =
            AvaloniaProperty.RegisterAttached<AppearanceBridge, Control, bool>("FollowPreference");
        /// <summary>Retains the attached host's lifecycle-bound preference observer.</summary>
        private static readonly AttachedProperty<PreferenceObserver?> ObserverProperty =
            AvaloniaProperty.RegisterAttached<AppearanceBridge, Control, PreferenceObserver?>("Observer");

        /// <summary>Creates or removes the observer only for explicitly opted-in music hosts.</summary>
        static AppearanceBridge()
        {
            FollowPreferenceProperty.Changed.AddClassHandler<Control>((control, change) =>
            {
                control.GetValue(ObserverProperty)?.Dispose();
                control.SetValue(ObserverProperty, change.NewValue is true ? new PreferenceObserver(control) : null);
            });
        }

        /// <summary>Prevents construction of the attached behavior.</summary>
        private AppearanceBridge()
        {
        }

        /// <summary>Gets whether this host follows its shared workspace's appearance preference.</summary>
        /// <param name="control">The music view host.</param>
        /// <returns>Whether preference projection is enabled.</returns>
        public static bool GetFollowPreference(Control control)
        {
            return control.GetValue(FollowPreferenceProperty);
        }

        /// <summary>Enables appearance projection for one attached music host.</summary>
        /// <param name="control">The music view host.</param>
        /// <param name="value">Whether the host follows its view model's appearance.</param>
        public static void SetFollowPreference(Control control, bool value)
        {
            control.SetValue(FollowPreferenceProperty, value);
        }

        /// <summary>Observes only the current attached view model so responsive views do not duplicate appearance work.</summary>
        private sealed class PreferenceObserver : IDisposable
        {
            /// <summary>Owns the current shared view attachment.</summary>
            private readonly Control _control;
            /// <summary>Stores the view model whose property notifications are observed.</summary>
            private MainViewModel? _owner;

            /// <summary>Registers attachment and context observation for one host.</summary>
            /// <param name="control">The music view host.</param>
            public PreferenceObserver(Control control)
            {
                _control = control;
                control.AttachedToVisualTree += OnAttached;
                control.DetachedFromVisualTree += OnDetached;
                control.DataContextChanged += OnContextChanged;
                Observe();
            }

            /// <summary>Releases all control and view-model observers.</summary>
            public void Dispose()
            {
                _control.AttachedToVisualTree -= OnAttached;
                _control.DetachedFromVisualTree -= OnDetached;
                _control.DataContextChanged -= OnContextChanged;
                ClearOwner();
            }

            /// <summary>Attaches to the current workspace when the host enters a rendered view.</summary>
            /// <param name="sender">The attached music host.</param>
            /// <param name="e">The attachment notification.</param>
            private void OnAttached(object? sender, VisualTreeAttachmentEventArgs e)
            {
                Observe();
            }

            /// <summary>Stops observing a workspace when this presentation leaves its host.</summary>
            /// <param name="sender">The detached music host.</param>
            /// <param name="e">The detachment notification.</param>
            private void OnDetached(object? sender, VisualTreeAttachmentEventArgs e)
            {
                ClearOwner();
            }

            /// <summary>Transfers observation after the host receives another workspace.</summary>
            /// <param name="sender">The shared music host.</param>
            /// <param name="e">The data-context notification.</param>
            private void OnContextChanged(object? sender, EventArgs e)
            {
                Observe();
            }

            /// <summary>Follows only an attached workspace and applies an already restored preference.</summary>
            private void Observe()
            {
                ClearOwner();
                if (_control.IsAttachedToVisualTree() && _control.DataContext is MainViewModel owner)
                {
                    _owner = owner;
                    owner.PropertyChanged += OnPreferenceChanged;
                    Apply();
                }
            }

            /// <summary>Detaches the preceding workspace property listener.</summary>
            private void ClearOwner()
            {
                if (_owner is not null)
                {
                    _owner.PropertyChanged -= OnPreferenceChanged;
                    _owner = null;
                }
            }

            /// <summary>Applies the latest immediate user choice or completed startup restoration.</summary>
            /// <param name="sender">The current shared view model.</param>
            /// <param name="e">The changed property.</param>
            private void OnPreferenceChanged(object? sender, PropertyChangedEventArgs e)
            {
                if (e.PropertyName == nameof(MainViewModel.ThemePreference))
                {
                    Apply();
                }
            }

            /// <summary>Leaves construction defaults alone until startup restoration or an explicit edit establishes a preference.</summary>
            private void Apply()
            {
                if (_owner is { IsAppearanceInitialized: true } owner)
                {
                    MajMusicTheme.ApplyAppearance(owner.ThemePreference);
                }
            }
        }
    }
}

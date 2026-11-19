#nullable enable

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Documents;
using System.Globalization;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.VisualTree;
using Avalonia.Styling;
using TKCode.Arc.Primitives;
using TKCode.Arc.Motion;
using MajMusic.Presentation.ViewModels;
using SettingsPopover = TKCode.Arc.Popover;

namespace MajMusic.Presentation.Views
{
    public partial class ChartPreviewPane
    {
        private TopLevel? _top;
        private SettingsPopover? _settingsMenu;
        private (bool Full, bool Wide)? _layout;
        private bool? _previousSystemBars;
        private bool _ownsSystemBars;

        private MotionValue<double>? _aperture;
        private bool? _fullscreenTarget;
        private int _transitionVersion;

        private void RequestFullscreen(bool target)
        {
            if (_owner is not { IsPracticeLocked: false } owner || _displayedAddress is null)
            {
                return;
            }
            owner.PauseChart();
            var version = ++_transitionVersion;
            _fullscreenTarget = target;
            if (_aperture is null || ArcMotion.GetEffectiveLevel(this) != ArcMotionLevel.Full)
            {
                ResetFullscreenMotion();
                owner.IsChartFullscreen = target;
                return;
            }
            _ = AnimateFullscreenAsync(owner, target, version, _aperture);
        }

        /// <summary>Closes the live disc before changing layout and reveals it afterwards. A reverse tap takes over from the current aperture.</summary>
        private async Task AnimateFullscreenAsync(MainViewModel owner, bool target, int version, MotionValue<double> motion)
        {
            if (owner.IsChartFullscreen != target)
            {
                if (!await motion.AnimateTo(0, MotionSpec.Tween(TimeSpan.FromMilliseconds(110), ArcEasings.Standard)) || version != _transitionVersion)
                {
                    return;
                }
                owner.IsChartFullscreen = target;
                _top?.UpdateLayout();
            }
            if (await motion.AnimateTo(1, MotionSpec.Tween(TimeSpan.FromMilliseconds(185), ArcEasings.Standard)) && version == _transitionVersion)
            {
                _fullscreenTarget = null;
            }
        }

        private void ApplyAperture(double fraction)
        {
            if (_surface.Content is IPracticeDisc renderer)
            {
                renderer.SetAperture(fraction);
            }
            PracticeDisc.Opacity = PracticeNavigation.Opacity = PracticeTools.Opacity = PracticeTransport.Opacity =
                PracticeNotes.Opacity = PracticeRightNotes.Opacity = fraction;
        }

        private void ResetFullscreenMotion()
        {
            _transitionVersion++;
            _fullscreenTarget = null;
            _aperture?.Jump(1);
        }

        private void InitializePracticeLayout()
        {
            ArcMotion.SetTracks(this, true);
            SizeChanged += (_, _) => ArrangePractice();
            ActualThemeVariantChanged += (_, _) =>
            {
                if (_displayedAddress is not null && _owner is not null)
                {
                    _owner.SetChartTheme(ActualThemeVariant == Avalonia.Styling.ThemeVariant.Light);
                }
            };
        }

        private static readonly string[] NoteNames = ["TAP", "HOLD", "SLIDE", "TOUCH", "BREAK"];

        private readonly Run?[] _noteValues = new Run?[NoteNames.Length];
        private readonly int[] _lastCounts = new int[NoteNames.Length];
        private IReadOnlyList<int>? _legendTotals;

        /// <summary>Builds each legend once per difficulty and updates only counts that actually change.</summary>
        private void UpdateNoteLegend()
        {
            var totals = _owner?.NoteTotals;
            if (totals is not { Count: 5 })
            {
                _legendTotals = null;
                PracticeNotes.Inlines!.Clear();
                PracticeRightNotes.Inlines!.Clear();
                PracticeNotes.Inlines.Add(new Run("正在读取音符"));
                return;
            }
            if (!ReferenceEquals(totals, _legendTotals))
            {
                _legendTotals = totals;
                PracticeNotes.Inlines!.Clear();
                PracticeRightNotes.Inlines!.Clear();
                Array.Fill(_lastCounts, -1);
                for (var index = 0; index < NoteNames.Length; index++)
                {
                    var lines = index < 3 ? PracticeNotes.Inlines : PracticeRightNotes.Inlines;
                    if (lines.Count > 0)
                    {
                        lines.Add(new LineBreak());
                    }
                    var dot = new Run("●") { Foreground = PracticeTimeline.Types[index] };
                    if (index < 3)
                    {
                        lines.Add(dot);
                        lines.Add(new Run(" "));
                    }
                    lines.Add(new Run(NoteNames[index] + " "));
                    var value = new Run();
                    _noteValues[index] = value;
                    lines.Add(value);
                    if (index >= 3)
                    {
                        lines.Add(new Run(" "));
                        lines.Add(dot);
                    }
                }
            }
            for (var index = 0; index < NoteNames.Length; index++)
            {
                var passed = _owner!.PassedNotes[index];
                if (passed != _lastCounts[index])
                {
                    _lastCounts[index] = passed;
                    var total = totals[index].ToString(CultureInfo.InvariantCulture);
                    var current = passed.ToString(CultureInfo.InvariantCulture).PadLeft(total.Length, '\u2007');
                    _noteValues[index]!.Text = $"{current}/{total}";
                }
            }
        }

        private void ArrangePractice()
        {
            var full = _owner?.IsChartFullscreen == true && _owner.IsSongChartPreviewSelected && IsEffectivelyVisible;
            var wide = full && (_top?.ClientSize.Width ?? Bounds.Width) > (_top?.ClientSize.Height ?? Bounds.Height);
            if (_layout == (full, wide))
            {
                return;
            }

            _layout = (full, wide);
            Stage.SetLayout(wide ? PracticeLayout.Landscape : full ? PracticeLayout.Portrait : PracticeLayout.Inline);
            PracticeGrid.RowDefinitions = new RowDefinitions(full && !wide ? "*,110" : "*");
            PracticeGrid.ColumnDefinitions = new ColumnDefinitions("*");
            if (wide)
            {
                PracticeGrid.ColumnDefinitions.Add(new ColumnDefinition(PracticeStage.TransportRail, GridUnitType.Pixel));
            }
            Grid.SetRow(PracticeTransport, full && !wide ? 1 : 0);
            Grid.SetColumn(PracticeTransport, wide ? 1 : 0);
            PracticeNavigation.Orientation = wide ? Orientation.Vertical : Orientation.Horizontal;
            PracticeClock.Orientation = wide ? Orientation.Vertical : Orientation.Horizontal;
            PracticeOverview.Vertical = wide;
            if (OperatingSystem.IsAndroid() && full && !_ownsSystemBars && _top?.InsetsManager is { } insets)
            {
                _previousSystemBars = insets.IsSystemBarVisible;
                _ownsSystemBars = true;
                insets.IsSystemBarVisible = false;
            }
            if (!full)
            {
                RestoreSystemBars();
            }
        }

        private void RestoreSystemBars()
        {
            if (_ownsSystemBars && _top?.InsetsManager is { } insets)
            {
                _ownsSystemBars = false;
                insets.IsSystemBarVisible = _previousSystemBars;
            }
        }

        private void OnFullscreenClick(object? sender, RoutedEventArgs args)
        {
            if (_owner is { IsPracticeLocked: false } owner)
            {
                RequestFullscreen(!(_fullscreenTarget ?? owner.IsChartFullscreen));
            }
        }

        private void OnSettingsClick(object? sender, RoutedEventArgs args)
        {
            if (_owner is not { IsPracticeLocked: false } owner || _top is null)
            {
                return;
            }
            if (_settingsMenu is { } opened)
            {
                opened.Close();
                return;
            }
            owner.PauseChart();
            var menu = new SettingsPopover { TriggerMode = TKCode.Arc.PopoverTriggerMode.Manual,
                Placement = _layout?.Wide == true ? TKCode.Arc.PopoverPlacement.Right : TKCode.Arc.PopoverPlacement.Bottom,
                Alignment = _layout?.Wide == true ? TKCode.Arc.PopoverAlignment.Start : TKCode.Arc.PopoverAlignment.End,
                Offset = 6, HasShadow = true, IsModal = true };
            var content = new ChartPracticeDialog(owner, menu.Close);
            menu.Content = content;
            Avalonia.Automation.AutomationProperties.SetName(menu, "谱面设置");
            SettingsPopover.SetAttach(PracticeSettings, menu);
            // A separate Windows popup can stack above WebView2's child window.
            if (OperatingSystem.IsWindows() && _surface.Content is IMajViewNavigator)
            {
                menu.Styles.Add(new Style(selector => selector.Is<ArcPopup>())
                {
                    Setters = { new Setter(Popup.ShouldUseOverlayLayerProperty, false) }
                });
            }
            _settingsMenu = menu;
            SizeSettingsMenu();
            menu.ShowAt(PracticeSettings);
            if (menu.Popup is { } popup)
            {
                popup.Closed += OnSettingsPopupClosed;
                // Overlay-hosted controls share the app's drawing layer. Park the native pixels
                // until the exit animation has finished, keeping the renderer and clock mounted.
                if (popup.Child is null || TopLevel.GetTopLevel(popup.Child) is not PopupRoot)
                {
                    PreviewSurface.IsVisible = false;
                }
            }
        }

        private void SizeSettingsMenu()
        {
            if (_settingsMenu?.Content is ChartPracticeDialog content && _top is not null)
            {
                content.Width = Math.Clamp(_top.ClientSize.Width - 64, 160, 286);
                content.MaxHeight = Math.Clamp(_top.ClientSize.Height - 88, 140, 452);
            }
        }

        private void OnSettingsPopupClosed(object? sender, EventArgs args)
        {
            if (_settingsMenu?.Popup == sender)
            {
                DismissPracticeSettings();
            }
        }

        private void DismissPracticeSettings()
        {
            if (_settingsMenu is not { } menu)
            {
                return;
            }
            _settingsMenu = null;
            if (menu.Popup is { } popup)
            {
                popup.Closed -= OnSettingsPopupClosed;
            }
            menu.Close();
            SettingsPopover.SetAttach(PracticeSettings, null);
            PreviewSurface.IsVisible = _displayedAddress is not null;
        }

        private void ConnectPracticeInput()
        {
            _top = TopLevel.GetTopLevel(this);
            if (_top is null)
            {
                return;
            }

            _aperture = new MotionValue<double>(this, 1, MotionVectors.Double);
            _aperture.Changed += ApplyAperture;
            _top.BackRequested += OnPracticeBack;
            _top.SizeChanged += OnPracticeResize;
            _top.AddHandler(PointerPressedEvent, FilterPointer, RoutingStrategies.Tunnel);
            _top.AddHandler(PointerReleasedEvent, FilterPointer, RoutingStrategies.Tunnel);
            _top.AddHandler(PointerMovedEvent, FilterPointer, RoutingStrategies.Tunnel);
            _top.AddHandler(PointerWheelChangedEvent, FilterPointer, RoutingStrategies.Tunnel);
            _top.AddHandler(KeyDownEvent, FilterKey, RoutingStrategies.Tunnel);
        }

        private void DisconnectPracticeInput()
        {
            DismissPracticeSettings();
            RestoreSystemBars();
            if (_top is null)
            {
                return;
            }

            ResetFullscreenMotion();
            if (_aperture is { } motion)
            {
                motion.Changed -= ApplyAperture;
                motion.Dispose();
                _aperture = null;
            }
            _layout = null;
            _top.BackRequested -= OnPracticeBack;
            _top.SizeChanged -= OnPracticeResize;
            _top.RemoveHandler(PointerPressedEvent, FilterPointer);
            _top.RemoveHandler(PointerReleasedEvent, FilterPointer);
            _top.RemoveHandler(PointerMovedEvent, FilterPointer);
            _top.RemoveHandler(PointerWheelChangedEvent, FilterPointer);
            _top.RemoveHandler(KeyDownEvent, FilterKey);
            _top = null;
        }

        private void OnPracticeResize(object? sender, SizeChangedEventArgs args)
        {
            ArrangePractice();
            SizeSettingsMenu();
        }

        private bool HandleBack()
        {
            if (!IsEffectivelyVisible || _owner is null)
            {
                return false;
            }

            if (_owner.IsPracticeLocked)
            {
                return true;
            }

            if (_settingsMenu is { } menu)
            {
                menu.Close();
                return true;
            }
            if (!_owner.IsChartFullscreen && _fullscreenTarget is null)
            {
                return false;
            }

            RequestFullscreen(false);
            return true;
        }

        private void OnPracticeBack(object? sender, RoutedEventArgs args)
        {
            if (HandleBack())
            {
                args.Handled = true;
            }
        }

        private bool OutsideLock(object? source) => _owner?.IsPracticeLocked == true && IsEffectivelyVisible &&
            !(source is Visual visual && (visual == PracticeLock || visual.GetVisualAncestors().Contains(PracticeLock)));

        private void FilterPointer(object? sender, PointerEventArgs args)
        {
            if (OutsideLock(args.Source))
            { args.PreventGestureRecognition(); args.Handled = true; }
        }
        private void FilterKey(object? sender, KeyEventArgs args)
        {
            if ((args.Key == Key.Escape && HandleBack()) || OutsideLock(args.Source) ||
                _owner?.IsPracticeLocked == true && args.Key is not (Key.Space or Key.Enter))
            {
                args.Handled = true;
            }
        }
    }
}

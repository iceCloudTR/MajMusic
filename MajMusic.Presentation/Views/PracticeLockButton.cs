#nullable enable

using System.ComponentModel;
using System.Diagnostics;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Markup.Xaml.MarkupExtensions;
using GlyphPath = Avalonia.Controls.Shapes.Path;
using ArcIcons = TKCode.Arc.ArcIcons;
using Avalonia.Threading;
using MajMusic.Presentation.ViewModels;

namespace MajMusic.Presentation.Views
{
    /// <summary>Activates the practice lock on tap and requires an uninterrupted half-second hold to release it.</summary>
    public sealed class PracticeLockButton : Avalonia.Controls.Button
    {
        private readonly GlyphPath _glyph = new() { Classes = { "practiceGlyph" }, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        private readonly Border _progress = new()
        {
            Height = 3,
            Width = 0,
            CornerRadius = new CornerRadius(2),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Bottom,
            Background = Brushes.CadetBlue
        };
        private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(16) };
        private MainViewModel? _owner;
        private IPointer? _held;
        private long _started;
        private bool _keyboardHold;

        /// <summary>Creates a practice lock button with touch and keyboard hold feedback.</summary>
        public PracticeLockButton()
        {
            _progress[!Border.BackgroundProperty] = new DynamicResourceExtension("MusicAccentBrush");
            Content = new Grid { Width = 26, Height = 30, Children = { _glyph, _progress } };
            _timer.Tick += (_, _) =>
            {
                var fraction = Math.Min(1, Stopwatch.GetElapsedTime(_started).TotalSeconds / 0.5);
                _progress.Width = 26 * fraction;
                if (fraction >= 1 && _owner is not null)
                {
                    _timer.Stop();
                    _progress.Width = 0;
                    _owner.IsPracticeLocked = false;
                }
            };
            LostFocus += (_, _) => { if (_keyboardHold) { CancelHold(); } };
            DataContextChanged += (_, _) => Observe(TopLevel.GetTopLevel(this) is null ? null : DataContext as MainViewModel);
        }

        /// <inheritdoc />
        protected override Type StyleKeyOverride => typeof(Avalonia.Controls.Button);
        /// <inheritdoc />
        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            Observe(DataContext as MainViewModel);
        }
        /// <inheritdoc />
        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            CancelHold();
            Observe(null);
            base.OnDetachedFromVisualTree(e);
        }
        private void Observe(MainViewModel? owner)
        {
            if (_owner is not null)
            {
                _owner.PropertyChanged -= OnChanged;
            }

            _owner = owner;
            if (_owner is not null)
            {
                _owner.PropertyChanged += OnChanged;
            }

            RefreshLabel();
        }
        private void OnChanged(object? sender, PropertyChangedEventArgs args)
        {
            if (args.PropertyName == nameof(MainViewModel.IsPracticeLocked))
            {
                RefreshLabel();
            }
        }
        private void RefreshLabel()
        {
            var locked = _owner?.IsPracticeLocked == true;
            _glyph.Data = (locked ? ArcIcons.Lock : ArcIcons.LockOpen).Clone();
            Classes.Set("latched", locked);
            ToolTip.SetTip(this, locked ? "长按 0.5 秒解锁" : "锁定练习触控");
            AutomationProperties.SetName(this, locked ? "长按半秒解锁" : "锁定练习触控");
        }
        /// <inheritdoc />
        protected override void OnClick()
        {
            if (_owner is not null)
            {
                _owner.IsPracticeLocked = true;
            }
        }
        /// <inheritdoc />
        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            if (_owner?.IsPracticeLocked != true)
            { base.OnPointerPressed(e); return; }
            if (_held is null && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            {
                _held = e.Pointer;
                e.Pointer.Capture(this);
                _started = Stopwatch.GetTimestamp();
                _timer.Start();
            }
            e.Handled = true;
        }
        /// <inheritdoc />
        protected override void OnPointerMoved(PointerEventArgs e)
        {
            if (_held == e.Pointer && !new Rect(Bounds.Size).Contains(e.GetPosition(this)))
            {
                CancelHold();
            }

            base.OnPointerMoved(e);
        }
        /// <inheritdoc />
        protected override void OnPointerReleased(PointerReleasedEventArgs e)
        {
            if (_held != e.Pointer)
            { base.OnPointerReleased(e); return; }
            CancelHold();
            e.Handled = true;
        }
        /// <inheritdoc />
        protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
        {
            if (_held == e.Pointer)
            {
                CancelHold();
            }

            base.OnPointerCaptureLost(e);
        }
        /// <inheritdoc />
        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (_owner?.IsPracticeLocked == true && e.Key is Key.Space or Key.Enter)
            {
                if (!_keyboardHold)
                { _keyboardHold = true; _started = Stopwatch.GetTimestamp(); _timer.Start(); }
                e.Handled = true;
                return;
            }
            base.OnKeyDown(e);
        }
        /// <inheritdoc />
        protected override void OnKeyUp(KeyEventArgs e)
        {
            if (_keyboardHold && e.Key is Key.Space or Key.Enter)
            { CancelHold(); e.Handled = true; return; }
            base.OnKeyUp(e);
        }
        private void CancelHold()
        {
            _timer.Stop();
            _progress.Width = 0;
            var pointer = _held;
            _held = null;
            _keyboardHold = false;
            if (pointer?.Captured == this)
            {
                pointer.Capture(null);
            }
        }
    }
}

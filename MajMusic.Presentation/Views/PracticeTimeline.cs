#nullable enable

using System.ComponentModel;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using MajMusic.Presentation.ViewModels;

namespace MajMusic.Presentation.Views
{
    /// <summary>Draws a cached two-lane practice overview and a seek handle on a shared time axis.</summary>
    public sealed class PracticeTimeline : Control
    {
        internal static readonly IBrush[] Types = [Brush("#E95591"), Brush("#ECA347"), Brush("#408DAE"), Brush("#45B7A2"), Brush("#E86A53")];
        private static readonly IBrush Wave = Brush("#71A8B9"), Rail = Brush("#88939E"), Handle = Brush("#48A6BD");
        private DrawingGroup? _lanes;
        private MainViewModel? _owner;
        private IPointer? _pointer;
        private bool _vertical;
        private static IBrush Brush(string color) => new SolidColorBrush(Color.Parse(color)).ToImmutable();

        /// <summary>Creates an accessible seek surface backed by a cached chart overview.</summary>
        public PracticeTimeline()
        {
            Focusable = true;
            AutomationProperties.SetName(this, "练习时间轴，波形及音符密度");
            DataContextChanged += (_, _) => Observe(TopLevel.GetTopLevel(this) is not null ? DataContext as MainViewModel : null);
            SizeChanged += (_, _) => InvalidateLanes();
        }

        internal bool Vertical
        {
            get => _vertical;
            set { if (_vertical != value) { _vertical = value; InvalidateLanes(); } }
        }

        /// <inheritdoc />
        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            Observe(DataContext as MainViewModel);
        }

        /// <inheritdoc />
        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            Observe(null);
            base.OnDetachedFromVisualTree(e);
        }

        private void Observe(MainViewModel? owner)
        {
            if (_owner is not null)
            {
                _owner.PropertyChanged -= OnOwnerChanged;
            }

            _owner = owner;
            if (owner is not null)
            {
                owner.PropertyChanged += OnOwnerChanged;
            }

            InvalidateLanes();
        }

        private void OnOwnerChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName is nameof(MainViewModel.ChartWaveform) or nameof(MainViewModel.ChartDensity))
            {
                InvalidateLanes();
            }
            else if (e.PropertyName == nameof(MainViewModel.PositionSeconds))
            {
                InvalidateVisual();
            }
        }

        private void InvalidateLanes() { _lanes = null; InvalidateVisual(); }
        private double Length => Math.Max(1, (Vertical ? Bounds.Height : Bounds.Width) - 16);
        private double Cross => Math.Max(1, (Vertical ? Bounds.Width : Bounds.Height) - 12);
        private Point At(double time, double lane) => Vertical ? new Point(4 + lane, 8 + time) : new Point(8 + time, 4 + lane);
        private Rect Bar(double time, double lane, double width, double height) => new(At(time, lane), At(time + width, lane + height));

        /// <inheritdoc />
        public override void Render(DrawingContext context)
        {
            context.DrawRectangle(Brushes.Transparent, null, new Rect(Bounds.Size));
            if (_owner is null || Bounds.Width < 16 || Bounds.Height < 16)
            {
                return;
            }

            if (_lanes is null)
            {
                _lanes = new DrawingGroup();
                using var draw = _lanes.Open();
                DrawLanes(draw);
            }
            _lanes.Draw(context);
            var cursor = Length * Math.Clamp(_owner.PositionSeconds / _owner.DurationSeconds, 0, 1);
            context.DrawLine(new Pen(Handle, 1.5), At(cursor, 0), At(cursor, Cross));
            context.DrawEllipse(Handle, null, At(cursor, Cross - 1), 4.5, 4.5);
        }

        private void DrawLanes(DrawingContext draw)
        {
            var width = Length / 128;
            var density = _owner?.ChartDensity;
            var waveform = _owner?.ChartWaveform;
            var peak = waveform is { Count: 128 } ? Math.Max(1, waveform.Max()) : 1;
            var max = 1;
            if (density is { Count: 640 })
            {
                for (var i = 0; i < 640; i += 5)
                {
                    max = Math.Max(max, density[i] + density[i + 1] + density[i + 2] + density[i + 3] + density[i + 4]);
                }
            }
            for (var i = 0; i < 128; i++)
            {
                if (waveform is { Count: 128 })
                {
                    var amplitude = waveform[i] / (double)peak * Cross * 0.16;
                    draw.DrawRectangle(Wave, null, Bar(i * width, Cross * 0.17 - amplitude, width * 0.88, 2 * amplitude));
                }
                if (density is not { Count: 640 })
                {
                    continue;
                }

                var bottom = Cross * 0.83;
                for (var t = 0; t < 5; t++)
                {
                    var height = Cross * 0.45 * density[i * 5 + t] / max;
                    if (height <= 0)
                    {
                        continue;
                    }

                    bottom -= height;
                    draw.DrawRectangle(Types[t], null, Bar(i * width, bottom, width * 0.84, height));
                }
            }
            draw.DrawLine(new Pen(Rail, 2), At(0, Cross - 1), At(Length, Cross - 1));
        }

        /// <inheritdoc />
        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            if (_pointer is not null || _owner?.BeginSeek() != true)
            {
                return;
            }

            _pointer = e.Pointer;
            e.Pointer.Capture(this);
            Focus();
            Move(e);
        }

        private void Move(PointerEventArgs e)
        {
            var point = e.GetPosition(this);
            if (_owner is not null)
            {
                _owner.PositionSeconds = Math.Clamp(((Vertical ? point.Y : point.X) - 8) / Length, 0, 1) * _owner.DurationSeconds;
            }

            e.PreventGestureRecognition();
            e.Handled = true;
        }

        /// <inheritdoc />
        protected override void OnPointerMoved(PointerEventArgs e)
        {
            if (e.Pointer == _pointer)
            {
                Move(e);
            }
        }

        /// <inheritdoc />
        protected override void OnPointerReleased(PointerReleasedEventArgs e)
        {
            if (e.Pointer != _pointer)
            {
                return;
            }

            Move(e);
            _pointer = null;
            _ = _owner?.CommitSeekAsync();
            e.Pointer.Capture(null);
        }

        /// <inheritdoc />
        protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
        {
            if (e.Pointer == _pointer)
            { _pointer = null; _owner?.CancelSeek(); }
        }

        /// <inheritdoc />
        protected override void OnKeyDown(KeyEventArgs e)
        {
            var delta = e.Key switch { Key.Left or Key.Up => -1, Key.Right or Key.Down => 1, _ => 0 };
            if (delta == 0 || _owner?.BeginSeek() != true)
            {
                return;
            }

            _owner.PositionSeconds += delta * (e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? 10 : 1);
            _ = _owner.CommitSeekAsync();
            e.Handled = true;
        }
    }
}

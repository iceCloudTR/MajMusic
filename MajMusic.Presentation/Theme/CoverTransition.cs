#nullable enable

using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace MajMusic.Presentation.Theme
{
    /// <summary>Crossfades artwork using owned bounded clones rather than borrowing the view model's disposable bitmap.</summary>
    public sealed class CoverTransition : FixedTransitionHost
    {
        /// <summary>Identifies externally owned artwork that may be released as soon as the binding changes.</summary>
        public static readonly StyledProperty<Bitmap?> SourceProperty = AvaloniaProperty.Register<CoverTransition, Bitmap?>(nameof(Source));
        /// <summary>Identifies the existing artwork fitting policy.</summary>
        public static readonly StyledProperty<Stretch> StretchProperty = AvaloniaProperty.Register<CoverTransition, Stretch>(nameof(Stretch), Stretch.UniformToFill);
        /// <summary>Contains only independently owned artwork.</summary>
        private readonly Image _image = new() { Stretch = Stretch.UniformToFill };
        /// <summary>Owns the current bounded image clone.</summary>
        private Bitmap? _owned;

        /// <summary>Creates one live image within the unchanged artwork viewport.</summary>
        public CoverTransition()
        {
            Content = _image;
        }

        /// <summary>Gets or sets externally owned current artwork.</summary>
        public Bitmap? Source
        {
            get => GetValue(SourceProperty);
            set => SetValue(SourceProperty, value);
        }
        /// <summary>Gets or sets the existing image fitting policy.</summary>
        public Stretch Stretch
        {
            get => GetValue(StretchProperty);
            set => SetValue(StretchProperty, value);
        }

        /// <inheritdoc />
        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            ReplaceOwnedImage();
            base.OnAttachedToVisualTree(e);
        }

        /// <inheritdoc />
        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnDetachedFromVisualTree(e);
            _image.Source = null;
            _owned?.Dispose();
            _owned = null;
        }

        /// <inheritdoc />
        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            if (change.Property == SourceProperty)
            {
                BeginTransition();
                if (TopLevel.GetTopLevel(this) is not null)
                {
                    ReplaceOwnedImage();
                }
            }
            else if (change.Property == StretchProperty)
            {
                _image.Stretch = Stretch;
            }

            base.OnPropertyChanged(change);
        }

        /// <summary>Clones incoming pixels before allowing the external owner to release the original image.</summary>
        private void ReplaceOwnedImage()
        {
            Bitmap? next = null;
            try
            {
                if (Source is { } source)
                {
                    var size = source.PixelSize;
                    var ratio = Math.Min(1, 512d / Math.Max(size.Width, size.Height));
                    next = source.CreateScaledBitmap(new PixelSize(Math.Max(1, (int)Math.Round(size.Width * ratio)),
                        Math.Max(1, (int)Math.Round(size.Height * ratio))), BitmapInterpolationMode.MediumQuality);
                }
            }
            catch (ObjectDisposedException)
            {
                // An obsolete detached binding may refer to a bitmap that its view model already replaced.
            }

            var previous = _owned;
            _owned = next;
            _image.Source = next;
            previous?.Dispose();
        }
    }
}

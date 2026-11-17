#nullable enable

using Avalonia;

namespace MajMusic.Presentation.Theme
{
    /// <summary>Crossfades the existing play and pause paths without moving their painted geometry.</summary>
    public sealed class PlayPauseTransition : FixedTransitionHost
    {
        /// <summary>Identifies the authoritative playback state.</summary>
        public static readonly StyledProperty<bool> IsPlayingProperty = AvaloniaProperty.Register<PlayPauseTransition, bool>(nameof(IsPlaying));
        /// <summary>Gets or sets whether the existing canvas currently displays its pause icon.</summary>
        public bool IsPlaying
        {
            get => GetValue(IsPlayingProperty);
            set => SetValue(IsPlayingProperty, value);
        }
        /// <summary>Keeps both icon surfaces at their exact resting geometry throughout the opacity transition.</summary>
        protected override bool IsGlyph => false;
        /// <inheritdoc />
        protected override TimeSpan FullDuration => TimeSpan.FromMilliseconds(150);

        /// <inheritdoc />
        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            if (change.Property == IsPlayingProperty)
            {
                BeginTransition();
            }

            base.OnPropertyChanged(change);
        }
    }
}

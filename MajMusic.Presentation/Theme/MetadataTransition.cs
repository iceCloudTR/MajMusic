#nullable enable

using Avalonia;

namespace MajMusic.Presentation.Theme
{
    /// <summary>Transitions existing fixed-size title and artist controls without replacing their layout or bindings.</summary>
    public sealed class MetadataTransition : FixedTransitionHost
    {
        /// <summary>Identifies a title or source change.</summary>
        public static readonly StyledProperty<object?> ChangeKeyProperty = AvaloniaProperty.Register<MetadataTransition, object?>(nameof(ChangeKey));
        /// <summary>Identifies an independent artist change in the same binding turn.</summary>
        public static readonly StyledProperty<object?> SecondaryKeyProperty = AvaloniaProperty.Register<MetadataTransition, object?>(nameof(SecondaryKey));
        /// <summary>Gets or sets the primary title change identity.</summary>
        public object? ChangeKey
        {
            get => GetValue(ChangeKeyProperty);
            set => SetValue(ChangeKeyProperty, value);
        }
        /// <summary>Gets or sets the secondary artist change identity.</summary>
        public object? SecondaryKey
        {
            get => GetValue(SecondaryKeyProperty);
            set => SetValue(SecondaryKeyProperty, value);
        }
        /// <inheritdoc />
        protected override double TextTravel => 6;

        /// <inheritdoc />
        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            if (change.Property == ChangeKeyProperty || change.Property == SecondaryKeyProperty)
            {
                BeginTransition();
            }

            base.OnPropertyChanged(change);
        }
    }
}

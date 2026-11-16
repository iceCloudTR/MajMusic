#nullable enable

using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace MajMusic.Android
{
    /// <summary>Shares application services between recreated Android activity views.</summary>
    public sealed partial class MobileApplication : Avalonia.Application
    {
        /// <summary>Owns the workspace and audio session independently of the activity.</summary>
        private AndroidServices? _services;

        /// <inheritdoc />
        public override void Initialize()
        {
            AvaloniaXamlLoader.Load(this);
        }

        /// <inheritdoc />
        public override void OnFrameworkInitializationCompleted()
        {
            _services = new AndroidServices(global::Android.App.Application.Context);
            if (ApplicationLifetime is IActivityApplicationLifetime lifetime)
            {
                lifetime.MainViewFactory = _services.CreateMainView;
            }

            base.OnFrameworkInitializationCompleted();
        }
    }
}

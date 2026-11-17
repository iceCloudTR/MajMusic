#nullable enable

using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using MajMusic.Android.Preview;
using MajMusic.Presentation.Views;

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
            ChartPreviewPane.CreateEmbeddedView = static (address, failed) => new AndroidMajViewHost(address, failed);
            _services = new AndroidServices(global::Android.App.Application.Context);
            if (ApplicationLifetime is IActivityApplicationLifetime lifetime)
            {
                lifetime.MainViewFactory = _services.CreateMainView;
            }

            base.OnFrameworkInitializationCompleted();
        }

        /// <summary>Releases a foreground chart preview while leaving music playback services in place.</summary>
        internal void CloseChartPreview()
        {
            _services?.CloseChartPreview();
        }
    }
}

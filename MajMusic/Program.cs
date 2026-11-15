using Avalonia;
using System;

#nullable enable

namespace MajMusic
{
    /// <summary>Starts the desktop host independently of the shared views.</summary>
    internal static class Program
    {
        /// <summary>Starts the desktop application.</summary>
        /// <param name="args">The command-line arguments.</param>
        [STAThread]
        public static void Main(string[] args)
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }

        /// <summary>Configures the desktop application and the visual designer entry point.</summary>
        /// <returns>The configured application builder.</returns>
        public static AppBuilder BuildAvaloniaApp()
        {
            return AppBuilder.Configure<App>()
                .UsePlatformDetect()
#if DEBUG
                .WithDeveloperTools()
#endif
                .WithInterFont()
                .LogToTrace();
        }
    }
}

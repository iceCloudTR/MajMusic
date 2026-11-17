using Avalonia.Controls;

using MajMusic.Presentation.ViewModels;

#nullable enable

namespace MajMusic
{
    /// <summary>Hosts a shared music view inside a resizable desktop window.</summary>
    public partial class MainWindow : Window
    {
        /// <summary>Owns the desktop application services when constructed at runtime.</summary>
        private readonly IDisposable? _services;
        /// <summary>Tracks the asynchronous close sequence.</summary>
        private bool _isClosing;
        /// <summary>Tracks whether resources have been released.</summary>
        private bool _isDisposed;

        /// <summary>Initializes the desktop window.</summary>
        public MainWindow()
        {
            InitializeComponent();
        }

        /// <summary>Initializes the runtime window and its owned services.</summary>
        /// <param name="viewModel">The shared music view model.</param>
        /// <param name="services">The services released when the window closes.</param>
        /// <param name="mobilePreview">Whether to open the shared mobile layout at a phone-sized width.</param>
        public MainWindow(MainViewModel viewModel, IDisposable services, bool mobilePreview = false) : this()
        {
            DataContext = viewModel;
            _services = services;

            if (mobilePreview)
            {
                MinWidth = 320;
                Width = 420;
                Height = 760;
                Title = "MajMusic · 移动端预览";
            }
        }

        /// <summary>Initializes the workspace after the window has opened.</summary>
        /// <param name="e">The window event arguments.</param>
        protected override async void OnOpened(EventArgs e)
        {
            base.OnOpened(e);
            if (DataContext is MainViewModel viewModel)
            {
                await viewModel.InitializeAsync();
            }
        }

        /// <summary>Cancels outstanding work and releases the media backend before exiting.</summary>
        /// <param name="e">The cancellable close event.</param>
        protected override async void OnClosing(WindowClosingEventArgs e)
        {
            if (_isDisposed)
            {
                base.OnClosing(e);
                return;
            }

            e.Cancel = true;
            base.OnClosing(e);
            if (_isClosing)
            {
                return;
            }

            _isClosing = true;
            try
            {
                if (DataContext is MainViewModel viewModel)
                {
                    await viewModel.DisposeAsync();
                }
            }
            catch (Exception exception)
            {
                System.Diagnostics.Trace.TraceError("MajMusic shutdown failed: {0}", exception.GetType().Name);
            }
            finally
            {
                _services?.Dispose();
                _isDisposed = true;
                Close();
            }
        }
    }
}

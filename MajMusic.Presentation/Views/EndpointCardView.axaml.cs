#nullable enable

using Avalonia.Controls;
using MajMusic.Presentation.ViewModels;

namespace MajMusic.Presentation.Views
{
    /// <summary>Edits one independently configured endpoint and its selected saved identity.</summary>
    public partial class EndpointCardView : UserControl
    {
        /// <summary>Initializes the endpoint and account editor.</summary>
        public EndpointCardView()
        {
            InitializeComponent();
        }

        /// <summary>Persists an account choice only when a real option enters the selection.</summary>
        /// <param name="sender">The saved-account selector belonging to this endpoint.</param>
        /// <param name="e">The added and removed selection entries.</param>
        private void OnSavedAccountSelectionChanged(object? sender, SelectionChangedEventArgs e)
        {
            if (e.AddedItems.OfType<SavedAccountOption>().Any()
                && DataContext is EndpointCardViewModel viewModel
                && viewModel.SelectAccountCommand.CanExecute(null))
            {
                viewModel.SelectAccountCommand.Execute(null);
            }
        }
    }
}

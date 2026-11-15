#nullable enable

using System.Windows.Input;

namespace MajMusic.Presentation.Commands
{
    /// <summary>Observes command failures and prevents duplicate execution.</summary>
    public sealed class AsyncCommand : ICommand
    {
        /// <summary>Stores the asynchronous operation.</summary>
        private readonly Func<Task> _execute;
        /// <summary>Stores the optional availability check.</summary>
        private readonly Func<bool>? _canExecute;
        /// <summary>Reports failures to the owning view model.</summary>
        private readonly Action<Exception> _onError;
        /// <summary>Tracks whether an execution is in progress.</summary>
        private bool _isExecuting;

        /// <summary>Creates an observed asynchronous command.</summary>
        /// <param name="execute">The operation to run.</param>
        /// <param name="onError">The callback reporting execution errors.</param>
        /// <param name="canExecute">The optional availability check.</param>
        public AsyncCommand(Func<Task> execute, Action<Exception> onError, Func<bool>? canExecute = null)
        {
            _execute = execute;
            _onError = onError;
            _canExecute = canExecute;
        }

        /// <summary>Occurs when command availability changes.</summary>
        public event EventHandler? CanExecuteChanged;

        /// <summary>Determines whether the command can run.</summary>
        /// <param name="parameter">The unused command parameter.</param>
        /// <returns>Whether the operation is available.</returns>
        public bool CanExecute(object? parameter)
        {
            return !_isExecuting && (_canExecute?.Invoke() ?? true);
        }

        /// <summary>Runs an operation and reports any observed failure.</summary>
        /// <param name="parameter">The unused command parameter.</param>
        public async void Execute(object? parameter)
        {
            if (!CanExecute(parameter))
            {
                return;
            }

            _isExecuting = true;
            NotifyCanExecuteChanged();
            try
            {
                await _execute();
            }
            catch (OperationCanceledException)
            {
                // The owning view lifetime or an explicit offline switch canceled this action.
            }
            catch (Exception exception)
            {
                _onError(exception);
            }
            finally
            {
                _isExecuting = false;
                NotifyCanExecuteChanged();
            }
        }

        /// <summary>Refreshes the command's availability.</summary>
        public void NotifyCanExecuteChanged()
        {
            CanExecuteChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}

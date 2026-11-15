#nullable enable

using MajMusic.Presentation.Commands;

namespace MajMusic.Tests
{
    /// <summary>Provides assertions for the dependency-free regression runner.</summary>
    internal static class TestAssert
    {
        /// <summary>Runs an available command and observes its return to availability without assuming synchronous query completion.</summary>
        /// <param name="command">The finite command expected to remain available after completing its work.</param>
        /// <returns>A task representing the command's completed observed execution.</returns>
        /// <exception cref="InvalidOperationException">The command is not initially available.</exception>
        /// <exception cref="TimeoutException">The command does not return to availability within three seconds.</exception>
        public static async Task ExecuteAsync(AsyncCommand command)
        {
            True(command.CanExecute(null), "The requested command must be available before execution.");
            var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var executionObserved = false;
            void OnAvailabilityChanged(object? sender, EventArgs args)
            {
                if (!command.CanExecute(null))
                {
                    executionObserved = true;
                }
                else if (executionObserved)
                {
                    completed.TrySetResult();
                }
            }

            command.CanExecuteChanged += OnAvailabilityChanged;
            try
            {
                command.Execute(null);
                if (command.CanExecute(null))
                {
                    completed.TrySetResult();
                }

                await completed.Task.WaitAsync(TimeSpan.FromSeconds(3));
            }
            finally
            {
                command.CanExecuteChanged -= OnAvailabilityChanged;
            }
        }

        /// <summary>Checks a condition and reports its purpose on failure.</summary>
        /// <param name="condition">The condition expected to be true.</param>
        /// <param name="message">The failure description.</param>
        public static void True(bool condition, string message)
        {
            if (!condition)
            {
                throw new InvalidOperationException(message);
            }
        }

        /// <summary>Compares expected and actual values.</summary>
        /// <typeparam name="T">The compared value type.</typeparam>
        /// <param name="expected">The expected value.</param>
        /// <param name="actual">The actual value.</param>
        /// <param name="message">The failure description.</param>
        public static void Equal<T>(T expected, T actual, string message)
        {
            True(EqualityComparer<T>.Default.Equals(expected, actual), $"{message}: expected {expected}, actual {actual}");
        }

        /// <summary>Checks that an asynchronous action throws the expected exception.</summary>
        /// <typeparam name="T">The expected exception type.</typeparam>
        /// <param name="action">The action to execute.</param>
        /// <returns>A task representing the assertion.</returns>
        public static async Task ThrowsAsync<T>(Func<Task> action) where T : Exception
        {
            try
            {
                await action();
            }
            catch (T)
            {
                return;
            }

            throw new InvalidOperationException($"Expected {typeof(T).Name}.");
        }
    }
}

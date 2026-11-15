#nullable enable

namespace MajMusic.Tests
{
    /// <summary>Provides assertions for the dependency-free regression runner.</summary>
    internal static class TestAssert
    {
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

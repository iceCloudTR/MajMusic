#nullable enable

using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Headless;
using Avalonia.Input.TextInput;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MajMusic.Core.Models;
using MajMusic.Core.Playback;
using MajMusic.Presentation.ViewModels;
using MajMusic.Presentation.Views;
using MobileTextField = TKCode.Arc.Mobile.TextField;

namespace MajMusic.Tests
{
    /// <summary>Checks actual mobile account keyboard hints and mixed-case credential binding using generated input only.</summary>
    internal static class MobileAccountInputTests
    {
        /// <summary>Identifies this test's worker process without changing production behavior or the selected validation groups.</summary>
        private const string WorkerEnvironmentKey = "MAJMUSIC_MOBILE_ACCOUNT_INPUT_TEST_WORKER";

        /// <summary>Checks realized Arc mobile account fields without reading saved secrets or contacting a source.</summary>
        /// <returns>A task representing the mobile input configuration and binding regressions.</returns>
        public static async Task RunAsync()
        {
            if (Environment.GetEnvironmentVariable(WorkerEnvironmentKey) == "1")
            {
                await RunHeadlessAsync();
                return;
            }

            // Arc's process-global icon geometries retain their originating dispatcher. A separate
            // process gives this additional Android input fixture a complete application lifetime.
            var start = new ProcessStartInfo
            {
                FileName = Environment.ProcessPath ?? throw new InvalidOperationException("The validation executable is unavailable."),
                WorkingDirectory = Environment.CurrentDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            if (string.Equals(Path.GetFileNameWithoutExtension(start.FileName), "dotnet", StringComparison.OrdinalIgnoreCase))
            {
                start.ArgumentList.Add(typeof(MobileAccountInputTests).Assembly.Location);
            }

            start.ArgumentList.Add("mobile-account-input");
            start.Environment[WorkerEnvironmentKey] = "1";
            using var process = Process.Start(start) ?? throw new InvalidOperationException("The mobile input validation worker could not start.");
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync();
                }

                await Task.WhenAll(output, error);
                throw;
            }

            var diagnostics = (await output) + (await error);
            TestAssert.True(process.ExitCode == 0, "The isolated mobile input and binding regression must pass. " + diagnostics);
        }

        /// <summary>Exercises the real mobile template in one process-owned Avalonia application and dispatcher.</summary>
        /// <returns>A task representing all actual keyboard-hint and two-way binding assertions.</returns>
        private static async Task RunHeadlessAsync()
        {
            await using var session = HeadlessUnitTestSession.StartNew(typeof(HeadlessTestApplication));
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await session.Dispatch<bool>(async () =>
            {
                var account = new SavedEndpointAccount { Id = "generated-account", Username = "Generated.Saved" };
                var endpoint = new EndpointConfiguration
                {
                    Id = "generated-endpoint",
                    Name = "Generated endpoint",
                    BaseUri = new Uri("https://generated.invalid/api/"),
                    Accounts = [account],
                    SelectedAccountId = account.Id
                };
                var workspace = new LibraryExperienceTests.ExperienceWorkspace
                {
                    Settings = new AppSettings { Endpoints = [endpoint], ActiveEndpointId = endpoint.Id }
                };
                await using var model = new MainViewModel(workspace, new PlaybackService(new UnavailablePlaybackBackend()));
                await model.InitializeAsync().WaitAsync(timeout.Token);
                var view = new MainView { DataContext = model };
                var window = new Window { Content = view, Width = 390, Height = 844 };
                try
                {
                    window.Show();
                    Layout(window);
                    view.NavigateToSection(3);
                    Layout(window);
                    var mobile = view.GetVisualDescendants().OfType<MobileMusicView>().Single();
                    var fields = mobile.GetVisualDescendants().OfType<MobileTextField>().ToArray();
                    var username = fields.Single(field => field.PlaceholderText == "账号");
                    var password = fields.Single(field => field.PlaceholderText == "密码");
                    var card = model.EndpointCards.Single();
                    TestAssert.True(ReferenceEquals(username.DataContext, card) && ReferenceEquals(password.DataContext, card)
                        && username.IsEnabled && password.IsEnabled,
                        "The real mobile account template must edit its saved endpoint card rather than an unrelated or hidden form.");

                    var usernameOptions = TextInputOptions.FromStyledElement(username);
                    var passwordOptions = TextInputOptions.FromStyledElement(password);
                    TestAssert.True(usernameOptions.ShowSuggestions == false && !usernameOptions.AutoCapitalization,
                        "The account keyboard must preserve case and avoid prediction replacing an account name.");
                    TestAssert.True(passwordOptions.ContentType == TextInputContentType.Password && passwordOptions.IsSensitive
                        && passwordOptions.ShowSuggestions == false && !passwordOptions.AutoCapitalization && password.PasswordChar == '●',
                        "The masked field must request the actual native sensitive password keyboard, with suggestions and automatic capitalization disabled.");

                    const string generatedUsername = "GeNerated.User";
                    username.Focus();
                    username.SelectAll();
                    window.KeyTextInput(generatedUsername);
                    Layout(window);
                    TestAssert.Equal(generatedUsername, card.Username, "Committed mobile account text must retain exact mixed case in its two-way view-model binding.");

                    const string generatedPassword = "Generated_Test! 7";
                    password.Focus();
                    password.SelectAll();
                    window.KeyTextInput(generatedPassword);
                    Layout(window);
                    TestAssert.True(card.Password == generatedPassword && password.Text == generatedPassword
                        && password.GetVisualDescendants().OfType<TextPresenter>().Single().Text == generatedPassword,
                        "Masking must affect rendering only; generated committed input must reach the view model and presenter unchanged.");

                    const string replacement = "ReplacementInput_42?";
                    card.Password = replacement;
                    Layout(window);
                    username.Focus();
                    Layout(window);
                    TestAssert.True(password.Text == replacement && card.Password == replacement && card.Username == generatedUsername,
                        "View-model replacement and leaving the password field must preserve the two-way credential binding without rewriting case or masking the stored value.");
                    TestAssert.True(!workspace.IsOnline && workspace.Logins.Count == 0,
                        "Keyboard and binding checks must remain offline and must never submit generated credentials.");
                }
                finally
                {
                    window.Close();
                }

                return true;
            }, timeout.Token);
        }

        /// <summary>Runs queued bindings and actual templates before inspecting or editing mobile inputs.</summary>
        /// <param name="window">The headless mobile presentation.</param>
        private static void Layout(Window window)
        {
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
        }
    }
}

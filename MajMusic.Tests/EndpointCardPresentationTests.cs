#nullable enable

using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using MajMusic.Core.Abstractions;
using MajMusic.Core.Models;
using MajMusic.Core.Playback;
using MajMusic.Infrastructure.Application;
using MajMusic.Infrastructure.Storage;
using MajMusic.Presentation.Commands;
using MajMusic.Presentation.ViewModels;

namespace MajMusic.Tests
{
    /// <summary>Checks independent endpoint-card editing, authenticated sessions and narrow preference persistence.</summary>
    internal static class EndpointCardPresentationTests
    {
        /// <summary>Runs synthetic multi-source, credential-cleanup and concurrent-settings regressions.</summary>
        /// <returns>A task representing all endpoint-card cases.</returns>
        public static async Task RunAsync()
        {
            var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "MajMusic-endpoint-cards-" + Guid.NewGuid().ToString("N")));
            try
            {
                await ValidateIndependentCardsAsync(Path.Combine(root, "independent"));
                await ValidateFailedSelectionAsync(Path.Combine(root, "failed-selection"));
                await ValidateRemovalAndAddressChangesAsync(Path.Combine(root, "removal"));
                await ValidateConcurrentPreferencesAsync(Path.Combine(root, "concurrent"));
            }
            finally
            {
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                var temporaryParent = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                if (!root.StartsWith(temporaryParent, StringComparison.OrdinalIgnoreCase)
                    || !Path.GetFileName(root).StartsWith("MajMusic-endpoint-cards-", StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("Refusing to clean outside the endpoint-card fixture.");
                }

                if (Directory.Exists(root))
                {
                    Directory.Delete(root, true);
                }
            }
        }

        /// <summary>Logs into separate cards without switching the browsing source or replacing another card's draft.</summary>
        /// <param name="root">The isolated application-data directory.</param>
        /// <returns>A task representing independent source and account operations.</returns>
        private static async Task ValidateIndependentCardsAsync(string root)
        {
            await using var harness = new Harness(root);
            await harness.OpenAsync();
            var view = harness.View;
            TestAssert.Equal(3, view.EndpointCards.Count, "Every saved source must have its own card.");
            TestAssert.True(view.EndpointCards.All(card => card.IsGuest && card.IsIdle && card.Password.Length == 0), "Saved Guest cards load without reading passwords.");
            TestAssert.True(harness.Transport.Requests.IsEmpty && harness.Vault.ReadCount == 0, "Opening offline cards performs no authentication or secret lookup.");
            var first = harness.Card("a");
            var second = harness.Card("b");
            var third = harness.Card("c");
            view.SelectedEndpointProfile = view.EndpointProfiles.Single(profile => profile.Configuration.Id == third.Id);
            await view.HomeUpdate.WaitAsync(TimeSpan.FromSeconds(4));
            await SaveAccountAsync(first, "Fixture Alice", "Generated-card-A-fixture", true, false);
            await SaveAccountAsync(second, "Fixture Bob", "Generated-card-B-fixture", true, false);
            await SaveAccountAsync(third, "Fixture Guest choice", null, false, false);
            await SaveAccountAsync(third, "Fixture other C account", null, false, false);
            third.SelectedAccount = third.Accounts.Single(option => option.Account?.Username == "Fixture Guest choice");
            await ExecuteCardAsync(third, third.SelectAccountCommand);
            TestAssert.True(third.Username == "Fixture Guest choice" && third.Configuration?.SelectedAccountId == third.SelectedAccount?.AccountId,
                "A card can independently restore a different remembered account selection.");
            TestAssert.Equal(third.Id, view.SelectedEndpointProfile!.Configuration.Id, "Saving a card cannot switch the browsing source.");
            TestAssert.True(first.Accounts.Count == 2 && second.Accounts.Count == 2 && first.SelectedAccount?.Account?.Username == "Fixture Alice"
                && second.SelectedAccount?.Account?.Username == "Fixture Bob", "Each card retains its own remembered account options.");
            await ValidateOnlineCommandNotificationsAsync(view, first);
            view.OnlineEnabled = true;
            await view.OnlineSettingsUpdate.WaitAsync(TimeSpan.FromSeconds(4));
            await view.AutomaticLogin.WaitAsync(TimeSpan.FromSeconds(4));
            TestAssert.True(harness.Transport.Logins.IsEmpty, "A manual-only remembered account cannot log in when online mode is enabled.");

            harness.Transport.BlockLoginHost = Endpoint("a").BaseUri.Host;
            TestAssert.True(first.LoginCommand.CanExecute(null), "A remembered password must enable explicit login without populating its field.");
            first.LoginCommand.Execute(null);
            await harness.Transport.LoginStarted.Task.WaitAsync(TimeSpan.FromSeconds(4));
            try
            {
                TestAssert.True(!first.IsIdle && second.IsIdle && third.IsIdle && view.IsIdle, "A blocked source login disables only that card's operations.");
                await ExecuteCardAsync(second, second.LoginCommand);
                TestAssert.True(Session(harness.Workspace, "b").IsLoggedIn && !Session(harness.Workspace, "a").IsLoggedIn,
                    "A second source can complete authentication while the first source is waiting.");
            }
            finally
            {
                harness.Transport.ReleaseLogin.TrySetResult();
            }

            await WaitForCardAsync(first);
            TestAssert.True(Session(harness.Workspace, "a") is { IsLoggedIn: true, Username: "Fixture Alice" }
                && Session(harness.Workspace, "b") is { IsLoggedIn: true, Username: "Fixture Bob" }, "Both independent source sessions remain authenticated together.");
            TestAssert.True(first.Status == "Fixture Alice" && second.Status == "Fixture Bob", "Each card's session label reflects its own authenticated account.");
            TestAssert.True(first.Password.Length == 0 && second.Password.Length == 0 && harness.Vault.ReadCount >= 2,
                "Saved logins read only the fake vault and retain no presentation password.");
            third.Password = "Generated-transient-C-fixture";
            await ExecuteCardAsync(third, third.LoginCommand);
            TestAssert.True(harness.Workspace.GetEndpointSessions().All(session => session.IsLoggedIn), "Three independently configured source cards can remain logged in together.");
            third.SelectedAccount = third.Accounts.Single(option => option.AccountId is null);
            await ExecuteCardAsync(third, third.SelectAccountCommand);
            TestAssert.True(third.IsGuest && !Session(harness.Workspace, "c").IsLoggedIn
                && Session(harness.Workspace, "a").IsLoggedIn && Session(harness.Workspace, "b").IsLoggedIn, "Choosing Guest on a third card cannot end other source sessions.");
            TestAssert.True(third.Status == "Guest" && first.Status == "Fixture Alice" && second.Status == "Fixture Bob", "Guest selection updates only its own source's session label.");

            await ExecuteCardAsync(third, third.NewAccountCommand);
            third.EndpointName = "Unsaved third source";
            third.EndpointUrl = "https://draft-c.example.test/api/";
            third.Username = "Unsaved third account";
            third.Password = "Generated-unsaved-card-fixture";
            third.RememberPassword = true;
            third.AutoLogin = true;
            first.EndpointName = "Renamed first source";
            await ExecuteCardAsync(first, first.SaveEndpointCommand);
            await ExecuteCardAsync(second, second.SynchronizeCommand);
            AssertThirdDraft(third);
            TestAssert.True(ReferenceEquals(third, harness.Card("c")), "Refreshing source labels retains existing card object identities.");
            TestAssert.Equal(third.Id, view.SelectedEndpointProfile!.Configuration.Id, "Independent address saves and synchronization retain the browsing selection.");
            TestAssert.True(harness.Transport.Requests.Any(request => request.Host == Endpoint("b").BaseUri.Host
                && request.Path.EndsWith("maichart/list", StringComparison.Ordinal) && request.Account == "Fixture Bob"), "Synchronization uses the owning card's authenticated transport.");
            TestAssert.True(Session(harness.Workspace, "a").IsLoggedIn && Session(harness.Workspace, "b").IsLoggedIn, "Metadata synchronization cannot log out other source cards.");

            TestAssert.True(view.AddEndpointCardCommand.CanExecute(null), "An idle account page exposes source-card creation.");
            view.AddEndpointCardCommand.Execute(null);
            await WaitUntilAsync(() => view.EndpointCards.Count == 4 && view.IsIdle);
            var created = view.EndpointCards.Single(card => card.IsNew);
            created.EndpointName = "Fourth fixture source";
            created.EndpointUrl = "https://point-d.example.test/api/";
            await ExecuteCardAsync(created, created.SaveEndpointCommand);
            TestAssert.True(!created.IsNew && (await harness.Workspace.LoadSettingsAsync()).Endpoints.Count == 4,
                "Saving a new card adds only that configured source.");
            await ExecuteCardAsync(created, created.RemoveEndpointCommand, () => view.EndpointCards.All(card => card.Id != created.Id));
            AssertThirdDraft(third);
            TestAssert.Equal(third.Id, view.SelectedEndpointProfile!.Configuration.Id, "Adding and removing another source cannot switch the parent browsing choice.");
            var json = await File.ReadAllTextAsync(Path.Combine(root, "settings.json"));
            TestAssert.True(!json.Contains("Generated-", StringComparison.Ordinal),
                "Saved preferences cannot contain remembered or unsaved presentation passwords.");
        }

        /// <summary>Refreshes command bindings immediately after online-mode changes, independently of automatic login completion.</summary>
        /// <param name="view">The source-card owner.</param>
        /// <param name="card">The idle source with a manually remembered account.</param>
        /// <returns>A task representing both saved online choices.</returns>
        private static async Task ValidateOnlineCommandNotificationsAsync(MainViewModel view, EndpointCardViewModel card)
        {
            var loginStates = new ConcurrentQueue<bool>();
            var synchronizeStates = new ConcurrentQueue<bool>();
            EventHandler loginChanged = (_, _) => loginStates.Enqueue(card.LoginCommand.CanExecute(null));
            EventHandler synchronizeChanged = (_, _) => synchronizeStates.Enqueue(card.SynchronizeCommand.CanExecute(null));
            card.LoginCommand.CanExecuteChanged += loginChanged;
            card.SynchronizeCommand.CanExecuteChanged += synchronizeChanged;
            try
            {
                view.OnlineEnabled = true;
                TestAssert.True(loginStates.Contains(true) && synchronizeStates.Contains(true), "Enabling online mode must notify bound card buttons immediately.");
                await view.OnlineSettingsUpdate.WaitAsync(TimeSpan.FromSeconds(4));
                await view.AutomaticLogin.WaitAsync(TimeSpan.FromSeconds(4));
                loginStates.Clear();
                synchronizeStates.Clear();
                view.OnlineEnabled = false;
                TestAssert.True(loginStates.Contains(false) && synchronizeStates.Contains(false), "Disabling online mode must notify bound card buttons without waiting for another refresh.");
                await view.OnlineSettingsUpdate.WaitAsync(TimeSpan.FromSeconds(4));
            }
            finally
            {
                card.LoginCommand.CanExecuteChanged -= loginChanged;
                card.SynchronizeCommand.CanExecuteChanged -= synchronizeChanged;
            }
        }

        /// <summary>Clears old account-private data after failed automatic selection and keeps slow feeds outside the source-refresh gate.</summary>
        /// <param name="root">The isolated application-data directory.</param>
        /// <returns>A task representing failed-authentication and unrelated-card progress checks.</returns>
        private static async Task ValidateFailedSelectionAsync(string root)
        {
            await using var harness = new Harness(root);
            harness.Transport.ProvidePrivateData = true;
            harness.Transport.RejectedUsername = "Fixture failed selection";
            await harness.OpenAsync();
            var view = harness.View;
            var first = harness.Card("a");
            var second = harness.Card("b");
            await SaveAccountAsync(first, "Fixture previous owner", "Generated-previous-owner-fixture", true, false);
            await SaveAccountAsync(first, "Fixture failed selection", "Generated-failed-selection-fixture", true, true);
            var failingAccount = first.SelectedAccount!.AccountId;
            first.SelectedAccount = first.Accounts.Single(option => option.Account?.Username == "Fixture previous owner");
            await ExecuteCardAsync(first, first.SelectAccountCommand);
            view.OnlineEnabled = true;
            await view.OnlineSettingsUpdate.WaitAsync(TimeSpan.FromSeconds(4));
            await view.AutomaticLogin.WaitAsync(TimeSpan.FromSeconds(4));
            await ExecuteCardAsync(first, first.LoginCommand);
            TestAssert.True(view.SelectedEndpointProfile is { IsLoggedIn: true, Username: "Fixture previous owner" }
                && view.Tracks.Any(track => track.Title == "Fixture previous owner private music"), "The initial account must own a real loaded recommendation snapshot.");

            harness.Transport.BlockFeedHost = Endpoint("a").BaseUri.Host;
            first.LoginCommand.Execute(null);
            await harness.Transport.FeedStarted.Task.WaitAsync(TimeSpan.FromSeconds(4));
            try
            {
                second.EndpointName = "Saved during another feed";
                await ExecuteCardAsync(second, second.SaveEndpointCommand);
                TestAssert.True(!first.IsIdle && second.IsIdle && second.Configuration!.Name == "Saved during another feed",
                    "A source waiting for recommendations cannot hold the local-refresh gate needed by another card.");
            }
            finally
            {
                harness.Transport.ReleaseFeed.TrySetResult();
                harness.Transport.BlockFeedHost = null;
            }

            await WaitForCardAsync(first);
            view.RefreshCloudPlaylistsCommand.Execute(null);
            await WaitUntilAsync(() => view.IsIdle);
            TestAssert.True(view.Playlists.Any(playlist => playlist.IsCloud), "The previous account must own a loaded private cloud list before the selection fails.");
            await SaveAccountAsync(second, "Fixture unaffected owner", "Generated-unaffected-owner-fixture", true, false);
            await ExecuteCardAsync(second, second.LoginCommand);
            harness.Transport.RejectedLibraryHost = Endpoint("a").BaseUri.Host;
            try
            {
                TestAssert.True(first.SynchronizeCommand.CanExecute(null), "A logged-in source exposes synchronization before its session expires.");
                first.SynchronizeCommand.Execute(null);
                await WaitUntilAsync(() => first.IsIdle && first.Status == "请重新登录");
                TestAssert.Equal("请重新登录", view.Status, "A card's expired session must request reauthentication in both status locations.");
                TestAssert.True(!Session(harness.Workspace, first.Id).IsLoggedIn && view.SelectedEndpointProfile is { IsLoggedIn: false },
                    "A synchronization response with HTTP 401 must refresh the actual source and visible login state.");
                TestAssert.True(view.Tracks.Count == 0 && view.Playlists.All(playlist => !playlist.IsCloud), "Session expiry during a card operation clears that owner's private feed and cloud snapshots.");
                TestAssert.True(Session(harness.Workspace, second.Id) is { IsLoggedIn: true, Username: "Fixture unaffected owner" }
                    && second.Status == "Fixture unaffected owner", "A rejected source request cannot change another card's authenticated session or label.");
            }
            finally
            {
                harness.Transport.RejectedLibraryHost = null;
            }

            await ExecuteCardAsync(first, first.LoginCommand);
            view.RefreshCloudPlaylistsCommand.Execute(null);
            await WaitUntilAsync(() => view.IsIdle);
            TestAssert.True(view.Tracks.Any(track => track.Title == "Fixture previous owner private music")
                && view.Playlists.Any(playlist => playlist.IsCloud), "Successful reauthentication restores the selected owner's private snapshots before another account is chosen.");
            first.SelectedAccount = first.Accounts.Single(option => option.AccountId == failingAccount);
            TestAssert.True(first.SelectAccountCommand.CanExecute(null), "The alternate saved-account selection must be available.");
            first.SelectAccountCommand.Execute(null);
            await WaitUntilAsync(() => first.IsIdle && first.Status == "账号或密码错误");
            TestAssert.Equal("账号或密码错误", view.Status, "A real rejected saved-account login must explain its credentials failure in both status locations.");
            TestAssert.True(!Session(harness.Workspace, first.Id).IsLoggedIn && view.SelectedEndpointProfile is { IsLoggedIn: false }
                && view.SelectedEndpointProfile.Configuration.SelectedAccountId == failingAccount, "A failed automatic selection cannot leave the old account marked logged in.");
            TestAssert.True(first.Configuration!.SelectedAccountId == failingAccount && first.Username == "Fixture failed selection" && first.Password.Length == 0,
                "The failed-login editor must retain the actual saved account choice without a password.");
            TestAssert.True(view.Tracks.Count == 0 && view.Playlists.All(playlist => !playlist.IsCloud), "A failed account selection clears the previous owner's recommendation and cloud snapshots.");
        }

        /// <summary>Removes only the chosen account or source and invalidates credentials when its address changes.</summary>
        /// <param name="root">The isolated application-data directory.</param>
        /// <returns>A task representing scoped credential and connection cleanup.</returns>
        private static async Task ValidateRemovalAndAddressChangesAsync(string root)
        {
            await using var harness = new Harness(root);
            await harness.OpenAsync();
            var view = harness.View;
            var first = harness.Card("a");
            var second = harness.Card("b");
            var third = harness.Card("c");
            view.SelectedEndpointProfile = view.EndpointProfiles.Single(profile => profile.Configuration.Id == third.Id);
            await view.HomeUpdate.WaitAsync(TimeSpan.FromSeconds(4));
            await SaveAccountAsync(first, "Fixture Alice", "Generated-removal-A-fixture", true, false);
            await SaveAccountAsync(second, "Fixture Bob", "Generated-removal-B-fixture", true, false);
            view.OnlineEnabled = true;
            await view.OnlineSettingsUpdate.WaitAsync(TimeSpan.FromSeconds(4));
            await view.AutomaticLogin.WaitAsync(TimeSpan.FromSeconds(4));
            await ExecuteCardAsync(first, first.LoginCommand);
            await ExecuteCardAsync(second, second.LoginCommand);
            var requests = harness.Transport.Requests.Count;
            await ExecuteCardAsync(first, first.RemoveAccountCommand);
            TestAssert.True(first.IsGuest && first.Accounts.Count == 1 && !Session(harness.Workspace, "a").IsLoggedIn,
                "Removing the selected saved account returns only its source to Guest.");
            TestAssert.True(!harness.Vault.ContainsSecret("Generated-removal-A-fixture") && harness.Vault.ContainsSecret("Generated-removal-B-fixture")
                && Session(harness.Workspace, "b").IsLoggedIn, "Account removal deletes only its protected password and session.");
            TestAssert.Equal(requests, harness.Transport.Requests.Count, "Removing remembered account preferences is a local operation.");

            await SaveAccountAsync(first, "Fixture Alice replacement", "Generated-replacement-A-fixture", true, true);
            await ExecuteCardAsync(first, first.LoginCommand);
            var accountId = first.SelectedAccount!.AccountId;
            requests = harness.Transport.Requests.Count;
            await ExecuteCardAsync(second, second.RemoveEndpointCommand, () => view.EndpointCards.All(card => card.Id != second.Id));
            TestAssert.True(harness.Workspace.GetEndpointSessions().All(session => session.EndpointId != second.Id)
                && Session(harness.Workspace, "a").IsLoggedIn && !Session(harness.Workspace, "c").IsLoggedIn,
                "Removing one source retires only its own connection.");
            TestAssert.True(!harness.Vault.ContainsSecret("Generated-removal-B-fixture") && harness.Vault.ContainsSecret("Generated-replacement-A-fixture"),
                "Source removal cannot delete another source's retained credential.");
            TestAssert.Equal(requests, harness.Transport.Requests.Count, "Removing an endpoint does not contact unrelated or removed servers.");
            TestAssert.Equal(third.Id, view.SelectedEndpointProfile!.Configuration.Id, "Removing a nonselected card retains the browsing source.");
            var savedAccountName = first.Username;
            first.EndpointUrl = "https://replacement-a.example.test/other-api/";
            await ExecuteCardAsync(first, first.SaveEndpointCommand);
            var changed = (await harness.Workspace.LoadSettingsAsync()).Endpoints.Single(endpoint => endpoint.Id == first.Id);
            TestAssert.True(changed.Accounts.Single().Id == accountId && changed.Accounts.Single().Username == savedAccountName
                && changed.Accounts.All(account => !account.RememberPassword && !account.AutoLogin), "Changing an API root keeps account names while disabling remembered automatic authentication.");
            TestAssert.True(first.Username == savedAccountName && !first.RememberPassword && !first.AutoLogin && first.Password.Length == 0,
                "The changed-address card shows the retained name and cleared authentication flags.");
            TestAssert.True(!harness.Vault.ContainsSecret("Generated-replacement-A-fixture") && !Session(harness.Workspace, "a").IsLoggedIn,
                "An API address change deletes its old credential and retires the old authenticated connection.");
            TestAssert.True(changed.BaseUri.Host == "replacement-a.example.test" && Session(harness.Workspace, "c").EndpointId == third.Id,
                "Address cleanup retains the other configured Guest source.");
            TestAssert.Equal(third.Id, view.SelectedEndpointProfile!.Configuration.Id, "An address change cannot select its card as the parent browsing source.");
        }

        /// <summary>Saves two cards alongside online and audio preferences without replacing independent state.</summary>
        /// <param name="root">The isolated application-data directory.</param>
        /// <returns>A task representing controlled settings interleaving and restart persistence.</returns>
        private static async Task ValidateConcurrentPreferencesAsync(string root)
        {
            await using var harness = new Harness(root);
            await harness.OpenAsync();
            var view = harness.View;
            var first = harness.Card("a");
            var second = harness.Card("b");
            var third = harness.Card("c");
            await SaveAccountAsync(first, "Fixture saved A", "Generated-concurrent-A-fixture", true, false);
            await SaveAccountAsync(second, "Fixture saved B", "Generated-concurrent-B-fixture", true, false);
            view.SelectedEndpointProfile = view.EndpointProfiles.Single(profile => profile.Configuration.Id == third.Id);
            await view.HomeUpdate.WaitAsync(TimeSpan.FromSeconds(4));
            await ExecuteCardAsync(third, third.NewAccountCommand);
            third.EndpointName = "Unsaved third source";
            third.EndpointUrl = "https://draft-c.example.test/api/";
            third.Username = "Unsaved third account";
            third.Password = "Generated-unsaved-card-fixture";
            third.RememberPassword = true;
            third.AutoLogin = true;
            first.EndpointName = "Concurrent source A";
            second.EndpointName = "Concurrent source B";
            harness.Store.PauseNextSave();
            TestAssert.True(first.SaveEndpointCommand.CanExecute(null), "The first source save must be available.");
            first.SaveEndpointCommand.Execute(null);
            await harness.Store.Started.Task.WaitAsync(TimeSpan.FromSeconds(4));
            Task<AppSettings>? audio = null;
            try
            {
                TestAssert.True(!first.IsIdle && second.IsIdle && view.IsIdle, "One pending source save cannot block every card or the player.");
                second.SaveEndpointCommand.Execute(null);
                view.OnlineEnabled = true;
                audio = ((IPlaybackPreferencesWorkspace)harness.Workspace).UpdateAudioOutputAsync(new AudioOutputPreferences
                {
                    Volume = 24,
                    Muted = true,
                    DeviceId = "fixture-device"
                });
            }
            finally
            {
                harness.Store.Release.TrySetResult();
            }

            await WaitForCardAsync(first);
            await WaitForCardAsync(second);
            await audio!.WaitAsync(TimeSpan.FromSeconds(4));
            await view.OnlineSettingsUpdate.WaitAsync(TimeSpan.FromSeconds(4));
            await view.AutomaticLogin.WaitAsync(TimeSpan.FromSeconds(4));
            var saved = await harness.Store.LoadAsync();
            TestAssert.True(saved.Endpoints.Single(endpoint => endpoint.Id == "a").Name == "Concurrent source A"
                && saved.Endpoints.Single(endpoint => endpoint.Id == "b").Name == "Concurrent source B", "Concurrent card saves retain both independently edited source names.");
            TestAssert.True(saved.Endpoints.Single(endpoint => endpoint.Id == "a").Accounts.Single().Username == "Fixture saved A"
                && saved.Endpoints.Single(endpoint => endpoint.Id == "b").Accounts.Single().Username == "Fixture saved B", "Saving source addresses cannot overwrite current saved account choices.");
            TestAssert.True(saved.OnlineEnabled && saved.AudioOutput is { Volume: 24, Muted: true, DeviceId: "fixture-device" },
                "Endpoint persistence cannot replace independently queued online or audio preferences.");
            TestAssert.True(saved.Endpoints.Single(endpoint => endpoint.Id == "c").Name == "Point c"
                && saved.Endpoints.Single(endpoint => endpoint.Id == "c").Accounts.Count == 0, "An unrelated unsaved card draft cannot be published by another source save.");
            AssertThirdDraft(third);
            TestAssert.Equal(third.Id, view.SelectedEndpointProfile!.Configuration.Id, "Concurrent source saves do not select the card that finishes last.");
            TestAssert.True(harness.Transport.Logins.IsEmpty && harness.Vault.ReadCount == 0, "Preference-only updates cannot authenticate manual accounts or an unsaved draft.");

            var endpoints = (IEndpointPreferencesWorkspace)harness.Workspace;
            var snapshot = saved.Endpoints.Single(endpoint => endpoint.Id == "a");
            await harness.Workspace.SaveAccountAsync(snapshot, "Later remembered A", "Generated-later-account-fixture", true, false);
            await endpoints.SaveEndpointAsync(snapshot with { Name = "Latest source A", Accounts = Array.Empty<SavedEndpointAccount>(), SelectedAccountId = null });
            var latest = (await harness.Store.LoadAsync()).Endpoints.Single(endpoint => endpoint.Id == "a");
            TestAssert.True(latest.Name == "Latest source A" && latest.Accounts.Count == 2
                && latest.Accounts.Single(account => account.Id == latest.SelectedAccountId).Username == "Later remembered A",
                "A stale address-save snapshot retains account changes that completed after the snapshot was read.");
            using var reopened = new MusicWorkspace(root, harness.Client, harness.Client, credentialStore: harness.Vault);
            var restored = await reopened.LoadSettingsAsync();
            TestAssert.True(restored.Endpoints.Single(endpoint => endpoint.Id == "a").Name == "Latest source A"
                && restored.Endpoints.Single(endpoint => endpoint.Id == "b").Name == "Concurrent source B"
                && restored.AudioOutput.Volume == 24 && restored.OnlineEnabled, "Independent source and global preferences survive a fresh workspace instance.");
        }

        /// <summary>Creates and saves one card's account using only that card's public actions.</summary>
        /// <param name="card">The independently edited source.</param>
        /// <param name="username">The generated account name.</param>
        /// <param name="password">The generated password, or null for an unremembered account.</param>
        /// <param name="remember">Whether the fake vault should retain the password.</param>
        /// <param name="automatic">Whether the saved account may automatically log in.</param>
        /// <returns>A task representing account persistence.</returns>
        private static async Task SaveAccountAsync(EndpointCardViewModel card, string username, string? password, bool remember, bool automatic)
        {
            await ExecuteCardAsync(card, card.NewAccountCommand);
            card.Username = username;
            card.Password = password ?? string.Empty;
            card.RememberPassword = remember;
            card.AutoLogin = automatic;
            await ExecuteCardAsync(card, card.SaveAccountCommand);
            TestAssert.True(card.Username == username && card.Password.Length == 0 && card.SelectedAccount?.Account?.Username == username,
                "Saving an account keeps its source-specific selection and clears its typed password.");
        }

        /// <summary>Executes a card action and observes its local busy lifetime or completed removal.</summary>
        /// <param name="card">The independent source presentation.</param>
        /// <param name="command">The enabled source action.</param>
        /// <param name="removed">An optional predicate that observes removal of the disposed card.</param>
        /// <returns>A task representing command completion.</returns>
        private static async Task ExecuteCardAsync(EndpointCardViewModel card, AsyncCommand command, Func<bool>? removed = null)
        {
            TestAssert.True(command.CanExecute(null), "The requested card action must be available.");
            command.Execute(null);
            await WaitUntilAsync(() => card.IsIdle || removed?.Invoke() == true);
            TestAssert.True(card.Status != "操作未完成", "An endpoint-card operation must complete without a reported failure.");
        }

        /// <summary>Waits for a previously started independent card operation.</summary>
        /// <param name="card">The busy card.</param>
        /// <returns>A task representing its idle state.</returns>
        private static async Task WaitForCardAsync(EndpointCardViewModel card)
        {
            await WaitUntilAsync(() => card.IsIdle);
            TestAssert.True(card.Status != "操作未完成", "A pending card operation must complete successfully.");
        }

        /// <summary>Waits for an observable test boundary without depending on a fixed disk or network delay.</summary>
        /// <param name="condition">The state change expected from an observed asynchronous command.</param>
        /// <returns>A bounded task representing the state change.</returns>
        private static async Task WaitUntilAsync(Func<bool> condition)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (!condition())
            {
                await Task.Delay(5, timeout.Token);
            }
        }

        /// <summary>Checks that another card's complete unsaved form survives unrelated source operations.</summary>
        /// <param name="card">The third source's uncommitted presentation.</param>
        private static void AssertThirdDraft(EndpointCardViewModel card)
        {
            TestAssert.True(card.EndpointName == "Unsaved third source" && card.EndpointUrl == "https://draft-c.example.test/api/"
                && card.SelectedAccount is null && card.Username == "Unsaved third account" && card.Password == "Generated-unsaved-card-fixture"
                && card.RememberPassword && card.AutoLogin, "A different source's refresh cannot clear or save this card's unsaved endpoint and account draft.");
        }

        /// <summary>Reads the current runtime session for one configured fixture source.</summary>
        /// <param name="workspace">The real workspace with isolated synthetic transports.</param>
        /// <param name="id">The source identity.</param>
        /// <returns>The source's current runtime session.</returns>
        private static EndpointSession Session(MusicWorkspace workspace, string id)
        {
            return workspace.GetEndpointSessions().Single(session => session.EndpointId == id);
        }

        /// <summary>Creates an isolated synthetic source address.</summary>
        /// <param name="id">The source's independent saved identity.</param>
        /// <returns>The Guest source configuration.</returns>
        private static EndpointConfiguration Endpoint(string id)
        {
            return new EndpointConfiguration { Id = id, Name = "Point " + id, BaseUri = new Uri("https://point-" + id + ".example.test/api/") };
        }

        /// <summary>Owns real local persistence and presentation while replacing network, audio and vault operations.</summary>
        private sealed class Harness : IAsyncDisposable
        {
            /// <summary>Creates the isolated fixture services without accessing the network or system vault.</summary>
            /// <param name="root">The generated application-data directory.</param>
            public Harness(string root)
            {
                Store = new GateSettingsStore(Path.Combine(root, "settings.json"));
                Client = new HttpClient(Transport, false);
                Workspace = new MusicWorkspace(root, Client, Client, Store, Vault);
                View = new MainViewModel(Workspace, new PlaybackService(new UnavailablePlaybackBackend()), static action => action());
            }

            /// <summary>Gets the source-isolated synthetic HTTP handler.</summary>
            public CardTransport Transport { get; } = new();
            /// <summary>Gets the in-memory credential store.</summary>
            public MemoryVault Vault { get; } = new();
            /// <summary>Gets the controllable real local settings store.</summary>
            public GateSettingsStore Store { get; }
            /// <summary>Gets the in-memory HTTP client owned by this fixture.</summary>
            public HttpClient Client { get; }
            /// <summary>Gets the real account-aware workspace.</summary>
            public MusicWorkspace Workspace { get; }
            /// <summary>Gets the real independent-card presentation.</summary>
            public MainViewModel View { get; }

            /// <summary>Seeds three Guest sources and initializes their account-card catalog.</summary>
            /// <returns>A task representing the initialized local presentation.</returns>
            public async Task OpenAsync()
            {
                var settings = await Workspace.LoadSettingsAsync();
                await Workspace.SaveSettingsAsync(settings with
                {
                    ActiveEndpointId = "a",
                    Endpoints = [Endpoint("a"), Endpoint("b"), Endpoint("c")],
                    AudioOutput = new AudioOutputPreferences { Volume = 71 }
                });
                await View.InitializeAsync();
            }

            /// <summary>Finds one saved source card by stable identity.</summary>
            /// <param name="id">The saved source identity.</param>
            /// <returns>The source's stable card object.</returns>
            public EndpointCardViewModel Card(string id)
            {
                return View.EndpointCards.Single(card => card.Id == id);
            }

            /// <inheritdoc />
            public async ValueTask DisposeAsync()
            {
                Store.Release.TrySetResult();
                Transport.ReleaseLogin.TrySetResult();
                Transport.ReleaseFeed.TrySetResult();
                await View.DisposeAsync();
                Workspace.Dispose();
                Client.Dispose();
                Transport.Dispose();
            }
        }

        /// <summary>Records synthetic source-specific authentication and media-list requests without opening sockets.</summary>
        private sealed class CardTransport : HttpMessageHandler
        {
            /// <summary>Gets source addresses, routes and cookie owners observed by the fake handler.</summary>
            public ConcurrentQueue<(string Host, string Path, string? Account)> Requests { get; } = new();
            /// <summary>Gets explicit logins by source host and generated account name.</summary>
            public ConcurrentQueue<(string Host, string Account)> Logins { get; } = new();
            /// <summary>Gets or sets the host whose login is paused for the independence regression.</summary>
            public string? BlockLoginHost { get; set; }
            /// <summary>Gets or sets the generated account whose login is rejected.</summary>
            public string? RejectedUsername { get; set; }
            /// <summary>Gets or sets the host whose library request reports an expired session.</summary>
            public string? RejectedLibraryHost { get; set; }
            /// <summary>Gets or sets whether a source exposes synthetic account-private recommendation and cloud data.</summary>
            public bool ProvidePrivateData { get; set; }
            /// <summary>Gets or sets the host whose recommendation request waits for explicit release.</summary>
            public string? BlockFeedHost { get; set; }
            /// <summary>Signals that the controlled login reached the fake handler.</summary>
            public TaskCompletionSource LoginStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            /// <summary>Allows the controlled login to return.</summary>
            public TaskCompletionSource ReleaseLogin { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            /// <summary>Signals that a controlled recommendation request reached the fake handler.</summary>
            public TaskCompletionSource FeedStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            /// <summary>Allows a controlled recommendation response to finish.</summary>
            public TaskCompletionSource ReleaseFeed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

            /// <inheritdoc />
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var uri = request.RequestUri!;
                request.Headers.TryGetValues("Cookie", out var cookies);
                var account = cookies?.SelectMany(value => value.Split(';')).Select(value => value.Trim())
                    .FirstOrDefault(value => value.StartsWith("session=", StringComparison.Ordinal));
                account = account is null ? null : Uri.UnescapeDataString(account["session=".Length..]);
                Requests.Enqueue((uri.Host, uri.AbsolutePath, account));
                if (request.Method == HttpMethod.Post && uri.AbsolutePath.EndsWith("/account/login", StringComparison.Ordinal)
                    && request.Content is MultipartFormDataContent form)
                {
                    var username = string.Empty;
                    foreach (var part in form)
                    {
                        if (part.Headers.ContentDisposition?.Name?.Trim('"') == "username")
                        {
                            username = await part.ReadAsStringAsync(cancellationToken);
                        }
                    }

                    Logins.Enqueue((uri.Host, username));
                    if (username == RejectedUsername)
                    {
                        return new HttpResponseMessage(HttpStatusCode.Unauthorized)
                        {
                            Content = new StringContent("{\"code\":1}", Encoding.UTF8, "application/json")
                        };
                    }

                    if (uri.Host == BlockLoginHost)
                    {
                        LoginStarted.TrySetResult();
                        await ReleaseLogin.Task.WaitAsync(cancellationToken);
                    }

                    var response = Json(new { code = 0 });
                    response.Headers.TryAddWithoutValidation("Set-Cookie", "session=" + Uri.EscapeDataString(username) + "; Path=/; Secure");
                    return response;
                }

                if (request.Method == HttpMethod.Get && uri.AbsolutePath.EndsWith("/info", StringComparison.Ordinal)
                    && !uri.AbsolutePath.Contains("/maichart/", StringComparison.Ordinal))
                {
                    return Json(new { id = uri.Host, name = "Synthetic source" });
                }

                if (request.Method == HttpMethod.Get && (uri.AbsolutePath.EndsWith("/maichart/list", StringComparison.Ordinal)
                    || uri.AbsolutePath.EndsWith("/recommend/feed", StringComparison.Ordinal)
                    || uri.AbsolutePath.EndsWith("/account/favorite/collection/list", StringComparison.Ordinal)))
                {
                    if (uri.AbsolutePath.EndsWith("/maichart/list", StringComparison.Ordinal) && uri.Host == RejectedLibraryHost)
                    {
                        return new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent("{}") };
                    }

                    if (ProvidePrivateData)
                    {
                        if (uri.AbsolutePath.EndsWith("/recommend/feed", StringComparison.Ordinal) && uri.Host == BlockFeedHost)
                        {
                            FeedStarted.TrySetResult();
                            await ReleaseFeed.Task.WaitAsync(cancellationToken);
                        }

                        return uri.AbsolutePath.EndsWith("/account/favorite/collection/list", StringComparison.Ordinal)
                            ? Json(new[] { new { id = "private-cloud", name = "Private fixture", songHashs = new[] { "AAAAAAAAAAAAAAAAAAAAAA==" } } })
                            : Json(new[] { new { id = "private-fixture", title = account + " private music", hash = "AAAAAAAAAAAAAAAAAAAAAA==" } });
                    }

                    return Json(Array.Empty<object>());
                }

                throw new InvalidOperationException("Unexpected endpoint-card fixture request.");
            }

            /// <summary>Builds an in-memory response for the synthetic endpoint protocol.</summary>
            /// <param name="value">The response payload.</param>
            /// <returns>The successful JSON response.</returns>
            private static HttpResponseMessage Json(object value)
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json")
                };
            }
        }

        /// <summary>Retains only generated fixture secrets in process memory and records scoped cleanup.</summary>
        private sealed class MemoryVault : ICredentialStore
        {
            /// <summary>Stores generated secrets by their opaque application credential keys.</summary>
            private readonly ConcurrentDictionary<string, string> _secrets = new(StringComparer.Ordinal);
            /// <summary>Counts explicit remembered-password reads.</summary>
            private int _readCount;
            /// <summary>Gets the number of fake-vault reads.</summary>
            public int ReadCount => Volatile.Read(ref _readCount);

            /// <summary>Checks retention of one known generated credential without exposing its storage key.</summary>
            /// <param name="secret">The generated fixture value.</param>
            /// <returns>Whether the in-memory vault retains that value.</returns>
            public bool ContainsSecret(string secret)
            {
                return _secrets.Values.Contains(secret, StringComparer.Ordinal);
            }

            /// <inheritdoc />
            public Task<string?> ReadAsync(string key, CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Interlocked.Increment(ref _readCount);
                return Task.FromResult(_secrets.GetValueOrDefault(key));
            }

            /// <inheritdoc />
            public Task WriteAsync(string key, string secret, CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                _secrets[key] = secret;
                return Task.CompletedTask;
            }

            /// <inheritdoc />
            public Task DeleteAsync(string key, CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                _secrets.TryRemove(key, out _);
                return Task.CompletedTask;
            }
        }

        /// <summary>Pauses one real atomic settings save to expose overlapping narrow preference edits.</summary>
        private sealed class GateSettingsStore : ISettingsStore
        {
            /// <summary>Reads and writes the fixture settings file.</summary>
            private readonly JsonSettingsStore _inner;
            /// <summary>Marks the next save for deterministic release.</summary>
            private int _pauseNext;

            /// <summary>Creates the controllable store beneath the fixture directory.</summary>
            /// <param name="path">The generated local settings path.</param>
            public GateSettingsStore(string path)
            {
                _inner = new JsonSettingsStore(path);
            }

            /// <summary>Signals that the selected settings publication is paused.</summary>
            public TaskCompletionSource Started { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            /// <summary>Releases the selected settings publication.</summary>
            public TaskCompletionSource Release { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

            /// <summary>Pauses exactly the next settings write until explicitly released.</summary>
            public void PauseNextSave()
            {
                Started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                Release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                Interlocked.Exchange(ref _pauseNext, 1);
            }

            /// <inheritdoc />
            public Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default)
            {
                return _inner.LoadAsync(cancellationToken);
            }

            /// <inheritdoc />
            public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
            {
                if (Interlocked.Exchange(ref _pauseNext, 0) != 0)
                {
                    Started.TrySetResult();
                    await Release.Task.WaitAsync(cancellationToken);
                }

                await _inner.SaveAsync(settings, cancellationToken);
            }
        }
    }
}

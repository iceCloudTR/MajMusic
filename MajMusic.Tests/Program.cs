#nullable enable

namespace MajMusic.Tests
{
    /// <summary>Runs deterministic regressions without contacting real services or starting a user interface.</summary>
    internal static class Program
    {
        /// <summary>Executes the selected validation groups and returns a process result.</summary>
        /// <param name="args">Optional group names; desktop-audio, desktop-output and desktop-integration explicitly enable native checks.</param>
        /// <returns>Zero when all selected regressions pass, otherwise one.</returns>
        public static async Task<int> Main(string[] args)
        {
            var groups = new (string Name, Func<Task> Run, bool RunByDefault)[]
            {
                ("storage", StorageTests.RunAsync, true),
                ("credentials", CredentialStoreTests.RunAsync, true),
                ("account-preferences", AccountPreferencesTests.RunAsync, true),
                ("cloud-collection-preferences", CloudCollectionPreferenceTests.RunAsync, true),
                ("online-preferences", OnlinePreferencesTests.RunAsync, true),
                ("portable-data", PortableDataDirectoryTests.RunAsync, true),
                ("account-ui", AccountPresentationTests.RunAsync, true),
                ("endpoint-cards", EndpointCardPresentationTests.RunAsync, true),
                ("endpoint-cards-layout", EndpointCardsLayoutTests.RunAsync, true),
                ("directory-sources", DirectorySourceTests.RunAsync, true),
                ("directory-ui", DirectoryPresentationTests.RunAsync, true),
                ("network", NetworkTests.RunAsync, true),
                ("auth-boundaries", AuthBoundaryTests.RunAsync, true),
                ("collection-network", CollectionNetworkTests.RunAsync, true),
                ("cloud-collection-sync", CloudCollectionSyncTests.RunAsync, true),
                ("cloud-collection-ui", CloudCollectionPresentationTests.RunAsync, true),
                ("media-cache-concurrency", MediaCacheConcurrencyTests.RunAsync, true),
                ("offline-covers", OfflineCoverTests.RunAsync, true),
                ("playback", PlaybackTests.RunAsync, true),
                ("transport-responsiveness", TransportResponsivenessTests.RunAsync, true),
                ("audio-download-progress", AudioDownloadProgressTests.RunAsync, true),
                ("card-download-ui", CardDownloadPresentationTests.RunAsync, true),
                ("card-download-layout", CardDownloadLayoutTests.RunAsync, true),
                ("audio-output", AudioOutputTests.RunAsync, true),
                ("audio-settings-ui", AudioSettingsPresentationTests.RunAsync, true),
                ("workspace", WorkspaceTests.RunAsync, true),
                ("collection", CollectionTests.RunAsync, true),
                ("collection-workflow", CollectionWorkflowTests.RunAsync, true),
                ("playlists", PlaylistTests.RunAsync, true),
                ("playlist-management", PlaylistManagementTests.RunAsync, true),
                ("metadata", MetadataTests.RunAsync, true),
                ("online-workspace", OnlineWorkspaceTests.RunAsync, true),
                ("presentation", PresentationTests.RunAsync, true),
                ("library-ui", LibraryExperienceTests.RunAsync, true),
                ("home-ui", HomeExperienceTests.RunAsync, true),
                ("cover-ui", CoverPresentationTests.RunAsync, true),
                ("cover-resources", CoverResourceTests.RunAsync, true),
                ("card-covers-ui", CardCoverPresentationTests.RunAsync, true),
                ("browse-metadata", BrowseMetadataTests.RunAsync, true),
                ("panel-paging", PanelPagingTests.RunAsync, true),
                ("playback-modes-ui", PlaybackModePresentationTests.RunAsync, true),
                ("metadata-ui", MetadataPresentationTests.RunAsync, true),
                ("headless", HeadlessTests.RunAsync, true),
                ("alignment", AlignmentTests.RunAsync, true),
                ("arc-responsive", ArcResponsiveLayoutTests.RunAsync, true),
                ("arc-motion", ArcMotionTests.RunAsync, true),
                ("appearance", AppearanceTests.RunAsync, true),
                ("surface-motion", SurfaceMotionTests.RunAsync, true),
                ("browse-loading", BrowseLoadingPresentationTests.RunAsync, true),
                ("cover-preload", CoverPreloadTests.RunAsync, true),
                ("browse-entry", BrowseEntryPreparationTests.RunAsync, true),
                ("catalog-read-ordering", CatalogReadOrderingTests.RunAsync, true),
                ("recommendation-paging", RecommendationPagingTests.RunAsync, true),
                ("recommendation-paging-layout", RecommendationPagingLayoutTests.RunAsync, true),
                ("recommendation-playback", RecommendationPlaybackTests.RunAsync, true),
                ("card-feedback", SongCardFeedbackTests.RunAsync, true),
                ("context-actions", ContextActionTests.RunAsync, true),
                ("playback-region-motion", PlaybackRegionMotionTests.RunAsync, true),
                ("playlist-mirrors", PlaylistMirrorTests.RunAsync, true),
                ("context-menus-layout", ContextMenusLayoutTests.RunAsync, true),
                ("desktop-audio", DesktopAudioTests.RunAsync, false),
                ("desktop-output", DesktopAudioTests.RunOutputAsync, false),
                ("desktop-integration", LocalPlaybackIntegrationTests.RunAsync, false),
                ("desktop-metadata", MetadataTests.RunNativeAsync, false),
                ("native-vault", CredentialStoreTests.RunNativeAsync, false),
                ("desktop-audio-controls", AudioOutputTests.RunNativeAsync, false)
            };
            var selected = groups.Where(group => args.Length == 0 ? group.RunByDefault
                : args.Contains(group.Name, StringComparer.OrdinalIgnoreCase)).ToArray();
            if (selected.Length == 0)
            {
                Console.Error.WriteLine("Unknown validation group.");
                return 1;
            }

            foreach (var group in selected)
            {
                try
                {
                    await group.Run();
                    Console.WriteLine($"PASS {group.Name}");
                }
                catch (Exception exception)
                {
                    Console.Error.WriteLine($"FAIL {group.Name}: {exception}");
                    return 1;
                }
            }

            Console.WriteLine($"Passed {selected.Length} regression groups.");
            return 0;
        }
    }
}

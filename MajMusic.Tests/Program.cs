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
                ("app-updates", AppUpdateTests.RunAsync, true),
                ("portable-data", PortableDataDirectoryTests.RunAsync, true),
                ("majview-runtime", MajViewRuntimeTests.RunAsync, true),
                ("majview-session", MajViewSessionTests.RunAsync, true),
                ("account-ui", AccountPresentationTests.RunAsync, true),
                ("mobile-account-input", MobileAccountInputTests.RunAsync, true),
                ("mobile-player-gesture", MobilePlayerGestureTests.RunAsync, true),
                ("mobile-safe-area", MobileSafeAreaTests.RunAsync, true),
                ("endpoint-cards", EndpointCardPresentationTests.RunAsync, true),
                Headless("endpoint-cards-layout", EndpointCardsLayoutTests.RunAsync),
                ("directory-sources", DirectorySourceTests.RunAsync, true),
                Headless("document-storage", DocumentStorageTests.RunAsync),
                ("directory-ui", DirectoryPresentationTests.RunAsync, true),
                ("network", NetworkTests.RunAsync, true),
                ("source-transport", SourceTransportTests.RunAsync, true),
                ("auth-boundaries", AuthBoundaryTests.RunAsync, true),
                ("collection-network", CollectionNetworkTests.RunAsync, true),
                ("social-network", SocialNetworkTests.RunAsync, true),
                ("social-presentation", SocialPresentationTests.RunAsync, true),
                ("song-details-layout", SongDetailsLayoutTests.RunAsync, true),
                ("chart-preview-assets", ChartPreviewAssetTests.RunAsync, true),
                ("cloud-collection-sync", CloudCollectionSyncTests.RunAsync, true),
                ("cloud-collection-ui", CloudCollectionPresentationTests.RunAsync, true),
                ("media-cache-concurrency", MediaCacheConcurrencyTests.RunAsync, true),
                ("offline-covers", OfflineCoverTests.RunAsync, true),
                ("playback", PlaybackTests.RunAsync, true),
                ("transport-responsiveness", TransportResponsivenessTests.RunAsync, true),
                ("audio-download-progress", AudioDownloadProgressTests.RunAsync, true),
                ("download-metadata", DownloadMetadataPresentationTests.RunAsync, true),
                ("card-download-ui", CardDownloadPresentationTests.RunAsync, true),
                Headless("card-download-layout", CardDownloadLayoutTests.RunAsync),
                ("audio-output", AudioOutputTests.RunAsync, true),
                ("audio-settings-ui", AudioSettingsPresentationTests.RunAsync, true),
                ("workspace", WorkspaceTests.RunAsync, true),
                ("independent-media-storage", IndependentMediaStorageTests.RunAsync, true),
                ("collection", CollectionTests.RunAsync, true),
                ("collection-workflow", CollectionWorkflowTests.RunAsync, true),
                ("playlists", PlaylistTests.RunAsync, true),
                ("default-favorites", DefaultFavoritesTests.RunAsync, true),
                ("playlist-management", PlaylistManagementTests.RunAsync, true),
                ("metadata", MetadataTests.RunAsync, true),
                ("online-workspace", OnlineWorkspaceTests.RunAsync, true),
                ("presentation", PresentationTests.RunAsync, true),
                ("library-ui", LibraryExperienceTests.RunAsync, true),
                ("home-ui", HomeExperienceTests.RunAsync, true),
                Headless("cover-ui", CoverPresentationTests.RunAsync),
                ("cover-resources", CoverResourceTests.RunAsync, true),
                Headless("card-covers-ui", CardCoverPresentationTests.RunAsync),
                ("browse-metadata", BrowseMetadataTests.RunAsync, true),
                ("panel-paging", PanelPagingTests.RunAsync, true),
                ("playback-modes-ui", PlaybackModePresentationTests.RunAsync, true),
                ("metadata-ui", MetadataPresentationTests.RunAsync, true),
                ("headless", HeadlessTests.RunAsync, true),
                Headless("alignment", AlignmentTests.RunAsync),
                ("surface-geometry", SurfaceGeometryTests.RunAsync, true),
                Headless("scroll-gutters", ScrollGutterLayoutTests.RunAsync),
                Headless("arc-responsive", ArcResponsiveLayoutTests.RunAsync),
                Headless("arc-motion", ArcMotionTests.RunAsync),
                Headless("rapid-nav-icons", RapidNavigationIconTests.RunAsync),
                Headless("appearance", AppearanceTests.RunAsync),
                Headless("surface-motion", SurfaceMotionTests.RunAsync),
                Headless("browse-loading", BrowseLoadingPresentationTests.RunAsync),
                Headless("browse-placeholder-lifecycle", BrowsePlaceholderLifecycleTests.RunAsync),
                Headless("cover-preload", CoverPreloadTests.RunAsync),
                Headless("browse-entry", BrowseEntryPreparationTests.RunAsync),
                ("browse-rows", BrowseRowsPresentationTests.RunAsync, true),
                ("song-card-row-reuse", SongCardRowReuseTests.RunAsync, true),
                ("catalog-memory", CatalogMemoryTests.RunAsync, true),
                Headless("presentation-retention", PresentationRetentionTests.RunAsync),
                Headless("catalog-read-ordering", CatalogReadOrderingTests.RunAsync),
                ("recommendation-paging", RecommendationPagingTests.RunAsync, true),
                Headless("recommendation-paging-layout", RecommendationPagingLayoutTests.RunAsync),
                ("recommendation-playback", RecommendationPlaybackTests.RunAsync, true),
                Headless("card-feedback", SongCardFeedbackTests.RunAsync),
                ("cover-play-feedback", CoverPlayFeedbackTests.RunAsync, true),
                ("context-actions", ContextActionTests.RunAsync, true),
                Headless("playback-region-motion", PlaybackRegionMotionTests.RunAsync),
                ("playlist-mirrors", PlaylistMirrorTests.RunAsync, true),
                Headless("context-menus-layout", ContextMenusLayoutTests.RunAsync),
                ("desktop-audio", DesktopAudioTests.RunAsync, false),
                ("desktop-output", DesktopAudioTests.RunOutputAsync, false),
                Headless("desktop-integration", LocalPlaybackIntegrationTests.RunAsync, runByDefault: false),
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

        /// <summary>Registers a group with its own Avalonia application and dispatcher lifetime.</summary>
        /// <param name="name">The registered group name.</param>
        /// <param name="run">The group's UI assertions.</param>
        /// <param name="runByDefault">Whether the default suite includes the group.</param>
        /// <returns>The process-isolated regression group.</returns>
        private static (string Name, Func<Task> Run, bool RunByDefault) Headless(string name, Func<Task> run, bool runByDefault = true)
        {
            return (name, () => HeadlessProcessIsolation.RunAsync(name, run), runByDefault);
        }
    }
}

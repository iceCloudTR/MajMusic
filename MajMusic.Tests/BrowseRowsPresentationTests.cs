#nullable enable

using System.Collections;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using MajMusic.Core.Playback;
using MajMusic.Presentation.ViewModels;
using MajMusic.Presentation.Views;

namespace MajMusic.Tests
{
    /// <summary>Checks indexed music-row notifications without replacing virtualized containers or materializing songs.</summary>
    internal static class BrowseRowsPresentationTests
    {
        /// <summary>Runs source-notification forwarding and trailing-footer identity regressions without a UI or I/O.</summary>
        /// <returns>A task representing the completed collection cases.</returns>
        public static async Task RunAsync()
        {
            await using var owner = new MainViewModel(new LibraryExperienceTests.ExperienceWorkspace(),
                new PlaybackService(new UnavailablePlaybackBackend()), static action => action());
            var source = owner.BrowseRows;
            var first = Row();
            var second = Row();
            var third = Row();
            source.Add(first);
            source.Add(second);
            source.Add(third);
            using var presentation = new BrowseRowsPresentation(owner);
            presentation.SetFooterVisible(true);
            var footer = presentation.Footer!;
            var consumer = presentation.Cast<object>().ToList();
            var forwarded = new List<NotifyCollectionChangedEventArgs>();
            NotifyCollectionChangedEventArgs? original = null;
            presentation.CollectionChanged += (sender, args) =>
            {
                TestAssert.True(ReferenceEquals(sender, presentation), "Consumers must receive the indexed presentation as the event sender.");
                forwarded.Add(args);
                ApplyChange(consumer, presentation, args);
            };
            source.CollectionChanged += (_, args) => original = args;

            void Change(Action mutation, NotifyCollectionChangedAction action, int newIndex, int oldIndex)
            {
                original = null;
                var eventsBefore = forwarded.Count;
                mutation();
                TestAssert.True(forwarded.Count == eventsBefore + 1 && ReferenceEquals(original, forwarded[^1]),
                    "A source mutation must forward its original event once, preserving affected item ranges instead of issuing Reset.");
                var args = forwarded[^1];
                TestAssert.True(args.Action == action && args.NewStartingIndex == newIndex && args.OldStartingIndex == oldIndex,
                    "The forwarded action and both source indices must remain unchanged.");
                var expected = source.Cast<object>();
                if (presentation.Footer is not null)
                {
                    expected = expected.Append(footer);
                }

                AssertContents(presentation, consumer, expected.ToArray());
                TestAssert.True(presentation.Footer is null ? presentation.IndexOf(footer) == -1
                    : ReferenceEquals(presentation.Footer, footer) && presentation.IndexOf(footer) == source.Count,
                    "The same visible footer must follow the current source count, while a hidden footer must have no index.");
            }

            var appended = Row();
            Change(() => source.Add(appended), NotifyCollectionChangedAction.Add, 3, -1);
            Change(() => source.Insert(1, Row()), NotifyCollectionChangedAction.Add, 1, -1);
            var replacement = Row();
            Change(() => source[2] = replacement, NotifyCollectionChangedAction.Replace, 2, 2);
            TestAssert.True(ReferenceEquals(forwarded[^1].OldItems![0], second)
                && ReferenceEquals(forwarded[^1].NewItems![0], replacement),
                "Replacement notifications must preserve both old and new row identities.");
            Change(() => source.Move(4, 0), NotifyCollectionChangedAction.Move, 0, 4);
            TestAssert.True(ReferenceEquals(forwarded[^1].NewItems![0], appended),
                "Moving a source row must retain its existing presentation instead of synthesizing a replacement.");
            Change(() => source.RemoveAt(3), NotifyCollectionChangedAction.Remove, -1, 3);
            Change(source.Clear, NotifyCollectionChangedAction.Reset, -1, -1);
            TestAssert.True(presentation.Count == 1 && ReferenceEquals(presentation[0], footer),
                "Resetting the source must retain a visible footer at index zero without reporting it as a removed song.");
            Change(() => source.Add(first), NotifyCollectionChangedAction.Add, 0, -1);

            var eventsBeforeFooter = forwarded.Count;
            presentation.SetFooterVisible(false);
            TestAssert.True(forwarded.Count == eventsBeforeFooter + 1 && forwarded[^1].Action == NotifyCollectionChangedAction.Remove
                && forwarded[^1].OldStartingIndex == source.Count && ReferenceEquals(forwarded[^1].OldItems![0], footer),
                "Hiding the footer must remove only its current trailing index.");
            AssertContents(presentation, consumer, source.Cast<object>().ToArray());
            presentation.SetFooterVisible(false);
            TestAssert.Equal(eventsBeforeFooter + 1, forwarded.Count, "Setting the same footer visibility must not notify or reset the music list.");
            Change(() => source.Add(second), NotifyCollectionChangedAction.Add, 1, -1);
            eventsBeforeFooter = forwarded.Count;
            presentation.SetFooterVisible(true);
            TestAssert.True(forwarded.Count == eventsBeforeFooter + 1 && forwarded[^1].Action == NotifyCollectionChangedAction.Add
                && forwarded[^1].NewStartingIndex == source.Count && ReferenceEquals(forwarded[^1].NewItems![0], footer),
                "Showing the footer again must reuse its identity at the updated trailing index.");
            AssertContents(presentation, consumer, source.Cast<object>().Append(footer).ToArray());
            presentation.SetFooterVisible(true);
            TestAssert.Equal(eventsBeforeFooter + 1, forwarded.Count, "An already visible footer cannot invalidate any music containers.");
            var eventsBeforeDisposal = forwarded.Count;
            presentation.Dispose();
            source.Add(third);
            TestAssert.Equal(eventsBeforeDisposal, forwarded.Count, "Disposal must stop forwarding source notifications to a retired browser.");
            ValidateRangeForwarding(owner);
        }

        /// <summary>Checks multirow tail growth and shrinkage with the footer remaining beyond the complete source range.</summary>
        /// <param name="owner">The owner of the footer, without an initialized view or workspace.</param>
        private static void ValidateRangeForwarding(MainViewModel owner)
        {
            var source = new RangeRows { Row(), Row() };
            using var presentation = new BrowseRowsPresentation(owner, source);
            presentation.SetFooterVisible(true);
            var footer = presentation.Footer!;
            var consumer = presentation.Cast<object>().ToList();
            var forwarded = new List<NotifyCollectionChangedEventArgs>();
            NotifyCollectionChangedEventArgs? original = null;
            source.CollectionChanged += (_, args) => original = args;
            presentation.CollectionChanged += (_, args) =>
            {
                forwarded.Add(args);
                ApplyChange(consumer, presentation, args);
            };
            var added = new[] { Row(), Row(), Row() };
            source.AppendRange(added);
            TestAssert.True(forwarded.Count == 1 && ReferenceEquals(original, forwarded[0])
                && forwarded[0].Action == NotifyCollectionChangedAction.Add && forwarded[0].NewStartingIndex == 2
                && forwarded[0].NewItems!.Count == 3,
                "A multirow append must forward the same Add range at its original index instead of resetting the virtualized source.");
            AssertContents(presentation, consumer, source.Cast<object>().Append(footer).ToArray());
            TestAssert.Equal(5, presentation.IndexOf(footer), "The footer shifts after the entire added range without a separate footer notification.");
            source.RemoveTail(4);
            TestAssert.True(forwarded.Count == 2 && ReferenceEquals(original, forwarded[1])
                && forwarded[1].Action == NotifyCollectionChangedAction.Remove && forwarded[1].OldStartingIndex == 1
                && forwarded[1].OldItems!.Count == 4,
                "A multirow tail removal must preserve its original Remove range and remaining prefix.");
            AssertContents(presentation, consumer, source.Cast<object>().Append(footer).ToArray());
            TestAssert.Equal(1, presentation.IndexOf(footer), "The footer follows the retained prefix after a range removal.");
        }

        /// <summary>Creates an identity-distinct empty row without constructing or loading any song presentation.</summary>
        /// <returns>A lightweight source row for collection changes.</returns>
        private static SongCardRow Row()
        {
            return new SongCardRow(new List<SongCardViewModel>(), 2);
        }

        /// <summary>Applies the same indexed actions a virtualized collection consumer receives.</summary>
        /// <param name="consumer">The independently maintained indexed consumer state.</param>
        /// <param name="presentation">The current live source, enumerated only for a genuine Reset.</param>
        /// <param name="args">The original source or footer notification.</param>
        private static void ApplyChange(List<object> consumer, IList presentation, NotifyCollectionChangedEventArgs args)
        {
            switch (args.Action)
            {
                case NotifyCollectionChangedAction.Add:
                    consumer.InsertRange(args.NewStartingIndex, args.NewItems!.Cast<object>());
                    break;
                case NotifyCollectionChangedAction.Remove:
                    consumer.RemoveRange(args.OldStartingIndex, args.OldItems!.Count);
                    break;
                case NotifyCollectionChangedAction.Replace:
                    consumer.RemoveRange(args.OldStartingIndex, args.OldItems!.Count);
                    consumer.InsertRange(args.NewStartingIndex, args.NewItems!.Cast<object>());
                    break;
                case NotifyCollectionChangedAction.Move:
                    var moving = consumer.GetRange(args.OldStartingIndex, args.OldItems!.Count);
                    consumer.RemoveRange(args.OldStartingIndex, moving.Count);
                    consumer.InsertRange(args.NewStartingIndex, moving);
                    break;
                case NotifyCollectionChangedAction.Reset:
                    consumer.Clear();
                    consumer.AddRange(presentation.Cast<object>());
                    break;
                default:
                    throw new InvalidOperationException("Unexpected collection action.");
            }
        }

        /// <summary>Compares enumeration, indexed lookup, copying and an independently updated consumer by reference.</summary>
        /// <param name="presentation">The footer-decorated live source.</param>
        /// <param name="consumer">The event-driven consumer state.</param>
        /// <param name="expected">The actual source rows followed by the optional footer.</param>
        private static void AssertContents(BrowseRowsPresentation presentation, IReadOnlyList<object> consumer, IReadOnlyList<object> expected)
        {
            var enumerated = presentation.Cast<object>().ToArray();
            var copied = new object[presentation.Count + 1];
            presentation.CopyTo(copied, 1);
            TestAssert.True(presentation.Count == expected.Count && consumer.Count == expected.Count && enumerated.Length == expected.Count,
                "The live indexed presentation and event-driven consumer must have the same row count.");
            for (var index = 0; index < expected.Count; index++)
            {
                TestAssert.True(ReferenceEquals(presentation[index], expected[index]) && ReferenceEquals(consumer[index], expected[index])
                    && ReferenceEquals(enumerated[index], expected[index]) && ReferenceEquals(copied[index + 1], expected[index]),
                    "Indexed changes must preserve source row order, identities and the footer's trailing position.");
            }
        }

        /// <summary>Publishes finite source ranges matching the snapshot collection's tail-resize contract.</summary>
        private sealed class RangeRows : ObservableCollection<SongCardRow>
        {
            /// <summary>Adds a complete row range with one indexed notification.</summary>
            /// <param name="rows">The newly appended rows in their display order.</param>
            public void AppendRange(SongCardRow[] rows)
            {
                var start = Count;
                foreach (var row in rows)
                {
                    Items.Add(row);
                }

                OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Add, rows, start));
            }

            /// <summary>Removes one contiguous tail while retaining all earlier row identities.</summary>
            /// <param name="count">The number of trailing rows removed by the source.</param>
            public void RemoveTail(int count)
            {
                var start = Count - count;
                var removed = this.Skip(start).ToArray();
                while (Items.Count > start)
                {
                    Items.RemoveAt(Items.Count - 1);
                }

                OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Remove, removed, start));
            }
        }
    }
}

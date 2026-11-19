#nullable enable

namespace MajMusic.Core.Models
{
    /// <summary>Indexes exact judgement times for the five MajView note types and the two native practice layouts.</summary>
    public sealed class ChartNoteTimeline
    {
        /// <summary>Number of judgement categories in the renderer protocol.</summary>
        public const int TypeCount = 5;
        /// <summary>Number of intervals in the density lane.</summary>
        public const int BinCount = 128;
        private const int MaximumNotes = 262144;
        private readonly int[][] _times;

        private ChartNoteTimeline(int[][] times, int[] totals, int[] density)
        {
            _times = times;
            Totals = Array.AsReadOnly(totals);
            Density = Array.AsReadOnly(density);
        }

        /// <summary>Gets totals in TAP, HOLD, SLIDE, TOUCH, BREAK order.</summary>
        public IReadOnlyList<int> Totals { get; }

        /// <summary>Gets the five interleaved counts in each audio-time bin.</summary>
        public IReadOnlyList<int> Density { get; }

        /// <summary>Validates and snapshots exported millisecond timestamps once per difficulty.</summary>
        public static ChartNoteTimeline? Create(int[][]? milliseconds, double duration)
        {
            if (milliseconds is not { Length: TypeCount } || !double.IsFinite(duration) || duration is <= 0 or > 604800)
            {
                return null;
            }
            var times = new int[TypeCount][];
            var totals = new int[TypeCount];
            var density = new int[BinCount * TypeCount];
            var remaining = MaximumNotes;
            for (var kind = 0; kind < TypeCount; kind++)
            {
                var source = milliseconds[kind];
                if (source is null || source.Length > remaining || Array.Exists(source, time => time < 0))
                {
                    return null;
                }
                remaining -= source.Length;
                times[kind] = (int[])source.Clone();
                Array.Sort(times[kind]);
                totals[kind] = source.Length;
                foreach (var time in source)
                {
                    var bin = (int)Math.Min(BinCount - 1, time / (duration * 1000) * BinCount);
                    density[bin * TypeCount + kind]++;
                }
            }
            return new ChartNoteTimeline(times, totals, density);
        }

        /// <summary>Counts all notes of a type judged at or before the cursor, including simultaneous notes.</summary>
        public int CountAt(int kind, double seconds)
        {
            var times = _times[kind];
            var milliseconds = seconds * 1000;
            var low = 0;
            var high = times.Length;
            while (low < high)
            {
                var mid = low + (high - low) / 2;
                if (times[mid] <= milliseconds)
                {
                    low = mid + 1;
                }
                else
                {
                    high = mid;
                }
            }
            return low;
        }
    }
}

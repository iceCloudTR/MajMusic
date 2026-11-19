#nullable enable

namespace MajMusic.Core.Models
{
    /// <summary>Locally saved renderer settings, independent of song, difficulty and browser origin.</summary>
    public sealed record ChartPracticeOptions(double NoteSpeed = 7, double TouchSpeed = 7, double Shade = 0.6,
        double OffsetMs = 0, double Effects = 0.6, double Speed = 1)
    {
        /// <summary>Clamps stored values to the ranges supported by the renderer, replacing non-finite values with defaults.</summary>
        public ChartPracticeOptions Normalize() => new(Limit(NoteSpeed, 0.1, 20, 7), Limit(TouchSpeed, 0.1, 20, 7),
            Limit(Shade, 0, 1, 0.6), Limit(OffsetMs, -500, 500, 0), Limit(Effects, 0, 1, 0.6), Limit(Speed, 0.25, 2, 1));

        private static double Limit(double value, double min, double max, double fallback) =>
            double.IsFinite(value) ? Math.Clamp(value, min, max) : fallback;
    }

    /// <summary>Latest absolute native intent. Seek revisions only advance on an explicit seek or document mount.</summary>
    public sealed record ChartIntent(int Generation, int Revision, int SeekRevision, double Position, bool Playing,
        ChartPracticeOptions Options, double Gain, bool NeedWaveform, bool? LightTheme = null);

    /// <summary>Acknowledged renderer clock and optional one-time analysis of the current document.</summary>
    public sealed record ChartFrame(int Generation, int Revision, double Position, double Duration, bool Playing,
        ChartNoteTimeline? Notes = null, int[]? Waveform = null, bool Failed = false);
}

#nullable enable

namespace MajMusic.Core.Models
{
    /// <summary>Describes one playable difficulty in the original Simai slot numbering.</summary>
    /// <param name="Number">The one-based maidata difficulty slot.</param>
    /// <param name="Level">The level label supplied by the chart author, if present.</param>
    public sealed record ChartPreviewDifficulty(int Number, string? Level);

    /// <summary>Resolves the source material shared by desktop and mobile chart previews.</summary>
    /// <param name="ChartText">The complete decoded maidata text with all original difficulty slots.</param>
    /// <param name="AudioPath">The resolved music file or platform document URI.</param>
    /// <param name="CoverPath">The optional resolved cover image.</param>
    /// <param name="VideoPath">The optional silent preview background file or platform document URI.</param>
    /// <param name="Difficulties">The playable slots in their original order.</param>
    public sealed record ChartPreviewAssets(string ChartText, string AudioPath, string? CoverPath, string? VideoPath,
        IReadOnlyList<ChartPreviewDifficulty> Difficulties);
}

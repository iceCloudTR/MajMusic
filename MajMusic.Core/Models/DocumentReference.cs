#nullable enable

namespace MajMusic.Core.Models
{
    /// <summary>Recognizes explicitly granted document-provider references without treating them as filesystem paths.</summary>
    public static class DocumentReference
    {
        /// <summary>Checks an absolute Android document-provider address.</summary>
        /// <param name="reference">The stored external document or tree reference.</param>
        /// <returns>Whether the reference has the content scheme and a nonempty provider authority.</returns>
        public static bool IsContentUri(string? reference)
        {
            return Uri.TryCreate(reference, UriKind.Absolute, out var uri) && uri.Scheme == "content" && !string.IsNullOrEmpty(uri.Authority);
        }
    }

    /// <summary>Identifies one read-only portable collection document from a granted tree.</summary>
    /// <param name="Reference">The document-provider URI, never a filesystem path.</param>
    /// <param name="Name">The original provider display name used to recognize MyFavorites.json.</param>
    public sealed record DocumentFileReference(string Reference, string Name);
}

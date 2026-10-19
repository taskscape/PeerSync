namespace PeerSync.Engine.Sync;

internal static class PathSpelling
{
    // Only use after the canonical key matches. Ancestor spelling belongs to
    // those directory records, not to the child's content/version identity.
    internal static bool SameLeaf(string left, string right) =>
        left.AsSpan(left.LastIndexOf('/') + 1).SequenceEqual(right.AsSpan(right.LastIndexOf('/') + 1));
}

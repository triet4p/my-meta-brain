using System.Globalization;
using System.Security.Cryptography;
using MetaBrain.Core.Domain;
using MetaBrain.Core.Security;

namespace MetaBrain.Application;

/// <summary>
/// The one bounded walk over the pinned citation lineage of memory items,
/// shared by the synthesis scope check and the proposal review so both read the
/// same edges with the same limits. A registered source and a preserved
/// snapshot are roots; the pinned citations of a memory item are the only edges
/// walked. The walk never rewrites a citation and never invents one: a pinned
/// revision whose bytes cannot be read contributes no further edges, a pin that
/// revisits one already on the current path is reported as a cycle, and a walk
/// that would exceed the node or depth budget is reported as exhausted instead
/// of being truncated silently. Callers decide what a reported cycle, an
/// unreadable pin or an exhausted budget means for them.
/// </summary>
internal static class MemoryLineage
{
    /// <summary>The most pins one lineage walk charts.</summary>
    internal const int MaximumNodes = 512;

    /// <summary>The deepest pin chain one lineage walk follows.</summary>
    internal const int MaximumDepth = 64;

    /// <summary>One resource pinned at one revision, with the depth it was reached at.</summary>
    internal readonly record struct Pin(string ResourceId, long Revision, int Depth)
    {
        /// <summary>The identity a pin is deduplicated and cycle-checked by.</summary>
        internal string Key => ResourceId + "@" + Revision.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// The result of one bounded walk: every distinct pin reached (the roots at
    /// depth 0 first), the pins whose bytes could not be read at the pinned
    /// revision, and — at most one of — the pin that closed a cycle or the pin
    /// that exhausted the budget.
    /// </summary>
    internal sealed class WalkResult
    {
        internal WalkResult(Pin[] pins, Pin[] unreadable, Pin? cycle, Pin? bounded)
        {
            Pins = pins;
            Unreadable = unreadable;
            Cycle = cycle;
            Bounded = bounded;
        }

        internal IReadOnlyList<Pin> Pins { get; }

        internal IReadOnlyList<Pin> Unreadable { get; }

        internal Pin? Cycle { get; }

        internal Pin? Bounded { get; }
    }

    /// <summary>
    /// Walks the closure of the pinned citations reachable from
    /// <paramref name="roots"/>. Roots are taken exactly as given: this method
    /// does not resolve, repair or re-pin them.
    /// </summary>
    internal static WalkResult Walk(IEnumerable<(string ResourceId, long Revision)> roots, IManagedResourceReader store)
    {
        ArgumentNullException.ThrowIfNull(roots);
        ArgumentNullException.ThrowIfNull(store);

        var pins = new List<Pin>();
        var unreadable = new List<Pin>();
        var completed = new HashSet<string>(StringComparer.Ordinal);
        var onPath = new HashSet<string>(StringComparer.Ordinal);
        Pin? cycle = null;
        Pin? bounded = null;
        var visited = 0;

        foreach (var (resourceId, revision) in roots)
        {
            Visit(resourceId, revision, depth: 0);
        }

        return new WalkResult(pins.ToArray(), unreadable.ToArray(), cycle, bounded);

        void Visit(string resourceId, long revision, int depth)
        {
            if (cycle is not null || bounded is not null)
            {
                return;
            }

            var pin = new Pin(resourceId, revision, depth);
            if (completed.Contains(pin.Key))
            {
                return;
            }

            if (!onPath.Add(pin.Key))
            {
                cycle = pin;
                return;
            }

            visited++;
            if (visited > MaximumNodes || depth > MaximumDepth)
            {
                bounded = pin;
                return;
            }

            pins.Add(pin);
            var citations = ReadPinnedCitations(resourceId, revision, store, out var readable);
            if (!readable)
            {
                unreadable.Add(pin);
            }

            foreach (var citation in citations)
            {
                Visit(citation.ResourceId, citation.Revision, depth + 1);
            }

            onPath.Remove(pin.Key);
            completed.Add(pin.Key);
        }
    }

    /// <summary>
    /// The pinned citations of one resource: only a memory item has a lineage to
    /// walk. A registered source is a root, bytes that decode as neither a
    /// memory item nor a source have no citations here, and a preserved snapshot
    /// is not read at all. <paramref name="readable"/> reports whether the
    /// pinned revision could be read, so a caller can tell a root from a pin the
    /// vault no longer serves at that revision.
    /// </summary>
    private static (string ResourceId, long Revision)[] ReadPinnedCitations(
        string resourceId,
        long revision,
        IManagedResourceReader store,
        out bool readable)
    {
        readable = true;
        if (SourceSnapshotIds.IsSnapshotId(resourceId))
        {
            return Array.Empty<(string, long)>();
        }

        byte[]? content = null;
        try
        {
            content = store.ReadContent(resourceId, revision);
        }
        catch (Exception ex) when (ex is ManagedResourceUnavailableException or IOException or ArgumentException)
        {
            readable = false;
            return Array.Empty<(string, long)>();
        }

        try
        {
            StoredMemoryEnvelope envelope;
            try
            {
                envelope = MemoryEnvelopeCodec.DecodeStored(content);
            }
            catch (ArgumentException)
            {
                return Array.Empty<(string, long)>();
            }

            var sources = envelope.Sources;
            if (sources is null || sources.Length == 0)
            {
                return Array.Empty<(string, long)>();
            }

            return sources
                .Where(source => !string.IsNullOrEmpty(source.SourceId) && source.SourceRevision is > 0)
                .Select(source => (source.SourceId!, source.SourceRevision!.Value))
                .Distinct()
                .ToArray();
        }
        finally
        {
            if (content is { Length: > 0 })
            {
                CryptographicOperations.ZeroMemory(content);
            }
        }
    }
}

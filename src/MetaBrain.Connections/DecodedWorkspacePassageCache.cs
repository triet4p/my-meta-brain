using System.Security.Cryptography;
using MetaBrain.Core.Security;
using MetaBrain.Core.Index;

namespace MetaBrain.Connections;

/// <summary>One authenticated, decoded passage and its exact posting tokens.</summary>
internal sealed record DecodedWorkspacePassage(
    WorkspacePassageAnchor Anchor,
    int[] TokenIds,
    float[] Vector,
    long Ordinal);

/// <summary>A complete cached group for one exact workspace file revision and model.</summary>
internal sealed record DecodedWorkspacePassageGroup(
    string FileId,
    long ContentRevision,
    string ModelRevision,
    DecodedWorkspacePassage[] Passages,
    int EstimatedBytes);

/// <summary>A byte-bounded LRU of unlocked-epoch decoded passage groups.</summary>
internal sealed class DecodedWorkspacePassageCache
{
    internal const int CapacityBytes = 16 * 1024 * 1024;

    private readonly Dictionary<(string FileId, long Revision, string ModelRevision), LinkedListNode<DecodedWorkspacePassageGroup>> _entries = new();
    private readonly LinkedList<DecodedWorkspacePassageGroup> _lru = new();
    private int _bytes;

    internal int Bytes => _bytes;

    internal bool TryGet(
        string fileId,
        long revision,
        string modelRevision,
        out DecodedWorkspacePassageGroup group)
    {
        if (!_entries.TryGetValue((fileId, revision, modelRevision), out var node))
        {
            group = null!;
            return false;
        }

        _lru.Remove(node);
        _lru.AddFirst(node);
        group = node.Value;
        return true;
    }

    internal void Add(DecodedWorkspacePassageGroup group)
    {
        ArgumentNullException.ThrowIfNull(group);
        var key = (group.FileId, group.ContentRevision, group.ModelRevision);
        if (group.EstimatedBytes > CapacityBytes)
        {
            ClearGroup(group);
            return;
        }

        Remove(key);
        while (_bytes > CapacityBytes - group.EstimatedBytes && _lru.Last is { } last)
        {
            Remove((last.Value.FileId, last.Value.ContentRevision, last.Value.ModelRevision));
        }

        var node = _lru.AddFirst(group);
        _entries.Add(key, node);
        _bytes = checked(_bytes + group.EstimatedBytes);
    }


    internal void RemoveFile(string fileId)
    {
        foreach (var key in _entries.Keys.ToArray())
        {
            if (StringComparer.Ordinal.Equals(key.FileId, fileId))
            {
                Remove(key);
            }
        }
    }

    internal void Remove(string fileId, long revision, string modelRevision) => Remove((fileId, revision, modelRevision));

    internal void Clear()
    {
        while (_lru.Last is { } last)
        {
            Remove((last.Value.FileId, last.Value.ContentRevision, last.Value.ModelRevision));
        }
    }

    private void Remove((string FileId, long Revision, string ModelRevision) key)
    {
        if (!_entries.Remove(key, out var node))
        {
            return;
        }

        _lru.Remove(node);
        _bytes -= node.Value.EstimatedBytes;
        ClearGroup(node.Value);
    }

    private static void ClearGroup(DecodedWorkspacePassageGroup group)
    {
        foreach (var passage in group.Passages)
        {
            CryptographicOperations.ZeroMemory(System.Runtime.InteropServices.MemoryMarshal.AsBytes(passage.TokenIds.AsSpan()));
            CryptographicOperations.ZeroMemory(System.Runtime.InteropServices.MemoryMarshal.AsBytes(passage.Vector.AsSpan()));
        }
    }
}

/// <summary>Builds one complete passage group without exceeding the cache cap.</summary>
internal sealed class DecodedWorkspacePassageGroupBuilder(
    string fileId,
    long revision,
    string modelRevision)
{
    private readonly List<DecodedWorkspacePassage> _passages = new();
    private int _estimatedBytes = EstimateGroupHeader(fileId, modelRevision);
    private bool _cacheable = true;

    internal bool Cacheable => _cacheable;

    internal void Add(
        WorkspacePassageAnchor anchor,
        ReadOnlySpan<int> tokenIds,
        ReadOnlySpan<float> vector,
        long ordinal)
    {
        if (!_cacheable)
        {
            return;
        }

        var entryBytes = EstimatePassage(anchor, tokenIds.Length);
        if (tokenIds.Length is < 1 or > 512 || vector.Length != 384 ||
            entryBytes > DecodedWorkspacePassageCache.CapacityBytes - _estimatedBytes)
        {
            Clear();
            _cacheable = false;
            return;
        }

        _passages.Add(new DecodedWorkspacePassage(anchor, tokenIds.ToArray(), vector.ToArray(), ordinal));
        _estimatedBytes += entryBytes;
    }

    internal DecodedWorkspacePassageGroup? Complete()
    {
        if (!_cacheable || _passages.Count == 0)
        {
            Clear();
            return null;
        }

        var group = new DecodedWorkspacePassageGroup(
            fileId,
            revision,
            modelRevision,
            _passages.ToArray(),
            _estimatedBytes);
        _passages.Clear();
        _estimatedBytes = EstimateGroupHeader(fileId, modelRevision);
        _cacheable = false;
        return group;
    }

    internal void Clear()
    {
        foreach (var passage in _passages)
        {
            CryptographicOperations.ZeroMemory(System.Runtime.InteropServices.MemoryMarshal.AsBytes(passage.TokenIds.AsSpan()));
            CryptographicOperations.ZeroMemory(System.Runtime.InteropServices.MemoryMarshal.AsBytes(passage.Vector.AsSpan()));
        }

        _passages.Clear();
        _estimatedBytes = EstimateGroupHeader(fileId, modelRevision);
    }

    private static int EstimateGroupHeader(string fileId, string modelRevision) =>
        checked(96 + fileId.Length * sizeof(char) + modelRevision.Length * sizeof(char));

    private static int EstimatePassage(WorkspacePassageAnchor anchor, int tokenCount) =>
        checked(192 + sizeof(float) * 384 + sizeof(int) * tokenCount +
            (anchor.FileId.Length + anchor.Sha256.Length + anchor.Kind.Length + anchor.Attribution.Length) * sizeof(char));
}

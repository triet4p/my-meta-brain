using MetaBrain.Core.Security;

namespace MetaBrain.Core.Index;

/// <summary>
/// Shared application contract for local passage embedding. Implementations
/// are responsible for loading only the explicitly selected local model and
/// must fail closed when its tokenizer or model is unavailable or invalid.
/// The returned vector belongs to the caller, which must clear it when the
/// encrypted generation write completes.
/// </summary>
public interface IWorkspacePassageEmbeddingModel : IDisposable
{
    /// <summary>The immutable revision identity of the public model assets.</summary>
    string ModelRevision { get; }

    /// <summary>The output width required by the pinned passage model.</summary>
    int Dimensions { get; }

    /// <summary>Tokenizes one passage using the pinned tokenizer, including its passage prefix.</summary>
    int[] Tokenize(ReadOnlySpan<char> passage, CancellationToken cancellationToken = default);

    /// <summary>Encodes actual, untruncated model token IDs into one embedding.</summary>
    float[] Encode(ReadOnlySpan<int> tokenIds, CancellationToken cancellationToken = default);
}

/// <summary>A stable owner-visible refusal from the workspace passage index.</summary>
public sealed class WorkspacePassageIndexException(string code) : Exception(code)
{
    public string Code { get; } = code;
}

/// <summary>Owner-visible status of the rebuildable encrypted workspace passage generation.</summary>
public sealed record WorkspacePassageIndexStatus(
    int SchemaVersion,
    string State,
    string WorkspaceDigest,
    long WorkspaceRevision,
    int FileCount,
    long PassageCount,
    int UnsupportedFileCount,
    long PostingCount,
    int VectorDimensions,
    bool Stale,
    string? RefusalCode = null);

/// <summary>One exact source anchor read from the authenticated passage generation.</summary>
public sealed record WorkspacePassageAnchor(
    string FileId,
    long ContentRevision,
    long ByteOffset,
    long ByteLength,
    string Sha256,
    string Kind,
    string Attribution,
    int ChunkOrdinal);

/// <summary>A bounded page of anchors for one exact file revision.</summary>
public sealed record WorkspacePassageAnchorPage(
    string FileId,
    long ContentRevision,
    int Total,
    int Offset,
    int Limit,
    bool HasMore,
    WorkspacePassageAnchor[] Anchors);

/// <summary>The owner-only workspace passage generation boundary.</summary>
public interface IWorkspacePassageIndexEngine : IDisposable
{
    WorkspacePassageIndexStatus ReadStatus(IWorkspacePersistence workspace, CancellationToken cancellationToken = default);

    WorkspacePassageIndexStatus Rebuild(
        IWorkspacePersistence workspace,
        string modelDirectory,
        CancellationToken cancellationToken = default);

    WorkspacePassageAnchorPage ReadAnchors(
        IWorkspacePersistence workspace,
        string fileId,
        long contentRevision,
        int offset,
        int limit,
        CancellationToken cancellationToken = default);

    void Reset();
}

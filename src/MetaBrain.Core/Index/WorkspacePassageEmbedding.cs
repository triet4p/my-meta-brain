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

    /// <summary>
    /// Tokenizes one search query using the pinned tokenizer and the model's
    /// query prefix. The model was pinned with distinct query/passage
    /// instructions, so a query embedded as a passage is a different input.
    /// </summary>
    int[] TokenizeQuery(ReadOnlySpan<char> query, CancellationToken cancellationToken = default);

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

/// <summary>
/// One exact file revision the caller is allowed to search. It is the scope
/// the semantic scan applies before any similarity is computed, so a passage
/// outside it never contributes a score, a candidate or a total.
/// </summary>
public readonly record struct WorkspacePassageScope(string FileId, long ContentRevision);

/// <summary>One semantic candidate at an exact, authenticated source anchor.</summary>
public sealed record WorkspacePassageSemanticMatch(
    string FileId,
    long ContentRevision,
    long ByteOffset,
    long ByteLength,
    string Sha256,
    string Kind,
    string Attribution,
    int ChunkOrdinal,
    double Similarity);

/// <summary>
/// The bounded semantic result of one query. <see cref="Similarity"/> values
/// are diagnostic evidence of the local model's ordering; they are not an
/// approved abstention threshold.
/// </summary>
public sealed record WorkspacePassageSemanticResult(
    string ModelRevision,
    int VectorDimensions,
    int QueryTokenCount,
    int ScopedPassageCount,
    WorkspacePassageSemanticMatch[] Matches);

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

    /// <summary>
    /// Ranks every in-scope passage of the current authenticated generation
    /// for one literal query. Only passages whose exact file identity and
    /// content revision belong to <paramref name="scope"/> are scored, so the
    /// scope is applied before any similarity exists. Every distinct scoped
    /// anchor is returned; the shared ranking owns filtering, totals and the
    /// final order. A generation that does not describe the live workspace
    /// refuses instead of degrading to lexical-only.
    /// </summary>
    WorkspacePassageSemanticResult SearchSemantic(
        IWorkspacePersistence workspace,
        string modelDirectory,
        string query,
        IReadOnlyCollection<WorkspacePassageScope> scope,
        CancellationToken cancellationToken = default);

    void Reset();
}

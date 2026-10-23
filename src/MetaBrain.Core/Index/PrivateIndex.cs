using MetaBrain.Core.Domain;
using MetaBrain.Core.Security;

namespace MetaBrain.Core.Index;

/// <summary>
/// S2-T7 private index record kind. The index is a derived projection of the
/// canonical encrypted records: it carries identity, revision, zone scope and
/// the record's own state, and never a body, title, locator or embedding. A
/// canonical envelope that decodes as none of the known record types stays
/// <see cref="Unknown"/> instead of being guessed at.
/// </summary>
public enum PrivateIndexRecordKind
{
    Unknown = 0,
    MemoryItem = 1,
    RegisteredSource = 2,
    Relation = 3,
    Lifecycle = 4,
    RetainedRevision = 5,
    PreservedSourceSnapshot = 6,
    Feedback = 7,
    Proposal = 8
}

/// <summary>
/// One materialized canonical record. Fields joined at rebuild time:
/// <list type="bullet">
/// <item><description><see cref="ResourceId"/>/<see cref="Revision"/>/<see cref="CanonicalSchemaVersion"/> come from the encrypted manifest descriptor.</description></item>
/// <item><description><see cref="ZoneId"/> is the record's zone, i.e. its permission scope.</description></item>
/// <item><description><see cref="State"/> is the record's own lifecycle/source/relation state.</description></item>
/// <item><description><see cref="LinkedResourceIds"/> names the exact other canonical IDs this record references (citations, relation endpoints, lifecycle subject/supersession/retained archives), so a rebuild reproduces link state without reading a body.</description></item>
/// </list>
/// </summary>
public sealed record PrivateIndexRow(
    string ResourceId,
    string ZoneId,
    long Revision,
    int CanonicalSchemaVersion,
    PrivateIndexRecordKind RecordKind,
    string State,
    string[] LinkedResourceIds);

/// <summary>
/// One search candidate document for S4-T2 scoped lexical recall. The token
/// set is normalized index text from one memory item's title, body and
/// applicability only (plus stems, stopwords removed); the temporal fields
/// are that item's own occurred/recorded instants joined with its lifecycle
/// publication state at rebuild time. Token sets never cross the wire: the
/// service scores them in process and only serves ranked hits with metadata.
/// </summary>
public sealed record SearchIndexDocument(
    string ResourceId,
    long Revision,
    string Kind,
    string Title,
    string[] Tokens,
    DateTimeOffset? OccurredAtUtc,
    DateTimeOffset RecordedAtUtc,
    string Publication,
    string Project,
    string Environment,
    string Version);

/// <summary>
/// Result of one private-index materialization. <see cref="CanonicalDigest"/>
/// is the SHA-256 fingerprint of the canonical resource/revision set the rows
/// were read from, so a later generation can be detected instead of trusted.
/// <see cref="RowCount"/> is the true number of materialized rows;
/// <see cref="Rows"/> is a bounded sample of them (the first rows in
/// resource-ID order), so a large vault cannot turn one materialized
/// generation into an unbounded in-memory collection.
/// </summary>
public sealed record PrivateIndexSnapshot(
    int SchemaVersion,
    string CanonicalDigest,
    int RowCount,
    PrivateIndexRow[] Rows);

/// <summary>
/// The private index file format. The index is a rebuildable derivative of
/// the canonical encrypted records: it never holds canonical state, and the
/// canonical vault stays the only truth.
/// <para>
/// The format is a sequence of individually authenticated records. Every
/// record is sealed with the vault's own AEAD under a key derived from the
/// vault data key, so no plaintext index, temp file, journal or cache path
/// exists on disk: a file, a staging file or a torn write is ciphertext or
/// nothing at all.
/// </para>
/// <para>
/// <see cref="CurrentVersion"/> is the format version this build reads and
/// writes. A file written by a newer build fails closed: this build cannot
/// read an index it does not know, and the owner rebuilds it from canonical.
/// </para>
/// </summary>
public static class PrivateIndexFormat
{
    /// <summary>The format version this build reads and writes.</summary>
    public const int CurrentVersion = 3;

    /// <summary>
    /// The number of token buckets the format keeps. A query term is routed to
    /// one bucket by a public hash of its text, so a term lookup reads one
    /// bucket instead of the whole dictionary, and the bucket table itself
    /// never leaves the sealed file.
    /// </summary>
    public const int TokenBucketCount = 4096;

    /// <summary>The maximum number of linked canonical IDs one row may carry.</summary>
    public const int MaximumLinkedIds = 512;
    public const int MaximumIdentifierBytes = 128;
    public const int MaximumStateBytes = 128;
    public const int MaximumMetadataFieldBytes = 8 * 1024;
    public const int MaximumLinkedIdsPayloadBytes = MaximumLinkedIds * (MaximumIdentifierBytes + 1);

    /// <summary>
    /// Derives storage bounds from the canonical memory field validators.
    /// Four spaces join title, body and the three applicability values. The
    /// focused smoke checks every non-surrogate BMP code unit against the
    /// current .NET Form-D normalizer (at most nine UTF-8 bytes after folding);
    /// the char-based tokenizer does not emit surrogate pairs as terms. A
    /// single accepted digit does not stem, and every ASCII word that adds a
    /// stem is at least four code units, so raw plus stem terms stay within L.
    /// These are source-derived conservative bounds, not a promise that a
    /// future Unicode table may not require a format update.
    /// </summary>
    public const int MaximumDocumentCodeUnits =
        MemoryEnvelopeCodec.MaximumTitleLength +
        MemoryEnvelopeCodec.MaximumBodyChars +
        (3 * MemoryEnvelopeCodec.MaximumApplicabilityLength) +
        4;
    public const int MaximumSearchTokens = MaximumDocumentCodeUnits;
    public const int MaximumTokenBytes = 9 * MaximumDocumentCodeUnits;
    /// <summary>
    /// Count prefix plus at most three UTF-8-length-prefix bytes per token and
    /// the independent 9L upper bound for total token text.
    /// </summary>
    public const int MaximumTokensRecordBytes =
        sizeof(int) + (3 * MaximumSearchTokens) + MaximumTokenBytes;

    /// <summary>
    /// The maximum number of rows one materialized snapshot sample carries.
    /// The sample is the same number of rows the owner wire reply can carry,
    /// so the reply still shows the rows with the smallest resource IDs, in
    /// the same order, as before the sample was bounded.
    /// </summary>
    public const int MaximumSnapshotRows = 512;

    /// <summary>
    /// The number of distinct query terms one streaming pass resolves at
    /// once. A query with more distinct terms than this is still answered:
    /// the engine resolves the terms in groups and verifies the affected
    /// candidates against the document itself.
    /// </summary>
    public const int MaximumQueryTermsPerPass = 64;

    /// <summary>
    /// The byte budget of the resident directory of one generation. The
    /// directory is the one structure the engine keeps resident, and it is
    /// bounded: a generation whose directory would not fit is refused instead
    /// of being loaded, so the resident size of the engine is a measured
    /// bound and not a function of the vault.
    /// </summary>
    public const long MaximumResidentDirectoryBytes = 32L * 1024 * 1024;

    /// <summary>
    /// The maximum bytes in one sealed token-bucket root. The root points to
    /// bounded pages; both the page count and each page's byte/entry count are
    /// format limits independent of the vocabulary size.
    /// </summary>
    public const int MaximumBucketRecordBytes = 256;
    public const int MaximumBucketPageBytes = 256 * 1024;
    public const int MaximumBucketPageEntries = 384;
    public const int MaximumBucketKeyBytes = 64;
    public const int MaximumTermKeyBytes = 32;

    /// <summary>The bounded per-document temporal metadata record.</summary>
    public const int MaximumDocumentRecordBytes = 64 * 1024;

    /// <summary>The bounded per-resource metadata record (including linked IDs).</summary>
    public const int MaximumResourceRecordBytes = 128 * 1024;

    /// <summary>
    /// The largest sealed directory record a generation may hold. It is the
    /// resident directory's own budget, so the record and the structure it
    /// decodes into are bounded by the same number.
    /// </summary>
    public const int MaximumDirectoryRecordBytes = (int)MaximumResidentDirectoryBytes;

    /// <summary>The maximum bytes in a posting-root descriptor record.</summary>
    public const int MaximumPostingRootBytes = 256;


    /// <summary>The maximum number of posting chunks in one root record.</summary>
    public const int MaximumPostingChunks = (MaximumIndexedRows + MaximumPostingChunkPostings - 1) / MaximumPostingChunkPostings;
    /// <summary>The canonical manifest's existing maximum resource count.</summary>
    public const int MaximumIndexedRows = 100_000;

    /// <summary>The byte budget of one in-memory posting arena.</summary>
    public const int MaximumPostingArenaBytes = 4 * 1024 * 1024;

    /// <summary>The maximum pairs held by one sorted posting run.</summary>
    public const int MaximumPostingPairsPerRun = 65_536;

    /// <summary>The maximum pair count derived from the row and per-document token contracts.</summary>
    public const long MaximumPostingPairs = (long)MaximumIndexedRows * MaximumSearchTokens;
    /// <summary>The maximum pairs and plaintext bytes in one encrypted, grouped-digest spill chunk.</summary>
    public const int MaximumSpillChunkPairs = 1_024;
    public const int MaximumSpillChunkBytes =
        (3 * sizeof(int)) +
        (MaximumSpillChunkPairs * (MaximumTermKeyBytes + (2 * sizeof(int))));

    /// <summary>The maximum number of sorted runs read in one merge pass.</summary>
    public const int MergeFanIn = 8;

    /// <summary>The maximum postings encoded in one final encrypted chunk.</summary>
    public const int MaximumPostingChunkPostings = 32_768;
    public const int MaximumPostingChunkBytes = 256 * 1024;

    /// <summary>The separate maximum entries in one token bucket.</summary>
    public const int MaximumBucketEntries = 4_096;
    public const int MaximumBucketPages = (MaximumBucketEntries + MaximumBucketPageEntries - 1) / MaximumBucketPageEntries;

    /// <summary>The largest version-2 encrypted spill record accepted before allocation.</summary>
    public const int MaximumSpillRecordBytes = MaximumSpillChunkBytes;

    /// <summary>
    /// Rejects a format version this build cannot read. A newer index is
    /// never partially read and never silently replaced: the owner rebuilds
    /// it from canonical.
    /// </summary>
    public static void RequireSupported(int version)
    {
        if (version != CurrentVersion)
        {
            throw new InvalidDataException("The private index format version is unsupported.");
        }
    }

    /// <summary>Rejects a generation beyond the vault manifest's resource-count limit.</summary>
    public static void RequireIndexedRowCount(int count)
    {
        if (count is < 0 or > MaximumIndexedRows)
        {
            throw new InvalidDataException("The private index row count is invalid.");
        }
    }

}
/// <summary>
/// The source of the private index key. The index is encrypted at rest with a
/// key derived from the vault's own data key, so the key exists only inside
/// an unlocked epoch: there is no separately stored key, no autonomous
/// unlock, and a locked vault has no way to produce one.
/// </summary>
public interface IPrivateIndexKeyProvider
{
    /// <summary>
    /// The current index key, or null when the vault is locked. The caller
    /// owns the returned bytes and must clear them when it is done with them.
    /// </summary>
    byte[]? PrivateIndexKey { get; }
}

/// <summary>
/// The private index engine. An implementation keeps its derived data
/// encrypted at rest and holds only bounded structures in process memory: a
/// directory of the materialized generation and a byte-bounded cache of
/// decrypted records. It owns no key material beyond the key it borrows for
/// the operation it is running, and it never writes canonical state.
/// </summary>
public interface IPrivateIndexStore : IDisposable
{
    /// <summary>
    /// Opens the engine. The published generation is read (and authenticated)
    /// when one exists; an engine without a published generation is empty.
    /// Opening without an unlocked key fails closed.
    /// </summary>
    void Open();

    /// <summary>
    /// Whether the engine currently holds an opened, authenticated published
    /// generation. A generation that exists on disk but has not been opened is
    /// not counted, and an engine without a published file is empty rather
    /// than failed.
    /// </summary>
    bool HasOpenGeneration { get; }

    /// <summary>The format version of the opened generation.</summary>
    int SchemaVersion { get; }

    /// <summary>The number of materialized rows of the opened generation.</summary>
    int RowCount { get; }

    /// <summary>Removes any materialized rows, leaving the engine empty.</summary>
    void Clear();

    /// <summary>
    /// Releases every materialized row, every cached record and the engine
    /// itself, so no private derived data outlives the unlocked epoch. The
    /// instance stays usable and the next <see cref="Open"/> starts again.
    /// </summary>
    void Reset();

    void Insert(PrivateIndexRow row);

    /// <summary>
    /// Reads at most <paramref name="maximumRows"/> materialized rows, in
    /// resource-ID order. The sample is bounded on purpose: the materialized
    /// generation itself is never truncated, only the sample is.
    /// </summary>
    PrivateIndexRow[] SelectSample(int maximumRows);

    /// <summary>
    /// S4-T2: stores one memory item's search document (the temporal row and
    /// its token postings). Only memory items are searchable; any other kind
    /// is refused so relations, lifecycles and snapshots can never enter
    /// recall.
    /// </summary>
    void InsertSearchDocument(SearchIndexDocument document);

    /// <summary>
    /// Reads one candidate's complete search document. Returns null when the
    /// candidate is not a materialized memory item at the approved revision
    /// (stale, withdrawn, or never indexed).
    /// </summary>
    SearchIndexDocument? SelectSearchDocument(string resourceId, long revision);

    /// <summary>
    /// Reads the complete search documents of many candidates in one engine
    /// pass. A candidate the engine does not hold at that exact revision is
    /// simply absent from the answer, exactly as the single read answers null
    /// for it.
    /// </summary>
    SearchIndexDocument[] SelectSearchDocuments(IReadOnlyList<ScopeResourceRevision> candidates);

    /// <summary>
    /// Streams the search candidates of one query in bounded batches.
    /// <para>
    /// The engine applies the caller's approved scope itself: a resource that
    /// is not in the scope never leaves the engine, exactly as the scope
    /// filter used to run inside the engine's own query.
    /// </para>
    /// <para>
    /// Every eligible candidate is visited: the stream is bounded in size,
    /// not in coverage. A candidate is scored by the terms it holds, and the
    /// document it carries holds only those terms, so the ranking never needs
    /// the whole corpus in memory at once.
    /// </para>
    /// </summary>
    /// <param name="includeTemporalRow">
    /// Whether the caller needs the temporal row of every candidate (its
    /// kind, its instants, its publication state and its applicability). A
    /// ranking that only scores the query terms does not need it, and the
    /// engine then reads one record per candidate less. The row is always
    /// read when the query has no terms, because the browse order needs the
    /// item's own instant.
    /// </param>
    /// <param name="cancellationToken">Cancels a stream and releases its exact touched-query state.</param>
    IEnumerable<SearchIndexCandidateBatch> StreamSearchCandidates(
        IReadOnlyList<ScopeResourceRevision> scope,
        IReadOnlyList<string> tokens,
        int batchSize,
        bool includeTemporalRow,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Opens a bulk-write scope for one materialization pass. Every insert
    /// made inside the scope lands in one staged generation, so a rebuild of a
    /// large vault does not publish a partial generation: the scope never
    /// publishes anything it did not finish.
    /// <para>
    /// The canonical digest of the generation is part of the scope: the
    /// generation's identity is derived from it, so a generation can only be
    /// read as the generation of the canonical set it was built from.
    /// </para>
    /// </summary>
    IPrivateIndexWriteScope BeginBulkWrite(string canonicalDigest);

    /// <summary>
    /// The canonical digest of the generation the engine holds. It is the
    /// digest the generation was built from, and it is authenticated: it
    /// lives inside the sealed directory of the generation.
    /// </summary>
    string CanonicalDigest { get; }
}

/// <summary>
/// One bounded batch of streamed search candidates. Every candidate is one
/// approved scope entry and the document it carries holds the query terms
/// that document holds.
/// </summary>
public sealed record SearchIndexCandidateBatch(IReadOnlyList<SearchIndexCandidate> Candidates);

/// <summary>
/// One streamed search candidate: the approved scope entry it answers for and
/// the search document the engine read for it.
/// </summary>
public sealed record SearchIndexCandidate(
    ScopeResourceRevision Resource,
    SearchIndexDocument Document);

/// <summary>
/// One bulk-write scope of the private index engine. The scope commits the
/// materialized rows exactly once, or rolls them back; the caller decides
/// which, and the engine keeps no rows of an uncommitted scope.
/// </summary>
public interface IPrivateIndexWriteScope : IDisposable
{
    /// <summary>Commits the rows written inside this scope.</summary>
    void Commit();

    /// <summary>Discards the rows written inside this scope.</summary>
    void Rollback();
}

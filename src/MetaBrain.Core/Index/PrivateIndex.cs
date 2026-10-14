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
    Feedback = 7
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
/// <see cref="Rows"/> is ordered by resource ID for deterministic output.
/// </summary>
public sealed record PrivateIndexSnapshot(
    int SchemaVersion,
    string CanonicalDigest,
    int RowCount,
    PrivateIndexRow[] Rows);

/// <summary>
/// One ordered schema upgrade step for the private index. Statements execute
/// in order inside the store's own transaction; the store advances its
/// recorded schema version only after every statement succeeds. S8 appends
/// steps here without touching any caller.
/// </summary>
public sealed record PrivateIndexMigration(int FromVersion, int ToVersion, IReadOnlyList<string> Statements);

/// <summary>
/// Versioned schema for the private index and its migration extension point.
/// The initial step (0 -> 1) is real and executed on every materialization:
/// a fresh engine carries no user schema version and must be upgraded to
/// <see cref="CurrentSchemaVersion"/> before any row is written. A version
/// above <see cref="CurrentSchemaVersion"/> fails closed: this build cannot
/// read a newer index than it knows.
/// </summary>
public static class PrivateIndexSchema
{
    public const int CurrentSchemaVersion = 2;
    public const string ResourceTable = "private_index_resources";
    public const string SearchTable = "private_index_search";
    public const string TemporalTable = "private_index_temporal";
    public const int MaximumLinkedIds = 512;
    public const int MaximumSearchTokens = 2048;

    /// <summary>
    /// Creates the v1 resource table. Column set is deliberately the canonical
    /// projection only: no body, title, locator, embedding, count or grant
    /// metadata is materialized, so an index export can never carry private
    /// text or authority.
    /// </summary>
    private static readonly string[] CreateResourceTableV1 =
    {
        "CREATE TABLE IF NOT EXISTS " + ResourceTable + " (" +
            "resource_id TEXT PRIMARY KEY NOT NULL, " +
            "zone_id TEXT NOT NULL, " +
            "revision INTEGER NOT NULL, " +
            "canonical_schema_version INTEGER NOT NULL, " +
            "record_kind TEXT NOT NULL, " +
            "state TEXT NOT NULL, " +
            "linked_resource_ids TEXT NOT NULL)",
        "CREATE INDEX IF NOT EXISTS " + ResourceTable + "_zone ON " + ResourceTable + " (zone_id, resource_id)",
        "CREATE INDEX IF NOT EXISTS " + ResourceTable + "_revision ON " + ResourceTable + " (resource_id, revision)"
    };

    /// <summary>
    /// S4-T2 search tables. The postings table holds one normalized token per
    /// row per memory item (token sets, not raw text); the temporal table
    /// holds that item's kind, title, occurred/recorded instants, publication
    /// state and applicability for ranking and time filtering. No raw body is
    /// stored: tokens are derived text, readable only in process memory, and
    /// dropped with the engine on lock/restart.
    /// </summary>
    private static readonly string[] CreateSearchTablesV2 =
    {
        "CREATE TABLE IF NOT EXISTS " + SearchTable + " (" +
            "resource_id TEXT NOT NULL, " +
            "revision INTEGER NOT NULL, " +
            "token TEXT NOT NULL, " +
            "PRIMARY KEY (resource_id, revision, token))",
        "CREATE INDEX IF NOT EXISTS " + SearchTable + "_token ON " + SearchTable + " (token, resource_id, revision)",
        "CREATE TABLE IF NOT EXISTS " + TemporalTable + " (" +
            "resource_id TEXT NOT NULL, " +
            "revision INTEGER NOT NULL, " +
            "kind TEXT NOT NULL, " +
            "title TEXT NOT NULL, " +
            "occurred_at_utc TEXT NULL, " +
            "recorded_at_utc TEXT NOT NULL, " +
            "publication TEXT NOT NULL, " +
            "project TEXT NOT NULL, " +
            "environment TEXT NOT NULL, " +
            "version TEXT NOT NULL, " +
            "PRIMARY KEY (resource_id, revision))",
    };

    public static IReadOnlyList<PrivateIndexMigration> Migrations { get; } = new[]
    {
        new PrivateIndexMigration(0, 1, CreateResourceTableV1),
        new PrivateIndexMigration(1, CurrentSchemaVersion, CreateSearchTablesV2)
    };


    /// <summary>
    /// Rejects a schema version this build cannot read or write. Version 0 is
    /// the un-initialized engine and is upgraded by <see cref="Migrations"/>
    /// before it reaches this check.
    /// </summary>
    public static void RequireSupported(int schemaVersion)
    {
        if (schemaVersion is < 1 or > CurrentSchemaVersion)
        {
            throw new InvalidDataException("The private index schema version is unsupported.");
        }
    }
}

/// <summary>
/// The disposable index engine. Implementations hold private derived data in
/// process memory only: no file, journal, WAL, temp or cache path is created,
/// so lock, restart and crash can never leave a plaintext private index at
/// rest. The engine owns no key material and never writes canonical state.
/// </summary>
public interface IPrivateIndexStore : IDisposable
{
    /// <summary>
    /// Opens the engine and applies <see cref="PrivateIndexSchema.Migrations"/>
    /// until the recorded schema version equals
    /// <see cref="PrivateIndexSchema.CurrentSchemaVersion"/>.
    /// </summary>
    void Open();

    int SchemaVersion { get; }

    /// <summary>Removes any materialized rows, leaving the schema in place.</summary>
    void Clear();

    /// <summary>
    /// Releases every materialized row and the engine itself, so no private
    /// derived data outlives the unlocked epoch. The instance stays usable and
    /// the next <see cref="Open"/> starts a fresh engine.
    /// </summary>
    void Reset();

    void Insert(PrivateIndexRow row);

    /// <summary>Reads every materialized row back ordered by resource ID.</summary>
    PrivateIndexRow[] SelectAll();

    /// <summary>
    /// S4-T2: stores one memory item's search document (token postings plus
    /// the temporal row). Only memory items are searchable; any other kind is
    /// refused so relations, lifecycles and snapshots can never enter recall.
    /// </summary>
    void InsertSearchDocument(SearchIndexDocument document);

    /// <summary>
    /// S4-T2: reads the token-matched candidate resource IDs for the query
    /// terms, restricted to the caller's frozen scope IDs up front. The SQL
    /// filters candidate rows to the approved ID set before any token row is
    /// returned, so out-of-scope tokens are never scored or decrypted.
    /// </summary>
    string[] SelectSearchCandidates(IReadOnlyList<string> scopeResourceIds, IReadOnlyList<string> tokens);

    /// <summary>
    /// S4-T2: reads one candidate's search document for scoring. Returns null
    /// when the candidate is not a materialized memory item at the approved
    /// revision (stale, withdrawn, or never indexed).
    /// </summary>
    SearchIndexDocument? SelectSearchDocument(string resourceId, long revision);
}

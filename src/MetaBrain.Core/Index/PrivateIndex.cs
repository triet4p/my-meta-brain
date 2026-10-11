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
    RetainedRevision = 5
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
    public const int CurrentSchemaVersion = 1;
    public const string ResourceTable = "private_index_resources";
    public const int MaximumLinkedIds = 512;

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

    public static IReadOnlyList<PrivateIndexMigration> Migrations { get; } = new[]
    {
        new PrivateIndexMigration(0, CurrentSchemaVersion, CreateResourceTableV1)
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
}

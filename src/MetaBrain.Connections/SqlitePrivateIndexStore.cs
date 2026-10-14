using System.Security.Cryptography;
using MetaBrain.Core.Index;

namespace MetaBrain.Connections;

/// <summary>
/// S2-T7 private index engine: a real SQLite database held entirely in process
/// memory (<c>Data Source=:memory:</c>) with temp storage forced to memory, so
/// no database file, WAL, rollback journal, temporary sort file or page cache
/// is ever created on disk. The engine holds no key material and no canonical
/// state: the encrypted canonical envelope stays the only durable truth, and
/// this index is discarded at lock, restart and crash.
///
/// Observed boundary (probe and S2-T7 smoke): the connection string names no
/// path, <c>PRAGMA temp_store=MEMORY</c> keeps large sorts in RAM, and a
/// rebuild of the synthetic fixture leaves the vault directory, its sibling
/// service directory and the process temp directory byte-for-byte unchanged.
/// Memory residency is a confidentiality trade-off, not a protection claim:
/// a same-owner process with hostile access to this process's memory is
/// outside the CR01 threat model, exactly as it is for the unlocked data key.
/// </summary>
internal sealed class SqlitePrivateIndexStore : IPrivateIndexStore
{
    private const string ConnectionString = "Data Source=:memory:;Cache=Shared";
    private const int CacheKiB = 8 * 1024;
    private readonly object _gate = new();
    private Microsoft.Data.Sqlite.SqliteConnection? _connection;
    private bool _disposed;

    public int SchemaVersion
    {
        get
        {
            var connection = RequireConnection();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA user_version;";
            return Convert.ToInt32(command.ExecuteScalar());
        }
    }

    public void Open()
    {
        ThrowIfDisposed();
        lock (_gate)
        {
            if (_connection is not null)
            {
                return;
            }

            var connection = new Microsoft.Data.Sqlite.SqliteConnection(ConnectionString);
            try
            {
                connection.Open();
                ApplyMemoryOnlyPragmas(connection);
                ApplyMigrations(connection);
                _connection = connection;
            }
            catch
            {
                connection.Dispose();
                throw;
            }
        }
    }

    public void Clear()
    {
        var connection = RequireConnection();
        lock (_gate)
        {
            using var command = connection.CreateCommand();
            // S4-T2: every materialized table is dropped, not just the S2-T7
            // resource rows, so a stale generation can never survive a clear.
            command.CommandText =
                "DELETE FROM " + PrivateIndexSchema.SearchTable + "; " +
                "DELETE FROM " + PrivateIndexSchema.TemporalTable + "; " +
                "DELETE FROM " + PrivateIndexSchema.ResourceTable + ";";
            command.ExecuteNonQuery();
        }
    }


    public void Reset()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            // Dropping the connection drops the whole in-memory database: the
            // engine, its rows and its page cache leave process memory with it.
            _connection?.Dispose();
            _connection = null;
        }
    }

    public void Insert(PrivateIndexRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        var connection = RequireConnection();
        ValidateRow(row);
        lock (_gate)
        {
            using var command = connection.CreateCommand();
            command.CommandText =
                "INSERT OR REPLACE INTO " + PrivateIndexSchema.ResourceTable + " " +
                "(resource_id, zone_id, revision, canonical_schema_version, record_kind, state, linked_resource_ids) " +
                "VALUES ($resourceId, $zoneId, $revision, $schemaVersion, $recordKind, $state, $linkedIds);";
            command.Parameters.AddWithValue("$resourceId", row.ResourceId);
            command.Parameters.AddWithValue("$zoneId", row.ZoneId);
            command.Parameters.AddWithValue("$revision", row.Revision);
            command.Parameters.AddWithValue("$schemaVersion", row.CanonicalSchemaVersion);
            command.Parameters.AddWithValue("$recordKind", row.RecordKind.ToString().ToLowerInvariant());
            command.Parameters.AddWithValue("$state", row.State);
            command.Parameters.AddWithValue("$linkedIds", string.Join(',', row.LinkedResourceIds));
            command.ExecuteNonQuery();
        }
    }

    public PrivateIndexRow[] SelectAll()
    {
        var connection = RequireConnection();
        lock (_gate)
        {
            var rows = new List<PrivateIndexRow>();
            using (var command = connection.CreateCommand())
            {
                command.CommandText =
                    "SELECT resource_id, zone_id, revision, canonical_schema_version, record_kind, state, linked_resource_ids " +
                    "FROM " + PrivateIndexSchema.ResourceTable + " ORDER BY resource_id ASC;";
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    rows.Add(new PrivateIndexRow(
                        reader.GetString(0),
                        reader.GetString(1),
                        reader.GetInt64(2),
                        reader.GetInt32(3),
                        ParseRecordKind(reader.GetString(4)),
                        reader.GetString(5),
                        string.IsNullOrEmpty(reader.GetString(6))
                            ? Array.Empty<string>()
                            : reader.GetString(6).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)));
                }
            }

            return rows.ToArray();
        }
    }
    public void InsertSearchDocument(PrivateIndexRow row, SearchIndexDocument document)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(document);
        if (!string.Equals(row.ResourceId, document.ResourceId, StringComparison.Ordinal) ||
            row.Revision != document.Revision ||
            row.RecordKind != PrivateIndexRecordKind.MemoryItem)
        {
            throw new InvalidDataException("A search document must describe its own memory item row.");
        }

        ValidateSearchDocument(document);
        var connection = RequireConnection();
        lock (_gate)
        {
            // Revision-pinned rows: every indexed revision keeps its own
            // postings, so a frozen grant keeps finding its approved revision
            // after later revisions rebuild. Re-indexing the same revision
            // only adds (INSERT OR IGNORE); tokens a later revision drops
            // stay truthful on that later revision's own rows.
            foreach (var token in document.Tokens)
            {
                using var posting = connection.CreateCommand();
                posting.CommandText =
                    "INSERT OR IGNORE INTO " + PrivateIndexSchema.SearchTable + " " +
                    "(resource_id, revision, token) VALUES ($resourceId, $revision, $token);";
                posting.Parameters.AddWithValue("$resourceId", document.ResourceId);
                posting.Parameters.AddWithValue("$revision", document.Revision);
                posting.Parameters.AddWithValue("$token", token);
                posting.ExecuteNonQuery();
            }


            using var temporal = connection.CreateCommand();
            temporal.CommandText =
                "INSERT OR REPLACE INTO " + PrivateIndexSchema.TemporalTable + " " +
                "(resource_id, revision, kind, title, occurred_at_utc, recorded_at_utc, publication, project, environment, version) " +
                "VALUES ($resourceId, $revision, $kind, $title, $occurred, $recorded, $publication, $project, $environment, $version);";
            temporal.Parameters.AddWithValue("$resourceId", document.ResourceId);
            temporal.Parameters.AddWithValue("$revision", document.Revision);
            temporal.Parameters.AddWithValue("$kind", document.Kind);
            temporal.Parameters.AddWithValue("$title", document.Title);
            temporal.Parameters.AddWithValue("$occurred", document.OccurredAtUtc?.ToString("O") ?? (object)DBNull.Value);
            temporal.Parameters.AddWithValue("$recorded", document.RecordedAtUtc.ToString("O"));
            temporal.Parameters.AddWithValue("$publication", document.Publication);
            temporal.Parameters.AddWithValue("$project", document.Project);
            temporal.Parameters.AddWithValue("$environment", document.Environment);
            temporal.Parameters.AddWithValue("$version", document.Version);
            temporal.ExecuteNonQuery();
        }
    }

    public void InsertSearchDocument(SearchIndexDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var connection = RequireConnection();
        lock (_gate)
        {
            var revision = SelectRevisionLocked(connection, document.ResourceId);
            if (revision is null || revision.Value != document.Revision)
            {
                throw new InvalidDataException("A search document must describe the materialized memory item revision.");
            }

            var kind = SelectRecordKindLocked(connection, document.ResourceId);
            if (!string.Equals(kind, "memoryitem", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("Only memory items are searchable.");
            }
        }

        InsertSearchDocument(SelectRowLocked(document.ResourceId), document);
    }

    public string[] SelectSearchCandidates(IReadOnlyList<string> scopeResourceIds, IReadOnlyList<string> tokens)
    {
        ArgumentNullException.ThrowIfNull(scopeResourceIds);
        ArgumentNullException.ThrowIfNull(tokens);
        if (scopeResourceIds.Count == 0 || tokens.Count == 0)
        {
            return Array.Empty<string>();
        }

        var connection = RequireConnection();
        lock (_gate)
        {
            // Scope IDs bind as parameters in the SQL WHERE clause: token rows
            // for out-of-scope resources are filtered by the engine before any
            // row is returned, never scored or decrypted by the caller.
            var scopeNames = new string[scopeResourceIds.Count];
            var tokenNames = new string[tokens.Count];
            using var command = connection.CreateCommand();
            var builder = new System.Text.StringBuilder(
                "SELECT DISTINCT resource_id FROM " + PrivateIndexSchema.SearchTable + " WHERE token IN (");
            for (var index = 0; index < tokens.Count; index++)
            {
                if (index > 0)
                {
                    builder.Append(", ");
                }

                var name = "$token" + index.ToString(System.Globalization.CultureInfo.InvariantCulture);
                builder.Append(name);
                tokenNames[index] = name;
                command.Parameters.AddWithValue(name, tokens[index]);
            }

            builder.Append(") AND resource_id IN (");
            for (var index = 0; index < scopeResourceIds.Count; index++)
            {
                if (index > 0)
                {
                    builder.Append(", ");
                }

                var name = "$scope" + index.ToString(System.Globalization.CultureInfo.InvariantCulture);
                builder.Append(name);
                scopeNames[index] = name;
                command.Parameters.AddWithValue(name, scopeResourceIds[index]);
            }

            builder.Append(") ORDER BY resource_id ASC;");
            command.CommandText = builder.ToString();
            var candidates = new List<string>();
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                candidates.Add(reader.GetString(0));
            }

            return candidates.ToArray();
        }
    }

    public SearchIndexDocument? SelectSearchDocument(string resourceId, long revision)
    {
        if (string.IsNullOrEmpty(resourceId) || revision < 1)
        {
            return null;
        }

        var connection = RequireConnection();
        lock (_gate)
        {
            using var temporal = connection.CreateCommand();
            temporal.CommandText =
                "SELECT revision, kind, title, occurred_at_utc, recorded_at_utc, publication, project, environment, version " +
                "FROM " + PrivateIndexSchema.TemporalTable + " WHERE resource_id = $resourceId AND revision = $revision;";
            temporal.Parameters.AddWithValue("$resourceId", resourceId);
            temporal.Parameters.AddWithValue("$revision", revision);
            string? kind = null;
            string? title = null;
            string? occurred = null;
            string? recorded = null;
            string? publication = null;
            string? project = null;
            string? environment = null;
            string? version = null;
            long storedRevision = 0;
            using (var reader = temporal.ExecuteReader())
            {
                if (!reader.Read())
                {
                    return null;
                }

                storedRevision = reader.GetInt64(0);
                kind = reader.GetString(1);
                title = reader.GetString(2);
                occurred = reader.IsDBNull(3) ? null : reader.GetString(3);
                recorded = reader.GetString(4);
                publication = reader.GetString(5);
                project = reader.GetString(6);
                environment = reader.GetString(7);
                version = reader.GetString(8);
            }

            if (storedRevision != revision)
            {
                return null;
            }

            var tokens = new List<string>();
            using (var postings = connection.CreateCommand())
            {
                postings.CommandText =
                    "SELECT token FROM " + PrivateIndexSchema.SearchTable + " WHERE resource_id = $resourceId AND revision = $revision ORDER BY token ASC;";
                postings.Parameters.AddWithValue("$resourceId", resourceId);
                postings.Parameters.AddWithValue("$revision", revision);
                using var reader = postings.ExecuteReader();
                while (reader.Read())
                {
                    tokens.Add(reader.GetString(0));
                }
            }


            DateTimeOffset? occurredAt = null;
            if (occurred is not null &&
                DateTimeOffset.TryParse(occurred, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out var parsedOccurred))
            {
                occurredAt = parsedOccurred;
            }

            if (!DateTimeOffset.TryParse(recorded, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out var recordedAt))
            {
                return null;
            }

            return new SearchIndexDocument(
                resourceId, storedRevision, kind!, title!, tokens.ToArray(),
                occurredAt, recordedAt, publication!, project!, environment!, version!);
        }
    }


    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _connection?.Dispose();
            _connection = null;
        }

        GC.SuppressFinalize(this);
    }

    private static void ApplyMemoryOnlyPragmas(Microsoft.Data.Sqlite.SqliteConnection connection)
    {
        // Every statement below is what keeps the engine off the filesystem:
        // temp_store=MEMORY forbids temporary sort/journal files, journal_mode
        // and synchronous are irrelevant for :memory: but are pinned so a later
        // configuration change cannot silently enable a spill path, and the
        // bounded page cache keeps the working set from growing with the vault.
        foreach (var pragma in new[]
                 {
                     "PRAGMA temp_store=MEMORY;",
                     "PRAGMA journal_mode=OFF;",
                     "PRAGMA synchronous=OFF;",
                     "PRAGMA locking_mode=EXCLUSIVE;",
                     "PRAGMA foreign_keys=ON;",
                     "PRAGMA cache_size=" + (-CacheKiB).ToString(System.Globalization.CultureInfo.InvariantCulture) + ";"
                 })
        {
            using var command = connection.CreateCommand();
            command.CommandText = pragma;
            command.ExecuteNonQuery();
        }
    }

    private static void ApplyMigrations(Microsoft.Data.Sqlite.SqliteConnection connection)
    {
        var version = 0;
        using (var read = connection.CreateCommand())
        {
            read.CommandText = "PRAGMA user_version;";
            version = Convert.ToInt32(read.ExecuteScalar());
        }

        // Migrations chain: a fresh engine walks 0 -> 1 -> 2, and an S2-T7
        // v1 engine walks 1 -> 2, before the supported-version check.
        var steps = PrivateIndexSchema.Migrations.OrderBy(step => step.FromVersion).ToArray();
        while (true)
        {
            var next = steps.FirstOrDefault(step => step.FromVersion == version);
            if (next is null)
            {
                break;
            }

            using var transaction = connection.BeginTransaction();
            try
            {
                foreach (var statement in next.Statements)
                {
                    using var command = connection.CreateCommand();
                    command.Transaction = transaction;
                    command.CommandText = statement;
                    command.ExecuteNonQuery();
                }

                using var versionCommand = connection.CreateCommand();
                versionCommand.Transaction = transaction;
                versionCommand.CommandText = "PRAGMA user_version=" + next.ToVersion.ToString(System.Globalization.CultureInfo.InvariantCulture) + ";";
                versionCommand.ExecuteNonQuery();
                transaction.Commit();
            }
            catch
            {
                transaction.Rollback();
                throw;
            }

            version = next.ToVersion;
        }

        PrivateIndexSchema.RequireSupported(SchemaVersionOf(connection));
    }


    private static int SchemaVersionOf(Microsoft.Data.Sqlite.SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";
        return Convert.ToInt32(command.ExecuteScalar());
    }

    private static PrivateIndexRecordKind ParseRecordKind(string value) =>
        Enum.TryParse<PrivateIndexRecordKind>(value, ignoreCase: true, out var kind) && Enum.IsDefined(kind)
            ? kind
            : PrivateIndexRecordKind.Unknown;

    private static void ValidateRow(PrivateIndexRow row)
    {
        if (string.IsNullOrEmpty(row.ResourceId) || string.IsNullOrEmpty(row.ZoneId) ||
            row.Revision < 1 || row.ResourceId.Contains(',') || row.ResourceId.Contains(';') ||
            row.ZoneId.Contains(',') || row.ZoneId.Contains(';') ||
            row.CanonicalSchemaVersion < 0 || string.IsNullOrEmpty(row.State) ||
            row.LinkedResourceIds is null || row.LinkedResourceIds.Length > PrivateIndexSchema.MaximumLinkedIds ||
            !Enum.IsDefined(row.RecordKind) || row.LinkedResourceIds.Any(id => string.IsNullOrEmpty(id) || id.Contains(',')))
        {
            throw new InvalidDataException("A private index row is not materializable.");
        }
    }
    private static void ValidateSearchDocument(SearchIndexDocument document)
    {
        if (string.IsNullOrEmpty(document.ResourceId) || document.Revision < 1 ||
            string.IsNullOrEmpty(document.Kind) || document.Title is null ||
            document.Tokens is null || document.Tokens.Length > PrivateIndexSchema.MaximumSearchTokens ||
            document.Tokens.Any(token => string.IsNullOrEmpty(token) || token.Length > 128) ||
            string.IsNullOrEmpty(document.Publication) ||
            document.Project is null || document.Environment is null || document.Version is null ||
            document.RecordedAtUtc.Offset != TimeSpan.Zero ||
            (document.OccurredAtUtc is not null && document.OccurredAtUtc.Value.Offset != TimeSpan.Zero))
        {
            throw new InvalidDataException("A search document is not materializable.");
        }
    }

    private long? SelectRevisionLocked(Microsoft.Data.Sqlite.SqliteConnection connection, string resourceId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT revision FROM " + PrivateIndexSchema.ResourceTable + " WHERE resource_id = $resourceId;";
        command.Parameters.AddWithValue("$resourceId", resourceId);
        var value = command.ExecuteScalar();
        return value is null or DBNull ? null : Convert.ToInt64(value);
    }

    private string? SelectRecordKindLocked(Microsoft.Data.Sqlite.SqliteConnection connection, string resourceId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT record_kind FROM " + PrivateIndexSchema.ResourceTable + " WHERE resource_id = $resourceId;";
        command.Parameters.AddWithValue("$resourceId", resourceId);
        var value = command.ExecuteScalar();
        return value is null or DBNull ? null : Convert.ToString(value);
    }

    private PrivateIndexRow SelectRowLocked(string resourceId)
    {
        var connection = RequireConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT resource_id, zone_id, revision, canonical_schema_version, record_kind, state, linked_resource_ids " +
            "FROM " + PrivateIndexSchema.ResourceTable + " WHERE resource_id = $resourceId;";
        command.Parameters.AddWithValue("$resourceId", resourceId);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            throw new InvalidDataException("The search document has no materialized resource row.");
        }

        return new PrivateIndexRow(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetInt64(2),
            reader.GetInt32(3),
            ParseRecordKind(reader.GetString(4)),
            reader.GetString(5),
            string.IsNullOrEmpty(reader.GetString(6))
                ? Array.Empty<string>()
                : reader.GetString(6).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }


    private Microsoft.Data.Sqlite.SqliteConnection RequireConnection()
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            return _connection ?? throw new InvalidOperationException("The private index engine is not open.");
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}

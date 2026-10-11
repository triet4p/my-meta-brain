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
            command.CommandText = "DELETE FROM " + PrivateIndexSchema.ResourceTable + ";";
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

        // Version 0 is the un-initialized engine: it is upgraded to
        // CurrentSchemaVersion by the migration steps below before any check.
        foreach (var migration in PrivateIndexSchema.Migrations.Where(step => step.FromVersion == version))
        {
            using var transaction = connection.BeginTransaction();
            try
            {
                foreach (var statement in migration.Statements)
                {
                    using var command = connection.CreateCommand();
                    command.Transaction = transaction;
                    command.CommandText = statement;
                    command.ExecuteNonQuery();
                }

                using var versionCommand = connection.CreateCommand();
                versionCommand.Transaction = transaction;
                versionCommand.CommandText = "PRAGMA user_version=" + migration.ToVersion.ToString(System.Globalization.CultureInfo.InvariantCulture) + ";";
                versionCommand.ExecuteNonQuery();
                transaction.Commit();
            }
            catch
            {
                transaction.Rollback();
                throw;
            }
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

using System.Security.Cryptography;
using System.Text.Json;
using MetaBrain.Core.Domain;
using MetaBrain.Core.Security;

namespace MetaBrain.Connections;

/// <summary>
/// One file the upgrade copied into its rollback point before touching it.
/// <see cref="Name"/> is a storage name (random <c>.mbv</c>) or the fixed
/// control-state file name; <see cref="SchemaVersion"/> and
/// <see cref="Revision"/> are the binding the copied ciphertext carries, so the
/// copy can be decrypted and re-derived without guessing.
/// </summary>
internal sealed record VaultUpgradeJournalFile(
    string Name,
    string Kind,
    string? ResourceId,
    int SchemaVersion,
    long Revision,
    string OriginalSha256);

/// <summary>
/// S8-T3 the durable, non-sensitive description of one upgrade. It records
/// the upgrade id, the vault, the phase, the profile observed before the
/// upgrade and the files the rollback point holds. It never records a record
/// body, a credential, a key or a content hash of private plaintext: only
/// storage names, schema versions, revisions and the SHA-256 of copied
/// ciphertext.
/// </summary>
internal sealed record VaultUpgradeJournal(
    int SchemaVersion,
    string UpgradeId,
    string VaultId,
    string Phase,
    DateTimeOffset CreatedAtUtc,
    VaultSchemaReport From,
    VaultUpgradeJournalFile[] Files,
    DateTimeOffset? CompletedAtUtc = null);

/// <summary>
/// S8-T3 the schema upgrade engine over one encrypted vault directory.
/// <para>
/// The upgrade rewrites only the files whose stored content schema is older
/// than the schema this build writes: the owner scope-grant control state and
/// the canonical lifecycle and job records. Every rewrite keeps the record's
/// resource ID, zone, envelope schema and exact revision, so identity,
/// attribution, relations, lifecycle state and archive references are
/// untouched; only the content schema inside the authenticated envelope
/// changes. The plaintext key envelope and the manifest are not rewritten.
/// </para>
/// <para>
/// Before the first mutation the engine copies every file it will change into
/// an encrypted rollback point (<c>vault/rollback/&lt;id&gt;/files</c>): the
/// copies are the vault's own ciphertext, so the rollback point needs no
/// second key and holds no plaintext. Each rewrite is the same atomic
/// temp-then-move write the store uses everywhere, so a process killed at any
/// point leaves, per file, either the old bytes or the new bytes; both are
/// readable by this build. A journal written before the copies and updated
/// before the first mutation lets the next run resume an interrupted upgrade
/// instead of guessing, and a completed journal keeps the rollback point until
/// the owner rolls back or discards it.
/// </para>
/// </summary>
internal sealed class VaultSchemaUpgradeEngine
{
    private const int JournalSchemaVersion = 1;
    private const string PhasePreparing = "preparing";
    private const string PhaseMigrating = "migrating";
    private const string PhaseComplete = "complete";
    private const string KindControl = "control";
    private const string KindRecord = "record";
    private const string JournalFileName = "journal.json";
    private const string FilesDirectoryName = "files";
    private const string ControlResourceId = "scope-grants";
    private const long ControlRevision = 1;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    private readonly EncryptedVaultStore _store;

    /// <summary>
    /// S8-T3 a test seam: invoked after each file the upgrade rewrites, in
    /// order. The permanent smoke uses it to throw at a material boundary and
    /// prove that the on-disk state is resumable; production never sets it.
    /// </summary>
    internal Action<int>? OnFileMigrated { get; set; }

    public VaultSchemaUpgradeEngine(EncryptedVaultStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    /// <summary>
    /// The keyless format status. It reads the non-sensitive format record and
    /// the journal only; it never needs a credential and never decrypts a
    /// record. The encrypted inventory stays authoritative.
    /// </summary>
    public VaultFormatStatus ReadStatus()
    {
        if (!File.Exists(Path.Combine(_store.RootPath, "vault-header.json")))
        {
            return new VaultFormatStatus(VaultFormatStatus.Uninitialized, false, null, null);
        }

        var journal = TryLoadJournal();
        var rollbackAvailable = journal is not null &&
            Directory.Exists(Path.Combine(_store.RollbackRootPath, journal.UpgradeId, FilesDirectoryName));
        if (journal is not null &&
            (string.Equals(journal.Phase, PhasePreparing, StringComparison.Ordinal) ||
             string.Equals(journal.Phase, PhaseMigrating, StringComparison.Ordinal)))
        {
            return new VaultFormatStatus(VaultFormatStatus.Interrupted, rollbackAvailable, journal.UpgradeId, journal.CreatedAtUtc);
        }

        var record = _store.ReadFormatRecord();
        if (record is null)
        {
            return new VaultFormatStatus(VaultFormatStatus.Unknown, rollbackAvailable, journal?.UpgradeId, null);
        }

        var current = record.EnvelopeFormat == VaultStorageProfile.EnvelopeFormat &&
            record.ManifestSchema == VaultStorageProfile.ManifestSchema &&
            record.ControlSchema == OwnerScopeGrantState.CurrentSchemaVersion &&
            record.Canonical.All(entry =>
                entry.SchemaVersion == VaultStorageProfile.CurrentCanonicalVersion(entry.Family));
        return new VaultFormatStatus(
            current ? VaultFormatStatus.Current : VaultFormatStatus.UpgradeAvailable,
            rollbackAvailable,
            journal?.UpgradeId,
            record.VerifiedAtUtc);
    }

    /// <summary>
    /// The decrypted, authoritative inventory: the stored control-state schema
    /// and, for each canonical family whose content schema can differ, how many
    /// records are stored at each version. Private content is decrypted only
    /// long enough to read its schema and is cleared immediately.
    /// </summary>
    public VaultSchemaReport Inspect(
        VaultManifest manifest,
        ReadOnlySpan<byte> dataKey,
        List<UpgradeTarget>? targets = null)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        var controlSchema = _store.ReadScopeGrantSchema(manifest, dataKey);
        var counts = new Dictionary<(string Family, int Version), int>();
        var resourceCount = 0;
        foreach (var descriptor in manifest.Resources)
        {
            if (descriptor.InternalOnly)
            {
                continue;
            }

            resourceCount++;
            var family = VaultStorageProfile.FamilyOf(descriptor.ResourceId);
            if (family is null)
            {
                continue;
            }

            var content = _store.ReadResourceAtStoredRevision(manifest, dataKey, descriptor);
            try
            {
                var version = ReadCanonicalVersion(content);
                counts[(family, version)] = counts.GetValueOrDefault((family, version)) + 1;
                if (targets is not null && version != VaultStorageProfile.CurrentCanonicalVersion(family))
                {
                    targets.Add(UpgradeTarget.ForRecord(descriptor));
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(content);
            }
        }

        if (targets is not null && controlSchema is 2 or 3)
        {
            targets.Insert(0, UpgradeTarget.ForControl(controlSchema));
        }

        var entries = new List<VaultCanonicalVersionCount>();
        foreach (var family in VaultStorageProfile.Families)
        {
            var observed = counts
                .Where(entry => string.Equals(entry.Key.Family, family, StringComparison.Ordinal))
                .OrderBy(entry => entry.Key.Version)
                .ToArray();
            if (observed.Length == 0)
            {
                entries.Add(new VaultCanonicalVersionCount(
                    family, VaultStorageProfile.CurrentCanonicalVersion(family), 0));
                continue;
            }

            foreach (var entry in observed)
            {
                entries.Add(new VaultCanonicalVersionCount(family, entry.Key.Version, entry.Value));
            }
        }

        return new VaultSchemaReport(
            VaultStorageProfile.EnvelopeFormat,
            VaultStorageProfile.ManifestSchema,
            controlSchema,
            entries.ToArray(),
            resourceCount);
    }

    /// <summary>
    /// Raises the vault to the profile this build writes. The caller has
    /// already opened the vault with the owner's credential and holds the
    /// maintenance lease; the engine keeps the key only for this call.
    /// </summary>
    public VaultUpgradeSummary Upgrade(VaultManifest manifest, byte[] dataKey)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(dataKey);
        var targets = new List<UpgradeTarget>();
        var observed = Inspect(manifest, dataKey, targets);
        var journal = LoadOrResetJournal();
        var rollbackDirectory = journal is null
            ? string.Empty
            : Path.Combine(_store.RollbackRootPath, journal.UpgradeId);
        if (journal is not null && observed.IsCurrent &&
            !string.Equals(journal.Phase, PhaseComplete, StringComparison.Ordinal))
        {
            // A run was killed after its last rewrite but before the journal
            // said so. Every file already verifies, so this call finishes the
            // commit instead of leaving an interrupted journal behind.
            WriteJournal(journal with { Phase = PhaseComplete, CompletedAtUtc = DateTimeOffset.UtcNow });
            _store.WriteFormatRecord(ToFormatRecord(observed));
            return new VaultUpgradeSummary(
                journal.UpgradeId,
                journal.From,
                observed,
                journal.Files.Count(file => string.Equals(file.Kind, KindRecord, StringComparison.Ordinal)),
                journal.Files.Count(file => string.Equals(file.Kind, KindControl, StringComparison.Ordinal)),
                rollbackDirectory,
                DirectorySize(rollbackDirectory),
                AlreadyCurrent: false,
                Resumed: true,
                DateTimeOffset.UtcNow);
        }

        if (observed.IsCurrent)
        {
            _store.WriteFormatRecord(ToFormatRecord(observed));
            return new VaultUpgradeSummary(
                journal?.UpgradeId ?? string.Empty,
                observed,
                observed,
                0,
                0,
                rollbackDirectory,
                journal is null ? 0 : DirectorySize(rollbackDirectory),
                AlreadyCurrent: true,
                Resumed: false,
                DateTimeOffset.UtcNow);
        }

        var resumed = journal is not null;
        journal ??= Prepare(manifest, observed, targets);
        for (var index = 0; index < journal.Files.Length; index++)
        {
            MigrateFile(manifest, dataKey, journal.Files[index]);
            OnFileMigrated?.Invoke(index);
        }

        var to = Inspect(manifest, dataKey);
        if (!to.IsCurrent)
        {
            throw new InvalidDataException("The vault schema upgrade did not reach the current profile.");
        }

        VerifyMigratedFiles(manifest, dataKey, journal);
        var completed = journal with { Phase = PhaseComplete, CompletedAtUtc = DateTimeOffset.UtcNow };
        WriteJournal(completed);
        _store.WriteFormatRecord(ToFormatRecord(to));
        rollbackDirectory = Path.Combine(_store.RollbackRootPath, completed.UpgradeId);
        return new VaultUpgradeSummary(
            completed.UpgradeId,
            resumed ? completed.From : observed,
            to,
            completed.Files.Count(file => string.Equals(file.Kind, KindRecord, StringComparison.Ordinal)),
            completed.Files.Count(file => string.Equals(file.Kind, KindControl, StringComparison.Ordinal)),
            rollbackDirectory,
            DirectorySize(rollbackDirectory),
            AlreadyCurrent: false,
            Resumed: resumed,
            DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// Restores the files the last upgrade wrote. A file is restored only when
    /// it still holds exactly what the upgrade left (the lossless re-encoding
    /// of its rollback copy); a file the owner changed since is kept and
    /// reported as skipped. With <paramref name="discardOnly"/> the rollback
    /// point is dropped instead, which is how the owner reclaims its space.
    /// </summary>
    public VaultRollbackSummary Rollback(VaultManifest manifest, byte[] dataKey, bool discardOnly)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(dataKey);
        var journal = LoadOrResetJournal()
            ?? throw new InvalidDataException("No completed vault schema upgrade is available to roll back.");
        if (!string.Equals(journal.VaultId, manifest.VaultId, StringComparison.Ordinal))
        {
            throw new InvalidDataException("The rollback point belongs to another vault.");
        }

        var directory = Path.Combine(_store.RollbackRootPath, journal.UpgradeId);
        if (discardOnly)
        {
            DeleteRollbackDirectory(journal.UpgradeId);
            var current = Inspect(manifest, dataKey);
            _store.WriteFormatRecord(ToFormatRecord(current));
            return new VaultRollbackSummary(journal.UpgradeId, current, 0, 0, Discarded: true, DateTimeOffset.UtcNow);
        }

        var filesDirectory = Path.Combine(directory, FilesDirectoryName);
        var restored = 0;
        var skipped = 0;
        foreach (var file in journal.Files)
        {
            var copyPath = Path.Combine(filesDirectory, file.Name);
            if (!File.Exists(copyPath) ||
                !string.Equals(HashFile(copyPath), file.OriginalSha256, StringComparison.Ordinal))
            {
                skipped++;
                continue;
            }

            if (string.Equals(file.Kind, KindControl, StringComparison.Ordinal))
            {
                var oldState = LoadCopiedControlState(manifest.VaultId, dataKey, file, copyPath);
                if (!SameControlState(oldState, _store.LoadScopeGrantState(manifest, dataKey)))
                {
                    skipped++;
                    continue;
                }

                CopyCiphertext(copyPath, _store.ScopeGrantCiphertextPath);
                restored++;
                continue;
            }

            var descriptor = FindDescriptor(manifest, file.Name);
            var oldContent = _store.DecryptCopiedEnvelope(
                copyPath, dataKey, manifest.VaultId, descriptor.ResourceId, descriptor.SchemaVersion, descriptor.Revision,
                _store.MaximumResourceEnvelopeBytes, _store.MaximumResourcePlaintextBytes);
            try
            {
                var family = VaultStorageProfile.FamilyOf(descriptor.ResourceId)
                    ?? throw new InvalidDataException("The rollback point names a record with no migration family.");
                var expected = MigrateContent(family, oldContent);
                try
                {
                    var current = _store.ReadResourceAtStoredRevision(manifest, dataKey, descriptor);
                    try
                    {
                        if (!current.AsSpan().SequenceEqual(expected))
                        {
                            skipped++;
                            continue;
                        }
                    }
                    finally
                    {
                        CryptographicOperations.ZeroMemory(current);
                    }

                    CopyCiphertext(copyPath, _store.ResolveResourceCiphertextPath(file.Name));
                    restored++;
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(expected);
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(oldContent);
            }
        }

        var restoredReport = Inspect(manifest, dataKey);
        _store.WriteFormatRecord(ToFormatRecord(restoredReport));
        DeleteRollbackDirectory(journal.UpgradeId);
        return new VaultRollbackSummary(
            journal.UpgradeId, restoredReport, restored, skipped, Discarded: false, DateTimeOffset.UtcNow);
    }

    /// <summary>One file the upgrade will change, resolved before the first write.</summary>
    internal sealed record UpgradeTarget(
        string Name,
        string Kind,
        string? ResourceId,
        int SchemaVersion,
        long Revision)
    {
        public static UpgradeTarget ForControl(int schemaVersion) => new(
            "scope-grants.enc", KindControl, ControlResourceId, schemaVersion, ControlRevision);

        public static UpgradeTarget ForRecord(VaultResourceDescriptor descriptor) => new(
            descriptor.StorageName, KindRecord, descriptor.ResourceId, descriptor.SchemaVersion,
            descriptor.Revision);
    }

    private VaultUpgradeJournal Prepare(VaultManifest manifest, VaultSchemaReport from, List<UpgradeTarget> targets)
    {
        if (targets.Count == 0)
        {
            throw new InvalidDataException("The vault reports an old profile but names nothing to migrate.");
        }

        var upgradeId = Guid.NewGuid().ToString("N");
        var directory = Path.Combine(_store.RollbackRootPath, upgradeId);
        var filesDirectory = Path.Combine(directory, FilesDirectoryName);
        Directory.CreateDirectory(filesDirectory);
        var files = new List<VaultUpgradeJournalFile>(targets.Count);
        foreach (var target in targets)
        {
            files.Add(new VaultUpgradeJournalFile(
                target.Name, target.Kind, target.ResourceId, target.SchemaVersion, target.Revision,
                OriginalSha256: string.Empty));
        }

        // The journal exists before the first byte is copied: a crash during
        // preparation leaves phase "preparing", which the next run discards
        // because no vault file has been touched yet.
        var preparing = new VaultUpgradeJournal(
            JournalSchemaVersion, upgradeId, manifest.VaultId, PhasePreparing, DateTimeOffset.UtcNow, from, files.ToArray());
        WriteJournal(preparing);
        for (var index = 0; index < targets.Count; index++)
        {
            var target = targets[index];
            var source = string.Equals(target.Kind, KindControl, StringComparison.Ordinal)
                ? _store.ScopeGrantCiphertextPath
                : _store.ResolveResourceCiphertextPath(target.Name);
            var copyPath = Path.Combine(filesDirectory, target.Name);
            files[index] = files[index] with { OriginalSha256 = CopyWithHash(source, copyPath) };
        }

        var migrating = preparing with { Phase = PhaseMigrating, Files = files.ToArray() };
        WriteJournal(migrating);
        return migrating;
    }

    private void MigrateFile(VaultManifest manifest, byte[] dataKey, VaultUpgradeJournalFile file)
    {
        if (string.Equals(file.Kind, KindControl, StringComparison.Ordinal))
        {
            if (_store.ReadScopeGrantSchema(manifest, dataKey) == OwnerScopeGrantState.CurrentSchemaVersion)
            {
                return;
            }

            var state = OwnerScopeGrantAuthority.ValidateAndMigrateState(_store.LoadScopeGrantState(manifest, dataKey));
            _store.SaveScopeGrantState(manifest, dataKey, state);
            if (_store.ReadScopeGrantSchema(manifest, dataKey) != OwnerScopeGrantState.CurrentSchemaVersion)
            {
                throw new InvalidDataException("The migrated owner control state did not reach the current schema.");
            }

            return;
        }

        var descriptor = FindDescriptor(manifest, file.Name);
        var family = VaultStorageProfile.FamilyOf(descriptor.ResourceId)
            ?? throw new InvalidDataException("The upgrade journal names a record with no migration family.");
        var currentVersion = VaultStorageProfile.CurrentCanonicalVersion(family);
        var content = _store.ReadResourceAtStoredRevision(manifest, dataKey, descriptor);
        try
        {
            if (ReadCanonicalVersion(content) == currentVersion)
            {
                return;
            }

            var migrated = MigrateContent(family, content);
            try
            {
                _store.RewriteResourceAtRevision(manifest, dataKey, descriptor, migrated);
                var readBack = _store.ReadResourceAtStoredRevision(manifest, dataKey, descriptor);
                try
                {
                    if (!readBack.AsSpan().SequenceEqual(migrated))
                    {
                        throw new InvalidDataException("The migrated record did not read back exactly as written.");
                    }
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(readBack);
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(migrated);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(content);
        }
    }

    /// <summary>
    /// Proves the migration was lossless for every file it touched: the
    /// rollback copy is decrypted, re-encoded into the current schema
    /// deterministically, and compared with the bytes now stored. A difference
    /// is accepted only when the stored file is itself already at the current
    /// schema, which is what an owner write after an interruption looks like.
    /// </summary>
    private void VerifyMigratedFiles(VaultManifest manifest, byte[] dataKey, VaultUpgradeJournal journal)
    {
        var filesDirectory = Path.Combine(_store.RollbackRootPath, journal.UpgradeId, FilesDirectoryName);
        foreach (var file in journal.Files)
        {
            var copyPath = Path.Combine(filesDirectory, file.Name);
            if (!File.Exists(copyPath) ||
                !string.Equals(HashFile(copyPath), file.OriginalSha256, StringComparison.Ordinal))
            {
                throw new InvalidDataException("The rollback point lost or changed a file the upgrade wrote.");
            }

            if (string.Equals(file.Kind, KindControl, StringComparison.Ordinal))
            {
                var oldState = LoadCopiedControlState(manifest.VaultId, dataKey, file, copyPath);
                if (!SameControlState(oldState, _store.LoadScopeGrantState(manifest, dataKey)) &&
                    _store.ReadScopeGrantSchema(manifest, dataKey) != OwnerScopeGrantState.CurrentSchemaVersion)
                {
                    throw new InvalidDataException("The migrated owner control state did not verify.");
                }

                continue;
            }

            var descriptor = FindDescriptor(manifest, file.Name);
            var family = VaultStorageProfile.FamilyOf(descriptor.ResourceId)
                ?? throw new InvalidDataException("The upgrade journal names a record with no migration family.");
            var oldContent = _store.DecryptCopiedEnvelope(
                copyPath, dataKey, manifest.VaultId, descriptor.ResourceId, descriptor.SchemaVersion, descriptor.Revision,
                _store.MaximumResourceEnvelopeBytes, _store.MaximumResourcePlaintextBytes);
            try
            {
                var expected = MigrateContent(family, oldContent);
                try
                {
                    var current = _store.ReadResourceAtStoredRevision(manifest, dataKey, descriptor);
                    try
                    {
                        if (!current.AsSpan().SequenceEqual(expected) &&
                            ReadCanonicalVersion(current) != VaultStorageProfile.CurrentCanonicalVersion(family))
                        {
                            throw new InvalidDataException("A migrated record did not verify losslessly.");
                        }
                    }
                    finally
                    {
                        CryptographicOperations.ZeroMemory(current);
                    }
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(expected);
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(oldContent);
            }
        }
    }

    private OwnerScopeGrantState LoadCopiedControlState(
        string vaultId,
        byte[] dataKey,
        VaultUpgradeJournalFile file,
        string copyPath)
    {
        var plaintext = _store.DecryptCopiedEnvelope(
            copyPath, dataKey, vaultId, ControlResourceId, file.SchemaVersion, ControlRevision,
            _store.MaximumScopeGrantEnvelopeBytesValue, _store.MaximumScopeGrantPlaintextBytes);
        try
        {
            return JsonSerializer.Deserialize<OwnerScopeGrantState>(plaintext, JsonOptions)
                ?? throw new InvalidDataException("Invalid copied owner control state.");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    private static bool SameControlState(OwnerScopeGrantState left, OwnerScopeGrantState right)
    {
        var leftBytes = JsonSerializer.SerializeToUtf8Bytes(
            OwnerScopeGrantAuthority.ValidateAndMigrateState(left), JsonOptions);
        var rightBytes = JsonSerializer.SerializeToUtf8Bytes(
            OwnerScopeGrantAuthority.ValidateAndMigrateState(right), JsonOptions);
        try
        {
            return leftBytes.AsSpan().SequenceEqual(rightBytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(leftBytes);
            CryptographicOperations.ZeroMemory(rightBytes);
        }
    }

    private static byte[] MigrateContent(string family, byte[] content) => family switch
    {
        VaultStorageProfile.LifecycleFamily => LifecycleCodec.EncodeStored(LifecycleCodec.DecodeStored(content)),
        VaultStorageProfile.JobFamily => JobCodec.EncodeStored(JobCodec.DecodeStored(content)),
        _ => throw new InvalidDataException("Unknown canonical migration family.")
    };

    private static int ReadCanonicalVersion(byte[] content)
    {
        using var document = JsonDocument.Parse(content);
        if (document.RootElement.ValueKind != JsonValueKind.Object ||
            !document.RootElement.TryGetProperty("schemaVersion", out var version) ||
            !version.TryGetInt32(out var value) || value < 1)
        {
            throw new InvalidDataException("The stored canonical record has no schema version.");
        }

        return value;
    }

    private static VaultResourceDescriptor FindDescriptor(VaultManifest manifest, string storageName) =>
        manifest.Resources.FirstOrDefault(resource =>
            !resource.InternalOnly && string.Equals(resource.StorageName, storageName, StringComparison.Ordinal))
        ?? throw new InvalidDataException("The upgrade journal names a record the manifest does not hold.");

    private static VaultFormatRecord ToFormatRecord(VaultSchemaReport report) => new(
        1,
        report.EnvelopeFormat,
        report.ManifestSchema,
        report.ControlSchema,
        report.Canonical,
        DateTimeOffset.UtcNow);

    private static string CopyWithHash(string sourcePath, string destinationPath)
    {
        using var source = new FileStream(
            sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.SequentialScan);
        using var destination = new FileStream(
            destinationPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.WriteThrough);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[64 * 1024];
        try
        {
            int read;
            while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
            {
                hash.AppendData(buffer, 0, read);
                destination.Write(buffer, 0, read);
            }

            destination.Flush(flushToDisk: true);
            return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(buffer);
        }
    }

    private void CopyCiphertext(string copyPath, string destinationPath)
    {
        var bytes = PrivateFile.ReadBytes(copyPath, Math.Max(_store.MaximumResourceEnvelopeBytes, _store.MaximumScopeGrantEnvelopeBytesValue));
        try
        {
            _store.WriteCiphertextAtomically(destinationPath, bytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private VaultUpgradeJournal? TryLoadJournal()
    {
        if (!Directory.Exists(_store.RollbackRootPath))
        {
            return null;
        }

        VaultUpgradeJournal? selected = null;
        foreach (var directory in Directory.GetDirectories(_store.RollbackRootPath, "*", SearchOption.TopDirectoryOnly))
        {
            var name = Path.GetFileName(directory);
            if (!IsUpgradeId(name))
            {
                continue;
            }

            var journal = ReadJournal(directory);
            if (journal is null)
            {
                continue;
            }

            if (selected is null || journal.CreatedAtUtc > selected.CreatedAtUtc)
            {
                selected = journal;
            }
        }

        return selected;
    }

    private VaultUpgradeJournal? LoadOrResetJournal()
    {
        if (!Directory.Exists(_store.RollbackRootPath))
        {
            return null;
        }

        VaultUpgradeJournal? selected = null;
        foreach (var directory in Directory.GetDirectories(_store.RollbackRootPath, "*", SearchOption.TopDirectoryOnly))
        {
            var name = Path.GetFileName(directory);
            if (!IsUpgradeId(name))
            {
                continue;
            }

            var journal = ReadJournal(directory);
            if (journal is null || string.Equals(journal.Phase, PhasePreparing, StringComparison.Ordinal))
            {
                // No journal, or a preparation that never reached the first
                // mutation: the vault files are untouched, so the partial copy
                // is discarded instead of being mistaken for a rollback point.
                DeleteRollbackDirectory(name);
                continue;
            }

            if (selected is null || journal.CreatedAtUtc > selected.CreatedAtUtc)
            {
                selected = journal;
            }
        }

        return selected;
    }

    private VaultUpgradeJournal? ReadJournal(string directory)
    {
        var path = Path.Combine(directory, JournalFileName);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var bytes = PrivateFile.ReadBytes(path, 1024 * 1024);
            try
            {
                var journal = JsonSerializer.Deserialize<VaultUpgradeJournal>(bytes, JsonOptions);
                return journal is not null &&
                    journal.SchemaVersion == JournalSchemaVersion &&
                    IsUpgradeId(journal.UpgradeId) &&
                    !string.IsNullOrEmpty(journal.VaultId) &&
                    journal.Files is not null
                    ? journal
                    : null;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(bytes);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            return null;
        }
    }

    private void WriteJournal(VaultUpgradeJournal journal)
    {
        var directory = Path.Combine(_store.RollbackRootPath, journal.UpgradeId);
        Directory.CreateDirectory(directory);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(journal, JsonOptions);
        try
        {
            _store.WriteCiphertextAtomically(Path.Combine(directory, JournalFileName), bytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private void DeleteRollbackDirectory(string upgradeId)
    {
        if (!IsUpgradeId(upgradeId))
        {
            throw new InvalidDataException("Invalid rollback point name.");
        }

        var directory = Path.Combine(_store.RollbackRootPath, upgradeId);
        var fullRollbackRoot = Path.GetFullPath(_store.RollbackRootPath) + Path.DirectorySeparatorChar;
        if (!Path.GetFullPath(directory).StartsWith(fullRollbackRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Invalid rollback point path.");
        }

        if (Directory.Exists(directory) && (File.GetAttributes(directory) & FileAttributes.ReparsePoint) == 0)
        {
            Directory.Delete(directory, recursive: true);
        }

        if (Directory.Exists(_store.RollbackRootPath) &&
            Directory.GetFileSystemEntries(_store.RollbackRootPath).Length == 0)
        {
            Directory.Delete(_store.RollbackRootPath);
        }
    }

    private static bool IsUpgradeId(string? value) =>
        value is { Length: 32 } && Guid.TryParseExact(value, "N", out _);

    private static string HashFile(string path)
    {
        using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.SequentialScan);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[64 * 1024];
        try
        {
            int read;
            while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                hash.AppendData(buffer, 0, read);
            }

            return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(buffer);
        }
    }

    private static long DirectorySize(string directory)
    {
        if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
        {
            return 0;
        }

        long total = 0;
        foreach (var path in Directory.GetFiles(directory, "*", SearchOption.AllDirectories))
        {
            try
            {
                total += new FileInfo(path).Length;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }

        return total;
    }
}

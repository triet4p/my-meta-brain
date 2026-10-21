using MetaBrain.Core.Domain;

namespace MetaBrain.Core.Security;

/// <summary>
/// S8-T3 the versioned storage profile of a vault.
/// <para>
/// The authenticated envelope of every canonical record, the manifest and the
/// key wrapper has been format 1 since the first release, so this release does
/// not renumber it and does not claim an envelope migration. What genuinely
/// changed across releases is the <em>content</em> schema of a few stored
/// records and of one owner control state: the lifecycle record, the model job
/// record and the owner scope-grant state each gained fields over time, and an
/// old vault still holds them at the version it wrote them with. The profile
/// below is the complete, observed set of those versions, plus the envelope and
/// manifest versions the store actually reads and writes. It is computed from
/// the decrypted files, not asserted from a constant.
/// </para>
/// </summary>
public static class VaultStorageProfile
{
    /// <summary>The envelope format this build reads and writes. Never migrated: it is validated on read.</summary>
    public const int EnvelopeFormat = 1;

    /// <summary>The manifest schema this build reads and writes. Never migrated: it is validated on read.</summary>
    public const int ManifestSchema = 1;

    /// <summary>The canonical record families whose stored schema version can genuinely differ.</summary>
    public const string LifecycleFamily = "lifecycle";

    public const string JobFamily = "job";

    /// <summary>The canonical schema this build writes for one family.</summary>
    public static int CurrentCanonicalVersion(string family) => family switch
    {
        LifecycleFamily => LifecycleCodec.CurrentSchemaVersion,
        JobFamily => JobCodec.CurrentSchemaVersion,
        _ => throw new ArgumentOutOfRangeException(nameof(family))
    };

    /// <summary>The canonical families whose stored version can differ.</summary>
    public static string[] Families { get; } = { JobFamily, LifecycleFamily };

    /// <summary>
    /// The family a canonical resource belongs to, from the reserved ID prefix
    /// the domain already uses. A resource outside both families has no
    /// versioned content schema and is never rewritten.
    /// </summary>
    public static string? FamilyOf(string resourceId)
    {
        ArgumentNullException.ThrowIfNull(resourceId);
        if (resourceId.StartsWith(DomainIdentifiers.LifecycleIdPrefix, StringComparison.Ordinal))
        {
            return LifecycleFamily;
        }

        return resourceId.StartsWith(DomainIdentifiers.JobIdPrefix, StringComparison.Ordinal) ? JobFamily : null;
    }
}

/// <summary>
/// One observed canonical family: how many records are stored at which
/// content schema. A family is reported even when the vault holds none of it,
/// so the report is a complete picture of the profile instead of a sparse one.
/// </summary>
public sealed record VaultCanonicalVersionCount(string Family, int SchemaVersion, int Records);

/// <summary>
/// S8-T3 the observed storage profile of one vault, read from its decrypted
/// manifest and records. It names schema versions and counts only: never a
/// record, a title, a body, a citation, a key or a token.
/// </summary>
public sealed record VaultSchemaReport(
    int EnvelopeFormat,
    int ManifestSchema,
    int ControlSchema,
    VaultCanonicalVersionCount[] Canonical,
    int ResourceCount)
{
    /// <summary>
    /// True when every stored canonical family is at the version this build
    /// writes and the control state is current (a vault with no control state
    /// file yet is current by construction).
    /// </summary>
    public bool IsCurrent =>
        EnvelopeFormat == VaultStorageProfile.EnvelopeFormat &&
        ManifestSchema == VaultStorageProfile.ManifestSchema &&
        (ControlSchema == 0 || ControlSchema == OwnerScopeGrantState.CurrentSchemaVersion) &&
        Canonical.All(entry => entry.SchemaVersion == VaultStorageProfile.CurrentCanonicalVersion(entry.Family) ||
            entry.Records == 0);

    /// <summary>The families that still hold records below the current schema.</summary>
    public string[] PendingFamilies => Canonical
        .Where(entry => entry.Records > 0 &&
            entry.SchemaVersion != VaultStorageProfile.CurrentCanonicalVersion(entry.Family))
        .Select(entry => entry.Family)
        .OrderBy(family => family, StringComparer.Ordinal)
        .ToArray();
}

/// <summary>
/// S8-T3 the result of one completed schema upgrade. <see cref="From"/> is the
/// profile the upgrade observed before it wrote anything, <see cref="To"/> the
/// profile it verified afterwards.
/// </summary>
public sealed record VaultUpgradeSummary(
    string UpgradeId,
    VaultSchemaReport From,
    VaultSchemaReport To,
    int MigratedRecords,
    int MigratedControlStates,
    string RollbackPath,
    long RollbackBytes,
    bool AlreadyCurrent,
    bool Resumed,
    DateTimeOffset CompletedAtUtc);

/// <summary>
/// S8-T3 the result of one rollback. A rollback restores only the files the
/// upgrade wrote and that are still exactly as the upgrade left them; a file
/// the owner changed afterwards is kept and counted in <see cref="SkippedFiles"/>.
/// </summary>
public sealed record VaultRollbackSummary(
    string UpgradeId,
    VaultSchemaReport Restored,
    int RestoredFiles,
    int SkippedFiles,
    bool Discarded,
    DateTimeOffset CompletedAtUtc);

/// <summary>
/// S8-T3 the keyless view of the vault format for the owner console and the
/// status command. It reads only the non-sensitive format record and the
/// upgrade journal, so it never needs a credential and never decrypts a
/// record. The encrypted inventory stays authoritative; this only tells the
/// owner whether an upgrade is worth starting.
/// </summary>
public sealed record VaultFormatStatus(
    string State,
    bool RollbackAvailable,
    string? UpgradeId,
    DateTimeOffset? RecordedAtUtc)
{
    public const string Uninitialized = "uninitialized";
    public const string Current = "current";
    public const string UpgradeAvailable = "upgrade_available";
    public const string Unknown = "unknown";
    public const string Interrupted = "interrupted";
}

/// <summary>
/// S8-T3 the non-sensitive record one verified profile leaves beside the
/// vault. It is written only after a complete verification, is never a second
/// truth for the encrypted inventory, and holds schema versions and counts
/// only.
/// </summary>
public sealed record VaultFormatRecord(
    int SchemaVersion,
    int EnvelopeFormat,
    int ManifestSchema,
    int ControlSchema,
    VaultCanonicalVersionCount[] Canonical,
    DateTimeOffset VerifiedAtUtc);

/// <summary>
/// S8-T3 the owner-facing maintenance authority: the schema upgrade, its
/// rollback and the keyless status. The application layer calls this through
/// the owner channel, so the owner CLI and the owner console reach exactly one
/// implementation of the migration rules.
/// </summary>
public interface IVaultMaintenanceService
{
    /// <summary>Keyless format status; safe while the vault is locked.</summary>
    VaultFormatStatus ReadFormatStatus();

    /// <summary>
    /// Raises an old vault to the profile this build writes. The owner presents
    /// the passphrase or the recovery code; a wrong or missing credential and
    /// a vault that is not idle fail closed with nothing written. The vault is
    /// left locked: this act never unlocks it.
    /// </summary>
    VaultUpgradeSummary Upgrade(string credential, bool useRecoveryCode);

    /// <summary>
    /// Restores the files the last upgrade wrote. The owner presents the same
    /// credential, and only files that still hold exactly what the upgrade
    /// wrote are reverted.
    /// </summary>
    VaultRollbackSummary Rollback(string credential, bool useRecoveryCode, bool discardOnly);
}

/// <summary>
/// S8-T3 the maintenance act was refused because the vault is not in the state
/// the act requires: another owner act is in flight, the vault is unlocked, or
/// the credential did not open it. Nothing is written and no key is kept.
/// </summary>
public sealed class VaultMaintenanceDeniedException : Exception
{
    public VaultMaintenanceDeniedException(string reason, Exception? inner = null)
        : base(reason, inner)
    {
        Reason = reason;
    }

    /// <summary>A stable, non-sensitive reason: <c>vault_busy</c> or <c>vault_denied</c>.</summary>
    public string Reason { get; }
}

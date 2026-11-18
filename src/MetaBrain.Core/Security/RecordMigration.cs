using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MetaBrain.Core.Domain;

namespace MetaBrain.Core.Security;

/// <summary>
/// S18-T2 stable refusal/eligibility codes for the guarded legacy-to-workspace
/// transfer. These are wire values: they are reported to the owner, never
/// interpreted as a permission and never used to relax the eligibility guard.
/// </summary>
public static class MigrationCodes
{
    public const string ZoneIndependent = "workspace";
    public const string Eligible = "eligible";
    public const string StructuredOnly = "structured_only";
    public const string OversizedSnapshot = "oversized_snapshot";
    public const string ZoneProtectionNotEnforced = "zone_protection_not_enforced";
    public const string UnknownClassification = "unknown_classification";
    public const string Purged = "purged";
    public const string UnknownOrCorrupt = "unknown_or_corrupt";
    public const string DestinationConflict = "destination_conflict";
    public const string AlreadyMigrated = "already_migrated";
    public const string NoEligibleRecords = "no_eligible_records";
    public const string Confirmation = "confirmation_required";
    public const string BackupRequired = "backup_required";
    public const string StalePreview = "stale_preview";
    public const string BackupMismatch = "backup_mismatch";
    public const string InconsistentState = "inconsistent_migration_state";
    public const string Refused = "migration_refused";
}

/// <summary>
/// One legacy record considered by the migration preview. A candidate is
/// either eligible for a guarded transfer or is reported ineligible with a
/// reason; an ineligible record is never transferred and is never silently
/// dropped. Digests are lowercase SHA-256 hex of exact original bytes.
/// </summary>
public sealed record MigrationCandidate(
    string Family,
    string LegacyId,
    long Revision,
    long Length,
    string Sha256,
    string ZoneId,
    string DestinationPath,
    bool Eligible,
    string Reason,
    string? SourceId);

/// <summary>The verified pre-migration checkpoint an execute must present.</summary>
public sealed record MigrationBackupRequirement(string VaultId, long ManifestRevision);

/// <summary>
/// The owner-only, write-free preview. The token is a deterministic digest of
/// vault, manifest, workspace tree and candidate state: it mints no authority
/// and writes nothing, and any state change invalidates it as stale.
/// </summary>
public sealed record MigrationPreviewInfo(
    string Token,
    string VaultId,
    long ManifestRevision,
    long TreeRevision,
    MigrationCandidate[] Candidates,
    MigrationBackupRequirement Backup,
    int EligibleCount,
    int IneligibleCount);

/// <summary>One entry of the encrypted legacy-id to workspace-id reverse map.</summary>
public sealed record MigrationMapEntry(
    string Family,
    string LegacyId,
    long LegacyRevision,
    string NodeId,
    long ContentRevision,
    long Length,
    string Sha256,
    string ZoneId,
    string SourceId,
    string NodeName);

/// <summary>
/// The encrypted reverse map record. It is provenance and traceability only;
/// it never enforces authorization and never widens a grant.
/// </summary>
public sealed record MigrationMapRecord(int SchemaVersion, string VaultId, MigrationMapEntry[] Entries)
{
    public const int CurrentSchemaVersion = 1;

    public static MigrationMapRecord Empty(string vaultId) =>
        new(CurrentSchemaVersion, vaultId, Array.Empty<MigrationMapEntry>());

    public static MigrationMapRecord Decode(byte[] bytes, string expectedVaultId)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        MigrationMapRecord? record;
        try
        {
            record = JsonSerializer.Deserialize<MigrationMapRecord>(bytes, JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new MigrationRefusalException(MigrationCodes.InconsistentState, "The encrypted migration map is unreadable.", ex);
        }

        if (record is null || record.SchemaVersion != CurrentSchemaVersion ||
            !string.Equals(record.VaultId, expectedVaultId, StringComparison.Ordinal) ||
            record.Entries is null)
        {
            throw new MigrationRefusalException(MigrationCodes.InconsistentState, "The encrypted migration map is invalid.");
        }

        return record;
    }

    public byte[] Encode() => JsonSerializer.SerializeToUtf8Bytes(this, JsonOptions);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };
}

/// <summary>The auditable result of one guarded migration execute.</summary>
public sealed record MigrationExecutionInfo(
    string VaultId,
    long TreeRevision,
    string Token,
    MigrationMapEntry[] Migrated,
    MigrationCandidate[] Unmigrated,
    string MapResourceId,
    long MapRevision);

/// <summary>The read-only view of the encrypted reverse map.</summary>
public sealed record MigrationReportInfo(string VaultId, MigrationMapEntry[] Entries, long MapRevision);

/// <summary>
/// A migration act was refused. The code is an owner-facing reason; the
/// message never contains record content, a key, a token or a credential.
/// </summary>
public sealed class MigrationRefusalException : Exception
{
    public MigrationRefusalException(string code, string message, Exception? inner = null)
        : base(message, inner) => Code = code;

    public string Code { get; }
}

/// <summary>
/// The vault manifest revision at a point in time. Kept here so the migration
/// routing can bind a preview to the exact manifest state it observed.
/// </summary>
public static class MigrationState
{
    public const string MapResourceId = "migration-map";
    public const int MaximumContentBytes = WorkspaceContentRevisionInfo.MaximumContentBytes;

    /// <summary>
    /// A deterministic, stateless binding of the exact state a preview saw.
    /// It is not a secret capability: execute still requires the owner channel,
    /// the explicit confirmation and a verified pre-migration backup.
    /// </summary>
    public static string ComputeToken(
        string vaultId,
        long manifestRevision,
        long treeRevision,
        IReadOnlyList<ScopeResourceRevision> resources,
        IReadOnlyList<MigrationCandidate> candidates)
    {
        var builder = new StringBuilder();
        builder.Append(vaultId).Append('\n')
            .Append(manifestRevision.ToString(CultureInfo.InvariantCulture)).Append('\n')
            .Append(treeRevision.ToString(CultureInfo.InvariantCulture)).Append('\n');
        foreach (var resource in resources.OrderBy(r => r.ResourceId, StringComparer.Ordinal))
        {
            builder.Append(resource.ResourceId).Append(':')
                .Append(resource.Revision.ToString(CultureInfo.InvariantCulture)).Append('\n');
        }

        foreach (var candidate in candidates.OrderBy(c => c.LegacyId, StringComparer.Ordinal))
        {
            builder.Append(candidate.LegacyId).Append(':')
                .Append(candidate.Revision.ToString(CultureInfo.InvariantCulture)).Append(':')
                .Append(candidate.Sha256).Append(':')
                .Append(candidate.ZoneId).Append(':')
                .Append(candidate.Eligible ? '1' : '0').Append(':')
                .Append(candidate.Reason).Append('\n');
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()))).ToLowerInvariant();
    }
}

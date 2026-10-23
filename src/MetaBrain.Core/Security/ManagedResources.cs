namespace MetaBrain.Core.Security;

/// <summary>
/// Service-owned catalog mapping opaque managed resource IDs to their zones.
/// The ID is never a path: it is an opaque key validated against the same
/// identifier grammar as grant scopes, so traversal text can never address a file.
/// This catalog is the single ID-to-zone authority reused by the grant check;
/// future source readers resolve through the same catalog plus grant authority.
/// </summary>
public sealed class ManagedResourceCatalog
{
    private readonly Dictionary<string, string> _zonesByResource;

    public ManagedResourceCatalog(IEnumerable<(string ResourceId, string ZoneId)> resources)
    {
        ArgumentNullException.ThrowIfNull(resources);
        _zonesByResource = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (resourceId, zoneId) in resources)
        {
            if (!IsValidIdentifier(resourceId) || !IsValidIdentifier(zoneId))
            {
                throw new ArgumentException("Managed resources must use valid identifiers.", nameof(resources));
            }

            if (_zonesByResource.ContainsKey(resourceId))
            {
                throw new ArgumentException("Managed resource IDs must be unique.", nameof(resources));
            }

            _zonesByResource.Add(resourceId, zoneId);
        }
    }

    public int Count => _zonesByResource.Count;

    public bool TryGetZone(string? resourceId, out string? zoneId)
    {
        zoneId = null;
        return resourceId is not null && _zonesByResource.TryGetValue(resourceId, out zoneId);
    }

    private static bool IsValidIdentifier(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 128 || !IsAsciiAlphaNumeric(value[0]))
        {
            return false;
        }

        for (var index = 1; index < value.Length; index++)
        {
            var character = value[index];
            if (!IsAsciiAlphaNumeric(character) && character is not '.' and not '_' and not '-')
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsAsciiAlphaNumeric(char value) =>
        value is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9';
}

/// <summary>
/// Resolves only opaque resource IDs through unlocked private storage. Callers
/// authorize the exact operation, ID, and revision before reading; implementations
/// decrypt per request, bind ciphertext to vault/resource/schema/revision, and fail
/// closed on missing, stale, malformed, or tampered resources. A requested revision
/// that no longer matches the stored revision fails closed instead of serving
/// newer bytes under an older grant.
/// </summary>
public interface IManagedResourceReader
{
    byte[] ReadContent(string resourceId, long? expectedRevision = null);

    /// <summary>
    /// Confirms the mapping still names the authorized revision after the final
    /// policy re-check, before the response is created.
    /// </summary>
    void ValidateRegistration(string resourceId, long? expectedRevision = null);
}

/// <summary>
/// Private encrypted resource storage exposed through an unlocked owner
/// operation. Implementations must bind ciphertext to the vault, resource ID,
/// schema, and revision; callers hold an operation until its response is sent.
/// </summary>
public interface IManagedResourceStore : IManagedResourceReader
{
    /// <summary>
    /// Returns the immutable canonical resource snapshot in strictly ascending
    /// ordinal resource-ID order.
    /// </summary>
    IReadOnlyList<ScopeResourceRevision> ListResources();
    bool TryGetZone(string resourceId, out string? zoneId);
    long WriteContent(string resourceId, string zoneId, byte[] content);
    /// <summary>
    /// Revision-gated write. An <paramref name="expectedRevision"/> of zero
    /// requires the ID to be absent (create-only); a positive value must equal
    /// the live revision (revise). Any mismatch fails closed and publishes
    /// nothing. Implementations enforce the check atomically with the manifest
    /// commit under the content gate, so concurrent stale writers cannot
    /// overwrite the winner.
    /// </summary>
    long WriteContent(string resourceId, string zoneId, byte[] content, long expectedRevision);
    void ValidateRegistrationForAll(IReadOnlyList<ScopeResourceRevision> expected)
    {
        ArgumentNullException.ThrowIfNull(expected);
        foreach (var resource in expected)
        {
            ValidateRegistration(resource.ResourceId, resource.Revision);
        }
    }

    /// <summary>
    /// Reads a bounded batch of canonical envelopes. Every (ID, revision) still
    /// passes the same per-resource decrypt, AEAD binding, and revision gate as
    /// <see cref="IManagedResourceReader.ReadContent"/>; the batch only overlaps
    /// independent reads inside the operation that authorized them, and is
    /// deliberately bounded so never more than the batch's plaintext is resident
    /// at once (never a whole vault). Implementations honor the token so the
    /// caller's lock/cancel boundary bounds the work.
    /// </summary>
    byte[][] ReadBatch(IReadOnlyList<ScopeResourceRevision> batch, int maxDegreeOfParallelism, CancellationToken cancellationToken);
}

/// <summary>
/// Owner-controlled vault lifecycle. Acquired operations remain valid until
/// disposed; lock prevents new operations and completes only after all existing
/// operations have drained.
/// <para>
/// S3-T6 safe background refresh: the service links its owner-opt-in background
/// refresh loop to a lock-epoch cancellation signal. A requested lock cancels the
/// in-flight refresh reads at the next per-source boundary instead of letting a
/// multi-source pass run to completion while the owner already asked for the vault
/// to stop private work. Single request/reply reads keep their existing drain-only
/// semantics; only the bounded background refresh loop observes this signal.
/// </para>
/// </summary>
public interface IVaultLifecycle
{
    string State { get; }
    bool Provision(string passphrase, out string? recoveryCode);
    bool Unlock(string credential, bool useRecoveryCode);
    Task LockAsync();
    IVaultOperation? TryBeginOperation();
    /// <summary>
    /// Cancellation signal for the current unlocked epoch. The token is cancelled
    /// when <see cref="LockAsync"/> is requested and reset on the next successful
    /// unlock. The safe background refresh loop links this signal with its own
    /// pause/disable cessation so an in-flight refresh pass stops at the next
    /// per-source boundary when the vault locks.
    /// </summary>
    CancellationToken RefreshCancellation { get; }
    /// <summary>
    /// Monotonic lock-epoch counter: it advances on every successful unlock and on
    /// every completed lock. The background refresh loop records the epoch it started
    /// in and stops when the live epoch no longer matches, so a restarted or
    /// re-locked vault can never keep a stale loop reading with an old lease.
    /// </summary>
    long LockEpoch { get; }
}

/// <summary>
/// Owner purge deletion: removes one canonical resource from the encrypted
/// manifest and deletes its ciphertext file. S7-T5 only: called exclusively
/// by the owner purge service after an explicit confirmed intent. Deleting an
/// ID that is already gone succeeds idempotently (already deleted), so an
/// interrupted purge resumes honestly instead of failing on partial progress.
/// </summary>
public interface IPurgeResourceDeletion
{
    bool DeleteCanonicalResource(string resourceId);
}

/// <summary>
/// The private operations state of the unlocked vault: the owner audit trail,
/// the error diagnostics, the owner's retention choice and the tombstones of
/// the entries a purge removed.
/// </summary>
/// <remarks>
/// S8-T4. The state lives in one dedicated encrypted file of the vault, with
/// the same data key and envelope every other dedicated owner state uses. It
/// is not a canonical resource, it is not part of the private index and it
/// never reaches the approved catalog. The lease owns the read and the
/// atomic update, so a concurrent owner act cannot interleave two writes.
/// </remarks>
public interface IOperationsAuditPersistence
{
    OperationsAuditState LoadOperationsAuditState();

    T UpdateOperationsAuditState<T>(
        Func<OperationsAuditState, (OperationsAuditState State, T Result)> update);
}

/// <summary>
/// Scoped unlocked access to private resource metadata and content. The
/// application disposes this lease only after the operation response is sent.
/// </summary>
public interface IVaultOperation : IManagedResourceStore, IScopeGrantPersistence, ICatalogPersistence, ISourceEnrollmentPersistence, IRecallPolicyPersistence, IReviewStatePersistence, IPurgeLedgerPersistence, IOperationsAuditPersistence, IPurgeResourceDeletion, IDisposable
{
 }

/// <summary>
/// Thrown when a managed resource cannot be served. The message is deliberately
/// generic: it is mapped to a single wire error shared by nonexistent,
/// forbidden, malformed, and tampered resource requests.
/// </summary>
public sealed class ManagedResourceUnavailableException : IOException
{
    public ManagedResourceUnavailableException()
        : base("The requested managed resource is unavailable.")
    {
    }
}

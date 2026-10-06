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
    IReadOnlyList<ScopeResourceRevision> ListResources();
    bool TryGetZone(string resourceId, out string? zoneId);
    long WriteContent(string resourceId, string zoneId, byte[] content);
    void ValidateRegistrationForAll(IReadOnlyList<ScopeResourceRevision> expected)
    {
        ArgumentNullException.ThrowIfNull(expected);
        foreach (var resource in expected)
        {
            ValidateRegistration(resource.ResourceId, resource.Revision);
        }
    }
}

/// <summary>
/// Owner-controlled vault lifecycle. Acquired operations remain valid until
/// disposed; lock prevents new operations and completes only after all existing
/// operations have drained.
/// </summary>
public interface IVaultLifecycle
{
    string State { get; }
    bool Provision(string passphrase, out string? recoveryCode);
    bool Unlock(string credential, bool useRecoveryCode);
    Task LockAsync();
    IVaultOperation? TryBeginOperation();
}

/// <summary>
/// Scoped unlocked access to private resource metadata and content. The
/// application disposes this lease only after the operation response is sent.
/// </summary>
public interface IVaultOperation : IManagedResourceStore, IScopeGrantPersistence, ICatalogPersistence, IDisposable
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

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
/// Reads managed resource bytes for an already-authorized opaque resource ID.
/// Implementations must open the backing file per request, never cache content
/// across policy generations, and fail closed with
/// <see cref="ManagedResourceUnavailableException"/> on any filesystem anomaly
/// so callers cannot distinguish missing, forbidden, or tampered resources.
/// </summary>
public interface IManagedResourceReader
{
    byte[] ReadContent(string resourceId);

    /// <summary>
    /// Confirms the ID is still registered after an authorization re-check, so a
    /// mapping change between two grant checks fails the request instead of
    /// serving bytes resolved under a stale registration.
    /// </summary>
    void ValidateRegistration(string resourceId);
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

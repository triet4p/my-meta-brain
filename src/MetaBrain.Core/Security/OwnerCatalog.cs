namespace MetaBrain.Core.Security;

/// <summary>
/// Owner-approved public catalog entry: the only metadata ever served to
/// discovery clients. Contains exactly the owner-chosen opaque catalog ID,
/// label, and short description. Never carries a resource ID, zone, revision,
/// path, title, citation, count, embedding, or any private text.
/// </summary>
public sealed record PublishedCatalogEntry(string CatalogId, string Label, string Description);

/// <summary>
/// Plaintext file envelope for the rebuildable public catalog projection.
/// Minimal format envelope only; every entry inside is owner-approved metadata.
/// </summary>
public sealed record PublishedCatalogProjection(int SchemaVersion, PublishedCatalogEntry[] Entries)
{
    public const int CurrentSchemaVersion = 1;
}

/// <summary>
/// Owner-authoritative catalog record. The <see cref="ResourceId"/> mapping is
/// private and stays encrypted; only <see cref="PublishedCatalogEntry"/>
/// triplets derived from live records are ever written to the public
/// projection. <see cref="Revision"/> is the mapped resource revision observed
/// at publication time: T8 catalog-ID resolution must revalidate it against
/// the live manifest at request time instead of trusting this hint.
/// </summary>
public sealed record OwnerCatalogEntry(
    string CatalogId,
    string Label,
    string Description,
    string ResourceId,
    long Revision,
    DateTimeOffset UpdatedAtUtc);

/// <summary>
/// Encrypted owner catalog state: the private catalog-ID to resource mapping
/// plus the exact approved labels/descriptions. Never served to discovery.
/// </summary>
public sealed record OwnerCatalogState(int SchemaVersion, OwnerCatalogEntry[] Entries)
{
    public const int CurrentSchemaVersion = 1;
    public static OwnerCatalogState Empty { get; } = new(CurrentSchemaVersion, Array.Empty<OwnerCatalogEntry>());
}

/// <summary>
/// Owner-only publication preview. Shows exactly what publication would store
/// plus the currently registered resource revision; mutates nothing, mints no
/// token, and never includes private body/title/path/source content.
/// </summary>
public sealed record OwnerCatalogPreview(
    string CatalogId,
    string Label,
    string Description,
    string ResourceId,
    long ResourceRevision,
    OwnerCatalogEntry? ExistingEntry);

/// <summary>
/// Unlocked owner access to the encrypted private catalog mapping.
/// </summary>
public interface ICatalogPersistence
{
    OwnerCatalogState LoadCatalogState();
    T UpdateCatalogState<T>(
        Func<OwnerCatalogState, (OwnerCatalogState State, T Result)> update);
}

/// <summary>
/// Lock-independent public catalog projection. Implementations read the
/// plaintext projection file per request without the vault key; writers
/// replace it atomically after the private mapping commit.
/// </summary>
public interface ICatalogProjectionStore
{
    PublishedCatalogEntry[] LoadProjections();
    void SaveProjections(IReadOnlyList<PublishedCatalogEntry> entries);
}

/// <summary>
/// Carries the owner-facing error code for a rejected catalog request.
/// Private-mapping problems surface as <see cref="InvalidDataException"/>
/// instead so callers fail closed with a storage-unavailable code.
/// </summary>
public sealed class OwnerCatalogValidationException : ArgumentException
{
    public OwnerCatalogValidationException(string code, string message)
        : base(message)
    {
        Code = code;
    }

    public string Code { get; }
}

/// <summary>
/// Single owner catalog authority. Publication labels and descriptions are
/// always explicit owner text: nothing is derived from private resource
/// titles, bodies, paths, or metadata. Discovery serves only live approved
/// triplets; withdrawal deletes the record so future serve boundaries,
/// restarts, and fresh reads converge to no-match.
/// </summary>
public sealed class OwnerCatalogAuthority
{
    public const int MaximumEntries = 1000;
    public const int MaximumLabelLength = 128;
    public const int MaximumDescriptionLength = 512;

    public OwnerCatalogPreview Preview(
        AuthenticatedContext actor,
        string? catalogId,
        string? label,
        string? description,
        string? resourceId,
        IReadOnlyList<ScopeResourceRevision> liveResources,
        ICatalogPersistence persistence)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(liveResources);
        ArgumentNullException.ThrowIfNull(persistence);
        EnsureOwner(actor);
        var normalized = ValidateInputs(catalogId, label, description, resourceId);
        var revision = RequireLiveRevision(normalized.ResourceId, liveResources);
        var state = ValidateAndNormalizeState(persistence.LoadCatalogState());
        var existing = Array.Find(state.Entries, entry =>
            string.Equals(entry.CatalogId, normalized.CatalogId, StringComparison.Ordinal));
        return new OwnerCatalogPreview(
            normalized.CatalogId, normalized.Label, normalized.Description,
            normalized.ResourceId, revision, existing);
    }

    public OwnerCatalogEntry Publish(
        AuthenticatedContext actor,
        string? catalogId,
        string? label,
        string? description,
        string? resourceId,
        IReadOnlyList<ScopeResourceRevision> liveResources,
        ICatalogPersistence persistence)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(liveResources);
        ArgumentNullException.ThrowIfNull(persistence);
        EnsureOwner(actor);
        var normalized = ValidateInputs(catalogId, label, description, resourceId);
        var revision = RequireLiveRevision(normalized.ResourceId, liveResources);
        return persistence.UpdateCatalogState(state =>
        {
            var current = ValidateAndNormalizeState(state);
            if (current.Entries.Length >= MaximumEntries &&
                Array.Find(current.Entries, entry =>
                    string.Equals(entry.CatalogId, normalized.CatalogId, StringComparison.Ordinal)) is null)
            {
                throw new OwnerCatalogValidationException(
                    "catalog_capacity_exhausted", "The owner catalog cannot accept another entry.");
            }

            var published = new OwnerCatalogEntry(
                normalized.CatalogId, normalized.Label, normalized.Description,
                normalized.ResourceId, revision, DateTimeOffset.UtcNow);
            var entries = current.Entries
                .Where(entry => !string.Equals(entry.CatalogId, normalized.CatalogId, StringComparison.Ordinal))
                .Append(published)
                .OrderBy(entry => entry.CatalogId, StringComparer.Ordinal)
                .ToArray();
            return (current with { Entries = entries }, published);
        });
    }

    public string Withdraw(
        AuthenticatedContext actor,
        string? catalogId,
        ICatalogPersistence persistence)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(persistence);
        EnsureOwner(actor);
        if (!IsValidCatalogId(catalogId))
        {
            throw new OwnerCatalogValidationException(
                "invalid_catalog_request", "A valid owner catalog identifier is required.");
        }

        return persistence.UpdateCatalogState(state =>
        {
            var current = ValidateAndNormalizeState(state);
            var existing = Array.Find(current.Entries, entry =>
                string.Equals(entry.CatalogId, catalogId, StringComparison.Ordinal));
            if (existing is null)
            {
                throw new OwnerCatalogValidationException(
                    "catalog_unknown", "The owner catalog entry does not exist.");
            }

            var entries = current.Entries
                .Where(entry => !string.Equals(entry.CatalogId, catalogId, StringComparison.Ordinal))
                .ToArray();
            return (current with { Entries = entries }, catalogId!);
        });
    }

    public IReadOnlyList<OwnerCatalogEntry> List(
        AuthenticatedContext actor,
        ICatalogPersistence persistence)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(persistence);
        EnsureOwner(actor);
        return Array.AsReadOnly(ValidateAndNormalizeState(persistence.LoadCatalogState()).Entries);
    }

    /// <summary>
    /// T8/S5 reuse contract: resolves a catalog ID to its currently registered
    /// resource revision. Returns null for unknown, withdrawn, or unregistered
    /// mappings. Callers freeze the returned concrete resource/revision into
    /// their own preview/grant snapshot at request time; the stored revision
    /// hint alone never authorizes a read.
    /// </summary>
    public static OwnerCatalogEntry? ResolveLive(
        string? catalogId,
        OwnerCatalogState state,
        IReadOnlyList<ScopeResourceRevision> liveResources)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(liveResources);
        if (!IsValidCatalogId(catalogId))
        {
            return null;
        }

        var normalized = ValidateAndNormalizeState(state);
        var mapped = Array.Find(normalized.Entries, entry =>
            string.Equals(entry.CatalogId, catalogId, StringComparison.Ordinal));
        if (mapped is null)
        {
            return null;
        }

        var live = liveResources.FirstOrDefault(resource =>
            string.Equals(resource.ResourceId, mapped.ResourceId, StringComparison.Ordinal));
        return live is null ? null : mapped with { Revision = live.Revision };
    }

    public static OwnerCatalogState ValidateAndNormalizeState(OwnerCatalogState state)
    {
        if (state is null || state.SchemaVersion != OwnerCatalogState.CurrentSchemaVersion ||
            state.Entries is null || state.Entries.Length > MaximumEntries)
        {
            throw new InvalidDataException("Invalid encrypted owner catalog state.");
        }

        var normalized = state.Entries.OrderBy(entry => entry.CatalogId, StringComparer.Ordinal).ToArray();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < normalized.Length; index++)
        {
            var entry = normalized[index];
            if (entry is null)
            {
                throw new InvalidDataException("Invalid encrypted owner catalog record.");
            }

            var validated = ValidateInputs(entry.CatalogId, entry.Label, entry.Description, entry.ResourceId);
            if (entry.Revision < 1 || entry.UpdatedAtUtc.Offset != TimeSpan.Zero || !ids.Add(validated.CatalogId))
            {
                throw new InvalidDataException("Invalid encrypted owner catalog record.");
            }

            normalized[index] = new OwnerCatalogEntry(
                validated.CatalogId, validated.Label, validated.Description,
                validated.ResourceId, entry.Revision, entry.UpdatedAtUtc);
        }

        return new OwnerCatalogState(OwnerCatalogState.CurrentSchemaVersion, normalized);
    }

    public static PublishedCatalogEntry[] ValidateProjections(PublishedCatalogProjection projection)
    {
        if (projection is null || projection.SchemaVersion != PublishedCatalogProjection.CurrentSchemaVersion ||
            projection.Entries is null || projection.Entries.Length > MaximumEntries)
        {
            throw new InvalidDataException("Invalid published catalog projection.");
        }

        return NormalizeProjections(projection.Entries);
    }

    public static PublishedCatalogEntry[] NormalizeProjections(IReadOnlyList<PublishedCatalogEntry>? entries)
    {
        if (entries is null || entries.Count > MaximumEntries || entries.Any(entry => entry is null))
        {
            throw new ArgumentException("The published catalog entries are invalid.", nameof(entries));
        }

        var normalized = entries
            .Select(entry =>
            {
                var validated = ValidateCatalogText(entry.CatalogId, entry.Label, entry.Description);
                return new PublishedCatalogEntry(validated.CatalogId, validated.Label, validated.Description);
            })
            .OrderBy(entry => entry.CatalogId, StringComparer.Ordinal)
            .ToArray();
        if (normalized.Select(entry => entry.CatalogId).Distinct(StringComparer.Ordinal).Count() != normalized.Length)
        {
            throw new ArgumentException("The published catalog identifiers must be unique.", nameof(entries));
        }

        return normalized;
    }

    public static PublishedCatalogEntry ToPublished(OwnerCatalogEntry entry) =>
        new(entry.CatalogId, entry.Label, entry.Description);

    private static (string CatalogId, string Label, string Description, string ResourceId) ValidateInputs(
        string? catalogId, string? label, string? description, string? resourceId)
    {
        var text = ValidateCatalogText(catalogId, label, description);
        if (!IsValidCatalogId(resourceId))
        {
            throw new OwnerCatalogValidationException(
                "catalog_unknown_resource", "The mapped private resource is not registered.");
        }

        return (text.CatalogId, text.Label, text.Description, resourceId!);
    }

    private static (string CatalogId, string Label, string Description) ValidateCatalogText(
        string? catalogId, string? label, string? description)
    {
        if (!IsValidCatalogId(catalogId))
        {
            throw new OwnerCatalogValidationException(
                "invalid_catalog_request", "A valid owner catalog identifier is required.");
        }

        if (string.IsNullOrWhiteSpace(label) || label!.Length > MaximumLabelLength || label.Any(char.IsControl))
        {
            throw new OwnerCatalogValidationException(
                "invalid_catalog_request", "The owner catalog label must be 1-128 characters without control characters.");
        }

        if (description is null || description.Length > MaximumDescriptionLength || description.Any(char.IsControl))
        {
            throw new OwnerCatalogValidationException(
                "invalid_catalog_request", "The owner catalog description must be 0-512 characters without control characters.");
        }

        return (catalogId!, label, description);
    }

    private static long RequireLiveRevision(
        string resourceId, IReadOnlyList<ScopeResourceRevision> liveResources)
    {
        var live = liveResources.FirstOrDefault(resource =>
            string.Equals(resource.ResourceId, resourceId, StringComparison.Ordinal));
        return live is null
            ? throw new OwnerCatalogValidationException(
                "catalog_unknown_resource", "The mapped private resource is not registered.")
            : live.Revision;
    }

    private static bool IsValidCatalogId(string? value)
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

    private static void EnsureOwner(AuthenticatedContext actor)
    {
        if (actor.Kind != PrincipalKind.Owner)
        {
            throw new UnauthorizedAccessException("Catalog publication requires the authenticated owner workflow.");
        }
    }
}

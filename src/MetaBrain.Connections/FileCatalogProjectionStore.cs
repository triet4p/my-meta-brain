using MetaBrain.Core.Security;

namespace MetaBrain.Connections;

/// <summary>
/// Lock-independent public catalog projection backed by the plaintext
/// <c>published-catalog.json</c> file beside the vault directory. Reads never
/// touch the vault key; writes replace only owner-approved triplets after the
/// encrypted private mapping commit.
/// </summary>
internal sealed class FileCatalogProjectionStore : ICatalogProjectionStore
{
    private readonly EncryptedVaultStore _store;

    public FileCatalogProjectionStore(EncryptedVaultStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public PublishedCatalogEntry[] LoadProjections() =>
        OwnerCatalogAuthority.NormalizeProjections(
            OwnerCatalogAuthority.ValidateProjections(_store.LoadPublishedProjection()));

    public void SaveProjections(IReadOnlyList<PublishedCatalogEntry> entries) =>
        _store.SavePublishedProjection(entries);
}

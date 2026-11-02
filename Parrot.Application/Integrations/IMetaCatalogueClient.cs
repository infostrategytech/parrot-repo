using Parrot.Application.DTOs.Integrations;

namespace Parrot.Application.Integrations;

public interface IMetaCatalogueClient
{
    /// <summary>
    /// Returns the id of the catalogue attached to the given WhatsApp Business Account,
    /// or null when the WABA has no catalogue.
    /// </summary>
    Task<string?> GetCatalogueIdAsync(string wabaId, string accessToken, CancellationToken ct);

    /// <summary>
    /// Returns one page of Meta-approved products from the catalogue.
    /// Pass null for the first page; pass <see cref="MetaCatalogueProductsPage.NextCursor"/>
    /// on subsequent calls until it is null (no more pages).
    /// </summary>
    Task<MetaCatalogueProductsPage> GetApprovedProductsAsync(
        string catalogueId, string accessToken, string? cursor, CancellationToken ct);
}

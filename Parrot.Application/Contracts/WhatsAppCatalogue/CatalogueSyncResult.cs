namespace Parrot.Application.Contracts.WhatsAppCatalogue;

public record CatalogueSyncResult(int Added, int Updated, int Deleted, int Failed, string? CatalogueId);

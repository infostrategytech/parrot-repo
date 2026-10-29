namespace Parrot.Web.Features.Dashboard.Catalog;

/// <summary>Turns the owner's description of one offering into an editable, unapproved draft.
/// No item is saved and no availability is inferred. Photos are attached by the owner, not analysed.</summary>
public interface IOfferingDraftReader
{
    bool CanReadDescriptions { get; }
    Task<CatalogEntry> ReadAsync(string description, CatalogKind kind, CancellationToken cancellationToken = default);
}

public sealed class UnavailableOfferingDraftReader : IOfferingDraftReader
{
    public bool CanReadDescriptions => false;
    public Task<CatalogEntry> ReadAsync(string description, CatalogKind kind, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Description reading is not connected yet.");
}

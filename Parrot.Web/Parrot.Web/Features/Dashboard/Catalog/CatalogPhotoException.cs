namespace Parrot.Web.Features.Dashboard.Catalog;

/// <summary>
/// A photo that couldn't go with a save (designer, 23 November 2026): the save stops rather than quietly leaving
/// the photo out, and the form says which photo and why, next to Photos, with the photo marked. Photo is its
/// address as the form holds it; Number counts from 1 in the item's order. Reason is in the owner's words: the
/// catalog's own when it refused the photo ("A photo must be at most 5 MB."), else ours. It is an
/// HttpRequestException, so a screen that only knows about connection errors still handles it.
/// </summary>
public sealed class CatalogPhotoException(string photo, int number, string reason, Exception? inner = null)
    : HttpRequestException(reason, inner)
{
    public string Photo { get; } = photo;

    public int Number { get; } = number;

    public string Reason { get; } = reason;
}

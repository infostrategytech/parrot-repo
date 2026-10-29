namespace Parrot.Web.Features.Dashboard.Catalog;

/// <summary>
/// What "Manage categories" changed: the categories in their new order, and
/// where the items of a renamed, merged or deleted category go ("" for no
/// category).
/// </summary>
public sealed record CategoryChanges(IReadOnlyList<string> Order, IReadOnlyDictionary<string, string> Moves);

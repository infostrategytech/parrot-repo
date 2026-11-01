namespace Parrot.Web.Features.Dashboard.Catalog;

/// <summary>Where an owner-approved product fact came from. Never contains credentials.</summary>
public sealed record CatalogSource(string Channel, string PostId, string Url, string Excerpt,
    string ImageUrl, DateTimeOffset? PublishedAt, DateTimeOffset ImportedAt);

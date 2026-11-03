using System.ComponentModel.DataAnnotations;

namespace Parrot.Application.Integrations;

/// <summary>Connection details for the Parrot.Catalog internal API.</summary>
public sealed class CatalogClientSettings
{
    public const string SectionName = "CatalogClient";

    [Required]
    public string BaseUrl { get; set; } = string.Empty;
}

namespace Parrot.Application.BusinessSources;

public record FacebookPageData(
    string? Name,
    string? About,
    string? Description,
    string? Website,
    string? Category,
    long? FanCount);

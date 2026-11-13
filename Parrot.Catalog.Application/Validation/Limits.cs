namespace Parrot.Catalog.Application.Validation;

/// <summary>Hard caps from the spec; every endpoint and tool enforces them before touching the database.</summary>
public static class Limits
{
    public const int MaxBodyBytes = 1_048_576;
    public const int MaxAttributesBytes = 65_536;
    public const int MaxPageSize = 100;
    public const int DefaultPageSize = 20;
    public const int MaxAiPageSize = 10;
    public const int MaxBatchItems = 100;
    public const int MaxCompareItems = 10;

    /// <summary>Products one get_prices call may ask about: a page of search results, with room to spare.</summary>
    public const int MaxPriceItems = 20;
    public const int MaxFilterKeys = 20;
    public const int MaxInValues = 50;
    public const int MaxQueryLength = 256;
    public const int MaxNameLength = 256;
    public const int MaxSkuLength = 64;
    public const int MaxSlugLength = 256;
    public const int MaxDescriptionLength = 20_000;
    public const int MaxLabelLength = 128;
    public const int MaxUnitLength = 32;
    public const int MaxEnumOptions = 200;
    public const int MaxEnumOptionLength = 128;
    public const int MaxRegionLength = 8;
    public const int MaxS3KeyLength = 1024;
    public const int MaxMimeTypeLength = 128;
    public const int MaxAltTextLength = 512;
    public const int MaxAiTextLength = 2_000;
    public const int MaxCategoryDepth = 8;
    public const int MaxLeadTimeDays = 365;
    public const decimal MaxPriceAmount = 99_999_999_999_999.9999m;
}

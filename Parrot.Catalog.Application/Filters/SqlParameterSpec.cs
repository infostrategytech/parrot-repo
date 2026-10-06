namespace Parrot.Catalog.Application.Filters;

public enum SqlParamKind
{
    Uuid,
    Text,
    Numeric,
    Boolean,
    TimestampUtc,
    TextArray,
    NumericArray,
    Integer,
}

/// <summary>A parameter the infrastructure turns into an NpgsqlParameter. Values never reach SQL text.</summary>
public sealed record SqlParameterSpec(string Name, object? Value, SqlParamKind Kind);

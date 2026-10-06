using System.ComponentModel;
using System.Text.Json;
using Parrot.Catalog.Domain.Enums;

namespace Parrot.Catalog.Application.Contracts;

[Description("A page of results and the cursor for the next one; null when there is no next page.")]
public sealed record PagedResponse<T>(IReadOnlyList<T> Items, string? NextCursor);

[Description("A reference to a category: id, key and the names from the root down.")]
public sealed record CategoryRef(Guid Id, string Key, string Name, IReadOnlyList<string> Path);

[Description("A reference to a product type and the version the product pins.")]
public sealed record TypeRef(Guid Id, string Key, string Name, ProductTypeKind Kind, int Version);

[Description("One attribute value with the registry's label and unit. Never hardcode keys; read the label.")]
public sealed record AttributeValue(string Key, string Label, string? Unit, AttributeDataType DataType, string? DisplayGroup, JsonElement Value);

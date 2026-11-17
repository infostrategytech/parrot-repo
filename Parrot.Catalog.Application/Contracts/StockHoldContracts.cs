namespace Parrot.Catalog.Application.Contracts;

/// <summary>One line to hold: a product, or one of its options, and how many.</summary>
public sealed record StockHoldLine(Guid ProductId, Guid? VariantId, int Quantity);

/// <summary>Hold stock for a payment order until <paramref name="ExpiresAt"/>.</summary>
public sealed record PlaceStockHoldsRequest(string? Reference, DateTime ExpiresAt, IReadOnlyList<StockHoldLine>? Lines);

/// <summary>A line as held. Held is false where stock isn't counted, so there was nothing to hold.</summary>
public sealed record HeldLine(Guid ProductId, Guid? VariantId, int Quantity, bool Held);

public sealed record StockHoldsResponse(string Reference, IReadOnlyList<HeldLine> Lines);

/// <summary>A line that couldn't be taken off stock: its hold had run out and the stock went to someone else.</summary>
public sealed record ShortLine(Guid ProductId, Guid? VariantId, int Quantity, int Left);

/// <summary>What a commit took off stock, and what it couldn't.</summary>
public sealed record CommitStockHoldsResponse(string Reference, IReadOnlyList<HeldLine> Committed, IReadOnlyList<ShortLine> Short);

public sealed record ReleaseStockHoldsResponse(string Reference, int Released);

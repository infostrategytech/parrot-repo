namespace Parrot.Application.Payments;

/// <summary>Stock kept in the catalog for payment orders.</summary>
public interface ICatalogStockClient
{
    /// <summary>Holds every counted line until <paramref name="expiresAt"/>, or none.</summary>
    /// <exception cref="StockShortException">A line hasn't enough left.</exception>
    /// <exception cref="CatalogStockUnavailableException">The catalog couldn't be reached or failed.</exception>
    Task HoldAsync(string workspaceId, string reference, DateTime expiresAt, IReadOnlyList<StockLine> lines, CancellationToken ct);

    /// <summary>Takes the order's holds off stock. Returns the lines it couldn't, because their hold ran out and the stock went.</summary>
    /// <exception cref="CatalogStockUnavailableException">The catalog couldn't be reached or failed.</exception>
    Task<IReadOnlyList<StockLine>> CommitAsync(string workspaceId, string reference, CancellationToken ct);

    /// <exception cref="CatalogStockUnavailableException">The catalog couldn't be reached or failed.</exception>
    Task ReleaseAsync(string workspaceId, string reference, CancellationToken ct);
}

public sealed record StockLine(Guid ProductId, Guid? VariantId, int Quantity);

/// <summary>The catalog hasn't enough left. The message names the item and how many there are.</summary>
public sealed class StockShortException(string message) : Exception(message);

public sealed class CatalogStockUnavailableException(string message) : Exception(message);

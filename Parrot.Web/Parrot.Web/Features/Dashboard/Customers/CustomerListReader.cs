namespace Parrot.Web.Features.Dashboard.Customers;

/// <summary>
/// Reading a photo of a customer list (a page of a customer book, handwritten or printed, or a screenshot of a
/// phone's contacts) into customers. It needs a reading service (text recognition with AI); Import offers photos
/// only where one exists, as Products offers "Snap a photo" of a price list.
/// </summary>
public interface ICustomerListReader
{
    bool CanReadPhotos { get; }

    /// <summary>The customers in the photo, in the order they appear; an empty list when none could be read.</summary>
    Task<IReadOnlyList<ImportEntry>> ReadPhotoAsync(Stream photo, string contentType, CancellationToken cancellationToken = default);
}

/// <summary>Real mode today: no reading service yet (customers-backend-requests), so photos aren't offered.</summary>
public sealed class UnavailableCustomerListReader : ICustomerListReader
{
    public bool CanReadPhotos => false;

    public Task<IReadOnlyList<ImportEntry>> ReadPhotoAsync(Stream photo, string contentType, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Reading photos needs a reading service.");
}

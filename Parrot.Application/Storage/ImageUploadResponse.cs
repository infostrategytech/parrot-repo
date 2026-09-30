using System.ComponentModel;

namespace Parrot.Application.Storage;

/// <summary>Returned by the upload endpoint; the URL is saved with the next profile update.</summary>
[Description("The only thing the upload endpoint returns: where the stored bytes can be read from.")]
public record ImageUploadResponse(
    [property: Description("The URL the image can be read from. Save it with PUT /api/user/profile — uploading on its own does not put it on the account.")]
    string Url
);

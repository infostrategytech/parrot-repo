using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using Parrot.Application.Auth;

namespace Parrot.Application.DTOs.Auth;

/// <summary>The signed-in account as the setup step sees it.</summary>
[Description("The account after sign-in: what setup has collected so far, and whether it has been completed.")]
public record ProfileResponse(
    [property: Description("The account's identifier.")]
    string UserId,
    [property: Description("The address the account was created with. It cannot be changed here.")]
    string Email,
    [property: Description("The person's first name, or null when none has been saved.")]
    string? FirstName,
    [property: Description("The person's last name, or null when none has been saved.")]
    string? LastName,
    [property: Description("The business name, or null until setup has saved one.")]
    string? BusinessName,
    [property: Description("The image URL saved on the account, or null when none has been set.")]
    string? LogoUrl,
    [property: Description("The stored number in international display form, e.g. +2348012345678, or null when none is on the account.")]
    string? PhoneNumber,
    [property: Description("True once a business name has been saved — what the setup screen reads to decide whether to show itself again.")]
    bool SetupComplete
);

/// <summary>
/// What the setup step submits. The country arrives as its own field without a
/// plus — e.g. 234 — so it is stated by the form rather than worked out from the
/// number. The number may be sent in any of the shapes a person would type it.
/// </summary>
[Description("A partial update: every field is optional. Leave a field out (or null) to leave it as it is; send an empty string for firstName, lastName or logoUrl to clear it. The email address cannot be changed.")]
public record UpdateProfileRequest
{
    // The messages below come from AuthMessages rather than being written out
    // again, because a request can fail these rules before it reaches the
    // service — and a person must not be able to tell which path they hit.
    [Description("Trimmed to between 1 and 256 characters. Send it to set it, leave it out to leave it as it is.")]
    [StringLength(256, MinimumLength = 1, ErrorMessage = AuthMessages.BusinessNameTooLong)]
    public string? BusinessName { get; init; }

    [Description("Up to 80 characters. An empty string clears it.")]
    [StringLength(80, ErrorMessage = AuthMessages.NameTooLong)]
    public string? FirstName { get; init; }

    [Description("Up to 80 characters. An empty string clears it.")]
    [StringLength(80, ErrorMessage = AuthMessages.NameTooLong)]
    public string? LastName { get; init; }

    // Checked in the service rather than with [Url], because an empty string
    // is allowed here: it is how the image is removed.
    [Description("A URL, not the file itself. Upload with POST /api/user/image first, then send the URL it gave back here. An empty string removes the image.")]
    [StringLength(2048, ErrorMessage = "Logo URL must be 2048 characters or fewer")]
    public string? LogoUrl { get; init; }

    /// <summary>The dial code of the chosen country, without a plus, e.g. 234.</summary>
    [Description("The dial code of the chosen country without a plus, e.g. 234. Sent together with number, never on its own.")]
    [StringLength(3, MinimumLength = 1, ErrorMessage = AuthMessages.InvalidDialCode)]
    public string? CountryCode { get; init; }

    /// <summary>The number as typed locally, e.g. 08012345678.</summary>
    [Description("The number as typed locally — 08012345678, 8012345678, 2348012345678 or +2348012345678 are all accepted and all store the same value. Sent together with countryCode.")]
    [StringLength(32, MinimumLength = 1, ErrorMessage = AuthMessages.InvalidPhoneNumber)]
    public string? Number { get; init; }
}

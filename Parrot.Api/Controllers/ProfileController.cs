using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Parrot.Api.Middleware;
using Parrot.Api.Monitoring;
using Parrot.Api.Utils;
using Parrot.Application.DTOs.Auth;
using Parrot.Application.Profile;
using Parrot.Application.Storage;

namespace Parrot.Api.Controllers;

/// <summary>
/// The account's own details: read them, then save what the setup screen and
/// the settings page collect.
/// </summary>
[ApiController]
[Route("api/user")]
[Authorize]
[Monitor(Area = "user")]
public class ProfileController : ControllerBase
{
    private readonly IProfileService _profileService;
    private readonly IImageService _imageService;

    public ProfileController(IProfileService profileService, IImageService imageService)
    {
        _profileService = profileService;
        _imageService = imageService;
    }

    [HttpGet("profile")]
    [EndpointSummary("Read the signed-in account's profile")]
    [EndpointDescription("The account as the setup screen and settings page should draw it: first and last name, email, business name, logo, phone number in international display form, and setupComplete — true once a business name has been saved.")]
    [ProducesResponseType(typeof(ProfileResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ProfileResponse>> GetAsync()
    {
        string? userId = CurrentUserId();
        if (userId is null)
        {
            return Unauthorized();
        }

        return Ok(await _profileService.GetAsync(userId));
    }

    [HttpPut("profile")]
    [EndpointSummary("Save the details the setup screen and settings page collect")]
    [EndpointDescription("A partial update where no field is required: send only the fields being changed and the rest are left alone. An empty firstName, lastName or logoUrl clears it. The email address cannot be changed here. countryCode is the dial code without a plus (234) and number is what a person would type (08012345678, 8012345678, 2348012345678 or +2348012345678) — all four store the same value. Sending either phone field means sending both. An account already holding that number elsewhere is a 409.")]
    [ProducesResponseType(typeof(ProfileResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status409Conflict)]
    public async Task<ActionResult<ProfileResponse>> UpdateAsync([FromBody] UpdateProfileRequest request)
    {
        string? userId = CurrentUserId();
        if (userId is null)
        {
            return Unauthorized();
        }

        return Ok(await _profileService.UpdateAsync(userId, request));
    }

    /// <summary>
    /// Stores an image and returns the URL to save with the next profile
    /// update. The upload and the profile stay separate steps: this endpoint
    /// knows nothing about the account's fields, and the profile knows nothing
    /// about where its files live.
    /// </summary>
    [HttpPost("image")]
    // 5 MB of image plus the multipart framing; a bigger body is refused before it is buffered.
    [RequestSizeLimit(6 * 1024 * 1024)]
    [EnableRateLimiting(RateLimitPolicies.ImageUpload)]
    [EndpointSummary("Upload an image and get back a URL")]
    [EndpointDescription("multipart/form-data with one file: PNG, JPEG, GIF or WebP, at most 5 MB. The bytes go to object storage and only the URL comes back — save it with PUT /api/user/profile, which is what actually puts it on the account. The file name sent is ignored; the object key is minted here.")]
    [ProducesResponseType(typeof(ImageUploadResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<ImageUploadResponse>> UploadImageAsync(IFormFile? file)
    {
        string? userId = CurrentUserId();
        if (userId is null)
        {
            return Unauthorized();
        }

        // A missing file is a validation failure like the size and type checks
        // are, so it goes through the service rather than being worded here too.
        using Stream content = file?.OpenReadStream() ?? Stream.Null;
        string url = await _imageService.SaveUserImageAsync(
            userId,
            content,
            file?.ContentType ?? string.Empty,
            file?.Length ?? 0);

        return Ok(new ImageUploadResponse(url));
    }

    private string? CurrentUserId() =>
        User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub");
}

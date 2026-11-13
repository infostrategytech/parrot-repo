namespace Parrot.Application.BusinessSources;

public record InstagramProfileData(
    string? Name,
    string? Biography,
    string? Website,
    long? FollowerCount,
    string? ProfilePictureUrl);

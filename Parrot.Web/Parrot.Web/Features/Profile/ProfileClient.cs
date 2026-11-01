using System.Net.Http.Headers;
using System.Net.Http.Json;
using Parrot.Web.Features.Auth.Models;
using Parrot.Web.Features.Auth.State;
using Parrot.Web.Services;

namespace Parrot.Web.Features.Profile;

/// <summary>The signed-in account, as GET and PUT /api/user/profile read and write it.</summary>
public sealed record UserProfile(
    string UserId,
    string Email,
    string? FirstName,
    string? LastName,
    string? BusinessName,
    string? LogoUrl,
    string? PhoneNumber);

/// <summary>
/// A partial update. A null field is left as it is on the account; an empty
/// FirstName, LastName or LogoUrl clears it.
/// </summary>
public sealed record UserProfileUpdate
{
    public string? FirstName { get; init; }
    public string? LastName { get; init; }
    public string? BusinessName { get; init; }
    public string? LogoUrl { get; init; }

    /// <summary>The dial code without the plus, e.g. 234. Sent with Number.</summary>
    public string? CountryCode { get; init; }

    public string? Number { get; init; }
}

/// <summary>
/// Reads and saves the account's profile and uploads its image. After a save
/// the session is brought up to date, so the workspace switcher and the
/// dashboard show the new name and logo straight away. Failures throw an
/// <see cref="ApiException"/> carrying the API's message.
/// </summary>
public sealed class ProfileClient(HttpClient http, AuthState authState, TokenStorageService tokenStorage)
{
    public async Task<UserProfile> GetAsync()
    {
        using HttpRequestMessage request = Authorized(HttpMethod.Get, "api/user/profile");
        using HttpResponseMessage response = await http.SendAsync(request);
        return await ReadAsync<UserProfile>(response);
    }

    public async Task<UserProfile> UpdateAsync(UserProfileUpdate update)
    {
        using HttpRequestMessage request = Authorized(HttpMethod.Put, "api/user/profile");
        request.Content = JsonContent.Create(update);
        using HttpResponseMessage response = await http.SendAsync(request);
        UserProfile profile = await ReadAsync<UserProfile>(response);

        await KeepSessionInStepAsync(profile);
        return profile;
    }

    /// <summary>Stores the file and returns its URL. It is not on the account until saved with <see cref="UpdateAsync"/>.</summary>
    public async Task<string> UploadImageAsync(string fileName, string contentType, byte[] bytes)
    {
        ByteArrayContent file = new(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue(contentType);

        using HttpRequestMessage request = Authorized(HttpMethod.Post, "api/user/image");
        request.Content = new MultipartFormDataContent { { file, "file", fileName } };
        using HttpResponseMessage response = await http.SendAsync(request);
        return (await ReadAsync<ImageUploadResponse>(response)).Url;
    }

    private HttpRequestMessage Authorized(HttpMethod method, string path)
    {
        string token = authState.CurrentUser?.Token
            ?? throw new InvalidOperationException("The profile is only reachable while signed in.");

        HttpRequestMessage request = new(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response)
    {
        await response.EnsureApiSuccessAsync();
        return await response.Content.ReadFromJsonAsync<T>()
            ?? throw new HttpRequestException("The server returned an empty response.");
    }

    private async Task KeepSessionInStepAsync(UserProfile profile)
    {
        if (authState.CurrentUser is not { } current) return;

        AuthResponse updated = new()
        {
            Token = current.Token,
            ExpiresAt = current.ExpiresAt,
            UserId = current.UserId,
            Email = current.Email,
            WorkspaceId = current.WorkspaceId,
            BusinessName = profile.BusinessName,
            LogoUrl = profile.LogoUrl,
            Role = current.Role,
            Permissions = current.Permissions,
        };

        await tokenStorage.SaveAsync(updated);
        authState.SetUser(updated);
    }

    private sealed record ImageUploadResponse(string Url);
}

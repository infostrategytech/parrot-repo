using System.Net.Http.Json;
using Parrot.Web.Services;
using Parrot.Web.Features.Auth.Login.Models;
using Parrot.Web.Features.Auth.Models;

namespace Parrot.Web.Features.Auth.Login.Services;

public class LoginService : ILoginService
{
    private readonly HttpClient _httpClient;

    public LoginService(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task<AuthResponse?> LoginAsync(LoginRequest request)
    {
        HttpResponseMessage response = await _httpClient.PostAsJsonAsync("api/auth/login", request);
        await response.EnsureApiSuccessAsync();
        return await response.Content.ReadFromJsonAsync<AuthResponse>();
    }
}

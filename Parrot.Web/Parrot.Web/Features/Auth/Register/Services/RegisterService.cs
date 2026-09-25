using System.Net.Http.Json;
using Parrot.Web.Services;
using Parrot.Web.Features.Auth.Models;
using Parrot.Web.Features.Auth.Register.Models;

namespace Parrot.Web.Features.Auth.Register.Services;

public class RegisterService : IRegisterService
{
    private readonly HttpClient _httpClient;

    public RegisterService(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task<AuthResponse?> RegisterAsync(RegisterRequest request)
    {
        HttpResponseMessage response = await _httpClient.PostAsJsonAsync("api/auth/register", request);
        await response.EnsureApiSuccessAsync();

        // 200 carries a session (verification switched off); 201 means a code is on its way.
        return response.StatusCode == System.Net.HttpStatusCode.OK
            ? await response.Content.ReadFromJsonAsync<AuthResponse>()
            : null;
    }
}

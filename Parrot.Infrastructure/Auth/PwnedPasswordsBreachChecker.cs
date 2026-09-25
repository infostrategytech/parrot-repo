using System.Security.Cryptography;
using System.Text;
using Parrot.Application.Auth;

namespace Parrot.Infrastructure.Auth;

// Have I Been Pwned's range endpoint: the caller sends the first five hex
// characters of the password's SHA-1 and gets back every known suffix under
// that prefix, so the service never learns which password was asked about.
// SHA-1 is what the corpus is keyed by; nothing here relies on it for
// integrity.
public sealed class PwnedPasswordsBreachChecker : IPasswordBreachChecker
{
    private readonly HttpClient _http;

    public PwnedPasswordsBreachChecker(HttpClient http)
    {
        _http = http;
    }

    public async Task<bool> IsBreachedAsync(string password, CancellationToken cancellationToken = default)
    {
        string hash = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(password)));
        string prefix = hash[..5];
        string suffix = hash[5..];

        using HttpResponseMessage response = await _http.GetAsync($"range/{prefix}", cancellationToken);
        response.EnsureSuccessStatusCode();

        string body = await response.Content.ReadAsStringAsync(cancellationToken);
        foreach (string line in body.Split('\n'))
        {
            int colon = line.IndexOf(':');
            if (colon <= 0 || !line.AsSpan(0, colon).Trim().Equals(suffix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // With Add-Padding the response also lists decoy suffixes at a count
            // of zero; only a real sighting counts.
            return int.TryParse(line.AsSpan(colon + 1).Trim(), out int count) && count > 0;
        }

        return false;
    }
}

namespace Parrot.Application.Auth;

// An offline backstop for the breach lookup: the handful of shapes that clear
// a twelve-character floor and still turn up in every credential dump. A
// password is judged by its stem, so "Welcome2024!!" is caught along with
// "welcome", and by whether it is one short piece repeated.
public static class CommonPasswords
{
    private static readonly HashSet<string> Stems = new(StringComparer.OrdinalIgnoreCase)
    {
        "password", "passwort", "passw0rd", "p@ssword", "p@ssw0rd", "qwerty", "qwertyuiop",
        "asdfghjkl", "zxcvbnm", "letmein", "welcome", "iloveyou", "admin", "administrator",
        "login", "abc123", "monkey", "dragon", "football", "baseball", "basketball",
        "sunshine", "princess", "master", "superman", "batman", "trustno1", "whatever",
        "shadow", "michael", "jennifer", "computer", "internet", "changeme", "default",
        "secret", "starwars", "cheese", "summer", "winter", "spring", "autumn",
        "liverpool", "arsenal", "chelsea", "manchester", "charlie", "freedom", "hello",
        "access", "mustang", "pokemon", "minecraft", "fortnite", "samsung", "google",
        "facebook", "twitter", "instagram", "parrot", "omniai",
    };

    public static bool IsCommon(string password)
    {
        if (string.IsNullOrEmpty(password))
        {
            return false;
        }

        if (Stems.Contains(password) || Stems.Contains(Stem(password)))
        {
            return true;
        }

        return IsRepeated(password);
    }

    // Strips the trailing digits and punctuation people bolt on to satisfy a
    // rule: "Sunshine2019!" stems to "sunshine".
    private static string Stem(string password)
    {
        int end = password.Length;
        while (end > 0 && !char.IsLetter(password[end - 1]))
        {
            end--;
        }

        return password[..end];
    }

    // "abcabcabcabc" and "passwordpassword" are one piece written out until the
    // length rule is satisfied; the smallest period tells them apart from prose.
    private static bool IsRepeated(string password)
    {
        for (int period = 1; period <= password.Length / 2; period++)
        {
            if (password.Length % period != 0)
            {
                continue;
            }

            bool repeats = true;
            for (int i = period; i < password.Length && repeats; i++)
            {
                repeats = char.ToLowerInvariant(password[i]) == char.ToLowerInvariant(password[i - period]);
            }

            if (repeats)
            {
                return true;
            }
        }

        return false;
    }
}

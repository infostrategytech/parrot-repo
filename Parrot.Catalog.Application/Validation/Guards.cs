using System.Text;
using System.Text.RegularExpressions;
using Parrot.Domain.Exceptions;

namespace Parrot.Catalog.Application.Validation;

public static partial class Guards
{
    [GeneratedRegex("^[a-z][a-z0-9_]{0,63}$")]
    public static partial Regex AttributeKey();

    [GeneratedRegex("^[a-z0-9]+(?:-[a-z0-9]+)*$")]
    public static partial Regex Slug();

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$")]
    public static partial Regex Sku();

    [GeneratedRegex("^[A-Z]{3}$")]
    public static partial Regex Currency();

    [GeneratedRegex("^(\\*|[A-Z]{2}(-[A-Z0-9]{1,3})?)$")]
    public static partial Regex Region();

    [GeneratedRegex("^[a-z][a-z0-9_]{0,63}$")]
    public static partial Regex Key();

    public static string RequireText(string? value, string field, int maxLength, int minLength = 1)
    {
        if (value is null || value.Length < minLength)
        {
            throw new ValidationException($"{field} is required.");
        }

        if (value.Length > maxLength)
        {
            throw new ValidationException($"{field} must be at most {maxLength} characters.");
        }

        if (HasControlCharacters(value))
        {
            throw new ValidationException($"{field} must not contain control characters.");
        }

        return value;
    }

    public static string? OptionalText(string? value, string field, int maxLength)
    {
        if (value is null)
        {
            return null;
        }

        if (value.Length > maxLength)
        {
            throw new ValidationException($"{field} must be at most {maxLength} characters.");
        }

        if (HasControlCharacters(value, allowNewlines: true))
        {
            throw new ValidationException($"{field} must not contain control characters.");
        }

        return value;
    }

    public static string RequireMatch(string? value, Regex pattern, string field, string hint)
    {
        if (string.IsNullOrEmpty(value) || !pattern.IsMatch(value))
        {
            throw new ValidationException($"{field} {hint}");
        }

        return value;
    }

    public static Guid RequireId(Guid? value, string field)
        => value is { } id && id != Guid.Empty ? id : throw new ValidationException($"{field} is required.");

    public static int RequireRange(int value, string field, int min, int max)
        => value >= min && value <= max ? value : throw new ValidationException($"{field} must be between {min} and {max}.");

    public static void RequireDateOrder(DateTime from, DateTime? until, string fromField, string untilField)
    {
        if (until.HasValue && until.Value <= from)
        {
            throw new ValidationException($"{untilField} must be after {fromField}.");
        }
    }

    public static DateTime Utc(DateTime value)
        => value.Kind == DateTimeKind.Utc ? value : DateTime.SpecifyKind(value.ToUniversalTime(), DateTimeKind.Utc);

    public static DateTime? Utc(DateTime? value) => value.HasValue ? Utc(value.Value) : null;

    public static string Slugify(string name)
    {
        StringBuilder sb = new StringBuilder(name.Length);
        bool lastDash = true;
        foreach (char c in name.ToLowerInvariant())
        {
            if (char.IsAsciiLetterOrDigit(c))
            {
                sb.Append(c);
                lastDash = false;
            }
            else if (!lastDash)
            {
                sb.Append('-');
                lastDash = true;
            }
        }

        string slug = sb.ToString().Trim('-');
        return slug.Length == 0 ? "item" : slug[..Math.Min(slug.Length, Limits.MaxSlugLength)];
    }

    public static bool HasControlCharacters(string value, bool allowNewlines = false)
    {
        foreach (char c in value)
        {
            if (char.IsControl(c) && !(allowNewlines && (c == '\n' || c == '\r' || c == '\t')))
            {
                return true;
            }
        }

        return false;
    }
}

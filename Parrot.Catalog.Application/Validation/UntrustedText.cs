using System.Text;

namespace Parrot.Catalog.Application.Validation;

/// <summary>
/// Tenant-entered text is data, not instructions. Before it reaches an LLM it is
/// stripped of control characters, capped, and wrapped in a field whose name says
/// what it is (ADR and THREAT_MODEL: prompt-injection hardening).
/// </summary>
public static class UntrustedText
{
    public static string Sanitize(string? value, int maxLength = Limits.MaxAiTextLength)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        StringBuilder sb = new StringBuilder(Math.Min(value.Length, maxLength));
        foreach (char c in value)
        {
            if (c == '\n' || c == '\t')
            {
                sb.Append(c == '\t' ? ' ' : '\n');
            }
            else if (!char.IsControl(c) && !IsBidiOrInvisible(c))
            {
                sb.Append(c);
            }

            if (sb.Length >= maxLength)
            {
                break;
            }
        }

        string result = sb.ToString();
        return result.Length > maxLength ? result[..maxLength] : result;
    }

    // Zero-width and bidi override characters hide text from a reader while an LLM still sees it.
    private static bool IsBidiOrInvisible(char c) => c is '​' or '‌' or '‍' or '‎' or '‏'
        or '‪' or '‫' or '‬' or '‭' or '‮' or '⁦' or '⁧' or '⁨' or '⁩' or '﻿';
}

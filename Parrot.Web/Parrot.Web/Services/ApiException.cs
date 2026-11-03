using System.Net;
using System.Text.Json;

namespace Parrot.Web.Services;

// An HttpRequestException that also carries what the API said, so a page can
// show the real reason instead of a canned one. It extends the type the pages
// already filter on by status, so their existing catches keep working.
public sealed class ApiException : HttpRequestException
{
    public ApiException(HttpStatusCode statusCode, string message, string? errorCode, IReadOnlyList<string> errors)
        : base(message, null, statusCode)
    {
        ErrorCode = errorCode;
        Errors = errors;
    }

    public string? ErrorCode { get; }

    /// <summary>Field-level messages, when the API sent any.</summary>
    public IReadOnlyList<string> Errors { get; }

    /// <summary>The first thing worth showing a person: a field message if there is one, else the summary.</summary>
    public string UserMessage => Errors.Count > 0 ? Errors[0] : Message;
}

public static class ApiResponseExtensions
{
    // Reads both shapes the API produces: its own ErrorResponse
    // ({ errorCode, message, errors: [] }) and ASP.NET's model-state
    // ValidationProblemDetails ({ title, errors: { field: [] } }).
    public static async Task EnsureApiSuccessAsync(this HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        string? errorCode = null;
        string message = $"The request failed ({(int)response.StatusCode}).";
        List<string> errors = new();

        try
        {
            string body = await response.Content.ReadAsStringAsync();
            if (!string.IsNullOrWhiteSpace(body))
            {
                using JsonDocument document = JsonDocument.Parse(body);
                JsonElement root = document.RootElement;
                // The AI service's orientation routes (FastAPI) wrap theirs in detail: a
                // string, or the same { errorCode, message } object.
                if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("detail", out JsonElement detail))
                {
                    if (detail.ValueKind == JsonValueKind.String) message = detail.GetString()!;
                    else root = detail;
                }

                if (root.ValueKind == JsonValueKind.Object)
                {
                    if (root.TryGetProperty("errorCode", out JsonElement code) && code.ValueKind == JsonValueKind.String)
                    {
                        errorCode = code.GetString();
                    }

                    if (root.TryGetProperty("message", out JsonElement summary) && summary.ValueKind == JsonValueKind.String)
                    {
                        message = summary.GetString()!;
                    }
                    else if (root.TryGetProperty("title", out JsonElement title) && title.ValueKind == JsonValueKind.String)
                    {
                        message = title.GetString()!;
                    }

                    if (root.TryGetProperty("errors", out JsonElement list))
                    {
                        CollectErrors(list, errors);
                    }
                }
            }
        }
        catch (JsonException)
        {
            // Not JSON (a bare 429 from the rate limiter, a proxy page): the status is all there is.
        }

        throw new ApiException(response.StatusCode, message, errorCode, errors);
    }

    private static void CollectErrors(JsonElement list, List<string> errors)
    {
        if (list.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in list.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String)
                {
                    errors.Add(item.GetString()!);
                }
            }
        }
        else if (list.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty field in list.EnumerateObject())
            {
                CollectErrors(field.Value, errors);
            }
        }
    }
}

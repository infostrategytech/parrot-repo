using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Parrot.Api.Media;
using Parrot.Application.Storage;

namespace Parrot.Api.Controllers;

/// <summary>
/// Streams a customer's file for a signed address the conversation endpoints minted (see <see cref="MediaLinks"/>).
/// Anonymous on purpose: the address is the credential, and it expires.
/// </summary>
[ApiController]
[AllowAnonymous]
[Route("api/media")]
public sealed class MediaController(MediaLinks links, IPrivateObjectStorage storage) : ControllerBase
{
    // What a browser may draw in place. Everything else (HTML, SVG, scripts a customer might send) downloads.
    private static readonly string[] Inline =
    [
        "image/png", "image/jpeg", "image/gif", "image/webp",
        "video/mp4", "video/3gpp", "video/webm",
        "audio/ogg", "audio/mpeg", "audio/aac", "audio/mp4", "audio/amr", "audio/webm",
    ];

    [HttpGet("{token}")]
    public async Task<IActionResult> Get(string token, [FromQuery] bool download, CancellationToken cancellationToken)
    {
        if (!links.TryRead(token, out string key, out string? fileName)) return NotFound();

        // Buffered so the browser can seek in a video: a range request needs a seekable stream, and files are capped.
        using StoredObject? stored = await storage.GetAsync(key, cancellationToken);
        if (stored is null) return NotFound();
        MemoryStream bytes = new();
        await stored.Content.CopyToAsync(bytes, cancellationToken);
        bytes.Position = 0;

        // The type stored is the one the platform declared; trusted only for the list above.
        string contentType = stored.ContentType.Split(';')[0].Trim().ToLowerInvariant();
        bool inline = !download && Inline.Contains(contentType);

        Response.Headers["X-Content-Type-Options"] = "nosniff";
        Response.Headers["Content-Security-Policy"] = "default-src 'none'; sandbox";
        // Cross-origin: the inbox is another site, and loads this in <img>, <video> and <audio>.
        Response.Headers["Cross-Origin-Resource-Policy"] = "cross-site";
        Response.Headers.CacheControl = $"private, max-age={(int)MediaLinks.Lifetime.TotalSeconds / 2}";

        FileStreamResult result = File(bytes, inline ? contentType : "application/octet-stream", enableRangeProcessing: true);
        result.FileDownloadName = inline ? null : SafeName(fileName, key);
        if (inline)
        {
            // Keeps the name for "Save as" without forcing a download.
            Response.Headers.ContentDisposition = $"inline; filename*=UTF-8''{Uri.EscapeDataString(SafeName(fileName, key))}";
        }
        return result;
    }

    // The name the customer's file came with, cleaned of anything that is not a plain file name.
    private static string SafeName(string? fileName, string key)
    {
        string name = Path.GetFileName(fileName ?? "").Trim();
        return name.Length > 0 && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 ? name : Path.GetFileName(key);
    }
}

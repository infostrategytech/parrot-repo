using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Net.Http.Headers;
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

        // Only the size and type are read here; the bytes are streamed from storage as they are sent, so a large
        // video never sits in the API's memory and a seek in it fetches only the part asked for.
        StoredObjectInfo? info = await storage.StatAsync(key, cancellationToken);
        if (info is null) return NotFound();

        // The type stored is the one the platform declared; trusted only for the list above.
        string contentType = info.ContentType.Split(';')[0].Trim().ToLowerInvariant();
        bool inline = !download && Inline.Contains(contentType);

        Response.Headers["X-Content-Type-Options"] = "nosniff";
        Response.Headers["Content-Security-Policy"] = "default-src 'none'; sandbox";
        // Cross-origin: the inbox is another site, and loads this in <img>, <video> and <audio>.
        Response.Headers["Cross-Origin-Resource-Policy"] = "cross-site";
        Response.Headers.CacheControl = $"private, max-age={(int)MediaLinks.Lifetime.TotalSeconds / 2}";

        Response.Headers.AcceptRanges = "bytes";

        // One range, as a browser's player asks for. A range that cannot be met is answered 416; anything more
        // elaborate is served whole, which HTTP allows.
        long start = 0, end = info.Length - 1;
        bool partial = false;
        RangeHeaderValue? requested = Request.GetTypedHeaders().Range;
        if (requested is not null && requested.Unit == "bytes" && requested.Ranges.Count == 1)
        {
            RangeItemHeaderValue item = requested.Ranges.Single();
            bool suffix = item.From is null;
            bool unsatisfiable = info.Length == 0 || (suffix ? item.To is null or 0 : item.From >= info.Length);
            if (unsatisfiable)
            {
                Response.Headers.ContentRange = $"bytes */{info.Length}";
                return StatusCode(StatusCodes.Status416RangeNotSatisfiable);
            }

            // A first byte after the last ("5-2") is not a range at all and is ignored.
            if (suffix || item.To is null || item.To >= item.From)
            {
                partial = true;
                start = suffix ? Math.Max(0, info.Length - item.To!.Value) : item.From!.Value;
                end = suffix ? end : Math.Min(item.To ?? end, info.Length - 1);
            }
        }

        StoredObject? stored = partial
            ? await storage.GetRangeAsync(key, start, end, cancellationToken)
            : await storage.GetAsync(key, cancellationToken);
        if (stored is null) return NotFound();

        if (partial)
        {
            Response.StatusCode = StatusCodes.Status206PartialContent;
            Response.Headers.ContentRange = $"bytes {start}-{end}/{info.Length}";
        }

        Response.ContentLength = partial ? end - start + 1 : info.Length;

        // The result disposes the stream once it has been sent.
        FileStreamResult result = File(stored.Content, inline ? contentType : "application/octet-stream");
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

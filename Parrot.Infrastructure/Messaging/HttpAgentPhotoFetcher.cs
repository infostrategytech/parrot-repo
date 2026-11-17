using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using Parrot.Application.Conversations;
using Parrot.Application.Messaging;

namespace Parrot.Infrastructure.Messaging;

/// <summary>
/// Downloads the photos the AI sends with a reply. The link comes from another service, so it is
/// treated with care: https only, no redirects, and the address it connects to must be public
/// (checked on the address actually connected to, so a name that resolves to an internal one is
/// refused too). JPEG or PNG only, by its first bytes, and no more than WhatsApp's 5 MB.
/// </summary>
public sealed class HttpAgentPhotoFetcher(HttpClient http) : IAgentPhotoFetcher
{
    public const string HttpClientName = "agent-photos";

    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    public async Task<OutgoingFile> FetchAsync(string url, CancellationToken ct)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) || uri.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(uri.UserInfo))
        {
            throw new AgentPhotoRejectedException("The photo link isn't an https address.");
        }

        using HttpRequestMessage request = new(HttpMethod.Get, uri);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("image/jpeg"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("image/png"));
        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        catch (HttpRequestException ex)
        {
            throw new AgentPhotoRejectedException($"The photo couldn't be downloaded: {ex.Message}", ex);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new AgentPhotoRejectedException("The photo took too long to download.", ex);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                throw new AgentPhotoRejectedException($"The photo host answered {(int)response.StatusCode}.");
            }

            if (response.Content.Headers.ContentLength > ConversationService.MaxImageBytes)
            {
                throw new AgentPhotoRejectedException("The photo is over 5 MB.");
            }

            byte[] bytes = await ReadCappedAsync(await response.Content.ReadAsStreamAsync(ct), ct);
            (string contentType, string extension) = Kind(bytes)
                ?? throw new AgentPhotoRejectedException("The photo isn't a JPEG or PNG.");
            string name = Path.GetFileNameWithoutExtension(uri.AbsolutePath);
            name = new string(name.Where(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_').Take(80).ToArray());
            return new OutgoingFile((name.Length == 0 ? "photo" : name) + extension, contentType, bytes.Length, () => new MemoryStream(bytes, writable: false));
        }
    }

    /// <summary>The handler: no redirects, and only public addresses connected to.</summary>
    public static SocketsHttpHandler Handler() => new()
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        ConnectTimeout = TimeSpan.FromSeconds(10),
        ConnectCallback = async (context, ct) =>
        {
            IPAddress[] addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, ct);
            IPAddress address = addresses.FirstOrDefault(IsPublic)
                ?? throw new HttpRequestException($"{context.DnsEndPoint.Host} isn't a public address.");
            Socket socket = new(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), ct);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        },
    };

    /// <summary>Not loopback, private, link-local (cloud metadata), carrier-grade NAT, multicast or unspecified.</summary>
    public static bool IsPublic(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any) || address.Equals(IPAddress.Broadcast))
        {
            return false;
        }

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            byte[] b = address.GetAddressBytes();
            return !(b[0] == 0 || b[0] == 10 || b[0] == 127
                || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
                || (b[0] == 192 && b[1] == 168)
                || (b[0] == 169 && b[1] == 254)
                || (b[0] == 100 && b[1] >= 64 && b[1] <= 127)
                || b[0] >= 224);
        }

        return !(address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6Multicast || address.IsIPv6UniqueLocal);
    }

    private static async Task<byte[]> ReadCappedAsync(Stream body, CancellationToken ct)
    {
        await using (body)
        {
            using MemoryStream copy = new();
            byte[] buffer = new byte[81920];
            int read;
            while ((read = await body.ReadAsync(buffer, ct)) > 0)
            {
                if (copy.Length + read > ConversationService.MaxImageBytes)
                {
                    throw new AgentPhotoRejectedException("The photo is over 5 MB.");
                }

                copy.Write(buffer, 0, read);
            }

            return copy.ToArray();
        }
    }

    private static (string ContentType, string Extension)? Kind(byte[] bytes) =>
        bytes is [0xFF, 0xD8, 0xFF, ..] ? ("image/jpeg", ".jpg")
        : bytes is [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, ..] ? ("image/png", ".png")
        : null;
}

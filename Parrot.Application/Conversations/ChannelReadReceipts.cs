using Microsoft.Extensions.Options;
using Parrot.Application.Integrations;
using Parrot.Application.Logging;
using Parrot.Domain.Enums;

namespace Parrot.Application.Conversations;

/// <summary>
/// Tells the customer's channel that the business has read their messages, once an agent has read them
/// in Parrot: WhatsApp's blue ticks, "Seen" on Messenger and Instagram. A message is read to the customer
/// exactly when the agent has read it here, so a reply needs no separate handling: it can only be written in
/// an open thread, which has already been read.
///
/// Best effort and never throws: the agent's read must not wait on, or fail because of, a channel. A
/// receipt that does not go out is logged, and the customer simply sees no read. TikTok has no documented
/// call for this yet.
/// </summary>
public sealed class ChannelReadReceipts(
    IWhatsAppConnectionRepository whatsAppConnections, IWhatsAppSender whatsApp, IWhatsAppTokenProtector whatsAppProtector,
    IFacebookConnectionRepository facebookConnections, IFacebookSender facebook, IFacebookCredentialProtector facebookProtector,
    IInstagramConnectionRepository instagramConnections, IInstagramSender instagram, IInstagramCredentialProtector instagramProtector,
    IOptions<InboxSettings> settings, IAppLogger<ChannelReadReceipts> logger)
{
    // The agent's read is waiting on this, so it gets a short budget.
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(3);

    private const string WhatsAppIdPrefix = "whatsapp:";

    public async Task SendAsync(string owner, ReadAdvance read, CancellationToken ct)
    {
        if (!settings.Value.SendReadReceipts) return;

        using CancellationTokenSource budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(Budget);
        try
        {
            switch (read.Platform)
            {
                case MessagingPlatform.WhatsApp: await WhatsAppAsync(owner, read, budget.Token); break;
                case MessagingPlatform.Facebook: await FacebookAsync(owner, read, budget.Token); break;
                case MessagingPlatform.Instagram: await InstagramAsync(owner, read, budget.Token); break;
            }
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning("Read receipt not sent. Channel={Channel} ConnectionId={ConnectionId} Reason={Reason}",
                read.Platform, read.Connection, ex.Message);
        }
    }

    private async Task WhatsAppAsync(string owner, ReadAdvance read, CancellationToken ct)
    {
        Domain.Entities.WhatsAppConnection? connection = await whatsAppConnections.GetByOwnerAndIdAsync(owner, read.Connection);
        if (connection is null || connection.DisabledAt.HasValue || string.IsNullOrWhiteSpace(connection.AccessTokenEncrypted)
            || string.IsNullOrWhiteSpace(connection.PhoneNumberId)) return;

        // Stored as "whatsapp:{wamid}"; Meta knows it by the wamid.
        string wamid = read.MessageId.StartsWith(WhatsAppIdPrefix, StringComparison.Ordinal) ? read.MessageId[WhatsAppIdPrefix.Length..] : read.MessageId;
        await whatsApp.MarkReadAsync(connection.PhoneNumberId, whatsAppProtector.Unprotect(connection.AccessTokenEncrypted), wamid, ct);
    }

    private async Task FacebookAsync(string owner, ReadAdvance read, CancellationToken ct)
    {
        Domain.Entities.FacebookConnection? page = await facebookConnections.GetByOwnerAsync(owner, ct);
        if (page is null || page.Id != read.Connection || page.DisabledAt.HasValue || string.IsNullOrWhiteSpace(page.AccessTokenEncrypted)
            || string.IsNullOrWhiteSpace(page.PageId)) return;

        await facebook.MarkSeenAsync(page.PageId, facebookProtector.Unprotect(page.AccessTokenEncrypted), read.Sender, ct);
    }

    private async Task InstagramAsync(string owner, ReadAdvance read, CancellationToken ct)
    {
        Domain.Entities.InstagramConnection? account = await instagramConnections.GetByOwnerAsync(owner, ct);
        if (account is null || account.Id != read.Connection || account.DisabledAt.HasValue || string.IsNullOrWhiteSpace(account.AccessTokenEncrypted)
            || string.IsNullOrWhiteSpace(account.AccountId)) return;

        await instagram.MarkSeenAsync(account.AccountId, instagramProtector.Unprotect(account.AccessTokenEncrypted), read.Sender, ct);
    }
}

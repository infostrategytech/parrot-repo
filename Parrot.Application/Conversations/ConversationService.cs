using System.Diagnostics;
using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using Parrot.Application.Agents;
using Parrot.Application.Integrations;
using Parrot.Application.Logging;
using Parrot.Application.Messaging;
using Parrot.Application.Storage;
using Parrot.Domain.Entities;
using Parrot.Domain.Entities.Messages;
using Parrot.Domain.Enums;
using Parrot.Domain.Exceptions;

namespace Parrot.Application.Conversations;

public sealed class ConversationService(IConversationStore store, IWhatsAppConnectionRepository connections,
    IWhatsAppSender sender, IWhatsAppTokenProtector protector, IConversationSequence sequence,
    IFacebookConnectionRepository facebookConnections, IFacebookSender facebookSender,
    IFacebookCredentialProtector facebookProtector, IInstagramConnectionRepository instagramConnections,
    IInstagramSender instagramSender, IInstagramCredentialProtector instagramProtector, TikTokReplyService? tikTok = null,
    IDeliveryReceipts? receipts = null, IPrivateObjectStorage? mediaStorage = null,
    IOutboundJobPublisher? outboundJobs = null, IOptions<OutboundSettings>? outboundSettings = null,
    IHumanTakeoverStore? humanTakeover = null, IAppLogger<ConversationService>? logger = null,
    IMessagePersistedPublisher? publisher = null, ITenantIdLookup? tenantLookup = null)
{
    // A picture or file is queued for the send worker instead of being sent before the request answers.
    private bool DeferFileSends => outboundJobs is not null && outboundSettings?.Value.DeferFileSends == true;

    // Messenger's limit; WhatsApp's is 4096.
    private const int FacebookMaxText = 2000;

    // Instagram counts UTF-8 bytes, not characters.
    private const int InstagramMaxBytes = 1000;

    // A reply goes out on whichever channel the thread is on: a WhatsApp number, a Facebook Page,
    // TikTok or an Instagram account.
    public async Task<ConversationMessage> SendAsync(string owner, Guid connectionId, string recipient,
        SendReplyRequest request, CancellationToken ct)
    {
        ConversationMessage result = await SendCoreAsync(owner, connectionId, recipient, request, ct);

        // Best-effort: a human sent this reply, so mark the conversation as under human control.
        // TouchAsync preserves any existing ControlUntil so a timed takeover is not overwritten.
        if (humanTakeover is not null)
        {
            try { await humanTakeover.TouchAsync(owner, connectionId, recipient, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger?.LogWarning(ex, "human_takeover_touch_failed ConnectionId={ConnectionId} Sender={Sender}; continuing",
                    connectionId, recipient);
            }
        }
        await PublishTeammateReplyAsync(owner, connectionId, request.RequestId, result);
        return result;
    }

    // Omni-AI reads a teammate's reply as MessagePersisted(TenantToUser), so it knows what the business has told the
    // customer. One the channel refused never reached them, so it is not published.
    private Task PublishTeammateReplyAsync(string owner, Guid connectionId, Guid requestId, ConversationMessage sent) =>
        sent.Status == "failed"
            ? Task.CompletedTask
            // Not the request's token: the reply has gone, so the browser leaving must not keep it from the AI.
            // Kafka's own send timeout (seconds) bounds the wait.
            : PublishSentAsync(owner, connectionId, requestId, CommsType.TenantToUser, CancellationToken.None);

    // Called by the AI orchestrator. Sends as a human reply would, but does not set the human takeover
    // flag and explicitly publishes MessagePersisted with AgentToUser so Omni-AI sees its own send.
    public async Task<ConversationMessage> SendAgentReplyAsync(string owner, Guid connectionId, string recipient,
        SendReplyRequest request, CancellationToken ct)
    {
        ConversationMessage result = await SendCoreAsync(owner, connectionId, recipient, request, ct, isAiReply: true);
        await PublishSentAsync(owner, connectionId, request.RequestId, CommsType.AgentToUser, ct);
        return result;
    }

    /// <summary>
    /// A photo the AI sends after its text: stored and sent like a teammate's picture, marked as the
    /// AI's, and published as MessagePersisted(AgentToUser) so history has it. Messenger takes no
    /// caption with a file, so the caption is dropped there. Instagram and TikTok can't take files
    /// yet, so those refuse (ValidationException) and the caller skips the photo.
    /// </summary>
    public async Task<ConversationMessage> SendAgentFileAsync(string owner, Guid connectionId, string recipient,
        Guid requestId, string? caption, OutgoingFile file, CancellationToken ct)
    {
        ConversationMessage result = await SendFileCoreAsync(owner, connectionId, recipient, requestId, caption, file, ct, isAiReply: true);
        await PublishSentAsync(owner, connectionId, requestId, CommsType.AgentToUser, ct);
        return result;
    }

    // Omni-AI sees each reply through MessagePersisted: its own as AgentToUser, a teammate's as TenantToUser.
    // Best-effort: the send already happened, and the reply is in the inbox either way.
    private async Task PublishSentAsync(string owner, Guid connectionId, Guid requestId, CommsType commsType, CancellationToken ct)
    {
        if (publisher is not null)
        {
            try
            {
                MessageDocument? doc = await store.FindRequestAsync(owner, requestId, ct);
                if (doc?.ConversationKey is not null && doc.Sequence is not null)
                {
                    string? tenantId = tenantLookup is not null && doc.OwnerUserId is not null
                        ? await tenantLookup.FindOwnerIdAsync(doc.OwnerUserId, ct) ?? doc.OwnerUserId
                        : doc.OwnerUserId;
                    MessagePersisted persisted = new MessagePersisted
                    {
                        Source = doc.Platform,
                        Data = doc.ToStoredMessage() with { CommsType = commsType, TenantId = tenantId },
                        CorrelationId = InboundMessagePublisher.CurrentCorrelationId(),
                    };
                    await publisher.PublishAsync(persisted, ct);
                    logger?.LogInformation("reply.persisted_published CommsType={CommsType} MessageId={MessageId} ConnectionId={ConnectionId}",
                        commsType, doc.MessageId, connectionId);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                // Kafka down or slow: the AI misses this one reply; the customer and the inbox already have it.
                logger?.LogWarning(ex, "reply.persisted_failed CommsType={CommsType} ConnectionId={ConnectionId} RequestId={RequestId}; continuing",
                    commsType, connectionId, requestId);
            }
        }
    }

    private async Task<ConversationMessage> SendCoreAsync(string owner, Guid connectionId, string recipient,
        SendReplyRequest request, CancellationToken ct, bool isAiReply = false)
    {
        // A TikTok customer's id is not a phone number, so the reply is routed before WhatsApp's checks.
        if (tikTok is not null && await tikTok.FindConnectionAsync(owner, connectionId, ct) is { } tikTokConnection)
        {
            // TikTok's API has no way to quote a message, so the customer would not see what this answers.
            if (request.ReplyToMessageId is not null)
                throw new ValidationException("TikTok can't show which message you're replying to. Send it without the quote.");
            return await tikTok.SendAsync(tikTokConnection, recipient, request, ct);
        }

        if (request.RequestId == Guid.Empty || string.IsNullOrWhiteSpace(request.Text) || request.Text.Length > 4096
            || recipient.Length is < 1 or > 32 || !recipient.All(char.IsAsciiDigit))
            throw new ValidationException("Supply a request ID, a numeric recipient and text of 1–4096 characters.");

        WhatsAppConnection? whatsApp = await connections.GetByOwnerAndIdAsync(owner, connectionId);
        if (whatsApp is not null)
        {
            if (recipient.Length > 20)
                throw new ValidationException("Supply a request ID, a numeric recipient and text of 1–4096 characters.");
            return await SendWhatsAppAsync(owner, whatsApp, recipient, request, ct, isAiReply);
        }

        FacebookConnection? page = await facebookConnections.GetByOwnerAsync(owner, ct);
        if (page is not null && page.Id == connectionId)
        {
            if (request.Text.Length > FacebookMaxText)
                throw new ValidationException($"Messenger replies can be up to {FacebookMaxText} characters.");
            return await SendFacebookAsync(owner, page, recipient, request, ct, isAiReply);
        }

        InstagramConnection? account = await instagramConnections.GetByOwnerAsync(owner, ct);
        if (account is not null && account.Id == connectionId)
        {
            if (System.Text.Encoding.UTF8.GetByteCount(request.Text) > InstagramMaxBytes)
                throw new ValidationException("Instagram replies can be up to 1000 bytes, about 1000 letters or fewer with emoji.");
            return await SendInstagramAsync(owner, account, recipient, request, ct, isAiReply);
        }

        throw new NotFoundException("Connection not found.");
    }

    /// <summary>What a teammate may attach: the extension decides, never the name or type the browser sent.</summary>
    private static readonly IReadOnlyDictionary<string, (string Kind, string ContentType)> AllowedFiles =
        new Dictionary<string, (string, string)>(StringComparer.OrdinalIgnoreCase)
        {
            [".jpg"] = (MediaKinds.Image, "image/jpeg"), [".jpeg"] = (MediaKinds.Image, "image/jpeg"), [".png"] = (MediaKinds.Image, "image/png"),
            [".pdf"] = (MediaKinds.Document, "application/pdf"), [".txt"] = (MediaKinds.Document, "text/plain"),
            [".doc"] = (MediaKinds.Document, "application/msword"),
            [".docx"] = (MediaKinds.Document, "application/vnd.openxmlformats-officedocument.wordprocessingml.document"),
            [".xls"] = (MediaKinds.Document, "application/vnd.ms-excel"),
            [".xlsx"] = (MediaKinds.Document, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"),
            [".ppt"] = (MediaKinds.Document, "application/vnd.ms-powerpoint"),
            [".pptx"] = (MediaKinds.Document, "application/vnd.openxmlformats-officedocument.presentationml.presentation"),
        };

    public const long MaxDocumentBytes = 16 * 1024 * 1024;

    // WhatsApp's limit for a picture.
    public const long MaxImageBytes = 5 * 1024 * 1024;

    private const int MaxCaption = 1024;

    // A picture or file goes out on WhatsApp or Messenger. Instagram's API takes only a public address and TikTok's
    // no files at all, so those say so rather than failing at the channel.
    public async Task<ConversationMessage> SendFileAsync(string owner, Guid connectionId, string recipient,
        Guid requestId, string? caption, OutgoingFile file, CancellationToken ct, string? replyToMessageId = null)
    {
        ConversationMessage result = await SendFileCoreAsync(owner, connectionId, recipient, requestId, caption, file, ct, replyToMessageId);
        // A queued file is published as pending, as the AI's own files are: the send worker settles it later.
        await PublishTeammateReplyAsync(owner, connectionId, requestId, result);
        return result;
    }

    private async Task<ConversationMessage> SendFileCoreAsync(string owner, Guid connectionId, string recipient,
        Guid requestId, string? caption, OutgoingFile file, CancellationToken ct, string? replyToMessageId = null, bool isAiReply = false)
    {
        caption =string.IsNullOrWhiteSpace(caption) ? null : caption.Trim();
        if (requestId == Guid.Empty || recipient.Length is < 1 or > 32 || !recipient.All(char.IsAsciiDigit))
            throw new ValidationException("Supply a request ID, a numeric recipient and a file.");
        if (caption is { Length: > MaxCaption }) throw new ValidationException($"A caption can be up to {MaxCaption} characters.");
        if (mediaStorage is null) throw new ConflictException("Sending files is not set up on this server.");

        string extension = Path.GetExtension(file.FileName ?? "");
        if (!AllowedFiles.TryGetValue(extension, out (string Kind, string ContentType) allowed))
            throw new ValidationException("Attach a JPEG or PNG picture, or a PDF, Word, Excel, PowerPoint or text file.");
        long limit = allowed.Kind == MediaKinds.Image ? MaxImageBytes : MaxDocumentBytes;
        if (file.Length <= 0) throw new ValidationException("That file is empty.");
        if (file.Length > limit) throw new ValidationException($"Pictures can be up to {MaxImageBytes / 1024 / 1024} MB and other files up to {MaxDocumentBytes / 1024 / 1024} MB.");
        if (!LooksLike(file, extension)) throw new ValidationException("That file is not what its name says it is.");
        string fileName = new string(Path.GetFileName(file.FileName)!.Where(static c => !char.IsControl(c)).ToArray());
        if (fileName.Length > 120) fileName = fileName[^120..];
        OutgoingFile safe = file with { FileName = fileName, ContentType = allowed.ContentType };

        SendReplyRequest request = new(requestId, caption ?? "") { ReplyToMessageId = replyToMessageId };
        WhatsAppConnection? whatsApp = await connections.GetByOwnerAndIdAsync(owner, connectionId);
        if (whatsApp is not null)
        {
            if (recipient.Length > 20) throw new ValidationException("Supply a request ID, a numeric recipient and a file.");
            return await SendWhatsAppFileAsync(owner, whatsApp, recipient, request, caption, allowed.Kind, safe, ct, isAiReply);
        }

        FacebookConnection? page = await facebookConnections.GetByOwnerAsync(owner, ct);
        if (page is not null && page.Id == connectionId)
        {
            // The AI's photo follows its own text, so on Messenger its caption is simply left off.
            if (caption is not null && isAiReply)
            {
                caption = null;
                request = request with { Text = "" };
            }
            if (caption is not null) throw new ValidationException("Messenger cannot send text with a file. Send the file on its own, then your message.");
            return await SendFacebookFileAsync(owner, page, recipient, request, allowed.Kind, safe, ct, isAiReply);
        }

        if ((tikTok is not null && await tikTok.FindConnectionAsync(owner, connectionId, ct) is not null)
            || (await instagramConnections.GetByOwnerAsync(owner, ct))?.Id == connectionId)
            throw new ValidationException("Files cannot be sent on this channel yet.");
        throw new NotFoundException("Connection not found.");
    }

    // The first bytes say what the file is, for the kinds that have a signature.
    private static bool LooksLike(OutgoingFile file, string extension)
    {
        byte[] head = new byte[8];
        int read;
        using (Stream stream = file.Open()) read = stream.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);
        ReadOnlySpan<byte> bytes = head.AsSpan(0, read);
        return extension.ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" => bytes.StartsWith(new byte[] { 0xFF, 0xD8, 0xFF }),
            ".png" => bytes.StartsWith(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }),
            ".pdf" => bytes.StartsWith("%PDF-"u8),
            ".docx" or ".xlsx" or ".pptx" => bytes.StartsWith(new byte[] { 0x50, 0x4B, 0x03, 0x04 }),
            ".txt" => !bytes.Contains((byte)0),
            ".doc" or ".xls" or ".ppt" => bytes.StartsWith(new byte[] { 0xD0, 0xCF, 0x11, 0xE0 }),
            _ => true,
        };
    }

    // A picture or file takes longer than a line of text: the upload to the channel comes before the send.
    private static readonly TimeSpan MediaSendBudget = TimeSpan.FromSeconds(90);

    // Kept in private storage so the thread can show what was sent, under a key built from the request id alone.
    private static MessageAttachment AttachmentFor(string owner, Guid requestId, string kind, OutgoingFile file, string? caption)
    {
        string scope = new(owner.Where(static c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_').ToArray());
        return new MessageAttachment
        {
            Kind = kind, ContentType = file.ContentType, FileName = file.FileName, SizeBytes = file.Length, Caption = caption, Status = MediaStatus.Ready,
            StorageKey = $"business-media/{(scope.Length == 0 ? "unknown" : scope)}/{requestId:N}{Path.GetExtension(file.FileName).ToLowerInvariant()}",
        };
    }

    // False when the copy could not be saved: nothing has gone to the customer yet, so the send fails cleanly.
    private async Task<bool> KeepAsync(MessageAttachment attachment, OutgoingFile file, CancellationToken ct)
    {
        try
        {
            await using Stream stream = file.Open();
            await mediaStorage!.PutAsync(attachment.StorageKey!, stream, file.ContentType, ct);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return false;
        }
    }

    // A request id names one message: the same id with another file is a mistake, not a retry.
    private static ConversationMessage MatchFile(MessageDocument doc, Guid connection, string recipient, string text, OutgoingFile file)
    {
        MessageAttachment? kept = doc.Attachments?.FirstOrDefault();
        if (kept is null || kept.FileName != file.FileName || kept.SizeBytes != file.Length)
            throw new ConflictException("This request ID has already been used for a different message.");
        return Match(doc, connection, recipient, text);
    }

    // How long a reply may sit pending, with no job known to have been taken, before another request for it queues the job again.
    private static readonly TimeSpan RequeueAfter = TimeSpan.FromSeconds(30);

    // The same request id again. Normally the stored reply is simply returned; one still pending after a while had
    // its job lost (Kafka was down as it was queued, or the API stopped before it was), so it is queued again, which
    // is safe because the send worker claims a reply before it sends and only one claim wins.
    private async Task<ConversationMessage> ExistingFileAsync(MessageDocument existing, Guid connection, string recipient,
        string text, OutgoingFile file, CancellationToken ct)
    {
        ConversationMessage message = MatchFile(existing, connection, recipient, text, file);
        if (DeferFileSends && existing.SendStatus == "pending" && DateTime.UtcNow - existing.ReceivedAt > RequeueAfter)
        {
            try { await PublishJobAsync(existing, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException) { /* still pending; the next request tries again */ }
        }
        return message;
    }

    private Task PublishJobAsync(MessageDocument reply, CancellationToken ct) =>
        outboundJobs!.PublishAsync(
            new OutboundJob { MessageId = reply.MessageId, ThreadKey = OutboundJob.KeyFor(reply.ConnectionId!.Value, reply.From), EnqueuedAt = DateTime.UtcNow },
            ct);

    // Reserve, keep the file, queue the job, answer: the reply is pending and the send worker does the rest. Nothing
    // has gone to the customer by the time a step here fails, so each failure is a clean refusal.
    private async Task<ConversationMessage> QueueFileAsync(MessageDocument doc, SendReplyRequest request, MessageAttachment attachment,
        OutgoingFile file, CancellationToken ct)
    {
        if (!await store.ReserveAsync(doc, ct))
        {
            MessageDocument existing = await store.FindRequestAsync(doc.OwnerUserId!, request.RequestId, ct)
                ?? throw new ConflictException("The send request is being processed. Check again shortly.");
            return MatchFile(existing, doc.ConnectionId!.Value, doc.From, request.Text, file);
        }

        // Reserved, so the browser leaving must not stop the file being kept or the job being queued.
        using CancellationTokenSource budget = new(TimeSpan.FromSeconds(60));
        string? refused = null;
        if (!await KeepAsync(attachment, file, budget.Token)) refused = "storage";
        else
        {
            // Any failure, a timeout included: the job is not known to be queued, so the reply is refused.
            try { await PublishJobAsync(doc, budget.Token); }
            catch (Exception) { refused = "queue"; }
        }

        if (refused is not null)
        {
            using CancellationTokenSource persist = new(TimeSpan.FromSeconds(10));
            // Only if still pending: a job that did reach Kafka may already be with the send worker.
            if (await store.FailPendingAsync(doc.MessageId, persist.Token))
            {
                doc.SendStatus = "failed";
                MessageMetrics.Sent.Add(1, new KeyValuePair<string, object?>("status", "failed"), new KeyValuePair<string, object?>("meta_error_code", refused));
            }
        }
        return ToMessage(doc);
    }

    private async Task<ConversationMessage> SendWhatsAppFileAsync(string owner, WhatsAppConnection connection, string recipient,
        SendReplyRequest request, string? caption, string kind, OutgoingFile file, CancellationToken ct, bool isAiReply = false)
    {
        if (connection.DisabledAt.HasValue || connection.WebhookVerifiedAt is null)
            throw new ConflictException("This connection is disabled or unverified.");

        MessageDocument? existing = await store.FindRequestAsync(owner, request.RequestId, ct);
        if (existing is not null) return await ExistingFileAsync(existing, connection.Id, recipient, request.Text, file, ct);
        MessageReplyTo? quote = await QuoteAsync(owner, connection.Id, recipient, request.ReplyToMessageId, ct);

        if (string.IsNullOrWhiteSpace(connection.AccessTokenEncrypted)
            || string.IsNullOrWhiteSpace(connection.PhoneNumberId) || string.IsNullOrWhiteSpace(connection.WhatsAppBusinessAccountId))
            throw new ConflictException("Save the account IDs and access token in this WhatsApp connection's setup before sending.");
        string accessToken = Unprotect(() => protector.Unprotect(connection.AccessTokenEncrypted));
        (MessageDocument latest, string? conversationKey, long? replySequence) = await PrepareAsync(owner, connection.Id, recipient, ct);
        MessageAttachment attachment = AttachmentFor(owner, request.RequestId, kind, file, caption);

        DateTime now = DateTime.UtcNow;
        WhatsAppMessageDocument doc = new WhatsAppMessageDocument
        {
            MessageId = RequestKey(owner, request.RequestId), OwnerUserId = owner, ConnectionId = connection.Id,
            Platform = MessagingPlatform.WhatsApp, ExternalAccountId = connection.WhatsAppBusinessAccountId!,
            ChannelId = connection.PhoneNumberId!, ChannelDisplayName = connection.DisplayPhoneNumber ?? "",
            From = recipient, Timestamp = new DateTimeOffset(now).ToUnixTimeSeconds().ToString(), SentAt = now,
            Type = kind, TextBody = caption, Attachments = [attachment], ReceivedAt = now, StoredAt = now,
            Outgoing = true, SendStatus = "pending", ReplyTo = quote, IsAiReply = isAiReply ? true : null,
            ConversationKey = conversationKey, Sequence = replySequence, Receiver = latest.Receiver,
        };

        if (DeferFileSends) return await QueueFileAsync(doc, request, attachment, file, ct);

        return await ReserveAndSendAsync(owner, doc, request, latest, async timeout =>
        {
            try
            {
                // After the reserve, so a lost race or a refused request never leaves a stored file behind.
                if (!await KeepAsync(attachment, file, timeout)) return (null, "storage");
                await using Stream content = file.Open();
                return (quote is null
                    ? await sender.SendMediaAsync(connection.PhoneNumberId!, accessToken, recipient, kind, file.ContentType, file.FileName, content, caption, timeout)
                    : await sender.SendMediaReplyAsync(connection.PhoneNumberId!, accessToken, recipient, kind, file.ContentType, file.FileName, content, caption, quote.ProviderMessageId, timeout), null);
            }
            catch (WhatsAppRejectedException ex)
            {
                return (null, ex.MetaErrorCode ?? "unknown");
            }
        }, ct, MediaSendBudget);
    }

    private async Task<ConversationMessage> SendFacebookFileAsync(string owner, FacebookConnection page, string recipient,
        SendReplyRequest request, string kind, OutgoingFile file, CancellationToken ct, bool isAiReply = false)
    {
        if (page.DisabledAt.HasValue || page.WebhookVerifiedAt is null)
            throw new ConflictException("This connection is disabled or unverified.");

        MessageDocument? existing = await store.FindRequestAsync(owner, request.RequestId, ct);
        if (existing is not null) return await ExistingFileAsync(existing, page.Id, recipient, request.Text, file, ct);
        MessageReplyTo? quote = await QuoteAsync(owner, page.Id, recipient, request.ReplyToMessageId, ct);

        if (string.IsNullOrWhiteSpace(page.AccessTokenEncrypted) || string.IsNullOrWhiteSpace(page.PageId))
            throw new ConflictException("Save the Page and its access token in this Facebook connection's setup before sending.");
        string pageToken = Unprotect(() => facebookProtector.Unprotect(page.AccessTokenEncrypted));
        (MessageDocument latest, string? conversationKey, long? replySequence) = await PrepareAsync(owner, page.Id, recipient, ct);
        MessageAttachment attachment = AttachmentFor(owner, request.RequestId, kind, file, null);

        DateTime now = DateTime.UtcNow;
        FacebookMessageDocument doc = new FacebookMessageDocument
        {
            MessageId = RequestKey(owner, request.RequestId), OwnerUserId = owner, ConnectionId = page.Id,
            Platform = MessagingPlatform.Facebook, ExternalAccountId = page.PageId!,
            ChannelId = page.PageId!, ChannelDisplayName = page.PageName ?? "",
            From = recipient, Timestamp = new DateTimeOffset(now).ToUnixTimeMilliseconds().ToString(), SentAt = now,
            Type = kind, Attachments = [attachment], ReceivedAt = now, StoredAt = now,
            Outgoing = true, SendStatus = "pending", ReplyTo = quote, IsAiReply = isAiReply ? true : null,
            ConversationKey = conversationKey, Sequence = replySequence, Receiver = latest.Receiver ?? page.PageId,
        };

        if (DeferFileSends) return await QueueFileAsync(doc, request, attachment, file, ct);

        return await ReserveAndSendAsync(owner, doc, request, latest, async timeout =>
        {
            try
            {
                if (!await KeepAsync(attachment, file, timeout)) return (null, "storage");
                await using Stream content = file.Open();
                return (quote is null
                    ? await facebookSender.SendMediaAsync(page.PageId!, pageToken, recipient, kind, file.ContentType, file.FileName, content, timeout)
                    : await facebookSender.SendMediaReplyAsync(page.PageId!, pageToken, recipient, kind, file.ContentType, file.FileName, content, quote.ProviderMessageId, timeout), null);
            }
            catch (FacebookRejectedException ex)
            {
                return (null, ex.MetaErrorCode ?? "unknown");
            }
        }, ct, MediaSendBudget);
    }

    private async Task<ConversationMessage> SendWhatsAppAsync(string owner, WhatsAppConnection connection, string recipient,
        SendReplyRequest request, CancellationToken ct, bool isAiReply = false)
    {
        if (connection.DisabledAt.HasValue || connection.WebhookVerifiedAt is null)
            throw new ConflictException("This connection is disabled or unverified.");

        MessageDocument? existing = await store.FindRequestAsync(owner, request.RequestId, ct);
        if (existing is not null) return Match(existing, connection.Id, recipient, request.Text);
        MessageReplyTo? quote = await QuoteAsync(owner, connection.Id, recipient, request.ReplyToMessageId, ct);

        if (string.IsNullOrWhiteSpace(connection.AccessTokenEncrypted)
            || string.IsNullOrWhiteSpace(connection.PhoneNumberId) || string.IsNullOrWhiteSpace(connection.WhatsAppBusinessAccountId))
            throw new ConflictException("Save the account IDs and access token in this WhatsApp connection's setup before sending.");
        string accessToken = Unprotect(() => protector.Unprotect(connection.AccessTokenEncrypted));
        (MessageDocument latest, string? conversationKey, long? replySequence) = await PrepareAsync(owner, connection.Id, recipient, ct);

        DateTime now = DateTime.UtcNow;
        WhatsAppMessageDocument doc = new WhatsAppMessageDocument
        {
            MessageId = RequestKey(owner, request.RequestId), OwnerUserId = owner, ConnectionId = connection.Id,
            Platform = MessagingPlatform.WhatsApp, ExternalAccountId = connection.WhatsAppBusinessAccountId!,
            ChannelId = connection.PhoneNumberId!, ChannelDisplayName = connection.DisplayPhoneNumber ?? "",
            // Outgoing replies never pass through a webhook, so SentAt is our own clock.
            From = recipient, Timestamp = new DateTimeOffset(now).ToUnixTimeSeconds().ToString(), SentAt = now,
            Type = "text", TextBody = request.Text, ReceivedAt = now, StoredAt = now,
            Outgoing = true, SendStatus = "pending", ReplyTo = quote,
            ConversationKey = conversationKey, Sequence = replySequence, Receiver = latest.Receiver,
            IsAiReply = isAiReply ? true : null,
        };

        return await ReserveAndSendAsync(owner, doc, request, latest, async timeout =>
        {
            try
            {
                return (quote is null
                    ? await sender.SendAsync(connection.PhoneNumberId!, accessToken, recipient, request.Text, timeout)
                    : await sender.SendReplyAsync(connection.PhoneNumberId!, accessToken, recipient, request.Text, quote.ProviderMessageId, timeout), null);
            }
            catch (WhatsAppRejectedException ex)
            {
                return (null, ex.MetaErrorCode ?? "unknown");
            }
        }, ct);
    }

    private async Task<ConversationMessage> SendFacebookAsync(string owner, FacebookConnection page, string recipient,
        SendReplyRequest request, CancellationToken ct, bool isAiReply = false)
    {
        if (page.DisabledAt.HasValue || page.WebhookVerifiedAt is null)
            throw new ConflictException("This connection is disabled or unverified.");

        MessageDocument? existing = await store.FindRequestAsync(owner, request.RequestId, ct);
        if (existing is not null) return Match(existing, page.Id, recipient, request.Text);
        MessageReplyTo? quote = await QuoteAsync(owner, page.Id, recipient, request.ReplyToMessageId, ct);

        if (string.IsNullOrWhiteSpace(page.AccessTokenEncrypted) || string.IsNullOrWhiteSpace(page.PageId))
            throw new ConflictException("Save the Page and its access token in this Facebook connection's setup before sending.");
        string pageToken = Unprotect(() => facebookProtector.Unprotect(page.AccessTokenEncrypted));
        (MessageDocument latest, string? conversationKey, long? replySequence) = await PrepareAsync(owner, page.Id, recipient, ct);

        DateTime now = DateTime.UtcNow;
        FacebookMessageDocument doc = new FacebookMessageDocument
        {
            MessageId = RequestKey(owner, request.RequestId), OwnerUserId = owner, ConnectionId = page.Id,
            Platform = MessagingPlatform.Facebook, ExternalAccountId = page.PageId!,
            ChannelId = page.PageId!, ChannelDisplayName = page.PageName ?? "",
            // From is the customer for both directions, as the inbox groups by it. Messenger counts in milliseconds.
            From = recipient, Timestamp = new DateTimeOffset(now).ToUnixTimeMilliseconds().ToString(), SentAt = now,
            Type = "text", TextBody = request.Text, ReceivedAt = now, StoredAt = now,
            Outgoing = true, SendStatus = "pending", ReplyTo = quote,
            ConversationKey = conversationKey, Sequence = replySequence, Receiver = latest.Receiver ?? page.PageId,
            IsAiReply = isAiReply ? true : null,
        };

        return await ReserveAndSendAsync(owner, doc, request, latest, async timeout =>
        {
            try
            {
                return (quote is null
                    ? await facebookSender.SendAsync(page.PageId!, pageToken, recipient, request.Text, timeout)
                    : await facebookSender.SendReplyAsync(page.PageId!, pageToken, recipient, request.Text, quote.ProviderMessageId, timeout), null);
            }
            catch (FacebookRejectedException ex)
            {
                return (null, ex.MetaErrorCode ?? "unknown");
            }
        }, ct);
    }

    private async Task<ConversationMessage> SendInstagramAsync(string owner, InstagramConnection account, string recipient,
        SendReplyRequest request, CancellationToken ct, bool isAiReply = false)
    {
        if (account.DisabledAt.HasValue || account.WebhookVerifiedAt is null)
            throw new ConflictException("This connection is disabled or unverified.");

        MessageDocument? existing = await store.FindRequestAsync(owner, request.RequestId, ct);
        if (existing is not null) return Match(existing, account.Id, recipient, request.Text);
        MessageReplyTo? quote = await QuoteAsync(owner, account.Id, recipient, request.ReplyToMessageId, ct);

        if (string.IsNullOrWhiteSpace(account.AccessTokenEncrypted) || string.IsNullOrWhiteSpace(account.AccountId))
            throw new ConflictException("Save the access token in this Instagram connection's setup before sending.");
        string accessToken = Unprotect(() => instagramProtector.Unprotect(account.AccessTokenEncrypted));
        (MessageDocument latest, string? conversationKey, long? replySequence) = await PrepareAsync(owner, account.Id, recipient, ct);

        DateTime now = DateTime.UtcNow;
        InstagramMessageDocument doc = new InstagramMessageDocument
        {
            MessageId = RequestKey(owner, request.RequestId), OwnerUserId = owner, ConnectionId = account.Id,
            Platform = MessagingPlatform.Instagram, ExternalAccountId = account.AccountId!,
            ChannelId = account.AccountId!, ChannelDisplayName = account.Username ?? "",
            // From is the customer for both directions, as the inbox groups by it. Instagram counts in milliseconds.
            From = recipient, Timestamp = new DateTimeOffset(now).ToUnixTimeMilliseconds().ToString(), SentAt = now,
            Type = "text", TextBody = request.Text, ReceivedAt = now, StoredAt = now,
            Outgoing = true, SendStatus = "pending", ReplyTo = quote,
            ConversationKey = conversationKey, Sequence = replySequence, Receiver = latest.Receiver ?? account.AccountId,
            IsAiReply = isAiReply ? true : null,
        };

        return await ReserveAndSendAsync(owner, doc, request, latest, async timeout =>
        {
            try
            {
                return (quote is null
                    ? await instagramSender.SendAsync(account.AccountId!, accessToken, recipient, request.Text, timeout)
                    : await instagramSender.SendReplyAsync(account.AccountId!, accessToken, recipient, request.Text, quote.ProviderMessageId, timeout), null);
            }
            catch (InstagramRejectedException ex)
            {
                // 190: the token itself is no good (revoked, or it lapsed). The Channels card asks for a new one.
                if (ex.MetaErrorCode == "190")
                    await instagramConnections.MarkTokenRejectedAsync(account.Id, account.AccessTokenEncrypted!, DateTime.UtcNow, timeout);
                return (null, ex.MetaErrorCode ?? "unknown");
            }
        }, ct);
    }

    // The message a reply quotes: one of this conversation's, which the channel knows by an id. A reply of ours
    // that never reached the channel has none, so it cannot be quoted.
    private async Task<MessageReplyTo?> QuoteAsync(string owner, Guid connection, string recipient, string? replyToMessageId, CancellationToken ct)
    {
        if (replyToMessageId is null) return null;
        if (string.IsNullOrWhiteSpace(replyToMessageId) || replyToMessageId.Length > 512)
            throw new ValidationException("Choose the message you're replying to again.");
        MessageDocument quoted = await store.FindMessageAsync(owner, connection, recipient, replyToMessageId, ct)
            ?? throw new NotFoundException("The message you're replying to isn't in this conversation.");
        string providerId = MessageReplies.ProviderIdOf(quoted)
            ?? throw new ConflictException("That message didn't reach the customer, so it can't be quoted. Send your reply without the quote.");
        return MessageReplies.Quote(quoted, providerId);
    }

    // The customer's latest message: it must be within 24 hours (WhatsApp's and Messenger's rule
    // for a free-form reply), and the reply joins its conversation. The reply takes that message's
    // key and business id rather than rebuilding them from the connection, whose number was typed
    // in at setup and may be written differently. A message stored before the worker has no key;
    // the reply then has none either.
    private async Task<(MessageDocument Latest, string? ConversationKey, long? Sequence)> PrepareAsync(
        string owner, Guid connectionId, string recipient, CancellationToken ct)
    {
        MessageDocument? latest = await store.LatestIncomingAsync(owner, connectionId, recipient, ct);

        // SentAt is absent on documents written before it existed; ReceivedAt is within seconds of it.
        if (latest is null || !ReplyWindow.IsOpen(latest.Platform, latest.SentAt ?? latest.ReceivedAt, DateTime.UtcNow))
            throw new ConflictException("Ask this customer to message your business first. Text replies require an incoming message within 24 hours.");

        string? conversationKey = latest.ConversationKey;
        long? replySequence = conversationKey is null ? null : await sequence.NextAsync(conversationKey, ct);
        return (latest, conversationKey, replySequence);
    }

    // Reserved before the provider is called, so concurrent requests with one request id send once.
    private async Task<ConversationMessage> ReserveAndSendAsync(string owner, MessageDocument doc, SendReplyRequest request,
        MessageDocument latest, Func<CancellationToken, Task<(string? ProviderId, string? RejectedCode)>> send,
        CancellationToken ct, TimeSpan? budget = null)
    {
        if (!await store.ReserveAsync(doc, ct))
        {
            MessageDocument existing = await store.FindRequestAsync(owner, request.RequestId, ct)
                ?? throw new ConflictException("The send request is being processed. Check again shortly.");
            return Match(existing, doc.ConnectionId!.Value, doc.From, request.Text);
        }

        // Once reserved, browser disconnection must not cancel an already dispatched send.
        using CancellationTokenSource timeout = new CancellationTokenSource(budget ?? TimeSpan.FromSeconds(30));
        string status;
        string? providerId = null;
        string metaErrorCode = "none";
        try
        {
            (providerId, string? rejectedCode) = await send(timeout.Token);
            if (rejectedCode is null)
            {
                status = "accepted";

                // From the customer's message arriving to Meta accepting the reply.
                MessageMetrics.EndToEnd.Record(Math.Max(0, (DateTime.UtcNow - latest.ReceivedAt).TotalSeconds));
            }
            else
            {
                status = "failed";
                metaErrorCode = rejectedCode;
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or System.Text.Json.JsonException)
        {
            status = "unknown";
        }
        MessageMetrics.Sent.Add(
            1,
            new KeyValuePair<string, object?>("status", status),
            new KeyValuePair<string, object?>("meta_error_code", metaErrorCode));
        using CancellationTokenSource persistTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await store.CompleteAsync(doc.MessageId, status, providerId, persistTimeout.Token);
        doc.SendStatus = status;
        doc.ProviderMessageId = providerId;

        // The channel can report delivered before we had its id to find this reply by.
        if (receipts is not null && providerId is not null)
            await receipts.ApplyParkedAsync(doc.ConnectionId!.Value, providerId, persistTimeout.Token);
        return ToMessage(doc);
    }

    private static string Unprotect(Func<string> unprotect)
    {
        string token;
        try { token = unprotect(); }
        catch (CryptographicException)
        {
            throw new ConflictException("The saved access token cannot be read. Save a replacement token in this connection's setup.");
        }
        if (string.IsNullOrWhiteSpace(token))
            throw new ConflictException("Save a replacement access token in this connection's setup.");
        return token;
    }

    public static string RequestKey(string owner, Guid request) => $"outgoing:{owner}:{request:D}";
    public static ConversationMessage ToMessage(MessageDocument doc)
    {
        if (doc.Type == "system")
            return new ConversationMessage(doc.MessageId, doc.TextBody ?? "", doc.Outgoing, "received", doc.ReceivedAt) { Kind = "system" };

        return new ConversationMessage(doc.MessageId,
            doc.TextBody ?? (doc.Attachments is { Count: > 0 } ? "" : $"[{doc.Type} message]"), doc.Outgoing, DisplayStatus(doc), doc.ReceivedAt)
        {
            Kind = doc.IsAiReply == true ? "ai" : null,
            Attachments = doc.Attachments is { Count: > 0 } attachments ? [.. attachments.Select(ToMedia)] : null,
            ReplyTo = ToQuote(doc.ReplyTo),
        };
    }

    public static MessageQuote? ToQuote(MessageReplyTo? reply) =>
        reply is null ? null : new MessageQuote(reply.MessageId, reply.Outgoing, reply.Text, reply.Type);

    /// <summary>The unsigned form: a stored copy carries its key, a link its address. The API signs the key before it answers.</summary>
    public static MessageMedia ToMedia(MessageAttachment a) => new(a.Kind, a.ContentType, a.FileName, a.SizeBytes,
        a.Kind == MediaKinds.Link ? a.SourceUrl : null) { StorageKey = a.StorageKey, Status = MediaStatus.Of(a) };

    // What the customer's channel has told us about a reply, over our own send state. A reply that
    // failed stays failed.
    public static string DisplayStatus(MessageDocument doc) =>
        !doc.Outgoing || doc.SendStatus == "failed" ? doc.SendStatus
        : doc.SendStatus == "sending" ? "pending"
        : doc.ReadAt is not null ? "read"
        : doc.DeliveredAt is not null ? "delivered"
        : doc.SendStatus;

    internal static ConversationMessage Match(MessageDocument doc, Guid connection, string recipient, string text)
    {
        if (doc.ConnectionId != connection || doc.From != recipient || (doc.TextBody ?? "") != text)
            throw new ConflictException("This request ID has already been used for a different message.");
        return ToMessage(doc);
    }
}

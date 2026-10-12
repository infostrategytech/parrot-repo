# WhatsApp conversations: setup and audit

The Conversations page now reads persisted incoming WhatsApp messages and sends manual text replies through the REST API. It refreshes every five seconds, pages conversation lists and history, and separates incoming messages from outgoing replies. Placeholder AI responses, tasks, order statistics and account-merge controls are not displayed in this live view.

## Configure and run

The API and the message worker (`message-processor`) must use the **same MongoDB database and collection**. Configure these server environment variables (use the actual MongoDB hostname for your deployment):

```dotenv
MongoDB__ConnectionString=mongodb://mongodb:27017
MongoDB__Database=parrot
MongoDB__Collection=messages
WhatsApp__ApiVersion=v25.0
```

Each WhatsApp connection stores its own encrypted Meta access token. In **Channels → WhatsApp setup**, save the Business Account ID and Phone Number ID, then enter the Meta access token in the password field and click **Save access token**. Existing connections can use Back from the receiving step to return to configuration. Tokens can be replaced after messages have been received. Reads return only `HasAccessToken`, never the saved token or ciphertext. A saved token indicator does not verify Meta permissions or expiry.

Sending uses only the selected connection's token and phone ID. Global `WhatsApp__AccessToken`, `WhatsApp__PhoneNumberId`, and `WhatsApp__BusinessAccountId` values are no longer used for sending. Existing connections must save a token before replying. Tokens are protected with the existing ASP.NET Data Protection provider; retain the configured shared key ring and certificate across deployments/replicas.

Apply the PostgreSQL migration **AddWhatsAppConnectionAccessToken** before starting the updated API. It adds a nullable `AccessTokenEncrypted` column without modifying existing account IDs or messages. Rebuild/redeploy API, web, message worker, and gRPC together.

The owner-authenticated token endpoint is:

```http
PUT /api/whatsapp/connections/{connectionId}/access-token
Authorization: Bearer <Parrot JWT>
Content-Type: application/json

{"accessToken":"<Meta access token>"}
```

It returns 204 on success. Saving does not send a message or validate the credential against Meta. Incoming messages and sent/delivered/read notifications arrive through the webhook; tokens are not used to poll an inbox. Delivery status reconciliation remains unimplemented (status events are logged). A status webhook's message ID must match a send response's message ID to describe that send.

1. Save the actual account IDs in the existing WhatsApp setup flow. Verify the callback and subscribe to messages in Meta.
2. Send a message from your personal WhatsApp phone to the business number. The connection-scoped webhook URL must include its generated `ref` so the archive has `OwnerUserId` and `ConnectionId`.
3. Open `/dashboard/conversations` using the connection owner's login. A new conversation appears once the message worker stores the message and the inbox receives the push or polls.
4. Select the conversation, type a reply, and click Send. The recipient should receive it on WhatsApp.
5. Reply again from the phone; the incoming message appears in the same conversation.

Free-form replies require a recent incoming message within 24 hours. This implementation enforces that window and does not implement templates, outbound attachments, or starting new conversations.

Replies use the same endpoint for both channels: a thread on a WhatsApp number goes out through the WhatsApp Cloud API, a thread on a Facebook Page through Messenger's Send API (`POST /{page-id}/messages`, `messaging_type: RESPONSE`) with the Page access token saved in setup. Messenger has the same 24-hour window (outside it Meta answers error 10, subcode 2018278) and allows up to 2000 characters. Message tags such as `HUMAN_AGENT` are not implemented. Incoming non-text messages show a type placeholder. Meta may impose additional recipient/test-number restrictions.

`Accepted by WhatsApp` means Meta returned a message ID, not delivery/read confirmation. Delivery receipt reconciliation is not implemented; the pre-existing webhook logs statuses. Provider failures are displayed as failed/unknown, never represented as delivered.

## Webhook signatures: manual setup and Embedded Signup

Meta signs every webhook with the App Secret of the app that sends it (`X-Hub-Signature-256: sha256=<HMAC-SHA256 of the raw body>`). Parrot supports both ways a business connects:

| Setup | Whose Meta app | Secret used to check webhooks |
|---|---|---|
| **Manual** | The business's own app | That connection's App Secret, entered in Channels setup (App settings → Basic in Meta) and stored encrypted |
| **Embedded Signup** (Parrot's app) | Parrot's app | The app-level `WhatsApp__AppSecret` / `Facebook__AppSecret` |

A connection with its own App Secret is always checked with it; one without falls back to the app-level secret. Responses only report `hasAppSecret`; the secret is never returned or logged.

```http
PUT /api/whatsapp/connections/{connectionId}/app-secret
PUT /api/facebook/setup/app-secret
Content-Type: application/json

{"appSecret":"<32 hexadecimal characters>"}
```

`WhatsApp__SignatureMode` and `Facebook__SignatureMode` decide what a failed check does: `Off`, `LogOnly` (log `… signature check failed; accepted because SignatureMode is LogOnly` and accept), or `Enforce` (log and answer 401). WhatsApp starts at **LogOnly** so live connections keep working while they save their secret; set it to `Enforce` once no `Reason=no_app_secret` or `Reason=mismatch` warnings appear. Facebook stays at **Enforce**, as before.

## Instagram (manual setup)

Instagram DMs use the **Instagram API with Instagram Login** and the business's own Meta app. In Channels → Instagram:

1. In the Meta app, add **Instagram** and open **API setup with Instagram login**. Under **Configure webhooks**, paste Parrot's callback URL (`/api/webhooks/instagram?ref=…`) and verify token, select **Verify and save**, and subscribe to **messages**.
2. Under **Generate access tokens**, add the Instagram professional account and paste its token into Parrot, together with the **Instagram app secret** from the same page (**not** the App Secret under App settings → Basic; webhooks are signed with the Instagram one).

When the token is saved, Parrot calls `GET graph.instagram.com/{v}/me?fields=user_id,username` to read the account (webhooks carry `user_id` in `entry.id`, which differs from the id a token exchange shows) and `POST /me/subscribed_apps?subscribed_fields=messages`; nothing is saved unless both succeed. Then the setup's test message step waits for a real DM.

Webhooks have Messenger's shape (`object: "instagram"`); echoes of the business's own messages and read receipts are skipped. Conversations are keyed `instagram:{customer IGSID}:{account}`. Replies go to `POST graph.instagram.com/{v}/{account}/messages` with the account's token, within 24 hours of the customer's message (outside it Meta answers error 10, subcode 2534022), up to 1000 bytes of text.

```http
GET  /api/instagram/setup
PUT  /api/instagram/setup/account       {"accessToken":"…"}
PUT  /api/instagram/setup/app-secret    {"appSecret":"<32 hex>"}
POST /api/instagram/setup/disable
```

Settings: `Instagram__WebhookBaseUrl` (public HTTPS base, required), `Instagram__SignatureMode` (`Enforce` by default), `Instagram__ApiVersion` (`v25.0`), `Instagram__AppSecret` (only for connections through Parrot's own app later).

## Duplicate protection and failure behavior

- Incoming messages retain the existing unique Mongo `MessageId` index. Repeated webhook/Kafka deliveries insert one document. Cross-owner duplicate IDs are rejected.
- The frontend gives each send a UUID. The backend reserves a uniquely keyed outgoing document **before** calling Meta. Concurrent requests with the same user/request ID send once, even across API replicas.
- Reusing a request ID with a different connection, recipient, or body returns a conflict.
- An HTTP timeout in the browser keeps the same draft and request ID for Check / retry. It does not mint a new request ID for that retry.
- Meta/network timeouts and server failures are not retried automatically. They show `unknown`; a process crash or database failure after reservation can leave `pending`. The request remains reserved. Verify the recipient's phone before deliberately sending a new message.
- Polling, history pagination, and send responses replace existing UI rows by message ID rather than append duplicates.
- Identical text deliberately sent with two different request IDs represents two distinct messages. There is no text-based suppression, and no claim of end-to-end exactly-once delivery across Meta/network outages.

## API

All endpoints require a Parrot bearer JWT. Ownership comes from JWT claims, never a request body owner ID.

```http
GET /api/conversations?skip=0&take=50
GET /api/conversations/{connectionId}/messages?sender=2348012345678
GET /api/conversations/{connectionId}/messages?sender=2348012345678&before=<NextBefore>
POST /api/conversations/{connectionId}/messages?sender=2348012345678
Content-Type: application/json

{"requestId":"<new UUID for this send>","text":"Hello from Parrot"}
```

History pages contain at most 100 messages in chronological order and a `nextBefore` cursor. Outgoing records retain their stable application message ID; Meta's ID is stored separately as `ProviderMessageId`.

### Recovering missed live messages

Live pushes (`messagePersisted` on `/hubs/messaging`) are best-effort. A client that was offline, reconnected, or suspects a gap reads the conversation's stored messages after a sequence:

```http
GET /api/conversations/history?key=whatsapp:2348012345678:2349011112222&after=0&limit=50
```

```json
{ "messages": [ { "messageId": "whatsapp:wamid…", "conversationKey": "…", "sequence": 1, "platform": "whatsapp", "outgoing": false, "sender": "…", "receiver": "…", "text": "…", "persistedAt": "…" } ], "nextCursor": 1 }
```

- `key` is the push's `conversationKey`. It goes in the query string, not the path: it holds two phone numbers, and request logs print the path.
- Messages come oldest first, in the same shape as a push, with customer messages and business replies together. `limit` is capped at 100 (default 50). `nextCursor` is the `after` for the next page, or `null` on the last one.
- Only the signed-in user's messages are returned; someone else's conversation reads as empty.
- Sequences may skip numbers, and for a moment a later number can be stored before an earlier one (a reply and an incoming message landing together). Recover from a little below the highest sequence held, not from it exactly, and drop repeats by `messageId`.
- Messages stored before sequences existed, and replies to them, are not returned.

## Audit map

| File | Change |
|---|---|
| `Parrot.Api/Controllers/ConversationsController.cs` | Authenticated list, history and reply endpoints; ownership checks |
| `Parrot.Application/Integrations/WebhookSignature.cs` | Signature check and App Secret format, shared by WhatsApp and Facebook |
| `Parrot.Api/Controllers/MessageHistoryController.cs` | Owner-scoped recovery of messages after a sequence, in the live push's shape |
| `Parrot.Api/Program.cs` | Mongo, conversation service and WhatsApp HTTP client registrations |
| `Parrot.Application/Conversations/ConversationContracts.cs` | API models, repository/sender interfaces and provider rejection exception |
| `Parrot.Application/Conversations/ConversationService.cs` | Reply validation, connection token decryption, 24-hour window, atomic reservation and uncertain-outcome handling |
| `Parrot.Infrastructure/Conversations/MongoConversationStore.cs` | Scoped queries, pagination, grouping, unique reservation and send state persistence |
| `Parrot.Infrastructure/Conversations/WhatsAppSender.cs` | Server-to-Meta text send, no automatic retry |
| `Parrot.Domain/Entities/Messages/MessageDocument.cs` | Outgoing direction, send state and Meta message ID |
| `Parrot.Web/Parrot.Web/Services/ConversationClient.cs` | Authenticated REST calls |
| `Parrot.Web/Parrot.Web/Program.cs` | Client registration |
| `Parrot.Web/Parrot.Web/Parrot.Web.csproj` | Reference shared application contracts |
| `Parrot.Web/Parrot.Web/Components/Pages/Dashboard/Conversations/ConversationsState.cs` | Replace fixture data with real conversation state and deduplicating merges |
| `Parrot.Web/Parrot.Web/Components/Pages/Dashboard/Conversations/Conversation.cs` | Connection, history, send status and draft state |
| `Parrot.Web/Parrot.Web/Components/Pages/Dashboard/Conversations/Conversations.razor` | API loading, polling, pagination, selection and auth cleanup |
| `Parrot.Web/Parrot.Web/Components/Pages/Dashboard/Conversations/Conversations.razor.css` | Sidebar layout for pagination |
| `Parrot.Web/Parrot.Web/Components/Pages/Dashboard/Conversations/ConvChat.razor` | Real reply composer, stable retry IDs and send status display |
| `Parrot.Web/Parrot.Web/Components/Pages/Dashboard/Conversations/ConvChat.razor.css` | Send button, message wrapping and status styling |
| `docker-compose.local.yml` | API Mongo and server-only sending settings |
| `.env.docker.example` | Empty sending credential placeholders |
| `.gitignore` | Allow this audit guide despite the existing blanket Markdown ignore |
| `Test.Parrot.Api/Tests/Services/ConversationServiceTests.cs` | Concurrent retries, timeouts, persistence failures, ownership and configuration |
| `Test.Parrot.Api/Tests/Services/ConversationStateTests.cs` | Repeated polling and send response deduplication |
| `Test.Parrot.Api/Tests/Services/MongoConversationTests.cs` | Real Mongo concurrency, incoming deduplication, legacy documents, tenant isolation and pagination |
| `Test.Parrot.Api/Tests/Services/WhatsAppSenderTests.cs` | Meta HTTP request and outcome handling |
| `docs/CONVERSATIONS.md` | This setup/audit guide |

Webhook signature validation remains intentionally bypassed by the existing controller implementation. Tests cover processing accepted connections with missing or invalid signatures.

## Verification

```bash
dotnet build Backend.slnx --no-restore -m:1 -p:UseSharedCompilation=false -nodeReuse:false
dotnet test Test.Parrot.Api/Test.Parrot.Api.csproj --no-build --no-restore
```

Integration tests are skipped unless their service is set. Each creates and drops its own `parrot_test_*` database, so point them at the local compose stack, never a shared server:

```bash
PARROT_TEST_MONGO=mongodb://127.0.0.1:27018 \
PARROT_TEST_KAFKA=localhost:29092 \
PARROT_TEST_REDIS=localhost:16379 \
PARROT_TEST_POSTGRES='Host=localhost;Port=15432;Username=postgres;Password=parrot_dev_password' \
dotnet test Backend.slnx
```

Automated tests use a fake Meta HTTP endpoint and a real temporary MongoDB. They do not send messages to real phones. A real delivery check requires your server credentials and the phone steps above.

## AI orchestrator (Omni-AI) integration

How agents, their settings and their activity reach the AI orchestrator and come back (the `agent.events` topic, the internal resolve endpoint, the AI Operations API and the web screens) is in [AI_INTEGRATION.md](AI_INTEGRATION.md), with its own audit map.

# WhatsApp conversations: setup and audit

The Conversations page now reads persisted incoming WhatsApp messages and sends manual text replies through the REST API. It refreshes every five seconds, pages conversation lists and history, and separates incoming messages from outgoing replies. Placeholder AI responses, tasks, order statistics and account-merge controls are not displayed in this live view.

## Configure and run

The API and ingestor must use the **same MongoDB database and collection**. Configure these server environment variables (use the actual MongoDB hostname for your deployment):

```dotenv
MongoDB__ConnectionString=mongodb://mongodb:27017
MongoDB__Database=parrot
MongoDB__Collection=messages
WhatsApp__AccessToken=<Meta token authorized for this business number>
WhatsApp__PhoneNumberId=1075139625685928
WhatsApp__BusinessAccountId=1270095535252576
WhatsApp__ApiVersion=v25.0
```

Keep the token on the API server, not in browser configuration or git. This implementation deliberately supports the server-configured business credential only: it refuses to send for a connection whose saved phone/account IDs differ. Per-tenant credential onboarding is future work.

For the local Docker stack, set `WHATSAPP_ACCESS_TOKEN`, `WHATSAPP_PHONE_NUMBER_ID`, and `WHATSAPP_BUSINESS_ACCOUNT_ID` in `.env.docker`; see `.env.docker.example`. Mongo settings for the API are now supplied by `docker-compose.local.yml`.

Rebuild/redeploy API, web, ingestor, and gRPC together: they share the extended message document model. No PostgreSQL migration is required. Mongo indexes are created when repositories initialize. Existing incoming documents without the new fields remain readable.

1. Save the actual account IDs in the existing WhatsApp setup flow. Verify the callback and subscribe to messages in Meta.
2. Send a message from your personal WhatsApp phone to the business number. The connection-scoped webhook URL must include its generated `ref` so the archive has `OwnerUserId` and `ConnectionId`.
3. Open `/dashboard/conversations` using the connection owner's login. A new conversation appears after the ingestor persists the message and polling refreshes.
4. Select the conversation, type a reply, and click Send. The recipient should receive it on WhatsApp.
5. Reply again from the phone; the incoming message appears in the same conversation.

Free-form replies require a recent incoming message within 24 hours. This implementation enforces that window and does not implement templates, outbound attachments, or starting new conversations. Incoming non-text messages show a type placeholder. Meta may impose additional recipient/test-number restrictions.

`Accepted by WhatsApp` means Meta returned a message ID, not delivery/read confirmation. Delivery receipt reconciliation is not implemented; the pre-existing webhook logs statuses. Provider failures are displayed as failed/unknown, never represented as delivered.

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

## Audit map

| File | Change |
|---|---|
| `Parrot.Api/Controllers/ConversationsController.cs` | Authenticated list, history and reply endpoints; ownership checks |
| `Parrot.Api/Program.cs` | Mongo, conversation service and WhatsApp HTTP client registrations |
| `Parrot.Application/Conversations/ConversationContracts.cs` | API models, repository/sender interfaces and provider rejection exception |
| `Parrot.Application/Conversations/ConversationService.cs` | Reply validation, credential ownership, 24-hour window, atomic reservation and uncertain-outcome handling |
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

Pre-existing working-tree changes to `WhatsAppWebhookController.cs` and staged `docker-compose.yaml` were preserved, not edited by this implementation. In particular, the controller's disabled signature validation remains unchanged. The existing test `IncomingReference_WithInvalidSignature_IsRejected` fails because of that pre-existing edit. Reinstate signature validation before exposing real production traffic; synthetic curl requests then need a valid signature.

## Verification

```bash
dotnet build Backend.slnx --no-restore -m:1 -p:UseSharedCompilation=false -nodeReuse:false
dotnet test Test.Parrot.Api/Test.Parrot.Api.csproj --no-build --no-restore
```

The Mongo integration test is skipped unless `PARROT_TEST_MONGO` is set. Use a disposable local Mongo instance; the test creates and drops its own unique `parrot_test_*` database:

```bash
PARROT_TEST_MONGO=mongodb://127.0.0.1:27028 dotnet test Test.Parrot.Api/Test.Parrot.Api.csproj --no-build --no-restore
```

Automated tests use a fake Meta HTTP endpoint and a real temporary MongoDB. They do not send messages to real phones. A real delivery check requires your server credentials and the phone steps above.

Verification result: solution build succeeded with 0 errors (existing analyzer/dependency warnings remain). Full suite: 129 passed, 1 failed, 0 skipped. All 15 new test cases passed, including the real MongoDB test. The single failure is the pre-existing disabled-signature case identified above. The temporary MongoDB container was removed after testing.

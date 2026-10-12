# AI orchestrator (Omni-AI) integration: setup and audit

Parrot keeps the agents an owner sets up (name, look, channels, settings) and shows what they did. The orchestrator (Omni-AI, a separate service) does the answering: it reads every stored customer message from Kafka, runs the owner's live agent, and reports back on Kafka. The two never share a database. This guide covers what travels between them, how to run it, and what changed.

## Configure and run

The orchestrator shares the API's JWT settings (secret, issuer `ParrotApi`, audience `ParrotApp`), its Kafka and the owner ids. On the API and the web app:

```dotenv
OmniAi__Enabled=true
OmniAi__BaseUrl=http://omni-ai:8000
OmniAi__TimeoutSeconds=30        # API only; an approval decision resumes a whole turn
Kafka__AgentEventsTopic=agent.events             # default
Kafka__AgentEventsConsumerGroupId=parrot-agent-events   # default
```

`OmniAi__Enabled` does three things. On the API it allows calls out to the orchestrator (approval decisions, traces, the registry, test turns); off, those answer 503 and nothing is called. On the web app it switches the AI Agents screens from the session-only store and hidden figures to the API-backed store (`/api/agent*`), the AI Operations figures (`/api/ai-operations/*`) and a live switch that really turns an agent on. The events consumer runs whatever the flag says, so activity published while the flag was off is still kept.

The `kafka-topic-init` service creates `agent.events` with 6 partitions in both compose files (auto-create is off). The API reads it in its own consumer group from the beginning of the topic; the activity store's unique key makes a replay harmless.

Apply the PostgreSQL migration **AddAgentChannelsAndSettings** before starting the updated API. It adds `Channels` (text, comma-separated keys), `SettingsJson` (jsonb, `{}` for existing rows), `IsDraft`, `Version` (1 for existing rows) and `DeletedAt` to `Agents`; nothing existing is changed. Existing agents keep answering nothing until the owner chooses channels: an agent with no channels resolves for no channel.

MongoDB gets a collection `agent_activity` beside `messages`, with a unique index on `(OwnerUserId, Type, EventKey)` (the owner is part of the key, so one tenant's event can never stand in for another's), an index on `(OwnerUserId, OccurredAt desc)` and one on `(OwnerUserId, TurnId)`. The indexes are created on first use, so MongoDB being down at boot costs retries, not the API.

1. Start the stack with `OMNI_AI_ENABLED=true` and the orchestrator reachable at `OMNI_AI_BASE_URL`.
2. In AI Operations, set up an agent, choose WhatsApp, and turn it on. The API saves it and publishes `agent.config.changed`.
3. Send a message from a phone to the connected number. The orchestrator reads `messages.persisted`, asks `GET /api/internal/agents/resolve?channel=whatsapp` with a service token, answers through `POST /api/conversations/{connectionId}/messages`, and publishes `agent.turn.completed`.
4. The API stores the event and pushes `agentActivity` to the owner's open tabs; the agent's page shows the conversation under Today and Recent conversations.
5. When the agent asks for an approval, it appears under what is waiting; `POST /api/ai-operations/approvals/{id}` forwards the decision and the turn resumes.

## Events: `agent.events`

Keyed by tenant (the owner's user id), so one owner's events stay in order. JSON with snake_case names and an ISO 8601 instant with an offset: the orchestrator's own shape, which Parrot writes too (the bus options write camelCase, so every name is spelled out in `AgentConfigChangedEvent`).

```json
{ "type": "agent.turn.completed", "tenant_id": "<owner user id>", "conversation_key": "whatsapp:2348...:2349...",
  "turn_id": "turn_...", "occurred_at": "2026-10-08T12:00:00.000000+00:00", "correlation_id": "<trace id>",
  "schema_version": 1, "payload": { } }
```

| Type | Published by | Payload | Kept as |
|---|---|---|---|
| `agent.turn.completed` | orchestrator | `status` (completed, escalated, awaiting_approval, failed, skipped), `decision` (PERMIT, DENY, REQUIRE_APPROVAL, REQUIRE_FACT, NO_POLICY, ESCALATE or null), `confidence`, `reply`, `delivered`, `channel`, `customer_ref`, `customer_name`, `route`, `escalation_id`, `approval_id`, `cost_usd`, `latency_ms`, `message_id`, `agent_id`, `intent` | one document per turn **and status**: a turn that waited for an approval reports twice, and the later report says how it ended |
| `agent.approval.requested` | orchestrator | `approval_id`, `tool`, `args`, `reason`, `role`, `timeout_minutes`, `customer_ref` | one document per approval id |
| `agent.escalation.opened` | orchestrator | `escalation_id`, `priority` (P1, P2, P3), `problem`, `customer_ref`, `channel`, `external_ref`, `approval_id`, `confidence` | one document per escalation id |
| `agent.config.changed` | Parrot API | `agent_id`, `version`, `channels`, `is_live`, `deleted` | not kept; the orchestrator drops its cached configuration for the tenant |

The API stores the three orchestrator types as they arrive (the columns above plus the raw payload) and skips anything else with a log line rather than retrying it, so one unreadable event never holds up a partition. An insert that fails (MongoDB down) is thrown, so the bus redelivers it. Only the first copy of an event is pushed to the owner's live connections, as `agentActivity` on `/hubs/messaging`, in the same shape as the record's columns (camelCase: `type`, `turnId`, `conversationKey`, `agentId`, `status`, `decision`, `confidence`, `channel`, `customerRef`, `customerName`, `reply`, `approvalId`, `escalationId`, `priority`, `problem`, `tool`, `reason`, `occurredAt`). The push is best effort, like `messagePersisted`; the screens read the store again on it rather than apply it.

`agent.config.changed` is published after every create, update, live switch and delete. It is best effort: the row is saved first, and the orchestrator reads the row (through the resolve endpoint) on its next cache miss, so a missed event costs at most one cache lifetime of the old settings.

## Service tokens

Both sides mint HS256 JWTs with the shared `JwtSettings`. Claims: `sub` = owner user id, `tenant_id` = owner user id, `scope` = space-separated scopes, `svc` = who minted it (`omni-ai` or `parrot-api`), `jti`, `iat` (a number; the orchestrator refuses a token without it), `exp` five minutes out. The API's `ServiceTokenFactory` mints them with `scope = "agent:service"`; the orchestrator's carry `agent:service catalog:ai-tools`.

The API's `ServiceScope` authorization policy requires the `scope` (or `scp`) claim to contain `agent:service`; a session token carries only the catalog owner scopes and is refused. Only `/api/internal/*` uses it; every other endpoint keeps its plain `[Authorize]`, which a service token also satisfies because its `sub` is the owner.

## API

### For the orchestrator (service token)

```http
GET /api/internal/agents/resolve?channel=whatsapp
Authorization: Bearer <service JWT with scope agent:service>
```

```json
{ "agentId": "0f1e…", "name": "Nova", "isLive": true, "channels": ["whatsapp", "instagram"],
  "settings": { "role": "Support", "customRole": "", "handoverTo": "", "canOfferDiscounts": false, "canApproveRefunds": false,
                "tone": "Warm", "length": "Balanced", "emoji": "Light", "greeting": "", "avoidPhrases": [],
                "canRecommend": true, "canCheckOrders": false, "canTakeDetails": true, "handOverWhenUpset": true,
                "handOverAfterMisses": true, "handOverBigOrders": false, "bigOrderNaira": 50000, "notes": "", "leftOut": [] },
  "version": 4, "businessName": "Ada's Kitchen", "ownerUserId": "<owner user id>" }
```

The tenant comes from the token's `tenant_id` (or `sub`), never the query. 404 (with the usual error envelope) means no turned-on agent answers that channel, which the orchestrator caches as "no agent". The names with a fixed set of values (`role`, `tone`, `length`, `emoji`, `leftOut`) are the screens' enum names; the orchestrator lower-cases them.

### For the owner (session token)

```http
PUT    /api/agent/{id}                 {"name","description","mascotUrl","supportsHumanTakeover","channels":["whatsapp"],"settings":{…}}
PATCH  /api/agent/{id}/live            {"on": true}
DELETE /api/agent/{id}                 → 204
```

`POST /api/agent` takes the same body plus `isDraft`; a draft is saved off. `PUT` never switches an agent (its `isDraft` is ignored); `PATCH …/live` does, and ends the draft either way. Every response is the agent: `{id, name, description, mascotUrl, supportsHumanTakeover, isActive, channels, settings, isDraft, version, createdAt, updatedAt}`. Delete marks the row; the agent disappears from every list and resolves for no channel.

```http
GET  /api/ai-operations/today?agentId=&channel=     → {"conversations":3,"answeredAlone":2,"passedToTeam":1,"tasksCreated":1,"teamReplyMinutes":20}
GET  /api/ai-operations/week?agentId=&channel=      → [1,0,0,0,2,0,1]
GET  /api/ai-operations/recent?agentId=&channel=&limit=20
     → [{"customer":"Ada","channel":"whatsapp","outcome":"Answered","task":null,"minutesAgo":5,"turnId":"turn_…","conversationKey":"whatsapp:…"}]
GET  /api/ai-operations/waiting
     → [{"agentName":"Nova","customer":"Ada","question":"Refund above the limit","approvalId":"apr_…","escalationId":null,"priority":null,"occurredAt":"2026-10-08T12:00:00Z"}]
POST /api/ai-operations/approvals/{id}              {"decision":"approved","instruction":"Refund in full"}
     → {"approvalId":"apr_…","decision":"approved","turnId":"turn_…","status":"completed","reply":"Your refund is on its way."}
```

- Days are UTC days. A turn counts once, by the last status it reported; `skipped` turns are not activity. `conversations` is distinct conversation keys; `answeredAlone` is turns that `completed`; `passedToTeam` is turns that ended `escalated`, `awaiting_approval` or `failed`; `tasksCreated` is turns that opened an escalation; `teamReplyMinutes` averages how long the approvals and escalations decided today waited (null when none). `week` is seven days, oldest first, today last.
- `outcome` is `Answered`, `TaskCreated` (an escalation was opened; `task` is its problem) or `PassedToTeam`. `limit` is capped at 100.
- `waiting` lists approvals and escalations nobody has decided, oldest first; an escalation opened because an approval timed out is shown once, as the approval. The agent's name comes from the turn that asked.
- Deciding forwards to the orchestrator first (`POST {OmniAi:BaseUrl}/api/v1/agent/approvals/{id}` with `{"decision","decided_by","instruction"}`), then marks the approval and any escalation opened for it decided. An approval that is not the owner's, or was decided already, is 404 and nothing is forwarded. `decision` must be `approved` or `denied`. The orchestrator being unreachable is a 503 and the approval stays waiting; its error bodies never reach the client.

`IAgentOrchestratorClient` also reaches `GET /api/v1/agent/traces/{traceId}`, `GET /api/v1/agent/registry` and `POST /api/v1/agent/turn` (a `ChannelMessage` on the `web` channel, for test chats); no owner endpoint exposes them yet.

## Web

With `OmniAi:Enabled`, `ApiAgentStore` keeps agents through the API (mascot name to image and back, channels to keys, settings' enums to names), `ApiAiOperationsFeed` holds the figures per circuit (a figure asked for the first time is loaded in the background and `Changed` says when it is there; every figure is read again on each `agentActivity` push) and `OmniAiAgentCapabilities` lets agents be turned on. `AiAgentsState` loads the feed once the agents are read, starts the hub connection, and raises `Changed` for both, which the AI Operations and agent pages draw again on. Playbooks and note files are still frontend-only.

## Audit map

| File | Change |
|---|---|
| `Parrot.Domain/Entities/Agent.cs` | Channels, SettingsJson, IsDraft, Version, DeletedAt |
| `Parrot.Infrastructure/Data/ApplicationDbContext.cs` | Channels as a comma-separated text column with a converter and comparer; SettingsJson as jsonb; Version default |
| `Parrot.Infrastructure/Migrations/20261008134150_AddAgentChannelsAndSettings.cs` (+ Designer, snapshot) | The five columns; `{}` for existing settings |
| `Parrot.Infrastructure/Agents/AgentRepository.cs` | Update, live-for-channel lookup, deleted rows never returned, inactive agents listed |
| `Parrot.Application/DTOs/Agents/AgentSettingsDto.cs` | The settings shape shared by the screens, the column and the orchestrator |
| `Parrot.Application/DTOs/Agents/AgentInfo.cs`, `AgentResponse.cs`, `AgentLiveRequest.cs`, `AgentResolution.cs` | Channels, settings, draft and version on the wire; the live switch body; the resolve answer |
| `Parrot.Application/Agents/AgentSettingsJson.cs` | Writing and reading the settings column; a broken column reads as the defaults |
| `Parrot.Application/Agents/IAgentRepository.cs`, `IAgentService.cs`, `AgentService.cs` | Update, live switch, soft delete, resolve; validation; the best-effort `agent.config.changed` announcement |
| `Parrot.Application/Agents/AgentConfigChangedEvent.cs` | The outbound event in the orchestrator's snake_case shape, with a six-digit instant |
| `Parrot.Application/Agents/AgentEventEnvelope.cs` | The inbound envelope and the mapper that keys turns, approvals and escalations |
| `Parrot.Application/Agents/AgentActivity.cs` | The activity record, the event type names and the store contract |
| `Parrot.Application/Agents/AgentActivityEvent.cs` | What is pushed as `agentActivity` |
| `Parrot.Application/Agents/AiOperations.cs`, `AiOperationsService.cs` | The AI Operations figures and the approval decision, worked out from the rows |
| `Parrot.Application/Agents/IAgentOrchestratorClient.cs` | The orchestrator's agent API as this API calls it |
| `Parrot.Application/Auth/ServiceScopes.cs`, `ServiceTokenFactory.cs` | Scope names and parsing; the five-minute service token with `iat` |
| `Parrot.Application/Integrations/KafkaSettings.cs` | `AgentEventsTopic`, `AgentEventsConsumerGroupId` |
| `Parrot.Application/Integrations/OmniAiSettings.cs` | `OmniAi` section: BaseUrl, Enabled, TimeoutSeconds |
| `Parrot.Infrastructure/Agents/MongoAgentActivityStore.cs` | The `agent_activity` collection, unique on (OwnerUserId, Type, EventKey); indexes made on first use; real-MongoDB tests in `Test.Parrot.Api/Tests/Services/Agents/MongoAgentActivityStoreTests.cs` (set `PARROT_TEST_MONGO`) |
| `Parrot.Infrastructure/Agents/AgentOrchestratorClient.cs` | Typed HttpClient with a service token; 503 without body leakage, 404 and 409 mapped |
| `Parrot.Api/Security/ServiceScopeAuthorization.cs` | The `ServiceScope` policy |
| `Parrot.Api/Controllers/AgentController.cs` | PUT, PATCH live, DELETE |
| `Parrot.Api/Controllers/InternalAgentsController.cs` | `GET /api/internal/agents/resolve` under the policy, tenant from the token |
| `Parrot.Api/Controllers/AiOperationsController.cs` | today, week, recent, waiting, approvals |
| `Parrot.Api/Realtime/AgentEventsRouter.cs`, `AgentEventsConsumer.cs` | Reading `agent.events`, storing once, pushing to the owner |
| `Parrot.Api/Realtime/MessagingHub.cs` | The `agentActivity` method name |
| `Parrot.Api/Program.cs` | OmniAi settings, the policy, the token factory, the typed client, the activity store, the router and the consumer |
| `Parrot.Api/appsettings.json` | `OmniAi` defaults (off) |
| `Parrot.Web/Parrot.Web/Features/Dashboard/AiAgents/AgentMapping.cs` | Screen agent and settings to the API's shapes and back |
| `Parrot.Web/Parrot.Web/Features/Dashboard/AiAgents/ApiAgentStore.cs` | `IAgentStore` over `/api/agent*` with the session token |
| `Parrot.Web/Parrot.Web/Features/Dashboard/AiAgents/ApiAiOperationsFeed.cs` | `IAiOperationsFeed` over `/api/ai-operations/*`, one snapshot per circuit |
| `Parrot.Web/Parrot.Web/Features/Dashboard/AiAgents/OmniAiAgentCapabilities.cs` | `CanReply` from `OmniAi:Enabled` |
| `Parrot.Web/Parrot.Web/Features/Dashboard/AiAgents/AiOperationsFeed.cs` | `Changed` and `RefreshAsync` on the feed; the unavailable feed's no-ops |
| `Parrot.Web/Parrot.Web/Features/Dashboard/AiAgents/State/AiAgentsState.cs` | Loads the feed and the hub after the agents; re-reads the feed on `agentActivity`; raises `Changed` |
| `Parrot.Web/Parrot.Web/Services/Realtime/RealtimeClient.cs` | The `agentActivity` hub method and `AgentActivityReceived` |
| `Parrot.Web/Parrot.Web/Components/Pages/Dashboard/AiAgents/AiAgents.razor`, `AiAgentDetail.razor` | Draw again on `AiAgentsState.Changed` |
| `Parrot.Web/Parrot.Web/Program.cs` | The API-backed store, feed and capabilities when `OmniAi:Enabled`; the session store otherwise |
| `Parrot.Web/Parrot.Web/appsettings.json` | `OmniAi:Enabled` false |
| `docker-compose.local.yml`, `docker-compose.ec2.yml` | `agent.events 6` in kafka-topic-init; `OmniAi__*` on the API and the web app |
| `.env.docker.example`, `.env.ec2.example` | `OMNI_AI_ENABLED`, `OMNI_AI_BASE_URL` |
| `.gitignore` | Allow this guide and the conversations guide despite the blanket Markdown ignore |
| `docs/CONVERSATIONS.md` | Link here |
| `Test.Parrot.Api/Tests/Services/Agents/AgentServiceTests.cs` | Create, update, live switch, delete, resolve, validation, ownership, the announcement and a bus outage |
| `Test.Parrot.Api/Tests/Services/Agents/AgentSettingsMappingTests.cs` | Settings column round trip; screen to API and back |
| `Test.Parrot.Api/Tests/Services/Agents/AgentEventsRouterTests.cs`, `FakeAgentActivityStore.cs` | Idempotent insert, push to the owner only, skips, outages, cancellation |
| `Test.Parrot.Api/Tests/Services/Agents/AiOperationsServiceTests.cs` | Today, week, recent, waiting and the approval decision from fake rows |
| `Test.Parrot.Api/Tests/Services/Agents/ServiceTokenFactoryTests.cs` | The service token's claims, life and signature |
| `Test.Parrot.Api/Tests/Services/Agents/ServiceScopePolicyTests.cs` | The policy allows `agent:service` and denies a session token |
| `Test.Parrot.Api/Tests/Services/Agents/AgentOrchestratorClientTests.cs` | The wire shapes, the token header, and error mapping without body leakage |
| `Test.Parrot.Api/Tests/Services/Agents/AgentEventContractTests.cs` | Both directions of the topic's JSON; the Kafka defaults |
| `docs/AI_INTEGRATION.md` | This setup/audit guide |

## Assumptions about the orchestrator

- Its agent HTTP API (`/api/v1/agent/approvals/{id}`, `/traces/{id}`, `/registry`, `/turn`) is taken from the brief; the checked-out orchestrator only serves its onboarding routes so far. The client sends `{"decision","decided_by","instruction"}` and reads `{turn_id, status, reply}`; a test turn is a `ChannelMessage` on the `web` channel and reads `{turn_id, status, decision, reply, confidence}`.
- It requires `iat` on inbound tokens (PyJWT `require`), which Parrot's token helper does not add on its own; the service token factory adds it as a number.
- It does not consume `agent.config.changed` yet; the event is published so it can, and its sixty-second cache covers the gap meanwhile.
- `agent.turn.completed` is published once per status a turn reaches, so the activity store keys on turn and status and the figures take the latest report per turn.

## Verification

```bash
dotnet build Backend.slnx --no-restore -m:1 -p:UseSharedCompilation=false -nodeReuse:false
dotnet test Test.Parrot.Api/Test.Parrot.Api.csproj --no-build --no-restore
```

The activity store has no integration test; the Mongo-backed tests of the conversations guide show how to add one against a disposable instance (`PARROT_TEST_MONGO`).

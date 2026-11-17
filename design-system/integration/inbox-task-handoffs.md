# Inbox attention and task handoffs

Historical PR #121 handoff: queue membership and AI Question-task presentation
below are superseded by [Personal Inbox attention](inbox-personal-attention.md).
Existing task APIs/whole-task Pass on behaviour still apply.

Frontend handoff for `update/inbox-task-handoffs`, based on main `3ed73163`.
Selected from reviewed web checkpoint `4b2e1c3f`; not a merge of the cumulative
local design branch. No backend implementation, mobile files, sample records,
fake services, private notes, environment settings or token changes are included.

## Delivered UI

- Inbox uses All / Unread / Needs you. Needs you includes actionable tasks
  assigned to the viewer or unassigned, agent questions/approvals, and teammate
  questions/returned answers when their service becomes available. Counts cover
  the conversations currently loaded, not a server-wide total.
- One compact attention card above the composer, with previous/next for more
  than one item; task details open over the current conversation. Footer sizing
  reserves room so the latest message is not hidden by those cards.
- Pass on moves the whole task to a teammate through the existing task store,
  with an optional internal note. It does not transfer reply control, create
  another order, change amounts or complete the task. Existing takeover/return
  controls still decide who can reply.
- Prepared teammate-question UI supports Ask, Answer and Close. The asker keeps
  the task; the recipient answers; Use in reply returns editable text to the
  asker, never sends it automatically. This feature is unavailable in the
  production registration until a real service is supplied.
- Shared AssignedQuestionCard and saved-agent avatars keep attention and agent
  identity consistent. Main's existing AgentMascot component parameter and UTC
  takeover handling are preserved alongside the new look lookup.
- The frontend recognises an incoming Question task, linked to its agent
  message. It is NOT offered in manual task creation because the current
  backend WorkTaskKind supports only the existing six kinds.

## Real-service status

| Capability | This PR |
| --- | --- |
| Conversation reads, customer sends, current takeover/return and approval calls | Existing clients/endpoints retained; no backend contract replacement |
| Task reading, status, team selection, assignment, internal notes | Existing ApiTaskStore retained |
| Pass on whole task | Existing POST task note, then PUT task assignee; permissions remain server-authoritative |
| Teammate questions | ITaskQuestions registered as UnavailableTaskQuestions; Ask/Answer UI is not offered |
| Agent Question task creation/type | Backend/AI support required; no frontend-generated production task |
| Resolve an agent question without learning; have the agent send an answer | IInboxFeatures remains unavailable; no fabricated success |
| Teammate-question notifications | Catalogue definitions only; no fake feed or delivery implementation |

An optional note and assignment are two existing calls. If assignment fails
after the note succeeds, the note remains. The UI reports the failure; an atomic
assignment-with-note endpoint would remove this limitation. Undo uses the
existing assignment call and is not a substitute for server conflict control.

## Backend / AI integration contract

Confirm current contracts before implementing new routes; names below describe
capabilities, not endpoints this PR pretends already exist.

1. Workspace/viewer-authorised consultation reads: `id`, `taskId`,
   `conversationId`, `text`, `askedBy`, `askedOf`, optional `answer`, `closed`,
   `updatedAt`, `revision`. Prefer a server-filtered waiting-on-me view for
   global counts and pagination.
2. Eligible recipients for a task: active teammates with Tasks and channel
   access, their role, and reply capability. Enforce access independently of
   the choices shown in the UI and reconcile removal/deactivation.
3. Ask/Answer/Close writes: stable request ID, exact-payload idempotency,
   expected revision, server-derived actor, and correct recipient/asker rights.
   Never alter the parent's owner, state, lines or money as a side effect.
4. Persist activity/history and notify the recipient, then the asker on answer,
   once each. Wire real realtime updates and the existing notification boundary.
5. Dedicated agent Question tasks must correlate to the original message via
   `raisedInMessageId` or an explicit task ID. Support the new kind in the API
   before offering its creation. Preserve existing kinds and old task records.
6. Explicitly distinguish a saved knowledge answer, a suggested human reply and
   an acknowledged agent customer reply. A knowledge-save receipt alone cannot
   prove that the customer was answered. Human-owned answers stay drafts until
   successful send; agent-owned answers require an acknowledged agent reply.
7. Resolve an agent question without learning, and complete/reopen only its
   linked Question task with permissions/idempotency. Failed customer sends
   must not close it. Ordinary orders/bookings must not auto-complete from chat.

## Verification and review

- Build the web project; the clean branch has no local-preview configuration.
- `tools/WebTaskQuestionChecks` exercises public rules, draft handoff and static
  real-mode rendering with controlled stores. It never uses a sample dataset,
  account, network API or question provider from the local design environment.
- Token resolution, design-system lint and whitespace checks must pass without
  changing the lint baseline or generated Figma values.
- Live authenticated send/assignment permissions and cross-device behaviour
  still need the team's staging smoke test. Local design-flow tests do not
  establish production backend readiness.
- Existing main dependency warning: WebAssembly.Server 10.0.3 reports
  GHSA-g27p-qf6j-c96c and GHSA-qjpx-jqm5-h2c4 during restore/build. Its package
  version is unchanged by this frontend PR; dependency owners should review
  those advisories before deployment. Existing StyleCop/analyser and unrelated
  Razor warnings are not hidden or added to a baseline by this PR.

Example checks (the alternate output path keeps local builds isolated):

```text
dotnet build Parrot.Web/Parrot.Web/Parrot.Web.csproj
dotnet run --project tools/WebTaskQuestionChecks/WebTaskQuestionChecks.csproj
node design-system/tools/check-tokens.mjs
node design-system/tools/lint-design-system.mjs
git diff --check
```

If built to a separate directory, pass its absolute web DLL directory, ending
in a slash, as `-p:ParrotWebBin=.../` when running the checks. Token generation
was disabled during extraction checks; committed token values were preserved.

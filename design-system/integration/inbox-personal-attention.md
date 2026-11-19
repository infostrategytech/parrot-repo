# Personal Inbox attention and AI-question follow-up

Production-safe frontend follow-up to merged Inbox PR #121, prepared on main
`34f6c39c`. No backend implementation, mobile changes, demo providers/fixtures,
local reset helper, private notes or Figma-token changes are included.

## Experience

- Needs you is personal conversation input, not a list of open/urgent tasks.
  Include routed AI questions/approvals, human replies/handoffs, explicit
  teammate questions and returned answers waiting for use.
- Operational tasks remain visible in Tasks and linked inside chat. A reply
  does not complete an order, payment check, booking or handoff task. Urgent
  prioritises work; it does not itself add a chat to Needs you.
- Pending AI knowledge questions use the persistent amber Answer/Done strip,
  without an avatar. They do not create or settle Tasks. Manual replies do not
  trigger a second “Did that answer it?” confirmation.
- Completion feedback is a durable conversation event with actor/time, not a
  completed question card. Question/answer records are not deleted.
- Agent-owned Answer must quote the original customer message; human-owned
  Answer produces a draft for review. Done sends/learns nothing.
- Operations and agent Overview show counts plus View in Inbox. One question
  opens its conversation/card; several open a filtered queue. Pending questions
  remain reachable for paused agents. Operations prompt follows the metrics;
  recent conversations are capped at five, with See all in Inbox retained.
- Actual footer measurement protects the latest message with a small gap.
  New content/resizes follow only at the end; historical reading stays put.
  Existing shared helper signatures/default behaviour remain compatible.

## Backend requirements and rollout gate

This is ready for frontend review, not a claim of complete live integration.
Coordinate backend delivery/staging before enabling the new queue in production.

Existing `GET /api/conversations` needs optional canonical `attention`:

```json
{
  "responsiblePersonId": "stable-person-id",
  "replyNeeded": true,
  "handoverTaskId": "optional-real-handoff-task-id"
}
```

- IDs must match auth/task identities. Resolve an eligible recipient/actual
  owner fallback server-side; unassigned does not mean every teammate. If AI
  approvals/questions need different recipients, provide action-specific
  authorised routing before rollout rather than broadcasting the obligation.
- Human handoff creation/reuse, assignment, input responsibility and recipient
  notification must be atomic/idempotent. Passing the handoff changes its
  responsibility without automatically transferring reply control. Ordinary
  task assignment/status/urgency must not create a reply obligation.
- Clear reply need after confirmed customer delivery/acceptance or return to
  AI; notes and failed/pending/unknown sends do not clear it. A later inbound
  during human control can reopen it. Reconcile later delivery failure. A task
  being completed must not falsely say a waiting customer received a reply.
- Return canonical state before history opens; synchronise list/realtime,
  count/notification recipients and completion events. Enforce workspace,
  channel, role, current ownership, revisions and retry deduplication on writes.
  Reroute deactivated/ineligible recipients; reject stale/out-of-order changes.
- Existing `GET /api/ai-operations/waiting` should project those same pending
  Inbox records with additive `agentId`, `conversationId` and `messageId`.
  Old three-field responses remain readable and use generic queue navigation.
- Stop automatic AI knowledge-question Tasks/duplicate task notifications;
  preserve legacy records/history through an agreed server migration.
- Real Answer/Done adapters must supply actual source linkage, channel quote
  metadata and durable actor/timestamp completion events. Never infer source
  from newest inbound or fabricate learning, delivery or event success.

Old conversation JSON remains readable. Without `attention`, the frontend does
not infer human reply need from unread/paused/task state or partial history.
Existing explicit assignees can still route old AI-question/approval flags.
All conversations and existing Tasks APIs remain accessible; full personal
Needs you membership requires the projection above. Real IInboxFeatures and
ITaskQuestions remain unavailable until their actual adapters are supplied.

## Verification boundary

Clean-main frontend build passed with zero errors; 54 attention/contract checks,
27 existing task-handoff/real-mode render checks, six controlled scrolling tests
and token/lint/whitespace checks pass. Two existing high-severity advisory IDs
for WebAssembly.Server 10.0.3 and existing Razor/unused-field warnings remain;
package versions are unchanged by this PR.

The PR branch is verified against the current main, separately from the local
designer demo. Controlled tests use public contracts/HTTP fakes, not sample
customer data. The designer demo's browser evidence is supplementary, not a
live-backend claim. Authenticated real-service staging checks remain required:
owner/teammate routing, unassigned fallback, role loss, fresh/stale messages,
reply failure/retry, handoff passing, approval recipients and task independence.

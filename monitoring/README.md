# Message processing monitoring

This implements the *Parrot message processing monitoring guide*: the app emits metrics, Prometheus scrapes them, and Grafana shows the dashboard and runs the alerts.

| File | What it is |
|---|---|
| `message-processor-dashboard.json` | The four-row dashboard, importable through Dashboards → New → Import. It asks for the Prometheus data source on import. |
| `message-processor-alerts.json` | The guide's seven alerts as Grafana-managed rules (Grafana file-provisioning format). |
| `load-alerts.sh` | Loads those rules into a Grafana you can only reach over HTTP. |
| `prometheus.yml` | The scrape config for Parrot's two services. |

## Where the metrics come from

Everything is on one meter, `Parrot.Messaging` (`Parrot.Application/Messaging/MessageMetrics.cs`). Two services record on it, and every query sums them.

| Service | Endpoint | Stages |
|---|---|---|
| parrot-api | `:8080/metrics` | 1 webhook, received, 5 replies to Meta, 6 status updates, plus HTTP metrics |
| message-processor | `:9464/metrics` (`Monitoring__MetricsPort`) | 2 persistence, 3 queue, 4 processing, queue and dead-letter gauges |

`/metrics` must stay on the internal network. nginx returns 404 for it on the public API domain (`nginx.conf`, `infra/ec2/nginx/*.template`), and the processor's port isn't published.

### Metrics

| Metric | Type | Labels |
|---|---|---|
| `whatsapp_webhooks_total` | counter | `result`: ok, account_mismatch, unknown_connection, bad_signature, parse_error |
| `messages_received_total` | counter | `message_type` |
| `messages_persisted_total` | counter | `message_type` |
| `messages_deduplicated_total` | counter | — (another delivery of a stored message; a retry or replay of the same record is not counted) |
| `messages_persist_failed_total` | counter | `reason`: db_timeout, db_unavailable, constraint_violation, other |
| `message_queue_depth` | gauge | `queue` |
| `message_queue_wait_seconds` | histogram | `queue` |
| `messages_processed_total` | counter | `status`: success, failed, dead_lettered; `reason` |
| `message_processing_duration_seconds` | histogram | `stage`: decode, persist, receipt, publish |
| `messages_sent_total` | counter | `status`: accepted, failed, unknown; `meta_error_code` |
| `meta_api_request_duration_seconds` | histogram | `endpoint` |
| `message_status_updates_total` | counter | `status`: sent, delivered, read, failed |
| `message_delivery_latency_seconds` | histogram | `status` |
| `message_end_to_end_seconds` | histogram | — |
| `message_dead_letter_depth` | gauge | `queue` (dead letters not yet reviewed) |

### Where this differs from the guide, and why

- **Unaccounted = received − persisted − deduplicated.** The guide also subtracts persist failures, which treats a failed save as final.
  - Parrot retries every failed save until it succeeds, so `messages_persist_failed_total` counts *attempts*, not messages.
  - Subtracting it double-counts. In testing, a two-minute MongoDB pause drove the guide's formula to −296 for 40 messages that were all eventually stored. That would hide a real loss for the next 30 minutes.
  - Persist failures still have their own panels and alert, as the guide specifies.

- **`received` is counted once the message is safely on Kafka**, just before the webhook answers 200.
  - Counted earlier, a webhook that failed to publish (503) and was resent by Meta would count twice, and show up as a false loss.
- **`unknown_connection` is separate from `account_mismatch`.** Both are answered 200 and dropped, but they need different fixes.
- **`bad_signature` is never recorded today.** The WhatsApp signature check is commented out in `WhatsAppWebhookController`. That is a separate security fix.
- **Unparseable WhatsApp payloads get 200 and are logged with the body's length and SHA-256 hash.** Set `Webhooks__LogBodies=true` to log the body itself against the same CorrelationId.
- **Status updates are counted, not persisted.** Delivery latency uses the send time already stored with each reply's wamid.
- **`message_end_to_end_seconds` is the time from the customer's message arriving to Meta accepting the reply.** Replies are sent by agents from the inbox, so this is the team's response time.

## Reading the numbers

**Unaccounted (30m)** = received − persisted − deduplicated: acknowledged but not stored yet. That means in flight, retrying, or lost.

It's approximate, as the guide says:
- `increase()` extrapolates to the window edges.
- A stopped process's counters drop out until it restarts.
- Histograms (latencies, queue wait) lose what a freshly restarted process recorded before its first scrape.

The exact answer is the reconciliation job, which is not built yet (see below).

**Dead letter queue** counts dead letters nobody has reviewed: those past the commit of the consumer group `message-processor-dlq-reviewed`. Kafka keeps records after they are read, so this is how the number returns to 0. After fixing and replaying them, mark them reviewed:

```sh
kafka-consumer-groups.sh --bootstrap-server <broker> --group message-processor-dlq-reviewed \
  --topic messages.inbound.dlq --reset-offsets --to-latest --execute
```

**While MessageProcessor is down**, `message_queue_depth` and `message_dead_letter_depth` have no data, because the processor reports them. "Messages unaccounted" is the alert that catches this case.

## Setting it up

### 1. Prometheus

Add the two jobs in `prometheus.yml` to the Prometheus your Grafana uses. Then check both targets are up and that `/metrics` shows the `messages_*` series.

The targets have to be reachable from Prometheus on the same network as the containers. In `docker-compose.ec2.yml` that is the `parrot` network.

### 2. Dashboard

1. In Grafana, go to Dashboards → New → Import.
2. Upload `message-processor-dashboard.json`.
3. Pick that Prometheus data source.

### 3. Alerts

Use one of these. Don't do both.

**If you can only reach Grafana over HTTP:**
1. Create a service account with the Editor role.
2. Run:
   ```sh
   GRAFANA_URL=https://<grafana> GRAFANA_TOKEN=<service account token> \
   PROMETHEUS_DATASOURCE_UID=<uid of that data source> monitoring/load-alerts.sh
   ```

**If you manage Grafana's config:**
1. Replace `PROMETHEUS_DATASOURCE_UID` in `message-processor-alerts.json` with the data source uid.
2. Put the file in `provisioning/alerting/`.

Either way, the rules land in the folder **Parrot**, group `parrot-message-processing`, and are evaluated every minute.

### 4. Notifications (manual, in Grafana)

The rules carry the labels `team=parrot` and `severity=critical|warning`. Nothing is sent until a notification policy routes them. In Alerting → Notification policies, add a policy matching `team=parrot` that sends to the existing alerts contact point, which already feeds the pager. This repo can't configure Slack or the pager itself.

The team posts recovery messages in the `INFO` channels. Grafana sends "resolved" notifications to the same contact point as the alert. To get them into the `INFO` channel, either:
- give that contact point's Slack integration the `INFO` channel for resolved messages (a template that branches on `.Status`), or
- add a second contact point for `INFO` and route resolved notifications to it.

Choose whichever matches how the existing Grafana alerts already do this.

## Not built yet: reconciliation

The guide's reconciliation job finds exactly which messages are missing. It needs:
- an `inbound_receipts` store written on every accepted webhook, before anything else
- a job every 15 minutes that compares the receipts against `messages`
- `messages_reconciliation_missing_total`, and an alert on it

That adds a write to the webhook's hot path, so it is left as its own change. Until then, the dashboard's Unaccounted number is the approximate signal.

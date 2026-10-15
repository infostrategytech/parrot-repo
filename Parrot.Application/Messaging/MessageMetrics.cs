using System.Diagnostics.Metrics;

namespace Parrot.Application.Messaging;

/// <summary>
/// The message processing metrics from the Parrot message processing
/// monitoring guide, on one meter, "Parrot.Messaging". Parrot.Api and
/// MessageProcessor both record here and expose it on /metrics; Prometheus
/// sums them, so a stage's metric can come from either process.
///
/// Every label value comes from a small fixed set: never a phone number,
/// wamid, account, user or correlation id.
///
/// The intake counters balance:
/// received = persisted + deduplicated + persist failed + in flight.
/// </summary>
public static class MessageMetrics
{
    public const string MeterName = "Parrot.Messaging";

    /// <summary>The inbound topic, as the <c>queue</c> label.</summary>
    public const string InboundQueue = "messages.inbound";

    // The private fields come first on purpose: static fields initialise in
    // declaration order, and every instrument below is created from these.
    private static readonly Meter Meter = new(MeterName);

    // Bucket bounds in seconds; the exporter's defaults are for milliseconds.
    private static readonly InstrumentAdvice<double> Fast = new()
    {
        HistogramBucketBoundaries = [0.005, 0.01, 0.025, 0.05, 0.1, 0.25, 0.5, 1, 2.5, 5, 10, 30, 60, 120, 300],
    };

    // Waits on people or on the customer's phone: seconds to a day.
    private static readonly InstrumentAdvice<double> Slow = new()
    {
        HistogramBucketBoundaries = [1, 5, 15, 30, 60, 120, 300, 600, 1800, 3600, 7200, 21600, 43200, 86400],
    };

    // Stage 1: webhook. result: ok, account_mismatch, unknown_connection, bad_signature, parse_error.
    public static readonly Counter<long> Webhooks = Meter.CreateCounter<long>(
        "whatsapp_webhooks_total", description: "WhatsApp webhook requests, by validation result.");

    // Counted once the webhook has the message safely on the queue and will answer 200.
    public static readonly Counter<long> Received = Meter.CreateCounter<long>(
        "messages_received_total", description: "Messages accepted from a valid webhook, by message_type.");

    // Stage 2: persistence.
    public static readonly Counter<long> Persisted = Meter.CreateCounter<long>(
        "messages_persisted_total", description: "Messages written to the database, by message_type.");

    public static readonly Counter<long> Deduplicated = Meter.CreateCounter<long>(
        "messages_deduplicated_total", description: "Messages skipped because their id was already stored.");

    public static readonly Counter<long> PersistFailed = Meter.CreateCounter<long>(
        "messages_persist_failed_total", description: "Save attempts that threw, by reason. The message is retried.");

    // Stage 3: queue.
    public static readonly Histogram<double> QueueWait = Meter.CreateHistogram<double>(
        "message_queue_wait_seconds", description: "Time from the webhook queueing a message to the worker picking it up.", advice: Fast);

    // Stage 4: processing. status: success, failed (retried), dead_lettered.
    public static readonly Counter<long> Processed = Meter.CreateCounter<long>(
        "messages_processed_total", description: "Processing attempts, by status and reason.");

    public static readonly Histogram<double> ProcessingDuration = Meter.CreateHistogram<double>(
        "message_processing_duration_seconds", description: "Time spent in each processing stage.", advice: Fast);

    // Customer files copied to private storage by the media worker. outcome: ready, failed, too_large, skipped.
    public static readonly Counter<long> MediaProcessed = Meter.CreateCounter<long>(
        "media_jobs_total", description: "Media jobs finished, by outcome and reason.");

    public static readonly Histogram<double> MediaDuration = Meter.CreateHistogram<double>(
        "media_job_duration_seconds", description: "Time to copy one customer file, retries included.", advice: Fast);

    public static readonly Histogram<double> MediaQueueWait = Meter.CreateHistogram<double>(
        "media_job_queue_wait_seconds", description: "Time from a media job being queued to a worker picking it up.", advice: Fast);

    // Stage 5: replies to the Meta API. status: accepted, failed, unknown.
    public static readonly Counter<long> Sent = Meter.CreateCounter<long>(
        "messages_sent_total", description: "Replies sent to the Meta API, by status and meta_error_code.");

    public static readonly Histogram<double> MetaApiDuration = Meter.CreateHistogram<double>(
        "meta_api_request_duration_seconds", description: "Duration of each Meta API call, by endpoint.", advice: Fast);

    public static readonly Histogram<double> EndToEnd = Meter.CreateHistogram<double>(
        "message_end_to_end_seconds", description: "Time from the customer's message arriving to the reply being accepted by Meta.", advice: Slow);

    // Stage 6: status updates. status: sent, delivered, read, failed.
    public static readonly Counter<long> StatusUpdates = Meter.CreateCounter<long>(
        "message_status_updates_total", description: "Status updates received from Meta, by status.");

    public static readonly Histogram<double> DeliveryLatency = Meter.CreateHistogram<double>(
        "message_delivery_latency_seconds", description: "Time from a reply being sent to Meta reporting its status.", advice: Slow);

    // Meta's message types (WhatsApp and Messenger) and TikTok's. Anything else is "other".
    private static readonly HashSet<string> MessageTypes = new(StringComparer.Ordinal)
    {
        "text", "image", "audio", "video", "document", "sticker", "location", "contacts", "interactive", "button",
        "reaction", "order", "system", "unsupported", "postback", "file", "template", "fallback", "story_mention",
        "share_post",
    };

    /// <summary>
    /// The <c>message_type</c> label for a type read from a webhook payload. The
    /// payload is untrusted input, so only known types pass; the rest are "other",
    /// keeping the number of series fixed.
    /// </summary>
    public static KeyValuePair<string, object?> MessageType(string? type) =>
        new("message_type", type is not null && MessageTypes.Contains(type) ? type : "other");

    /// <summary>
    /// Records 0 for every known label value, at startup. Prometheus's increase()
    /// counts from a series' first sample, so a series first scraped at 1 would
    /// hide that message, and the received/persisted balance would drift after
    /// every restart. Values outside these lists still work; only their first
    /// count can be missed.
    /// </summary>
    public static void StartAtZero()
    {
        foreach (string result in (string[])["ok", "account_mismatch", "unknown_connection", "bad_signature", "parse_error"])
        {
            Webhooks.Add(0, new KeyValuePair<string, object?>("result", result));
        }

        // Meta's message types.
        foreach (string type in (string[])["text", "image", "audio", "video", "document", "sticker", "location", "contacts", "interactive", "button", "reaction"])
        {
            KeyValuePair<string, object?> tag = new("message_type", type);
            Received.Add(0, tag);
            Persisted.Add(0, tag);
        }

        Deduplicated.Add(0);
        foreach (string reason in (string[])["db_timeout", "db_unavailable", "constraint_violation", "other"])
        {
            PersistFailed.Add(0, new KeyValuePair<string, object?>("reason", reason));
        }

        Processed.Add(
            0,
            new KeyValuePair<string, object?>("status", "success"),
            new KeyValuePair<string, object?>("reason", "none"));
        foreach (string status in (string[])["sent", "delivered", "read", "failed"])
        {
            StatusUpdates.Add(0, new KeyValuePair<string, object?>("status", status));
        }

        Sent.Add(
            0,
            new KeyValuePair<string, object?>("status", "accepted"),
            new KeyValuePair<string, object?>("meta_error_code", "none"));
    }

    /// <summary>
    /// Registers the queue gauges. Their values are read when Prometheus
    /// scrapes, so <paramref name="depth"/> and <paramref name="deadLetters"/>
    /// must return quickly (a cached value, not a broker call).
    /// </summary>
    public static void RegisterQueueGauges(string queue, string deadLetterQueue, Func<long?> depth, Func<long?> deadLetters)
    {
        Meter.CreateObservableGauge(
            "message_queue_depth",
            () => Observe(depth(), queue),
            description: "Messages waiting on the queue (consumer lag).");
        Meter.CreateObservableGauge(
            "message_dead_letter_depth",
            () => Observe(deadLetters(), deadLetterQueue),
            description: "Dead letters not yet reviewed.");
    }

    /// <summary>
    /// The <c>reason</c> for a failed save, from a fixed list: never the
    /// exception message. Matched by type name so this layer needs no
    /// database driver.
    /// </summary>
    public static string Classify(Exception exception)
    {
        string name = exception.GetType().Name;
        if (exception is TimeoutException || name.Contains("Timeout", StringComparison.Ordinal))
        {
            return "db_timeout";
        }

        if (name.Contains("Connection", StringComparison.Ordinal))
        {
            return "db_unavailable";
        }

        if (name.Contains("DuplicateKey", StringComparison.Ordinal) || name == "MongoWriteException")
        {
            return "constraint_violation";
        }

        return "other";
    }

    private static IEnumerable<Measurement<long>> Observe(long? value, string queue) =>
        value is { } v ? [new Measurement<long>(v, new KeyValuePair<string, object?>("queue", queue))] : [];
}

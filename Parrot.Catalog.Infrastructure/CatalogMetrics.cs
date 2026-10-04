using System.Diagnostics.Metrics;

namespace Parrot.Catalog.Infrastructure;

/// <summary>
/// The service's own meter. Names are stable so dashboards and alerts (outbox lag,
/// RLS errors) do not move between releases.
/// </summary>
public sealed class CatalogMetrics : IDisposable
{
    public const string MeterName = "Parrot.Catalog";

    private readonly Meter _meter;
    private readonly Histogram<double> _searchDuration;
    private readonly Counter<long> _cacheHits;
    private readonly Counter<long> _cacheMisses;
    private readonly Histogram<double> _indexRebuildDuration;
    private readonly Counter<long> _outboxPublished;
    private readonly Counter<long> _outboxFailures;
    private readonly Counter<long> _rlsErrors;
    private readonly Counter<long> _eventsConsumed;
    private readonly Counter<long> _eventsDeduplicated;
    private double _outboxLagSeconds;
    private long _outboxPending;

    public CatalogMetrics(IMeterFactory? meterFactory = null)
    {
        _meter = meterFactory?.Create(MeterName) ?? new Meter(MeterName);
        _searchDuration = _meter.CreateHistogram<double>("catalog.search.duration", unit: "ms", description: "Latency of POST /search and search_catalog.");
        _cacheHits = _meter.CreateCounter<long>("catalog.cache.hits", description: "Read cache hits.");
        _cacheMisses = _meter.CreateCounter<long>("catalog.cache.misses", description: "Read cache misses.");
        _indexRebuildDuration = _meter.CreateHistogram<double>("catalog.index.rebuild_duration", unit: "ms", description: "Time to rebuild the derived index for one product.");
        _outboxPublished = _meter.CreateCounter<long>("catalog.outbox.published", description: "Outbox rows relayed to the bus.");
        _outboxFailures = _meter.CreateCounter<long>("catalog.outbox.failures", description: "Outbox rows whose publish attempt failed.");
        _rlsErrors = _meter.CreateCounter<long>("catalog.rls.errors", description: "Statements refused by Row Level Security or grants (SQLSTATE 42501). Alert on any.");
        _eventsConsumed = _meter.CreateCounter<long>("catalog.events.consumed", description: "Events handled by in-repo consumers.");
        _eventsDeduplicated = _meter.CreateCounter<long>("catalog.events.deduplicated", description: "Redelivered events skipped via consumer_inbox.");
        _meter.CreateObservableGauge("catalog.outbox.lag_seconds", () => _outboxLagSeconds, unit: "s", description: "Age of the oldest unpublished outbox row. Alert when it grows.");
        _meter.CreateObservableGauge("catalog.outbox.pending", () => _outboxPending, description: "Unpublished outbox rows.");
    }

    public void SearchCompleted(double milliseconds, string surface) => _searchDuration.Record(milliseconds, new KeyValuePair<string, object?>("surface", surface));

    public void CacheHit(string resource) => _cacheHits.Add(1, new KeyValuePair<string, object?>("resource", resource));

    public void CacheMiss(string resource) => _cacheMisses.Add(1, new KeyValuePair<string, object?>("resource", resource));

    public void IndexRebuilt(double milliseconds) => _indexRebuildDuration.Record(milliseconds);

    public void OutboxPublished(int count) => _outboxPublished.Add(count);

    public void OutboxFailed(int count) => _outboxFailures.Add(count);

    public void OutboxObserved(double lagSeconds, long pending)
    {
        _outboxLagSeconds = lagSeconds;
        _outboxPending = pending;
    }

    public void RlsError() => _rlsErrors.Add(1);

    public void EventConsumed(string consumer, string type) => _eventsConsumed.Add(1, new KeyValuePair<string, object?>("consumer", consumer), new KeyValuePair<string, object?>("type", type));

    public void EventDeduplicated(string consumer) => _eventsDeduplicated.Add(1, new KeyValuePair<string, object?>("consumer", consumer));

    public void Dispose() => _meter.Dispose();
}

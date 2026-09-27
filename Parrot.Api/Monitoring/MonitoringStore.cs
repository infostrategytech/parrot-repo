using System.Collections.Concurrent;

namespace Parrot.Api.Monitoring;

/// <summary>
/// An in-process rolling view of the last hour, per operation, behind
/// GET /api/monitoring: enough to answer "what is slow, what is failing, and
/// since when" without any external system. Counts live in one bucket per
/// minute; latency percentiles come from the most recent samples. It is per
/// instance and resets on restart; long-term history belongs to whatever
/// scrapes the Meter.
/// </summary>
public sealed class MonitoringStore
{
    public const int WindowMinutes = 60;
    private const int SamplesPerOperation = 2048;
    private const int RecentErrorsKept = 50;

    private readonly ConcurrentDictionary<string, OperationStats> _operations = new();
    private readonly LinkedList<RequestSample> _recentErrors = new();
    private readonly object _errorsLock = new();
    private readonly TimeProvider _time;

    public MonitoringStore(TimeProvider time)
    {
        _time = time;
    }

    public DateTimeOffset StartedAt { get; } = DateTimeOffset.UtcNow;

    public void Record(RequestSample sample)
    {
        _operations.GetOrAdd(sample.Operation, _ => new OperationStats()).Record(sample);

        if (sample.IsError)
        {
            lock (_errorsLock)
            {
                _recentErrors.AddFirst(sample);
                if (_recentErrors.Count > RecentErrorsKept) _recentErrors.RemoveLast();
            }
        }
    }

    public MonitoringSnapshot Snapshot(int minutes)
    {
        minutes = Math.Clamp(minutes, 1, WindowMinutes);
        DateTimeOffset now = _time.GetUtcNow();
        DateTimeOffset since = now.AddMinutes(-minutes);

        List<OperationSummary> operations = _operations
            .Select(pair => pair.Value.Summarise(pair.Key, since, now))
            .Where(summary => summary.Requests > 0)
            .OrderByDescending(summary => summary.ErrorRate)
            .ThenByDescending(summary => summary.P95Ms)
            .ToList();

        double[] all = operations.SelectMany(o => o.Samples).OrderBy(d => d).ToArray();
        long requests = operations.Sum(o => o.Requests);
        long errors = operations.Sum(o => o.Errors);

        List<RecentError> recent;
        lock (_errorsLock)
        {
            recent = _recentErrors
                .Where(e => e.At >= since)
                .Take(20)
                .Select(e => new RecentError(e.At, e.Operation, e.Method, e.StatusCode, Math.Round(e.ElapsedMs, 1), e.Outcome, e.CorrelationId))
                .ToList();
        }

        Dictionary<int, long> statusCodes = operations
            .SelectMany(o => o.StatusCodes)
            .GroupBy(pair => pair.Key)
            .OrderBy(g => g.Key)
            .ToDictionary(g => g.Key, g => g.Sum(pair => pair.Value));

        return new MonitoringSnapshot(
            GeneratedAt: now,
            WindowMinutes: minutes,
            InstanceStartedAt: StartedAt,
            Overall: new OverallSummary(
                requests,
                Math.Round(requests / (minutes * 60.0), 3),
                errors,
                requests == 0 ? 0 : Math.Round((double)errors / requests, 4),
                operations.Sum(o => o.Slow),
                Percentile(all, 0.50),
                Percentile(all, 0.95),
                Percentile(all, 0.99),
                statusCodes),
            Operations: operations,
            RecentErrors: recent);
    }

    public static double Percentile(double[] sorted, double p)
    {
        if (sorted.Length == 0) return 0;
        double rank = p * (sorted.Length - 1);
        int low = (int)Math.Floor(rank);
        int high = (int)Math.Ceiling(rank);
        double value = sorted[low] + ((sorted[high] - sorted[low]) * (rank - low));
        return Math.Round(value, 1);
    }

    private sealed class OperationStats
    {
        private readonly object _lock = new();
        private readonly MinuteBucket[] _minutes = new MinuteBucket[WindowMinutes];
        private readonly (DateTimeOffset At, double Ms)[] _samples = new (DateTimeOffset, double)[SamplesPerOperation];
        private int _next;
        private int _count;
        private DateTimeOffset? _lastErrorAt;

        public void Record(RequestSample sample)
        {
            long minute = sample.At.ToUnixTimeSeconds() / 60;
            lock (_lock)
            {
                ref MinuteBucket bucket = ref _minutes[minute % WindowMinutes];
                if (bucket.Minute != minute) bucket = new MinuteBucket { Minute = minute, StatusCodes = new Dictionary<int, long>() };

                bucket.Requests++;
                if (sample.IsError) bucket.Errors++;
                if (sample.IsSlow) bucket.Slow++;
                bucket.StatusCodes![sample.StatusCode] = bucket.StatusCodes.GetValueOrDefault(sample.StatusCode) + 1;

                _samples[_next] = (sample.At, sample.ElapsedMs);
                _next = (_next + 1) % SamplesPerOperation;
                _count = Math.Min(_count + 1, SamplesPerOperation);

                if (sample.IsError) _lastErrorAt = sample.At;
            }
        }

        public OperationSummary Summarise(string name, DateTimeOffset since, DateTimeOffset now)
        {
            long first = since.ToUnixTimeSeconds() / 60;
            long last = now.ToUnixTimeSeconds() / 60;
            long requests = 0, errors = 0, slow = 0;
            Dictionary<int, long> codes = new Dictionary<int, long>();
            double[] durations;
            DateTimeOffset? lastError;

            lock (_lock)
            {
                foreach (MinuteBucket bucket in _minutes)
                {
                    if (bucket.StatusCodes is null || bucket.Minute < first || bucket.Minute > last) continue;
                    requests += bucket.Requests;
                    errors += bucket.Errors;
                    slow += bucket.Slow;
                    foreach ((int code, long n) in bucket.StatusCodes) codes[code] = codes.GetValueOrDefault(code) + n;
                }

                durations = _samples.Take(_count).Where(s => s.At >= since).Select(s => s.Ms).ToArray();
                lastError = _lastErrorAt;
            }

            Array.Sort(durations);
            return new OperationSummary(
                name,
                requests,
                errors,
                requests == 0 ? 0 : Math.Round((double)errors / requests, 4),
                slow,
                Percentile(durations, 0.50),
                Percentile(durations, 0.95),
                Percentile(durations, 0.99),
                durations.Length == 0 ? 0 : Math.Round(durations[^1], 1),
                codes.OrderBy(pair => pair.Key).ToDictionary(pair => pair.Key, pair => pair.Value),
                lastError,
                durations);
        }

        private struct MinuteBucket
        {
            public long Minute;
            public long Requests;
            public long Errors;
            public long Slow;
            public Dictionary<int, long>? StatusCodes;
        }
    }
}

public sealed record MonitoringSnapshot(
    DateTimeOffset GeneratedAt,
    int WindowMinutes,
    DateTimeOffset InstanceStartedAt,
    OverallSummary Overall,
    IReadOnlyList<OperationSummary> Operations,
    IReadOnlyList<RecentError> RecentErrors);

public sealed record OverallSummary(
    long Requests,
    double RequestsPerSecond,
    long Errors,
    double ErrorRate,
    long Slow,
    double P50Ms,
    double P95Ms,
    double P99Ms,
    IReadOnlyDictionary<int, long> StatusCodes);

public sealed record OperationSummary(
    string Operation,
    long Requests,
    long Errors,
    double ErrorRate,
    long Slow,
    double P50Ms,
    double P95Ms,
    double P99Ms,
    double MaxMs,
    IReadOnlyDictionary<int, long> StatusCodes,
    DateTimeOffset? LastErrorAt,
    [property: System.Text.Json.Serialization.JsonIgnore] double[] Samples);

public sealed record RecentError(
    DateTimeOffset At,
    string Operation,
    string Method,
    int StatusCode,
    double ElapsedMs,
    string Outcome,
    string? CorrelationId);

using System.Collections.Concurrent;
using System.Diagnostics;

namespace GraphicsMiddleware.Services;

/// <summary>
/// Simple in-memory metrics collection for monitoring.
/// Thread-safe counters for request tracking.
/// </summary>
public interface IMetricsService
{
    void RecordRequest(string endpoint, long elapsedMs, bool success);
    MetricsSnapshot GetSnapshot();
    void Reset();
}

public sealed class MetricsService : IMetricsService
{
    private readonly ConcurrentDictionary<string, EndpointMetrics> _endpointMetrics = new();
    private long _totalRequests;
    private long _totalErrors;
    private long _totalLatencyMs;
    private readonly DateTime _startTime = DateTime.UtcNow;

    public void RecordRequest(string endpoint, long elapsedMs, bool success)
    {
        Interlocked.Increment(ref _totalRequests);
        Interlocked.Add(ref _totalLatencyMs, elapsedMs);

        if (!success)
            Interlocked.Increment(ref _totalErrors);

        var metrics = _endpointMetrics.GetOrAdd(endpoint, _ => new EndpointMetrics());
        metrics.Record(elapsedMs, success);
    }

    public MetricsSnapshot GetSnapshot()
    {
        var totalReqs = Interlocked.Read(ref _totalRequests);
        var totalErrs = Interlocked.Read(ref _totalErrors);
        var totalLatency = Interlocked.Read(ref _totalLatencyMs);

        var endpoints = _endpointMetrics
            .Select(kvp => new EndpointMetricsSnapshot
            {
                Endpoint = kvp.Key,
                RequestCount = kvp.Value.RequestCount,
                ErrorCount = kvp.Value.ErrorCount,
                AverageLatencyMs = kvp.Value.RequestCount > 0
                    ? (double)kvp.Value.TotalLatencyMs / kvp.Value.RequestCount
                    : 0,
                MaxLatencyMs = kvp.Value.MaxLatencyMs,
                MinLatencyMs = kvp.Value.MinLatencyMs
            })
            .OrderByDescending(e => e.RequestCount)
            .ToList();

        return new MetricsSnapshot
        {
            UptimeSeconds = (DateTime.UtcNow - _startTime).TotalSeconds,
            TotalRequests = totalReqs,
            TotalErrors = totalErrs,
            ErrorRate = totalReqs > 0 ? (double)totalErrs / totalReqs * 100 : 0,
            AverageLatencyMs = totalReqs > 0 ? (double)totalLatency / totalReqs : 0,
            RequestsPerSecond = totalReqs / Math.Max(1, (DateTime.UtcNow - _startTime).TotalSeconds),
            Endpoints = endpoints
        };
    }

    public void Reset()
    {
        _endpointMetrics.Clear();
        Interlocked.Exchange(ref _totalRequests, 0);
        Interlocked.Exchange(ref _totalErrors, 0);
        Interlocked.Exchange(ref _totalLatencyMs, 0);
    }

    private sealed class EndpointMetrics
    {
        private long _requestCount;
        private long _errorCount;
        private long _totalLatencyMs;
        private long _maxLatencyMs;
        private long _minLatencyMs = long.MaxValue;

        public long RequestCount => Interlocked.Read(ref _requestCount);
        public long ErrorCount => Interlocked.Read(ref _errorCount);
        public long TotalLatencyMs => Interlocked.Read(ref _totalLatencyMs);
        public long MaxLatencyMs => Interlocked.Read(ref _maxLatencyMs);
        public long MinLatencyMs
        {
            get
            {
                var val = Interlocked.Read(ref _minLatencyMs);
                return val == long.MaxValue ? 0 : val;
            }
        }

        public void Record(long elapsedMs, bool success)
        {
            Interlocked.Increment(ref _requestCount);
            Interlocked.Add(ref _totalLatencyMs, elapsedMs);

            if (!success)
                Interlocked.Increment(ref _errorCount);

            // Update max (lock-free)
            long currentMax;
            do
            {
                currentMax = Interlocked.Read(ref _maxLatencyMs);
                if (elapsedMs <= currentMax) break;
            } while (Interlocked.CompareExchange(ref _maxLatencyMs, elapsedMs, currentMax) != currentMax);

            // Update min (lock-free)
            long currentMin;
            do
            {
                currentMin = Interlocked.Read(ref _minLatencyMs);
                if (elapsedMs >= currentMin) break;
            } while (Interlocked.CompareExchange(ref _minLatencyMs, elapsedMs, currentMin) != currentMin);
        }
    }
}

/// <summary>
/// Snapshot of current metrics state.
/// </summary>
public sealed class MetricsSnapshot
{
    public double UptimeSeconds { get; init; }
    public long TotalRequests { get; init; }
    public long TotalErrors { get; init; }
    public double ErrorRate { get; init; }
    public double AverageLatencyMs { get; init; }
    public double RequestsPerSecond { get; init; }
    public List<EndpointMetricsSnapshot> Endpoints { get; init; } = new();
}

/// <summary>
/// Metrics for a single endpoint.
/// </summary>
public sealed class EndpointMetricsSnapshot
{
    public string Endpoint { get; init; } = "";
    public long RequestCount { get; init; }
    public long ErrorCount { get; init; }
    public double AverageLatencyMs { get; init; }
    public long MaxLatencyMs { get; init; }
    public long MinLatencyMs { get; init; }
}

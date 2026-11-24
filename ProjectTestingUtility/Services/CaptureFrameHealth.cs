using System.Diagnostics;
using System.Globalization;

namespace ProjectTestingUtility.Services;

/// <summary>
/// Reads the sampled image statistics emitted by FFmpeg's metadata filter.
/// Frame/progress counters alone cannot tell whether a desktop is actually visible.
/// </summary>
internal sealed class CaptureFrameHealth
{
    internal const int BlackPercentageThreshold = 99;
    internal const int UnhealthySampleCount = 3;
    internal const int MaximumIntervals = 100;
    private const string MetadataPrefix = "lavfi.blackframe.pblack=";
    private readonly object _sync = new();
    private readonly TaskCompletionSource _firstVisible = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _qualityAvailable = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly List<CaptureFrameHealthInterval> _intervals = [];
    private double? _pendingFrameSeconds;
    private double? _lastFrameSeconds;
    private DateTimeOffset? _lastSampleAtUtc;
    private long? _lastSampleTimestamp;
    private long _sampleCount;
    private long _blackSampleCount;
    private int _consecutiveBlackSamples;
    private bool? _currentFrameBlack;
    private bool _everUnhealthy;
    private long _droppedIntervalCount;

    public Task FirstVisible => _firstVisible.Task;
    public Task QualityAvailable => _qualityAvailable.Task;
    public long SampleCount { get { lock (_sync) return _sampleCount; } }
    public long BlackSampleCount { get { lock (_sync) return _blackSampleCount; } }
    public bool HasVisibleFrames { get { lock (_sync) return _sampleCount > _blackSampleCount; } }
    public int ConsecutiveBlackSamples { get { lock (_sync) return _consecutiveBlackSamples; } }
    public bool? CurrentFrameBlack { get { lock (_sync) return _currentFrameBlack; } }
    public DateTimeOffset? LastSampleAtUtc { get { lock (_sync) return _lastSampleAtUtc; } }
    public bool IsUnhealthy { get { lock (_sync) return _consecutiveBlackSamples >= UnhealthySampleCount; } }
    public bool EverUnhealthy { get { lock (_sync) return _everUnhealthy; } }

    public void Observe(string line)
    {
        lock (_sync)
        {
            // FFmpeg also writes progress as "frame=..." to this pipe. Only the
            // metadata header with a presentation timestamp belongs to our sample.
            if (line.StartsWith("frame:", StringComparison.Ordinal))
            {
                _pendingFrameSeconds = ParseFrameSeconds(line);
                return;
            }
            if (!line.StartsWith(MetadataPrefix, StringComparison.Ordinal))
                return;

            var frameSeconds = _pendingFrameSeconds;
            _pendingFrameSeconds = null; // Never count duplicate metadata as another frame.
            if (frameSeconds is not { } sampleSeconds ||
                (_lastFrameSeconds is { } lastSeconds && sampleSeconds <= lastSeconds) ||
                !double.TryParse(line.AsSpan(MetadataPrefix.Length), NumberStyles.Float,
                    CultureInfo.InvariantCulture, out var blackPercentage) ||
                !double.IsFinite(blackPercentage) || blackPercentage is < 0 or > 100)
                return;

            var isBlack = blackPercentage >= BlackPercentageThreshold;
            var now = DateTimeOffset.UtcNow;
            _sampleCount++;
            _lastFrameSeconds = sampleSeconds;
            _lastSampleAtUtc = now;
            _lastSampleTimestamp = Stopwatch.GetTimestamp();
            if (isBlack)
            {
                _blackSampleCount++;
                if (_consecutiveBlackSamples < int.MaxValue)
                    _consecutiveBlackSamples++;
                if (_consecutiveBlackSamples >= UnhealthySampleCount)
                    _everUnhealthy = true;
            }
            else
            {
                _consecutiveBlackSamples = 0;
                _firstVisible.TrySetResult();
            }

            if (!isBlack || _consecutiveBlackSamples >= UnhealthySampleCount)
                _qualityAvailable.TrySetResult();

            if (_currentFrameBlack != isBlack)
            {
                if (_intervals.Count > 0)
                    _intervals[^1] = _intervals[^1] with { EndFrameSeconds = sampleSeconds, EndedAtUtc = now };
                if (_intervals.Count == MaximumIntervals)
                {
                    _intervals.RemoveAt(0);
                    _droppedIntervalCount++;
                }
                _intervals.Add(new CaptureFrameHealthInterval(isBlack, sampleSeconds, null, now, null));
            }
            _currentFrameBlack = isBlack;
        }
    }

    public CaptureFrameHealthSnapshot GetSnapshot()
    {
        lock (_sync)
        {
            return new CaptureFrameHealthSnapshot(_sampleCount, _blackSampleCount,
                _sampleCount > _blackSampleCount, _consecutiveBlackSamples,
                _currentFrameBlack, _consecutiveBlackSamples >= UnhealthySampleCount, _everUnhealthy,
                _lastSampleAtUtc, _lastFrameSeconds,
                _lastSampleTimestamp is { } timestamp ? Stopwatch.GetElapsedTime(timestamp) : null,
                _intervals.ToArray(), _droppedIntervalCount);
        }
    }

    private static double? ParseFrameSeconds(string line)
    {
        const string prefix = "pts_time:";
        var index = line.IndexOf(prefix, StringComparison.Ordinal);
        if (index < 0)
            return null;
        var value = line.AsSpan(index + prefix.Length).TrimStart();
        var end = 0;
        while (end < value.Length && !char.IsWhiteSpace(value[end]))
            end++;
        return double.TryParse(value[..end], NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) &&
            double.IsFinite(seconds) && seconds >= 0 ? seconds : null;
    }
}

internal sealed record CaptureFrameHealthSnapshot(
    long SampleCount,
    long BlackSampleCount,
    bool HasVisibleFrames,
    int ConsecutiveBlackSamples,
    bool? CurrentFrameBlack,
    bool IsUnhealthy,
    bool EverUnhealthy,
    DateTimeOffset? LastSampleAtUtc,
    double? LastFrameSeconds,
    TimeSpan? TelemetryAge,
    IReadOnlyList<CaptureFrameHealthInterval> Intervals,
    long DroppedIntervalCount);

/// <summary>Sampled black/visible interval; a null end means it is the latest observed state.</summary>
internal sealed record CaptureFrameHealthInterval(
    bool IsBlack,
    double StartFrameSeconds,
    double? EndFrameSeconds,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset? EndedAtUtc);

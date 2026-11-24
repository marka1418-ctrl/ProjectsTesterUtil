using System.Globalization;
using ProjectTestingUtility.Services;

var health = new CaptureFrameHealth();
Require(health.GetSnapshot() is { SampleCount: 0, CurrentFrameBlack: null, TelemetryAge: null },
    "An unobserved stream must remain unknown.");
Require(!health.FirstVisible.IsCompleted && !health.QualityAvailable.IsCompleted,
    "An unobserved stream cannot satisfy startup readiness.");

health.Observe("frame=128");
health.Observe("lavfi.blackframe.pblack=0");
health.Observe("frame:0 pts:0 pts_time:N/A");
health.Observe("lavfi.blackframe.pblack=0");
health.Observe("frame:0 pts:0 pts_time:0");
health.Observe("lavfi.blackframe.pblack=NaN");
health.Observe("lavfi.blackframe.pblack=0");
Require(health.SampleCount == 0, "Progress, orphaned metadata and malformed values must not count as samples.");
Console.WriteLine("PASS invalid and unrelated telemetry cannot mark capture healthy");

Sample(health, 0, 99);
Sample(health, 1, 100);
Require(!health.QualityAvailable.IsCompleted && !health.IsUnhealthy,
    "A short black transition must not settle startup readiness or mark capture unhealthy.");
health.Observe("lavfi.blackframe.pblack=100");
Sample(health, 1, 100);
Require(health.SampleCount == 2, "Repeated sample metadata or timestamps must not manufacture a black run.");
Sample(health, 2, 99);
Require(health.QualityAvailable.IsCompleted && !health.FirstVisible.IsCompleted && health.IsUnhealthy,
    "Three cursor-on-black samples must settle readiness as unhealthy.");
Require(health.GetSnapshot() is { SampleCount: 3, BlackSampleCount: 3, ConsecutiveBlackSamples: 3,
    CurrentFrameBlack: true, HasVisibleFrames: false, LastFrameSeconds: 2, Intervals.Count: 1 },
    "Black capture statistics are incorrect.");
Console.WriteLine("PASS three black or cursor-on-black samples mark startup unhealthy");

var originalCulture = CultureInfo.CurrentCulture;
try
{
    CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("ru-RU");
    Sample(health, 3.5, 98.5);
}
finally { CultureInfo.CurrentCulture = originalCulture; }
var recovered = health.GetSnapshot();
Require(health.FirstVisible.IsCompleted && !health.IsUnhealthy && health.EverUnhealthy && recovered is
    { HasVisibleFrames: true, CurrentFrameBlack: false, ConsecutiveBlackSamples: 0, LastFrameSeconds: 3.5 },
    "Visible capture must recover from black capture under any process culture.");
Require(recovered.EverUnhealthy, "Recovery before a monitor poll must not hide a sustained black interval.");
Require(recovered.Intervals.Count == 2 && recovered.Intervals[0].EndFrameSeconds == 3.5 &&
    recovered.Intervals[1].EndFrameSeconds == null && recovered.LastSampleAtUtc != null && recovered.TelemetryAge != null,
    "Transitions and last-sample freshness must be observable.");
var previousAge = recovered.TelemetryAge;
await Task.Delay(30);
Require(health.GetSnapshot().TelemetryAge > previousAge, "Loss of telemetry must become visible through elapsed sample age.");
Console.WriteLine("PASS visible recovery records intervals and telemetry freshness");

var transitions = new CaptureFrameHealth();
for (var i = 0; i < 250; i++)
    Sample(transitions, i, i % 2 == 0 ? 100 : 0);
var bounded = transitions.GetSnapshot();
Require(bounded.Intervals.Count == 100 && bounded.DroppedIntervalCount == 150 &&
    bounded.Intervals[0].StartFrameSeconds == 150 && bounded.Intervals[^1].StartFrameSeconds == 249,
    "Transition history must retain at most the latest 100 intervals.");
Require(bounded.SampleCount == 250 && bounded.BlackSampleCount == 125,
    "Bounding history must not lose lifetime statistics.");
Require(!bounded.EverUnhealthy, "Single black samples must not count as a sustained black interval.");
Sample(transitions, 250, 100);
Require(bounded.Intervals[^1].EndFrameSeconds == null,
    "An earlier snapshot must not mutate when further samples arrive.");
Console.WriteLine("PASS bounded transition history preserves totals and immutable snapshots");

// A prior sustained incident survives even when later transitions age it out of history.
Sample(health, 4, 0);
for (var i = 5; i < 250; i++) Sample(health, i, i % 2 == 0 ? 100 : 0);
Require(health.GetSnapshot() is { IsUnhealthy: false, EverUnhealthy: true, DroppedIntervalCount: > 0 },
    "Bounded history and visible recovery must preserve the lifetime unhealthy flag.");
Console.WriteLine("PASS black incidents remain reported after recovery and history truncation");

var concurrentlyRead = new CaptureFrameHealth();
var producer = Task.Run(() =>
{
    for (var i = 0; i < 5_000; i++) Sample(concurrentlyRead, i, i % 2 == 0 ? 100 : 0);
});
var readers = Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
{
    for (var i = 0; i < 2_000; i++)
    {
        var snapshot = concurrentlyRead.GetSnapshot();
        Require(snapshot.BlackSampleCount <= snapshot.SampleCount &&
            snapshot.HasVisibleFrames == (snapshot.SampleCount > snapshot.BlackSampleCount) &&
            snapshot.IsUnhealthy == (snapshot.ConsecutiveBlackSamples >= 3) && snapshot.Intervals.Count <= 100,
            "Concurrent snapshots must remain internally consistent.");
    }
}));
await Task.WhenAll(readers.Append(producer));
Require(concurrentlyRead.SampleCount == 5_000, "Concurrent reads lost telemetry.");
Console.WriteLine("PASS telemetry updates and snapshots are thread safe");
Console.WriteLine("All capture frame health checks passed. No desktop was captured.");

static void Sample(CaptureFrameHealth health, double seconds, double blackPercentage)
{
    health.Observe($"frame:0    pts:0       pts_time:{seconds.ToString(CultureInfo.InvariantCulture)}");
    health.Observe($"lavfi.blackframe.pblack={blackPercentage.ToString(CultureInfo.InvariantCulture)}");
}

static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

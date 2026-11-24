namespace ProjectTestingUtility.Services;

// Diagnostics serialization needs this DTO; no UI activation code enters this test harness.
public sealed record CamWindowActivationResult
{
    public bool IsForeground { get; init; }
    public string? Warning { get; init; }
}

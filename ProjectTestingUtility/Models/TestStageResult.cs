namespace ProjectTestingUtility.Models;

public sealed class TestStageResult
{
    public string Stage { get; set; } = "";
    public DateTimeOffset CapturedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public TreeNode Results { get; set; } = new();
    public bool IsComplete { get; set; }
    public string? Note { get; set; }
}

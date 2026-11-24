using System.Text.Json;
using System.Text.Json.Serialization;

namespace ProjectTestingUtility.Models;

public class TreeNode
{
    public string Name { get; set; } = "";
    public string? OperationId { get; set; }
    public bool IsEnabled { get; set; } = true;
    public bool IsCalculated { get; set; }
    public bool IsCollisionError { get; set; }
    public bool IsCompensationError { get; set; }
    public bool IsError { get; set; }
    public bool IsGougeError { get; set; }
    public bool IsHolderError { get; set; }
    public bool IsMachiningResultCalculated { get; set; }
    public bool IsPlungeError { get; set; }
    public bool IsRapidError { get; set; }
    public bool IsSimulated { get; set; }
    public bool IsToolOverloadError { get; set; }
    public bool IsTravelError { get; set; }
    public bool IsTurnDirectionError { get; set; }
    [JsonIgnore]
    public TreeNode? Parent { get; set; }
    public List<TreeNode> Children { get; }

    public TreeNode()
    {
        Children = new List<TreeNode>();
    }

    public void AddChild(TreeNode child)
    {
        child.Parent = this;
        Children.Add(child);
    }
}

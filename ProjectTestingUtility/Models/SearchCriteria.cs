using System.Text.Json.Serialization;

namespace ProjectTestingUtility.Models;

public class SearchCriteria
{
    [JsonPropertyName("field")]
    public string Field { get; set; }

    [JsonPropertyName("matchMode")]
    public string MatchMode { get; set; }

    [JsonPropertyName("value")]
    public object Value { get; set; }
}
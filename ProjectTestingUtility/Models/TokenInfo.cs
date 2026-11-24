using System.Text.Json.Serialization;

namespace ProjectTestingUtility.Models;

public class TokenInfo
{
    [JsonPropertyName("access_token")]
    public string AccessToken { get; set; }

    [JsonPropertyName("refresh_token")]
    public string RefreshToken { get; set; }
}
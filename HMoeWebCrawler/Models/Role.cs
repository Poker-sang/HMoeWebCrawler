using System.Text.Json.Serialization;

namespace HMoeWebCrawler.Models;

public record Role
{
    [JsonPropertyName("color")]
    public required string Color { get; init; }

    [JsonPropertyName("name")]
    public required string Name { get; init; }
}

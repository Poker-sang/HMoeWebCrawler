using System.Text.Json.Serialization;

namespace HMoeWebCrawler.Models;

public record ImageDataResult
{
    [JsonPropertyName("imgData")]
    public required string ImgData { get; init; }
}


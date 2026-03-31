using System.Collections.Generic;
using System.Text.Encodings.Web;
using System.Text.Json.Serialization;
using HMoeWebCrawler.LocalModels;
using HMoeWebCrawler.Models;

namespace HMoeWebCrawler;

[JsonSerializable(typeof(Settings))]
[JsonSerializable(typeof(SearchData))]
[JsonSerializable(typeof(ApiResponse))]
[JsonSerializable(typeof(HashSet<Post>))]
[JsonSerializable(typeof(IReadOnlyList<Post>))]
[JsonSerializable(typeof(ImageDataResult))]
[JsonSerializable(typeof(PostsSearchResult))]
public partial class SerializerContext : JsonSerializerContext
{
    public static SerializerContext DefaultOverride => field ??= new(new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    });
}

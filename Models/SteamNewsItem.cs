using System.Text.Json.Serialization;

namespace LymdunetteBot.Models;

internal sealed class SteamNewsItem {
    [JsonPropertyName("gid")]
    public string Gid { get; init; } = string.Empty;

    [JsonPropertyName("title")]
    public string Title { get; init; } = string.Empty;

    [JsonPropertyName("url")]
    public string Url { get; init; } = string.Empty;

    [JsonPropertyName("contents")]
    public string Contents { get; init; } = string.Empty;

    [JsonPropertyName("date")]
    public long Date { get; init; }

    [JsonPropertyName("appid")]
    public int AppId { get; init; }

    [JsonPropertyName("feedname")]
    public string FeedName { get; init; } = string.Empty;
}

internal sealed class SteamNewsResponse {
    [JsonRequired, JsonPropertyName("appnews")]
    public required SteamAppNews AppNews { get; init; }
}

internal sealed class SteamAppNews {
    [JsonRequired, JsonPropertyName("appid")]
    public int AppId { get; init; }

    [JsonRequired, JsonPropertyName("newsitems")]
    public required List<SteamNewsItem> NewsItems { get; init; }
}

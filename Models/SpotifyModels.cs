using System.Text.Json.Serialization;

namespace LymdunetteBot.Models;

public sealed class SpotifyTokenResponse {
    [JsonPropertyName("access_token")] public string? AccessToken { get; set; }

    [JsonPropertyName("expires_in")] public int ExpiresIn { get; set; }
}

public sealed class SpotifySearchResponse {
    [JsonPropertyName("tracks")] public SpotifyPaging<SpotifyTrack>? Tracks { get; set; }

    [JsonPropertyName("artists")] public SpotifyPaging<SpotifyArtist>? Artists { get; set; }
}

public sealed class SpotifyPaging<T> {
    [JsonPropertyName("items")] public List<T?>? Items { get; set; }
}

public sealed class SpotifyTrack {
    [JsonPropertyName("id")] public string? Id { get; set; }

    [JsonPropertyName("name")] public string? Name { get; set; }

    [JsonPropertyName("duration_ms")] public int DurationMs { get; set; }

    [JsonPropertyName("artists")] public List<SpotifyArtist>? Artists { get; set; }

    [JsonPropertyName("external_urls")] public SpotifyExternalUrls? ExternalUrls { get; set; }

    [JsonPropertyName("album")] public SpotifyAlbum? Album { get; set; }
}

public sealed class SpotifyArtist {
    [JsonPropertyName("id")] public string? Id { get; set; }

    [JsonPropertyName("name")] public string? Name { get; set; }
}

public sealed class SpotifyAlbum {
    [JsonPropertyName("name")] public string? Name { get; set; }
}

public sealed class SpotifyExternalUrls {
    [JsonPropertyName("spotify")] public string? Spotify { get; set; }
}

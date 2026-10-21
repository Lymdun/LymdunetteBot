using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace LymdunetteBot.Services;

public class XLinkConverter(HttpClient httpClient, ILogger<XLinkConverter> logger) {
    static readonly Regex Links = new(
        """(?<![\w./@:-])(?:https?://)?(?:www\.|mobile\.)?x\.com/[^\s<>`"\[\]()|]+""",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    static readonly Regex PostPath = new(
        @"^/(?<user>[a-z0-9_]+|i/web)/status/(?<id>[0-9]+)(?<media>/(?:photo|video)/[0-9]+)?(?:/[a-z]{2}(?:-[a-z]{2})?)?/?$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    static readonly Regex TextLinks = new(@"https?://\S+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    static readonly Regex IgnoredContent = new(
        @"(?<!`)(?<ticks>`+)(?!`)[\s\S]*?(?<!`)\k<ticks>(?!`)|(?<!`)`{3,}[\s\S]*$|<[^>\r\n]*>",
        RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(5);
    const int MaxCachedPosts = 512;
    readonly object cacheLock = new();
    readonly Dictionary<string, CachedPost> postCache = new();

    public async Task<string> ConvertAsync(string content) {
        var replacements = new Dictionary<int, string>();
        var posts = new Dictionary<string, PostInfo?>();
        var ignored = IgnoredContent.Matches(content);
        foreach (Match match in Links.Matches(content)) {
            if (ignored.Any(range => match.Index < range.Index + range.Length
                && match.Index + match.Length > range.Index))
                continue;

            string raw = match.Value.TrimEnd('.', ',', '!', '?', ';', ':');
            if (!Uri.TryCreate(raw.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? raw : "https://" + raw,
                    UriKind.Absolute, out var originalUrl))
                continue;

            var post = PostPath.Match(originalUrl.AbsolutePath);
            if (!post.Success) continue;

            string id = post.Groups["id"].Value;
            if (!posts.TryGetValue(id, out var info)) {
                info = await GetPostInfoAsync(id);
                posts[id] = info;
            }

            // A failed lookup cannot establish that replacing the link would help.
            if (info == null || !info.NeedsFixup) continue;

            string media = post.Groups["media"].Value;
            if (media.StartsWith("/photo/", StringComparison.OrdinalIgnoreCase) && !info.Translate)
                continue;

            // Rebuild the path so an existing language suffix is never duplicated.
            var url = new UriBuilder(originalUrl) {
                Scheme = "https", Host = "fixupx.com", Port = -1,
                Path = $"/{post.Groups["user"].Value}/status/{id}{media}" + (info.Translate ? "/en" : string.Empty)
            };
            replacements[match.Index] = url.Uri.AbsoluteUri + match.Value[raw.Length..];
        }

        return Links.Replace(content, match => replacements.GetValueOrDefault(match.Index, match.Value));
    }

    static bool NeedsTranslation(string? language) {
        string? primary = language?.Trim().ToLowerInvariant().Split('-', '_')[0];
        return !string.IsNullOrEmpty(primary) && primary is not ("en" or "fr" or "und" or "zxx" or "unknown");
    }

    async Task<PostInfo?> GetPostInfoAsync(string id) {
        CachedPost cached;
        lock (cacheLock) {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            if (!postCache.TryGetValue(id, out cached!) || cached.ExpiresAt <= now) {
                postCache.Remove(id);
                if (postCache.Count >= MaxCachedPosts) {
                    string oldest = postCache.MinBy(entry => entry.Value.ExpiresAt).Key;
                    postCache.Remove(oldest);
                }

                // Share in-flight requests without holding the lock during network I/O.
                cached = new CachedPost(new Lazy<Task<PostInfo?>>(() => FetchPostInfoAsync(id)), now + CacheDuration);
                postCache[id] = cached;
            }
        }

        PostInfo? info = null;
        try {
            info = await cached.Info.Value;
            return info;
        } finally {
            if (info == null) {
                lock (cacheLock) {
                    if (postCache.TryGetValue(id, out var current) && ReferenceEquals(current, cached))
                        postCache.Remove(id);
                }
            }
        }
    }

    async Task<PostInfo?> FetchPostInfoAsync(string id) {
        try {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.fxtwitter.com/status/{id}");
            request.Headers.UserAgent.ParseAdd("LymdunetteBot/1.0 (+https://github.com/Lymdun/LymdunetteBot)");
            using var response = await httpClient.SendAsync(request);
            response.EnsureSuccessStatusCode();
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            if (json.RootElement.ValueKind == JsonValueKind.Object
                && json.RootElement.TryGetProperty("tweet", out var tweet)
                && tweet.ValueKind == JsonValueKind.Object
                && tweet.TryGetProperty("text", out var text)
                && text.ValueKind == JsonValueKind.String) {
                string? language = tweet.TryGetProperty("lang", out var lang) && lang.ValueKind == JsonValueKind.String
                    ? lang.GetString() : null;
                // Media-only posts can contain a t.co URL without any text to translate.
                bool translate = NeedsTranslation(language)
                    && !string.IsNullOrWhiteSpace(TextLinks.Replace(text.GetString()!, string.Empty));

                bool needsFixup = translate || HasRichContent(tweet);
                return new PostInfo(needsFixup, translate);
            }
        } catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException) {
            logger.LogWarning(ex, "Could not inspect content for X post {PostId}", id);
        }
        return null;
    }

    static bool HasRichContent(JsonElement tweet) {
        // Fixup still adds useful context for quoted posts, polls, and long posts.
        if (HasObject(tweet, "quote") || HasObject(tweet, "poll")
            || (tweet.TryGetProperty("is_note_tweet", out var note) && note.ValueKind == JsonValueKind.True))
            return true;

        if (!tweet.TryGetProperty("media", out var media) || media.ValueKind != JsonValueKind.Object)
            return false;

        if (HasObject(media, "external") || HasObject(media, "broadcast"))
            return true;

        // Check each collection separately: `all` repeats the photos/videos arrays.
        foreach (string collection in new[] { "all", "photos", "videos" }) {
            if (!media.TryGetProperty(collection, out var items) || items.ValueKind != JsonValueKind.Array)
                continue;

            if (items.GetArrayLength() > 1 || (collection == "videos" && items.GetArrayLength() > 0))
                return true;

            foreach (var item in items.EnumerateArray()) {
                if (item.ValueKind != JsonValueKind.Object
                    || !item.TryGetProperty("type", out var type)
                    || type.ValueKind != JsonValueKind.String
                    || type.GetString() != "photo")
                    return true;
            }
        }

        return false;
    }

    static bool HasObject(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Object;

    sealed record PostInfo(bool NeedsFixup, bool Translate);
    sealed record CachedPost(Lazy<Task<PostInfo?>> Info, DateTimeOffset ExpiresAt);
}

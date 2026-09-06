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

    public async Task<string> ConvertAsync(string content) {
        var replacements = new Dictionary<string, string>();
        var languages = new Dictionary<string, string?>();
        foreach (var raw in Links.Matches(content).Select(m => m.Value.TrimEnd('.', ',', '!', '?', ';', ':')).Distinct()) {
            var url = new UriBuilder(raw.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? raw : "https://" + raw) {
                Scheme = "https", Host = "fixupx.com", Port = -1
            };
            var post = PostPath.Match(url.Path);
            if (post.Success) {
                string id = post.Groups["id"].Value;
                if (!languages.TryGetValue(id, out string? language)) {
                    language = await GetLanguageAsync(id);
                    languages[id] = language;
                }

                // Rebuild the path so an existing language suffix is never duplicated.
                url.Path = $"/{post.Groups["user"].Value}/status/{id}{post.Groups["media"].Value}";
                if (NeedsTranslation(language)) url.Path += "/en";
            }

            string converted = url.Uri.AbsoluteUri;
            replacements[raw] = converted;
        }
        return Links.Replace(content, match => {
            string raw = match.Value.TrimEnd('.', ',', '!', '?', ';', ':');
            return replacements[raw] + match.Value[raw.Length..];
        });
    }

    static bool NeedsTranslation(string? language) {
        string? primary = language?.Trim().ToLowerInvariant().Split('-', '_')[0];
        return !string.IsNullOrEmpty(primary) && primary is not ("en" or "fr" or "und" or "zxx" or "unknown");
    }

    async Task<string?> GetLanguageAsync(string id) {
        try {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.fxtwitter.com/status/{id}");
            request.Headers.UserAgent.ParseAdd("LymdunetteBot/1.0 (+https://github.com/Lymdun/LymdunetteBot)");
            using var response = await httpClient.SendAsync(request);
            response.EnsureSuccessStatusCode();
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            if (json.RootElement.ValueKind == JsonValueKind.Object
                && json.RootElement.TryGetProperty("tweet", out var tweet)
                && tweet.ValueKind == JsonValueKind.Object
                && tweet.TryGetProperty("lang", out var lang)
                && lang.ValueKind == JsonValueKind.String) {
                return lang.GetString();
            }
        } catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException) {
            logger.LogWarning(ex, "Could not determine language for X post {PostId}", id);
        }
        return null;
    }
}

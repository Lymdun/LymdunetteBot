using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using LymdunetteBot.Models;

namespace LymdunetteBot.Services;

internal sealed class SteamNewsTracker(HttpClient httpClient, string statePath) {
    const int APP_ID = 3055070;
    const string ANNOUNCEMENTS_FEED = "steam_community_announcements";
    const int PAGE_SIZE = 100;
    const string NEWS_URL = "https://api.steampowered.com/ISteamNews/GetNewsForApp/v2/";

    readonly string _statePath = Path.GetFullPath(statePath);
    SteamNewsState? _state;
    bool _stateNeedsSave;

    internal async Task CheckForUpdatesAsync(
        Func<SteamNewsItem, CancellationToken, Task> notify,
        CancellationToken cancellationToken) {
        _state ??= await ReadStateAsync(cancellationToken);

        // Finish any failed save before sending another notification.
        await SavePendingStateAsync(cancellationToken);
        List<SteamNewsItem> news = await FetchNewsAsync(cancellationToken);

        if (_state == null) {
            // The first successful poll establishes a baseline without posting old news.
            _state = new SteamNewsState {
                SeenIds = news.Select(item => item.Gid).ToHashSet(StringComparer.Ordinal)
            };
            _stateNeedsSave = true;
            await SavePendingStateAsync(cancellationToken);
            return;
        }

        foreach (SteamNewsItem item in news.OrderBy(item => item.Date).ThenBy(item => item.Gid, StringComparer.Ordinal)) {
            if (_state.SeenIds.Contains(item.Gid))
                continue;

            await notify(item, cancellationToken);
            _state.SeenIds.Add(item.Gid);
            _stateNeedsSave = true;
            await SavePendingStateAsync(cancellationToken);
        }
    }

    async Task<List<SteamNewsItem>> FetchNewsAsync(CancellationToken cancellationToken) {
        var news = new Dictionary<string, SteamNewsItem>(StringComparer.Ordinal);
        long? endDate = null;

        while (true) {
            string url = $"{NEWS_URL}?appid={APP_ID}&count={PAGE_SIZE}&maxlength=1000&feeds={ANNOUNCEMENTS_FEED}&l=french&format=json";
            // Steam can serve stale news despite no-cache headers; vary the URL on each request.
            url += $"&_={DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}";
            if (endDate.HasValue)
                url += $"&enddate={endDate.Value}";

            var response = await httpClient.GetFromJsonAsync<SteamNewsResponse>(url, cancellationToken);
            if (response?.AppNews?.AppId != APP_ID || response.AppNews.NewsItems == null)
                throw new InvalidDataException("Steam returned an invalid news response for Fateful Bullet.");

            List<SteamNewsItem> page = response.AppNews.NewsItems;
            foreach (SteamNewsItem item in page) {
                if (item == null || item.AppId != APP_ID || string.IsNullOrWhiteSpace(item.Gid)
                    || item.Date <= 0 || item.Date > 253402300799
                    || string.IsNullOrWhiteSpace(item.Title)
                    || !Uri.TryCreate(item.Url, UriKind.Absolute, out Uri? itemUrl)
                    || (itemUrl.Scheme != Uri.UriSchemeHttps && itemUrl.Scheme != Uri.UriSchemeHttp))
                    throw new InvalidDataException("Steam returned an invalid news item for Fateful Bullet.");

                if (item.FeedName == ANNOUNCEMENTS_FEED)
                    news.TryAdd(item.Gid, item);
            }

            if (page.Count < PAGE_SIZE)
                return news.Values.ToList();

            // Overlap the boundary second so posts sharing a timestamp are not skipped.
            long nextEndDate = page.Min(item => item.Date) + 1;
            if (endDate.HasValue && nextEndDate >= endDate.Value)
                throw new InvalidDataException("Steam news pagination did not advance.");

            endDate = nextEndDate;
        }
    }

    async Task<SteamNewsState?> ReadStateAsync(CancellationToken cancellationToken) {
        try {
            await using var stream = File.OpenRead(_statePath);
            var state = await JsonSerializer.DeserializeAsync<SteamNewsState>(stream, cancellationToken: cancellationToken);
            if (state == null || state.Version != 1 || state.AppId != APP_ID || state.SeenIds == null
                || state.SeenIds.Any(string.IsNullOrWhiteSpace))
                throw new InvalidDataException($"Invalid Steam notification state: {_statePath}");

            return state;
        } catch (FileNotFoundException) {
            return null;
        } catch (DirectoryNotFoundException) {
            return null;
        }
    }

    async Task SavePendingStateAsync(CancellationToken cancellationToken) {
        if (!_stateNeedsSave)
            return;

        Directory.CreateDirectory(Path.GetDirectoryName(_statePath)!);
        string temporaryPath = _statePath + ".tmp";
        try {
            await File.WriteAllTextAsync(temporaryPath, JsonSerializer.Serialize(_state), cancellationToken);
            File.Move(temporaryPath, _statePath, overwrite: true);
            _stateNeedsSave = false;
        } finally {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }

    sealed class SteamNewsState {
        [JsonRequired]
        public int Version { get; init; } = 1;

        [JsonRequired]
        public int AppId { get; init; } = APP_ID;

        [JsonRequired]
        public HashSet<string> SeenIds { get; init; } = [];
    }
}

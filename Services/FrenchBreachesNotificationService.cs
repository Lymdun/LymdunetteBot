using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Disqord;
using Disqord.Bot.Hosting;
using Disqord.Rest;
using Microsoft.Extensions.Logging;

namespace LymdunetteBot.Services;

public class FrenchBreachesNotificationService : DiscordBotService {
    const ulong CHANNEL_ID = 1563595759767654542;
    static readonly TimeSpan pollInterval = TimeSpan.FromMinutes(15);
    static readonly string statePath = Path.Combine(AppContext.BaseDirectory, "data", "french-breaches-state.json");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
        try {
            await Bot.WaitUntilReadyAsync(stoppingToken);

            using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("LymdunetteBot/1.0 (+https://github.com/Zerotistic/LymdunetteBot)");
            // Cloudflare answers requests without an Accept header with a 403 challenge page.
            httpClient.DefaultRequestHeaders.Accept.ParseAdd("application/rss+xml, application/xml;q=0.9, */*;q=0.8");
            var tracker = new FrenchBreachesFeedTracker(httpClient, statePath);

            Logger.LogInformation(
                "Watching FrenchBreaches RSS for channel {ChannelId}; state: {StatePath}",
                CHANNEL_ID,
                statePath);

            while (!stoppingToken.IsCancellationRequested) {
                try {
                    await tracker.CheckForUpdatesAsync(SendNotificationAsync, stoppingToken);
                } catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) {
                    break;
                } catch (Exception ex) {
                    Logger.LogError(ex, "Failed to check or deliver FrenchBreaches updates; retrying in 15 minutes");
                }

                await Task.Delay(pollInterval, stoppingToken);
            }
        } catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) {
            // Normal host shutdown, including cancellation while waiting for Discord.
        }
    }

    async Task SendNotificationAsync(FrenchBreachesItem item, CancellationToken cancellationToken) {
        var embed = new LocalEmbed()
            .WithTitle($"Nouvelle fuite référencée : {PlainText(item.Title, 180)}")
            .WithUrl(item.Url)
            .WithColor(Color.Orange);

        string summary = PlainText(item.Summary, 900);
        if (!string.IsNullOrWhiteSpace(summary))
            embed.WithDescription(summary);

        string status = GetStatusLabel(item.Category);
        if (!string.IsNullOrWhiteSpace(status))
            embed.AddField("Statut", status, true);

        if (item.PublishedAt.HasValue)
            embed.WithTimestamp(item.PublishedAt.Value);

        await Bot.SendMessageAsync(
            CHANNEL_ID,
            new LocalMessage()
                .WithEmbeds(embed)
                .WithAllowedMentions(LocalAllowedMentions.None),
            cancellationToken: cancellationToken);

        Logger.LogInformation("Sent FrenchBreaches alert {FeedId}: {Title}", item.Id, item.Title);
    }

    static string GetStatusLabel(string category) {
        return category.ToLowerInvariant() switch {
            "credible" => "Revendiquée (crédible)",
            "confirmed" => "Confirmée",
            "unreliable" => "Peu fiable",
            _ => category
        };
    }

    static string PlainText(string? text, int maxLength) {
        string value = Regex.Replace(text ?? string.Empty, "<[^>]*>", " ",
            RegexOptions.None, TimeSpan.FromSeconds(1));
        value = Regex.Replace(WebUtility.HtmlDecode(value), @"\s+", " ",
            RegexOptions.None, TimeSpan.FromSeconds(1)).Trim();

        if (value.Length <= maxLength)
            return value;

        int length = maxLength - 1;
        if (char.IsHighSurrogate(value[length - 1]))
            length--;

        return value[..length] + "…";
    }
}

internal sealed class FrenchBreachesFeedTracker(HttpClient httpClient, string statePath) {
    const string FEED_URL = "https://frenchbreaches.com/feed.xml";
    readonly string _statePath = Path.GetFullPath(statePath);
    FrenchBreachesState? _state;
    bool _stateNeedsSave;

    internal async Task CheckForUpdatesAsync(
        Func<FrenchBreachesItem, CancellationToken, Task> notify,
        CancellationToken cancellationToken) {
        _state ??= await ReadStateAsync(cancellationToken);
        await SavePendingStateAsync(cancellationToken);

        List<FrenchBreachesItem> items = await FetchItemsAsync(cancellationToken);
        if (_state == null) {
            // Establish a baseline silently so existing feed entries are not announced on first run.
            _state = new FrenchBreachesState {
                SeenIds = items.Select(item => item.Id).ToHashSet(StringComparer.Ordinal)
            };
            _stateNeedsSave = true;
            await SavePendingStateAsync(cancellationToken);
            return;
        }

        foreach (FrenchBreachesItem item in items
            .OrderBy(item => item.PublishedAt ?? DateTimeOffset.MinValue)
            .ThenBy(item => item.Id, StringComparer.Ordinal)) {
            if (_state.SeenIds.Contains(item.Id))
                continue;

            await notify(item, cancellationToken);
            _state.SeenIds.Add(item.Id);
            _stateNeedsSave = true;
            await SavePendingStateAsync(cancellationToken);
        }
    }

    async Task<List<FrenchBreachesItem>> FetchItemsAsync(CancellationToken cancellationToken) {
        await using var stream = await httpClient.GetStreamAsync(FEED_URL, cancellationToken);
        using XmlReader reader = XmlReader.Create(stream, new XmlReaderSettings {
            Async = true,
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = 5_000_000
        });
        XDocument document = await XDocument.LoadAsync(reader, LoadOptions.None, cancellationToken);
        XElement channel = document.Root?.Element("channel")
            ?? throw new InvalidDataException("FrenchBreaches RSS feed has no channel element");

        var items = new List<FrenchBreachesItem>();
        foreach (XElement element in channel.Elements("item")) {
            string id = element.Element("guid")?.Value.Trim() ?? string.Empty;
            string title = element.Element("title")?.Value.Trim() ?? string.Empty;
            string url = element.Element("link")?.Value.Trim() ?? string.Empty;
            string summary = element.Element("description")?.Value ?? string.Empty;
            string category = element.Element("category")?.Value.Trim() ?? string.Empty;

            if (string.IsNullOrWhiteSpace(id)
                || string.IsNullOrWhiteSpace(title)
                || !Uri.TryCreate(url, UriKind.Absolute, out Uri? itemUrl)
                || itemUrl.Scheme != Uri.UriSchemeHttps
                || !string.Equals(itemUrl.Host, "frenchbreaches.com", StringComparison.OrdinalIgnoreCase)) {
                throw new InvalidDataException("FrenchBreaches RSS feed contains an invalid item");
            }

            DateTimeOffset? publishedAt = DateTimeOffset.TryParse(
                element.Element("pubDate")?.Value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal,
                out DateTimeOffset parsedDate)
                ? parsedDate
                : null;

            items.Add(new FrenchBreachesItem(id, title, itemUrl.ToString(), summary, category, publishedAt));
        }

        if (items.Count == 0)
            throw new InvalidDataException("FrenchBreaches RSS feed contains no items");

        return items;
    }

    async Task<FrenchBreachesState?> ReadStateAsync(CancellationToken cancellationToken) {
        try {
            await using var stream = File.OpenRead(_statePath);
            FrenchBreachesState? state = await JsonSerializer.DeserializeAsync<FrenchBreachesState>(
                stream,
                cancellationToken: cancellationToken);
            if (state == null || state.Version != 1 || state.SeenIds == null || state.SeenIds.Any(string.IsNullOrWhiteSpace))
                throw new InvalidDataException($"Invalid FrenchBreaches notification state: {_statePath}");

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

    sealed class FrenchBreachesState {
        [JsonRequired]
        public int Version { get; init; } = 1;

        [JsonRequired]
        public HashSet<string> SeenIds { get; init; } = new(StringComparer.Ordinal);
    }
}

internal sealed record FrenchBreachesItem(
    string Id,
    string Title,
    string Url,
    string Summary,
    string Category,
    DateTimeOffset? PublishedAt);

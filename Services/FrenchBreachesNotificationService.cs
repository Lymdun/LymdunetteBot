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
    static readonly TimeZoneInfo digestTimeZone = TimeZoneInfo.FindSystemTimeZoneById("Europe/Paris");
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
                "Watching FrenchBreaches RSS for a daily 21:00 Europe/Paris digest in channel {ChannelId}; state: {StatePath}",
                CHANNEL_ID,
                statePath);

            while (!stoppingToken.IsCancellationRequested) {
                try {
                    await tracker.CheckForUpdatesAsync(stoppingToken);
                } catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) {
                    break;
                } catch (Exception ex) {
                    Logger.LogError(ex, "Failed to check FrenchBreaches RSS; retrying");
                }

                try {
                    DateTimeOffset parisNow = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, digestTimeZone);
                    await tracker.SendDailyDigestIfDueAsync(parisNow, SendDigestAsync, stoppingToken);
                } catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) {
                    break;
                } catch (Exception ex) {
                    Logger.LogError(ex, "Failed to send the FrenchBreaches daily digest; it will be retried");
                }

                await Task.Delay(GetNextWakeDelay(DateTimeOffset.UtcNow), stoppingToken);
            }
        } catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) {
            // Normal host shutdown, including cancellation while waiting for Discord.
        }
    }

    async Task SendDigestAsync(
        IReadOnlyList<FrenchBreachesItem> items,
        DateOnly digestDate,
        CancellationToken cancellationToken) {
        LocalMessage message = CreateDigestMessage(items, digestDate);
        await Bot.SendMessageAsync(
            CHANNEL_ID,
            message,
            cancellationToken: cancellationToken);

        Logger.LogInformation("Sent FrenchBreaches daily digest for {DigestDate} with {ItemCount} entries",
            digestDate,
            items.Count);
    }

    static TimeSpan GetNextWakeDelay(DateTimeOffset utcNow) {
        DateTimeOffset parisNow = TimeZoneInfo.ConvertTime(utcNow, digestTimeZone);
        DateTime nextDigestLocal = parisNow.Date.AddHours(21);
        if (parisNow.DateTime >= nextDigestLocal)
            nextDigestLocal = nextDigestLocal.AddDays(1);

        DateTimeOffset nextDigestUtc = new(
            TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(nextDigestLocal, DateTimeKind.Unspecified), digestTimeZone),
            TimeSpan.Zero);
        TimeSpan untilDigest = nextDigestUtc - utcNow;
        return untilDigest < pollInterval ? untilDigest : pollInterval;
    }

    static LocalMessage CreateDigestMessage(IReadOnlyList<FrenchBreachesItem> items, DateOnly digestDate) {
        List<string> lines = items.Select(item =>
            $"• **[{EscapeMarkdownLinkText(PlainText(item.Title, 100))}]({item.Url})** — {GetStatusLabel(item.Category)}\n  {PlainText(item.Summary, 100)}")
            .ToList();

        if (lines.Sum(line => line.Length) > 5_300) {
            lines = items.Select(item =>
                $"• **[{EscapeMarkdownLinkText(PlainText(item.Title, 100))}]({item.Url})** — {GetStatusLabel(item.Category)}")
                .ToList();
        }

        var descriptions = new List<string>();
        var current = new System.Text.StringBuilder();
        foreach (string line in lines) {
            int addedLength = line.Length + (current.Length == 0 ? 0 : 2);
            if (current.Length > 0 && current.Length + addedLength > 3_500) {
                descriptions.Add(current.ToString());
                current.Clear();
            }

            if (current.Length > 0)
                current.AppendLine().AppendLine();
            current.Append(line);
        }

        if (current.Length > 0)
            descriptions.Add(current.ToString());

        LocalEmbed[] embeds = descriptions.Select((description, index) => new LocalEmbed()
            .WithTitle(index == 0
                ? $"Digest quotidien FrenchBreaches — {digestDate:dd/MM/yyyy}"
                : "Suite du digest")
            .WithDescription(description)
            .WithColor(Color.Orange))
            .ToArray();

        return new LocalMessage()
            .WithContent($"🔐 **Nouvelles fuites et cyberattaques — {digestDate:dd/MM/yyyy}** ({items.Count})")
            .WithEmbeds(embeds)
            .WithAllowedMentions(LocalAllowedMentions.None);
    }

    static string EscapeMarkdownLinkText(string text) {
        return text.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("[", "\\[", StringComparison.Ordinal)
            .Replace("]", "\\]", StringComparison.Ordinal);
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

        bool changed = false;
        foreach (FrenchBreachesItem item in items
            .OrderBy(item => item.PublishedAt ?? DateTimeOffset.MinValue)
            .ThenBy(item => item.Id, StringComparer.Ordinal)) {
            if (_state.SeenIds.Contains(item.Id))
                continue;

            _state.SeenIds.Add(item.Id);
            _state.PendingItems.Add(item);
            changed = true;
        }

        if (changed) {
            _stateNeedsSave = true;
            await SavePendingStateAsync(cancellationToken);
            Logger.LogInformation("Queued {ItemCount} new FrenchBreaches entries for the daily digest", _state.PendingItems.Count);
        }
    }

    internal async Task SendDailyDigestIfDueAsync(
        DateTimeOffset parisNow,
        Func<IReadOnlyList<FrenchBreachesItem>, DateOnly, CancellationToken, Task> sendDigest,
        CancellationToken cancellationToken) {
        _state ??= await ReadStateAsync(cancellationToken);
        if (_state == null)
            return;

        DateOnly today = DateOnly.FromDateTime(parisNow.DateTime);
        if (TimeOnly.FromDateTime(parisNow.DateTime) < new TimeOnly(21, 0)
            || _state.LastDigestDate == today)
            return;

        if (_state.PendingItems.Count > 0) {
            FrenchBreachesItem[] digestItems = _state.PendingItems
                .OrderBy(item => item.PublishedAt ?? DateTimeOffset.MinValue)
                .ThenBy(item => item.Id, StringComparer.Ordinal)
                .ToArray();
            await sendDigest(digestItems, today, cancellationToken);
            _state.PendingItems.Clear();
        }

        _state.LastDigestDate = today;
        _stateNeedsSave = true;
        await SavePendingStateAsync(cancellationToken);
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
            if (state == null || state.Version != 1 || state.SeenIds == null || state.PendingItems == null
                || state.SeenIds.Any(string.IsNullOrWhiteSpace))
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

        public List<FrenchBreachesItem> PendingItems { get; init; } = [];

        public DateOnly? LastDigestDate { get; set; }
    }
}

internal sealed record FrenchBreachesItem(
    string Id,
    string Title,
    string Url,
    string Summary,
    string Category,
    DateTimeOffset? PublishedAt);

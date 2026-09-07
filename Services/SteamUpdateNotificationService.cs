using System.Net;
using System.Text.RegularExpressions;
using Disqord;
using Disqord.Bot.Hosting;
using Disqord.Gateway;
using Disqord.Rest;
using LymdunetteBot.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace LymdunetteBot.Services;

public class SteamUpdateNotificationService(IConfiguration configuration) : DiscordBotService {
    const ulong CHANNEL_ID = 1372263024638824669;
    static readonly TimeSpan pollInterval = TimeSpan.FromMinutes(5);
    readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);

    protected override ValueTask OnReady(ReadyEventArgs e) {
        _ready.TrySetResult();
        return default;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
        try {
            await _ready.Task.WaitAsync(stoppingToken);

            using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("LymdunetteBot/1.0");
            string statePath = configuration["STEAM_NEWS_STATE_PATH"] ?? Path.Combine("data", "steam-news-3055070.json");
            var tracker = new SteamNewsTracker(httpClient, statePath);

            Logger.LogInformation("Watching Fateful Bullet Steam announcements for channel {ChannelId}; state: {StatePath}",
                CHANNEL_ID, Path.GetFullPath(statePath));

            while (!stoppingToken.IsCancellationRequested) {
                try {
                    await tracker.CheckForUpdatesAsync(SendNotificationAsync, stoppingToken);
                } catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) {
                    break;
                } catch (Exception ex) {
                    Logger.LogError(ex, "Failed to check or deliver Steam updates; retrying in five minutes");
                }

                await Task.Delay(pollInterval, stoppingToken);
            }
        } catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) {
            // Normal host shutdown, including cancellation while waiting for Discord.
        }
    }

    async Task SendNotificationAsync(SteamNewsItem item, CancellationToken cancellationToken) {
        await Bot.SendMessageAsync(CHANNEL_ID, CreateMessage(item), cancellationToken: cancellationToken);
        Logger.LogInformation("Sent Steam announcement {NewsId}: {Title}", item.Gid, item.Title);
    }

    static LocalMessage CreateMessage(SteamNewsItem item) {
        var embed = new LocalEmbed()
            .WithTitle(PlainText(item.Title, 256))
            .WithUrl(item.Url)
            .WithColor(Color.Orange)
            .WithTimestamp(DateTimeOffset.FromUnixTimeSeconds(item.Date));

        string summary = PlainText(item.Contents, 1500);
        if (!string.IsNullOrWhiteSpace(summary))
            embed.WithDescription(summary);

        return new LocalMessage()
            .WithContent("🎮 Nouvelle mise à jour de **Fateful Bullet** sur Steam !")
            .WithEmbeds(embed)
            .WithAllowedMentions(LocalAllowedMentions.None);
    }

    static string PlainText(string? text, int maxLength) {
        string value = Regex.Replace(text ?? string.Empty,
            @"<[^>]*>|\[(?:/?(?:b|i|u|strike|h[1-6]|p|list|olist|url|img|previewyoutube|table|tr|td|th|code|quote|spoiler)(?:=[^\]]*)?|\*)\]", " ",
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

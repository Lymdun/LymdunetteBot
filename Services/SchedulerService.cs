using System.Text.Json;
using Cronos;
using Disqord;
using Disqord.Bot.Hosting;
using Disqord.Gateway;
using Disqord.Rest;
using LymdunetteBot.Models;
using Microsoft.Extensions.Logging;

namespace LymdunetteBot.Services;

public class SchedulerService : DiscordBotService {
    const ulong CHANNEL_ID = 1368277691765227675;
    static readonly TimeZoneInfo frenchTimeZone = TimeZoneInfo.FindSystemTimeZoneById("Romance Standard Time");
    static readonly HttpClient weatherHttpClient = new() {
        Timeout = TimeSpan.FromSeconds(10)
    };

    const string CRON_EXPRESSION = "0 12 * * MON,TUE";
    const string DAILY_WEEK_CRON_EXPRESSION = "59 5 * * MON-FRI";
    const string HACHIMI_MICHI_MAMBO_CRON_EXPRESSION = "0 10 * * *";
    const string BURNICE_HEAT_WAVE_CRON_EXPRESSION = "0 9 * * *";
    const string SEPTEMBER_CRON_EXPRESSION = "0 14 * 9 *";
    const string VIGILANCE_API_URL = "https://data.smartidf.services/api/explore/v2.1/catalog/datasets/weatherref-france-vigilance-meteo-departement/records";
    const string VIGILANCE_QUERY = "phenomenon='canicule' and color_id >= 3 and domain_id in ('69','75')";

    protected override ValueTask OnReady(ReadyEventArgs e) {
        Logger.LogInformation("SchedulerService Ready fired!");
        return default;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
        var cronExpression = CronExpression.Parse(CRON_EXPRESSION);
        var dailyWeekCronExpression = CronExpression.Parse(DAILY_WEEK_CRON_EXPRESSION);
        var hachimiMichiMamboCronExpression = CronExpression.Parse(HACHIMI_MICHI_MAMBO_CRON_EXPRESSION);
        var burniceHeatWaveCronExpression = CronExpression.Parse(BURNICE_HEAT_WAVE_CRON_EXPRESSION);
        var septemberCronExpression = CronExpression.Parse(SEPTEMBER_CRON_EXPRESSION);

        while (!stoppingToken.IsCancellationRequested) {
            DateTimeOffset now = DateTimeOffset.UtcNow;

            DateTimeOffset? nextMemeTime  = cronExpression.GetNextOccurrence(now, frenchTimeZone);
            DateTimeOffset? nextDailyWeekOccurrence = dailyWeekCronExpression.GetNextOccurrence(now, frenchTimeZone);
            DateTimeOffset? nextHachimiMichiMamboOccurrence = hachimiMichiMamboCronExpression.GetNextOccurrence(now, frenchTimeZone);
            DateTimeOffset? nextBurniceHeatWaveOccurrence = burniceHeatWaveCronExpression.GetNextOccurrence(now, frenchTimeZone);
            DateTimeOffset? nextSeptemberOccurrence = septemberCronExpression.GetNextOccurrence(now, frenchTimeZone);

            DateTimeOffset? nextOccurrence = GetNextOccurrence(
                nextMemeTime,
                nextDailyWeekOccurrence,
                nextHachimiMichiMamboOccurrence,
                nextBurniceHeatWaveOccurrence,
                nextSeptemberOccurrence);

            if (nextOccurrence.HasValue) {
                TimeSpan delay = nextOccurrence.Value - now;

                if (delay > TimeSpan.Zero) {
                    await Task.Delay(delay, stoppingToken);
                }

                try {
                    if (nextOccurrence == nextMemeTime) {
                        await SendDailyMemeIfApplicable(nextOccurrence.Value.DayOfWeek, stoppingToken);
                    }

                    if (nextOccurrence == nextHachimiMichiMamboOccurrence) {
                        await SendMeme(stoppingToken, Meme.HachimiMichiMambo());
                        Logger.LogInformation("SendHachimiMichiMambo fired!");
                    }

                    if (nextOccurrence == nextBurniceHeatWaveOccurrence) {
                        await SendBurniceIfHeatWave(nextOccurrence.Value, stoppingToken);
                    }

                    if (nextOccurrence == nextSeptemberOccurrence) {
                        await SendMeme(stoppingToken, Meme.September());
                        Logger.LogInformation("SendSeptember fired!");
                    }
                } catch (Exception ex) {
                    Logger.LogError(ex, "Failure while running scheduled task");
                }
            } else {
                await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);
            }
        }

        Logger.LogWarning("SchedulerService has stopped");
    }

    async Task SendBurniceIfHeatWave(DateTimeOffset scheduledTime, CancellationToken stoppingToken) {
        if (!await HasHeatWaveWarningForTargetLocations(scheduledTime, stoppingToken)) {
            Logger.LogInformation("No active canicule warning for Paris or Lyon/Rhone");
            return;
        }

        await SendMeme(stoppingToken, Meme.Burnice());
        Logger.LogInformation("SendBurnice fired!");
    }

    async Task<bool> HasHeatWaveWarningForTargetLocations(DateTimeOffset scheduledTime, CancellationToken stoppingToken) {
        string requestUri = $"{VIGILANCE_API_URL}?limit=20&where={Uri.EscapeDataString(VIGILANCE_QUERY)}";

        try {
            using HttpResponseMessage response = await weatherHttpClient.GetAsync(requestUri, stoppingToken);
            if (!response.IsSuccessStatusCode) {
                Logger.LogWarning(
                    "Could not fetch canicule vigilance data. HTTP status: {StatusCode}",
                    response.StatusCode);
                return false;
            }

            await using Stream responseStream = await response.Content.ReadAsStreamAsync(stoppingToken);
            using JsonDocument document = await JsonDocument.ParseAsync(responseStream, cancellationToken: stoppingToken);

            if (!document.RootElement.TryGetProperty("results", out JsonElement results) ||
                results.ValueKind != JsonValueKind.Array) {
                Logger.LogWarning("Canicule vigilance response does not contain a results array");
                return false;
            }

            foreach (JsonElement result in results.EnumerateArray()) {
                if (IsActiveHeatWaveWarning(result, scheduledTime)) {
                    TryGetString(result, "domain_id", out string? parsedDomainId);
                    TryGetString(result, "color", out string? parsedColor);
                    Logger.LogInformation(
                        "Active canicule warning found for domain {DomainId} with color {Color}",
                        parsedDomainId ?? "unknown",
                        parsedColor ?? "unknown");
                    return true;
                }
            }

            return false;
        } catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) {
            throw;
        } catch (Exception ex) {
            Logger.LogError(ex, "Failed to check canicule vigilance data");
            return false;
        }
    }

    static bool IsActiveHeatWaveWarning(JsonElement result, DateTimeOffset scheduledTime) {
        if (!TryGetString(result, "domain_id", out string? domainId) ||
            (domainId != "69" && domainId != "75")) {
            return false;
        }

        if (!TryGetString(result, "phenomenon", out string? phenomenon) ||
            !string.Equals(phenomenon, "canicule", StringComparison.OrdinalIgnoreCase)) {
            return false;
        }

        if (!TryGetInt32(result, "color_id", out int colorId) || colorId < 3) {
            return false;
        }

        if (!TryGetDateTimeOffset(result, "begin_time", out DateTimeOffset beginTime) ||
            !TryGetDateTimeOffset(result, "end_time", out DateTimeOffset endTime)) {
            return false;
        }

        return beginTime <= scheduledTime && scheduledTime < endTime;
    }

    static bool TryGetString(JsonElement element, string propertyName, out string? value) {
        value = null;

        if (!element.TryGetProperty(propertyName, out JsonElement property) ||
            property.ValueKind != JsonValueKind.String) {
            return false;
        }

        value = property.GetString();
        return !string.IsNullOrWhiteSpace(value);
    }

    static bool TryGetInt32(JsonElement element, string propertyName, out int value) {
        value = default;

        return element.TryGetProperty(propertyName, out JsonElement property) &&
               property.TryGetInt32(out value);
    }

    static bool TryGetDateTimeOffset(JsonElement element, string propertyName, out DateTimeOffset value) {
        value = default;

        return element.TryGetProperty(propertyName, out JsonElement property) &&
               property.ValueKind == JsonValueKind.String &&
               property.TryGetDateTimeOffset(out value);
    }

    async Task SendDailyMemeIfApplicable(DayOfWeek dayOfWeek, CancellationToken stoppingToken) {
        switch (dayOfWeek) {
            case DayOfWeek.Monday:
                await SendMeme(stoppingToken, Meme.MondayMeme());
                Logger.LogInformation("SendMorningMessage fired!");
                break;
            case DayOfWeek.Tuesday:
                await SendMeme(stoppingToken, Meme.TuesdayMeme());
                Logger.LogInformation("SendMorningMessage fired!");
                break;
            default:
                Logger.LogInformation("No meme to send today");
                break;
        }
    }

    async Task SendMeme(CancellationToken stoppingToken, Meme meme) {
        string filePath = Path.Combine("./resources", meme.ImageUrl);

        if (!File.Exists(filePath)) {
            Logger.LogError("This meme does not exist: {FilePath}", filePath);
            return;
        }

        var message = new LocalMessage();
        if (meme.Content != null) {
            message.WithContent(meme.Content);
        }

        await using (var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read)) {
            message.WithAttachments(LocalAttachment.File(fs));
            await Bot.SendMessageAsync(CHANNEL_ID, message, cancellationToken: stoppingToken);
        }
    }

    static DateTimeOffset? GetNextOccurrence(params DateTimeOffset?[] occurrences) {
        DateTimeOffset? nextOccurrence = null;

        foreach (DateTimeOffset? occurrence in occurrences) {
            if (!occurrence.HasValue)
                continue;

            if (!nextOccurrence.HasValue || occurrence.Value < nextOccurrence.Value) {
                nextOccurrence = occurrence;
            }
        }

        return nextOccurrence;
    }

    public static TimeZoneInfo GetTimeZoneInfo() => frenchTimeZone;
}

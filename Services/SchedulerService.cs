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

    const string CRON_EXPRESSION = "0 12 * * MON,TUE";
    const string DAILY_WEEK_CRON_EXPRESSION = "59 5 * * MON-FRI";
    const string HACHIMI_MICHI_MAMBO_CRON_EXPRESSION = "0 10 * * *";

    protected override ValueTask OnReady(ReadyEventArgs e) {
        Logger.LogInformation("SchedulerService Ready fired!");
        return default;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
        var cronExpression = CronExpression.Parse(CRON_EXPRESSION);
        var dailyWeekCronExpression = CronExpression.Parse(DAILY_WEEK_CRON_EXPRESSION);
        var hachimiMichiMamboCronExpression = CronExpression.Parse(HACHIMI_MICHI_MAMBO_CRON_EXPRESSION);

        while (!stoppingToken.IsCancellationRequested) {
            DateTimeOffset now = DateTimeOffset.UtcNow;

            DateTimeOffset? nextMemeTime  = cronExpression.GetNextOccurrence(now, frenchTimeZone);
            DateTimeOffset? nextDailyWeekOccurrence = dailyWeekCronExpression.GetNextOccurrence(now, frenchTimeZone);
            DateTimeOffset? nextHachimiMichiMamboOccurrence = hachimiMichiMamboCronExpression.GetNextOccurrence(now, frenchTimeZone);

            DateTimeOffset? nextOccurrence = GetNextOccurrence(
                nextMemeTime,
                nextDailyWeekOccurrence,
                nextHachimiMichiMamboOccurrence);

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
                } catch (Exception ex) {
                    Logger.LogError(ex, "Failure while sending meme");
                }
            } else {
                await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);
            }
        }

        Logger.LogWarning("SchedulerService has stopped");
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

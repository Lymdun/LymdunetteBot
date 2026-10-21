using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Disqord;
using Disqord.Bot.Hosting;
using Disqord.Rest;
using Microsoft.Extensions.Logging;

namespace LymdunetteBot.Services;

public class LeaderboardNotificationService : DiscordBotService {
    const ulong CHANNEL_ID = 1495814979138617535;
    const string LIVEBENCH_URL = "https://livebench.ai/";
    const string DEEPSWE_URL = "https://deepswe.datacurve.ai/";
    const string DEEPSWE_DATA_URL = "https://deepswe.datacurve.ai/artifacts/v1.1/leaderboard-live.json";
    const int POLL_INTERVAL_MINUTES = 30;
    const int MAX_NOTIFICATIONS_PER_POLL = 5;

    static readonly HttpClient httpClient = CreateHttpClient();
    static readonly TimeSpan POLL_INTERVAL = TimeSpan.FromMinutes(POLL_INTERVAL_MINUTES);
    static readonly string STATE_PATH = Path.Combine(
        AppContext.BaseDirectory,
        "data",
        "leaderboard-monitor-state.json");

    readonly LiveBenchClient liveBenchClient = new(LIVEBENCH_URL, GetStringAsync);

    LeaderboardMonitorState state = new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
        try {
            await Bot.WaitUntilReadyAsync(stoppingToken);
            state = await LoadStateAsync(stoppingToken);

            while (!stoppingToken.IsCancellationRequested) {
                try {
                    await RunCheckCycleAsync(stoppingToken);
                    await Task.Delay(POLL_INTERVAL, stoppingToken);
                } catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) {
                    break;
                } catch (Exception ex) {
                    Logger.LogError(ex, "Leaderboard monitoring cycle failed");
                    await Task.Delay(POLL_INTERVAL, stoppingToken);
                }
            }
        } catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) {
            // Normal hosted-service shutdown.
        } finally {
            Logger.LogInformation("LeaderboardNotificationService has stopped");
        }
    }

    async Task RunCheckCycleAsync(CancellationToken stoppingToken) {
        var pending = new List<PendingNotification>();
        bool stateChanged = await CollectLiveBenchNotificationsAsync(pending, stoppingToken);
        stateChanged |= await CollectDeepSweNotificationsAsync(pending, stoppingToken);

        IReadOnlyList<PendingNotification> batch = LeaderboardMonitorLogic.TakeNotificationBatch(
            pending,
            MAX_NOTIFICATIONS_PER_POLL);

        int sentCount = 0;
        foreach (PendingNotification notification in batch) {
            try {
                await Bot.SendMessageAsync(
                    CHANNEL_ID,
                    new LocalMessage().WithEmbeds(notification.Embed),
                    cancellationToken: stoppingToken);
            } catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) {
                throw;
            } catch (Exception ex) {
                Logger.LogError(
                    ex,
                    "Failed to post {Leaderboard} entry {EntryId}; remaining notifications will be retried",
                    notification.Source,
                    notification.EntryId);
                break;
            }

            MarkAsSeen(notification);
            sentCount++;
            stateChanged = true;
            Logger.LogInformation(
                "Posted new {Leaderboard} entry {EntryId}",
                notification.Source,
                notification.EntryId);
        }

        int deferredCount = pending.Count - sentCount;
        if (deferredCount > 0) {
            Logger.LogInformation(
                "Deferred {DeferredCount} leaderboard notifications to a later poll to avoid a channel burst",
                deferredCount);
        }

        if (stateChanged) {
            await SaveStateAsync(stoppingToken);
        }
    }

    async Task<bool> CollectLiveBenchNotificationsAsync(
        List<PendingNotification> pending,
        CancellationToken stoppingToken) {
        try {
            LiveBenchSnapshot snapshot = await liveBenchClient.FetchSnapshotAsync(stoppingToken);
            LeaderboardMonitorLogic.EnsureReleaseDoesNotRegress(
                snapshot.ReleaseDate,
                state.LiveBenchReleaseDate);

            // Each LiveBench generation names its models differently, so a new generation starts a new
            // baseline instead of announcing every model on its leaderboard.
            if (LeaderboardMonitorLogic.RequiresLiveBenchBaseline(state, snapshot.Generation)) {
                state.LiveBenchEntries.UnionWith(snapshot.ModelIds);
                state.LiveBenchReleaseDate = snapshot.ReleaseDate;
                state.LiveBenchGeneration = snapshot.Generation;
                state.LiveBenchInitialized = true;
                Logger.LogInformation(
                    "Initialized LiveBench {Generation} monitor with {EntryCount} existing entries from release {ReleaseDate}",
                    snapshot.Generation,
                    snapshot.ModelIds.Count,
                    snapshot.ReleaseDate);
                return true;
            }

            bool stateChanged = state.LiveBenchReleaseDate != snapshot.ReleaseDate;
            state.LiveBenchReleaseDate = snapshot.ReleaseDate;

            pending.AddRange(snapshot.ModelIds
                .Where(modelId => !state.LiveBenchEntries.Contains(modelId))
                .Select(modelId => new PendingNotification(
                    LeaderboardSource.LiveBench,
                    modelId,
                    CreateLiveBenchEmbed(modelId, snapshot.ReleaseDate))));

            return stateChanged;
        } catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) {
            throw;
        } catch (Exception ex) {
            Logger.LogError(ex, "Failed to check LiveBench for new entries");
            return false;
        }
    }

    async Task<bool> CollectDeepSweNotificationsAsync(
        List<PendingNotification> pending,
        CancellationToken stoppingToken) {
        try {
            DeepSweSnapshot snapshot = await FetchDeepSweSnapshotAsync(stoppingToken);
            List<DeepSweEntry> entries = snapshot.Rows
                .Where(entry => !string.IsNullOrWhiteSpace(entry.EntryId) && !string.IsNullOrWhiteSpace(entry.Model))
                .ToList();

            if (!state.DeepSweInitialized) {
                state.DeepSweEntries.UnionWith(entries.Select(entry => entry.EntryId));
                state.DeepSweInitialized = true;
                Logger.LogInformation(
                    "Initialized DeepSWE monitor with {EntryCount} existing entries generated at {GeneratedAt}",
                    entries.Count,
                    snapshot.GeneratedAt);
                return true;
            }

            pending.AddRange(entries
                .Where(entry => !state.DeepSweEntries.Contains(entry.EntryId))
                .Select(entry => new PendingNotification(
                    LeaderboardSource.DeepSwe,
                    entry.EntryId,
                    CreateDeepSweEmbed(entry))));

            return false;
        } catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) {
            throw;
        } catch (Exception ex) {
            Logger.LogError(ex, "Failed to check DeepSWE for new entries");
            return false;
        }
    }

    static LocalEmbed CreateLiveBenchEmbed(string modelId, DateOnly releaseDate) {
        return new LocalEmbed()
            .WithTitle("New LiveBench entry")
            .WithDescription($"`{EscapeInlineCode(modelId)}` was added to the leaderboard.")
            .AddField("Release", releaseDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), true)
            .WithUrl(LIVEBENCH_URL)
            .WithColor(Color.Orange);
    }

    static LocalEmbed CreateDeepSweEmbed(DeepSweEntry entry) {
        string effort = string.IsNullOrWhiteSpace(entry.ReasoningEffort)
            ? "default"
            : entry.ReasoningEffort;
        string score = entry.PassAt1?.ToString("P1", CultureInfo.InvariantCulture) ?? "unknown";

        return new LocalEmbed()
            .WithTitle("New DeepSWE entry")
            .WithDescription($"`{EscapeInlineCode(entry.Model!)}` was added to the leaderboard.")
            .AddField("Reasoning effort", effort, true)
            .AddField("Pass@1", score, true)
            .WithUrl(DEEPSWE_URL)
            .WithColor(Color.Orange);
    }

    void MarkAsSeen(PendingNotification notification) {
        if (notification.Source == LeaderboardSource.LiveBench) {
            state.LiveBenchEntries.Add(notification.EntryId);
        } else {
            state.DeepSweEntries.Add(notification.EntryId);
        }
    }

    static async Task<DeepSweSnapshot> FetchDeepSweSnapshotAsync(CancellationToken stoppingToken) {
        string json = await GetStringAsync(DEEPSWE_DATA_URL, stoppingToken);
        DeepSweResponse? response = JsonSerializer.Deserialize<DeepSweResponse>(json);

        if (response?.Rows == null) {
            throw new InvalidDataException("DeepSWE's leaderboard response does not contain rows");
        }

        return new DeepSweSnapshot(response.GeneratedAt, response.Rows);
    }

    async Task<LeaderboardMonitorState> LoadStateAsync(CancellationToken stoppingToken) {
        if (!File.Exists(STATE_PATH)) {
            return new LeaderboardMonitorState();
        }

        try {
            string json = await File.ReadAllTextAsync(STATE_PATH, stoppingToken);
            return LeaderboardMonitorLogic.DeserializeState(json);
        } catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) {
            throw;
        } catch (Exception ex) {
            Logger.LogError(
                ex,
                "Could not read leaderboard monitor state from {StatePath}; current entries will be used as a new baseline",
                STATE_PATH);
            return new LeaderboardMonitorState();
        }
    }

    async Task SaveStateAsync(CancellationToken stoppingToken) {
        string? directory = Path.GetDirectoryName(STATE_PATH);
        if (!string.IsNullOrWhiteSpace(directory)) {
            Directory.CreateDirectory(directory);
        }

        string temporaryPath = STATE_PATH + ".tmp";
        string json = JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(temporaryPath, json, stoppingToken);
        File.Move(temporaryPath, STATE_PATH, true);
    }

    static async Task<string> GetStringAsync(string url, CancellationToken stoppingToken) {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.CacheControl = new CacheControlHeaderValue {
            NoCache = true,
            NoStore = true
        };

        using HttpResponseMessage response = await httpClient.SendAsync(request, stoppingToken);
        if (!response.IsSuccessStatusCode) {
            throw new HttpRequestException(
                $"GET {url} returned {(int)response.StatusCode} ({response.ReasonPhrase})",
                null,
                response.StatusCode);
        }

        return await response.Content.ReadAsStringAsync(stoppingToken);
    }

    static HttpClient CreateHttpClient() {
        var client = new HttpClient {
            Timeout = TimeSpan.FromSeconds(30)
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("LymdunetteBot/1.0 leaderboard-monitor");
        return client;
    }

    static string EscapeInlineCode(string value) => value.Replace("`", "'", StringComparison.Ordinal);

    sealed record PendingNotification(LeaderboardSource Source, string EntryId, LocalEmbed Embed);

    enum LeaderboardSource {
        LiveBench,
        DeepSwe
    }
}

internal sealed class LiveBenchClient(
    string baseUrl,
    Func<string, CancellationToken, Task<string>> getStringAsync) {
    Uri? cachedBundleUri;
    LiveBenchRelease? cachedRelease;

    internal async Task<LiveBenchSnapshot> FetchSnapshotAsync(CancellationToken stoppingToken) {
        LiveBenchRelease release = await ResolveLatestReleaseAsync(stoppingToken);
        string dataUrl = $"{baseUrl}{release.TablePath}";
        string csv = await getStringAsync(dataUrl, stoppingToken);
        IReadOnlyList<string> modelIds = LeaderboardMonitorLogic.ParseLiveBenchModelIds(csv);

        if (modelIds.Count == 0) {
            throw new InvalidDataException("LiveBench's latest leaderboard table contains no models");
        }

        return new LiveBenchSnapshot(release.Date, release.Generation, modelIds);
    }

    async Task<LiveBenchRelease> ResolveLatestReleaseAsync(CancellationToken stoppingToken) {
        string html = await getStringAsync(baseUrl, stoppingToken);
        string bundleSource = LeaderboardMonitorLogic.ParseLiveBenchBundleSource(html);
        var bundleUri = new Uri(new Uri(baseUrl), bundleSource);

        if (bundleUri == cachedBundleUri && cachedRelease != null) {
            return cachedRelease;
        }

        string javascript = await getStringAsync(bundleUri.ToString(), stoppingToken);
        LiveBenchRelease release = LeaderboardMonitorLogic.ParseLatestLiveBenchRelease(javascript);

        cachedBundleUri = bundleUri;
        cachedRelease = release;
        return release;
    }
}

internal static class LeaderboardMonitorLogic {
    static readonly Regex liveBenchBundleRegex = new(
        "<script[^>]+src=[\"'](?<src>[^\"']*static/js/main\\.[^\"']+\\.js)[\"']",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    static readonly Regex releaseArrayRegex = new(
        "\\[(?<dates>\"20\\d{2}-\\d{2}-\\d{2}\"(?:,\"20\\d{2}-\\d{2}-\\d{2}\")+)\\]",
        RegexOptions.Compiled);

    static readonly Regex releaseDateRegex = new(
        "20\\d{2}-\\d{2}-\\d{2}",
        RegexOptions.Compiled);

    static readonly Regex releaseIdArrayRegex = new(
        "\\[(?<ids>\"20\\d{6}\"(?:,\"20\\d{6}\")*)\\]",
        RegexOptions.Compiled);

    static readonly Regex releaseIdRegex = new(
        "20\\d{6}",
        RegexOptions.Compiled);

    internal static string ParseLiveBenchBundleSource(string html) {
        Match bundleMatch = liveBenchBundleRegex.Match(html);
        if (!bundleMatch.Success) {
            throw new InvalidDataException("LiveBench's application bundle could not be found");
        }

        return bundleMatch.Groups["src"].Value;
    }

    internal static LiveBenchRelease ParseLatestLiveBenchRelease(string javascript) {
        // LiveBench 2.0 releases are listed as yyyyMMdd ids and come after every v1 release on the site's timeline.
        LiveBenchRelease? latestV2Release = releaseIdArrayRegex.Matches(javascript)
            .SelectMany(match => releaseIdRegex.Matches(match.Groups["ids"].Value))
            .Select(match => DateOnly.TryParseExact(
                match.Value,
                "yyyyMMdd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out DateOnly date)
                ? new LiveBenchRelease(LiveBenchGeneration.V2, match.Value, date)
                : null)
            .OfType<LiveBenchRelease>()
            .MaxBy(release => release.Date);

        if (latestV2Release != null) {
            return latestV2Release;
        }

        Match? releaseArray = releaseArrayRegex.Matches(javascript)
            .OrderByDescending(match => releaseDateRegex.Matches(match.Groups["dates"].Value).Count)
            .FirstOrDefault();

        if (releaseArray == null) {
            throw new InvalidDataException("LiveBench's release list could not be found");
        }

        DateOnly latestV1Date = releaseDateRegex.Matches(releaseArray.Groups["dates"].Value)
            .Select(match => DateOnly.ParseExact(match.Value, "yyyy-MM-dd", CultureInfo.InvariantCulture))
            .Max();
        return new LiveBenchRelease(
            LiveBenchGeneration.V1,
            latestV1Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            latestV1Date);
    }

    internal static bool RequiresLiveBenchBaseline(LeaderboardMonitorState state, LiveBenchGeneration generation) {
        // States saved before LiveBench 2.0 have no generation and were built from v1 releases.
        return !state.LiveBenchInitialized
            || (state.LiveBenchGeneration ?? LiveBenchGeneration.V1) != generation;
    }

    internal static IReadOnlyList<string> ParseLiveBenchModelIds(string csv) {
        using var reader = new StringReader(csv);
        string? headerLine = reader.ReadLine();
        if (headerLine == null) {
            throw new InvalidDataException("LiveBench's table is empty");
        }

        List<string> headers = ParseCsvLine(headerLine);
        int modelIndex = headers.FindIndex(header =>
            string.Equals(header, "model", StringComparison.OrdinalIgnoreCase));
        if (modelIndex < 0) {
            throw new InvalidDataException("LiveBench's table does not contain a model column");
        }

        var modelIds = new List<string>();
        while (reader.ReadLine() is { } line) {
            if (string.IsNullOrWhiteSpace(line)) {
                continue;
            }

            List<string> fields = ParseCsvLine(line);
            if (modelIndex < fields.Count && !string.IsNullOrWhiteSpace(fields[modelIndex])) {
                modelIds.Add(fields[modelIndex].Trim());
            }
        }

        return modelIds.Distinct(StringComparer.Ordinal).ToArray();
    }

    internal static void EnsureReleaseDoesNotRegress(DateOnly releaseDate, DateOnly? lastReleaseDate) {
        if (lastReleaseDate.HasValue && releaseDate < lastReleaseDate.Value) {
            throw new InvalidDataException(
                $"LiveBench release regressed from {lastReleaseDate:yyyy-MM-dd} to {releaseDate:yyyy-MM-dd}");
        }
    }

    internal static IReadOnlyList<T> TakeNotificationBatch<T>(IReadOnlyList<T> pending, int limit) {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        return pending.Take(limit).ToArray();
    }

    internal static LeaderboardMonitorState DeserializeState(string json) {
        LeaderboardMonitorState state = JsonSerializer.Deserialize<LeaderboardMonitorState>(json)
            ?? new LeaderboardMonitorState();

        state.LiveBenchEntries ??= new HashSet<string>(StringComparer.Ordinal);
        state.DeepSweEntries ??= new HashSet<string>(StringComparer.Ordinal);
        return state;
    }

    static List<string> ParseCsvLine(string line) {
        var fields = new List<string>();
        var field = new StringBuilder();
        bool quoted = false;

        for (int index = 0; index < line.Length; index++) {
            char character = line[index];

            if (character == '"') {
                if (quoted && index + 1 < line.Length && line[index + 1] == '"') {
                    field.Append('"');
                    index++;
                } else {
                    quoted = !quoted;
                }
            } else if (character == ',' && !quoted) {
                fields.Add(field.ToString());
                field.Clear();
            } else {
                field.Append(character);
            }
        }

        if (quoted) {
            throw new InvalidDataException("LiveBench's table contains an unterminated quoted field");
        }

        fields.Add(field.ToString());
        return fields;
    }
}

internal sealed record LiveBenchSnapshot(
    DateOnly ReleaseDate,
    LiveBenchGeneration Generation,
    IReadOnlyList<string> ModelIds);

internal sealed record LiveBenchRelease(LiveBenchGeneration Generation, string Id, DateOnly Date) {
    // Mirrors where livebench.ai loads each generation's leaderboard table from.
    internal string TablePath => Generation == LiveBenchGeneration.V2
        ? $"data/{Id}/table.csv"
        : $"v1/table_{Id.Replace('-', '_')}.csv";
}

[JsonConverter(typeof(JsonStringEnumConverter))]
internal enum LiveBenchGeneration {
    V1,
    V2
}

internal sealed record DeepSweSnapshot(DateTimeOffset GeneratedAt, IReadOnlyList<DeepSweEntry> Rows);

internal sealed class DeepSweResponse {
    [JsonPropertyName("generated_at")]
    public DateTimeOffset GeneratedAt { get; init; }

    [JsonPropertyName("rows")]
    public List<DeepSweEntry>? Rows { get; init; }
}

internal sealed class DeepSweEntry {
    [JsonPropertyName("config")]
    public string? Config { get; init; }

    [JsonPropertyName("model")]
    public string? Model { get; init; }

    [JsonPropertyName("harness")]
    public string? Harness { get; init; }

    [JsonPropertyName("reasoning_effort")]
    public string? ReasoningEffort { get; init; }

    [JsonPropertyName("pass_at_1")]
    public double? PassAt1 { get; init; }

    [JsonIgnore]
    public string EntryId => !string.IsNullOrWhiteSpace(Config)
        ? Config
        : $"{Harness}|{Model}|{ReasoningEffort ?? "default"}";
}

internal sealed class LeaderboardMonitorState {
    public bool LiveBenchInitialized { get; set; }
    public DateOnly? LiveBenchReleaseDate { get; set; }
    public LiveBenchGeneration? LiveBenchGeneration { get; set; }
    public HashSet<string> LiveBenchEntries { get; set; } = new(StringComparer.Ordinal);
    public bool DeepSweInitialized { get; set; }
    public HashSet<string> DeepSweEntries { get; set; } = new(StringComparer.Ordinal);
}

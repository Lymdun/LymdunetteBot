using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Disqord;
using Disqord.Bot.Hosting;
using Disqord.Gateway;
using Disqord.Rest;
using Microsoft.Extensions.Logging;

namespace LymdunetteBot.Services;

public class LeaderboardNotificationService : DiscordBotService {
    const ulong CHANNEL_ID = 1495814979138617535;
    const string LIVEBENCH_URL = "https://livebench.ai/";
    const string DEEPSWE_URL = "https://deepswe.datacurve.ai/";
    const string DEEPSWE_DATA_URL = "https://deepswe.datacurve.ai/artifacts/v1.1/leaderboard-live.json";
    const int DEFAULT_POLL_INTERVAL_MINUTES = 10;

    static readonly Regex liveBenchBundleRegex = new(
        "<script[^>]+src=[\"'](?<src>[^\"']*static/js/main\\.[^\"']+\\.js)[\"']",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    static readonly Regex releaseArrayRegex = new(
        "\\[(?<dates>\"20\\d{2}-\\d{2}-\\d{2}\"(?:,\"20\\d{2}-\\d{2}-\\d{2}\")*)\\]",
        RegexOptions.Compiled);

    static readonly Regex releaseDateRegex = new(
        "20\\d{2}-\\d{2}-\\d{2}",
        RegexOptions.Compiled);

    static readonly HttpClient httpClient = CreateHttpClient();

    readonly TaskCompletionSource readySource = new(TaskCreationOptions.RunContinuationsAsynchronously);
    readonly TimeSpan pollInterval = GetPollInterval();
    readonly string statePath = GetStatePath();

    LeaderboardMonitorState state = new();

    protected override ValueTask OnReady(ReadyEventArgs e) {
        readySource.TrySetResult();
        Logger.LogInformation("LeaderboardNotificationService ready");
        return default;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
        await readySource.Task.WaitAsync(stoppingToken);
        state = await LoadStateAsync(stoppingToken);

        while (!stoppingToken.IsCancellationRequested) {
            await CheckLiveBenchAsync(stoppingToken);
            await CheckDeepSweAsync(stoppingToken);

            try {
                await Task.Delay(pollInterval, stoppingToken);
            } catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) {
                break;
            }
        }

        Logger.LogWarning("LeaderboardNotificationService has stopped");
    }

    async Task CheckLiveBenchAsync(CancellationToken stoppingToken) {
        try {
            LiveBenchSnapshot snapshot = await FetchLiveBenchSnapshotAsync(stoppingToken);

            if (!state.LiveBenchInitialized) {
                state.LiveBenchEntries.UnionWith(snapshot.ModelIds);
                state.LiveBenchInitialized = true;
                await SaveStateAsync(stoppingToken);
                Logger.LogInformation(
                    "Initialized LiveBench monitor with {EntryCount} existing entries from release {ReleaseDate}",
                    snapshot.ModelIds.Count,
                    snapshot.ReleaseDate);
                return;
            }

            foreach (string modelId in snapshot.ModelIds.Where(x => !state.LiveBenchEntries.Contains(x))) {
                var embed = new LocalEmbed()
                    .WithTitle("New LiveBench entry")
                    .WithDescription($"`{EscapeInlineCode(modelId)}` was added to the leaderboard.")
                    .AddField("Release", snapshot.ReleaseDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), true)
                    .WithUrl(LIVEBENCH_URL)
                    .WithColor(Color.Orange);

                await Bot.SendMessageAsync(
                    CHANNEL_ID,
                    new LocalMessage().WithEmbeds(embed),
                    cancellationToken: stoppingToken);

                state.LiveBenchEntries.Add(modelId);
                await SaveStateAsync(stoppingToken);
                Logger.LogInformation("Posted new LiveBench entry {ModelId}", modelId);
            }
        } catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) {
            throw;
        } catch (Exception ex) {
            Logger.LogError(ex, "Failed to check LiveBench for new entries");
        }
    }

    async Task CheckDeepSweAsync(CancellationToken stoppingToken) {
        try {
            DeepSweSnapshot snapshot = await FetchDeepSweSnapshotAsync(stoppingToken);
            List<DeepSweEntry> entries = snapshot.Rows
                .Where(x => !string.IsNullOrWhiteSpace(x.EntryId) && !string.IsNullOrWhiteSpace(x.Model))
                .ToList();

            if (!state.DeepSweInitialized) {
                state.DeepSweEntries.UnionWith(entries.Select(x => x.EntryId));
                state.DeepSweInitialized = true;
                await SaveStateAsync(stoppingToken);
                Logger.LogInformation(
                    "Initialized DeepSWE monitor with {EntryCount} existing entries generated at {GeneratedAt}",
                    entries.Count,
                    snapshot.GeneratedAt);
                return;
            }

            foreach (DeepSweEntry entry in entries.Where(x => !state.DeepSweEntries.Contains(x.EntryId))) {
                string effort = string.IsNullOrWhiteSpace(entry.ReasoningEffort)
                    ? "default"
                    : entry.ReasoningEffort;

                var embed = new LocalEmbed()
                    .WithTitle("New DeepSWE entry")
                    .WithDescription($"`{EscapeInlineCode(entry.Model)}` was added to the leaderboard.")
                    .AddField("Reasoning effort", effort, true)
                    .AddField("Pass@1", entry.PassAt1.ToString("P1", CultureInfo.InvariantCulture), true)
                    .WithUrl(DEEPSWE_URL)
                    .WithColor(Color.Orange);

                await Bot.SendMessageAsync(
                    CHANNEL_ID,
                    new LocalMessage().WithEmbeds(embed),
                    cancellationToken: stoppingToken);

                state.DeepSweEntries.Add(entry.EntryId);
                await SaveStateAsync(stoppingToken);
                Logger.LogInformation("Posted new DeepSWE entry {EntryId}", entry.EntryId);
            }
        } catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) {
            throw;
        } catch (Exception ex) {
            Logger.LogError(ex, "Failed to check DeepSWE for new entries");
        }
    }

    static async Task<LiveBenchSnapshot> FetchLiveBenchSnapshotAsync(CancellationToken stoppingToken) {
        string html = await GetStringAsync(LIVEBENCH_URL, stoppingToken);
        Match bundleMatch = liveBenchBundleRegex.Match(html);
        if (!bundleMatch.Success) {
            throw new InvalidDataException("LiveBench's application bundle could not be found");
        }

        var bundleUri = new Uri(new Uri(LIVEBENCH_URL), bundleMatch.Groups["src"].Value);
        string javascript = await GetStringAsync(bundleUri.ToString(), stoppingToken);

        DateOnly[] releases = releaseArrayRegex.Matches(javascript)
            .SelectMany(match => releaseDateRegex.Matches(match.Groups["dates"].Value))
            .Select(match => DateOnly.ParseExact(match.Value, "yyyy-MM-dd", CultureInfo.InvariantCulture))
            .Distinct()
            .OrderDescending()
            .ToArray();

        if (releases.Length == 0) {
            throw new InvalidDataException("LiveBench's release list could not be found");
        }

        foreach (DateOnly release in releases) {
            string dataUrl = $"{LIVEBENCH_URL}table_{release:yyyy_MM_dd}.csv";

            try {
                string csv = await GetStringAsync(dataUrl, stoppingToken);
                IReadOnlyList<string> modelIds = ParseLiveBenchModelIds(csv);
                if (modelIds.Count > 0) {
                    return new LiveBenchSnapshot(release, modelIds);
                }
            } catch (HttpRequestException) {
                // A release can appear in front-end metadata before its table is deployed.
            }
        }

        throw new InvalidDataException("No usable LiveBench leaderboard table was found");
    }

    static async Task<DeepSweSnapshot> FetchDeepSweSnapshotAsync(CancellationToken stoppingToken) {
        string json = await GetStringAsync(DEEPSWE_DATA_URL, stoppingToken);
        DeepSweSnapshot? snapshot = JsonSerializer.Deserialize<DeepSweSnapshot>(json);

        if (snapshot?.Rows == null) {
            throw new InvalidDataException("DeepSWE's leaderboard response does not contain rows");
        }

        return snapshot;
    }

    static IReadOnlyList<string> ParseLiveBenchModelIds(string csv) {
        using var reader = new StringReader(csv);
        string? headerLine = reader.ReadLine();
        if (headerLine == null) {
            return [];
        }

        List<string> headers = ParseCsvLine(headerLine);
        int modelIndex = headers.FindIndex(x => string.Equals(x, "model", StringComparison.OrdinalIgnoreCase));
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

        fields.Add(field.ToString());
        return fields;
    }

    async Task<LeaderboardMonitorState> LoadStateAsync(CancellationToken stoppingToken) {
        if (!File.Exists(statePath)) {
            return new LeaderboardMonitorState();
        }

        try {
            string json = await File.ReadAllTextAsync(statePath, stoppingToken);
            return JsonSerializer.Deserialize<LeaderboardMonitorState>(json) ?? new LeaderboardMonitorState();
        } catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) {
            throw;
        } catch (Exception ex) {
            Logger.LogError(ex, "Could not read leaderboard monitor state from {StatePath}; current entries will be used as a new baseline", statePath);
            return new LeaderboardMonitorState();
        }
    }

    async Task SaveStateAsync(CancellationToken stoppingToken) {
        string? directory = Path.GetDirectoryName(statePath);
        if (!string.IsNullOrWhiteSpace(directory)) {
            Directory.CreateDirectory(directory);
        }

        string temporaryPath = statePath + ".tmp";
        string json = JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(temporaryPath, json, stoppingToken);
        File.Move(temporaryPath, statePath, true);
    }

    static async Task<string> GetStringAsync(string url, CancellationToken stoppingToken) {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.CacheControl = new CacheControlHeaderValue {
            NoCache = true,
            NoStore = true
        };

        using HttpResponseMessage response = await httpClient.SendAsync(request, stoppingToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(stoppingToken);
    }

    static HttpClient CreateHttpClient() {
        var client = new HttpClient {
            Timeout = TimeSpan.FromSeconds(30)
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("LymdunetteBot/1.0 leaderboard-monitor");
        return client;
    }

    static TimeSpan GetPollInterval() {
        string? value = Environment.GetEnvironmentVariable("LEADERBOARD_POLL_INTERVAL_MINUTES");
        if (!int.TryParse(value, CultureInfo.InvariantCulture, out int minutes)) {
            minutes = DEFAULT_POLL_INTERVAL_MINUTES;
        }

        return TimeSpan.FromMinutes(Math.Clamp(minutes, 1, 1440));
    }

    static string GetStatePath() {
        string? configuredPath = Environment.GetEnvironmentVariable("LEADERBOARD_STATE_PATH");
        return string.IsNullOrWhiteSpace(configuredPath)
            ? Path.Combine(AppContext.BaseDirectory, "data", "leaderboard-monitor-state.json")
            : Path.GetFullPath(configuredPath);
    }

    static string EscapeInlineCode(string value) => value.Replace("`", "'", StringComparison.Ordinal);

    sealed record LiveBenchSnapshot(DateOnly ReleaseDate, IReadOnlyList<string> ModelIds);

    sealed class DeepSweSnapshot {
        [JsonPropertyName("generated_at")]
        public DateTimeOffset GeneratedAt { get; init; }

        [JsonPropertyName("rows")]
        public List<DeepSweEntry> Rows { get; init; } = [];
    }

    sealed class DeepSweEntry {
        [JsonPropertyName("config")]
        public string Config { get; init; } = string.Empty;

        [JsonPropertyName("model")]
        public string Model { get; init; } = string.Empty;

        [JsonPropertyName("harness")]
        public string Harness { get; init; } = string.Empty;

        [JsonPropertyName("reasoning_effort")]
        public string? ReasoningEffort { get; init; }

        [JsonPropertyName("pass_at_1")]
        public double PassAt1 { get; init; }

        [JsonIgnore]
        public string EntryId => !string.IsNullOrWhiteSpace(Config)
            ? Config
            : $"{Harness}|{Model}|{ReasoningEffort ?? "default"}";
    }

    sealed class LeaderboardMonitorState {
        public bool LiveBenchInitialized { get; set; }
        public HashSet<string> LiveBenchEntries { get; init; } = new(StringComparer.Ordinal);
        public bool DeepSweInitialized { get; set; }
        public HashSet<string> DeepSweEntries { get; init; } = new(StringComparer.Ordinal);
    }
}

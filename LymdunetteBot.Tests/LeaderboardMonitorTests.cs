using LymdunetteBot.Services;
using Xunit;

namespace LymdunetteBot.Tests;

public class LeaderboardMonitorTests {
    const string BASE_URL = "https://livebench.example/";
    const string BUNDLE_URL = "https://livebench.example/static/js/main.test.js";
    const string LATEST_TABLE_URL = "https://livebench.example/table_2026_06_25.csv";
    const string OLDER_TABLE_URL = "https://livebench.example/table_2025_01_01.csv";

    static readonly string pageHtml = "<script defer src=\"./static/js/main.test.js\"></script>";
    static readonly string bundleJavascript = "const releases=[\"2025-01-01\",\"2026-06-25\"];";

    [Fact]
    public async Task LatestTableFailureDoesNotFallBackToOlderRelease() {
        var requests = new List<string>();
        var client = new LiveBenchClient(BASE_URL, (url, _) => {
            requests.Add(url);
            return url switch {
                BASE_URL => Task.FromResult(pageHtml),
                BUNDLE_URL => Task.FromResult(bundleJavascript),
                LATEST_TABLE_URL => Task.FromException<string>(new HttpRequestException("temporary failure")),
                OLDER_TABLE_URL => Task.FromResult("model,score\nlegacy-model,1"),
                _ => Task.FromException<string>(new InvalidOperationException(url))
            };
        });

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            client.FetchSnapshotAsync(CancellationToken.None));

        Assert.DoesNotContain(OLDER_TABLE_URL, requests);
    }

    [Fact]
    public async Task MalformedLatestTableDoesNotFallBackToOlderRelease() {
        var requests = new List<string>();
        var client = new LiveBenchClient(BASE_URL, (url, _) => {
            requests.Add(url);
            return Task.FromResult(url switch {
                BASE_URL => pageHtml,
                BUNDLE_URL => bundleJavascript,
                LATEST_TABLE_URL => "<!doctype html><title>Not CSV</title>",
                OLDER_TABLE_URL => "model,score\nlegacy-model,1",
                _ => throw new InvalidOperationException(url)
            });
        });

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            client.FetchSnapshotAsync(CancellationToken.None));

        Assert.DoesNotContain(OLDER_TABLE_URL, requests);
    }

    [Fact]
    public async Task UnchangedBundleIsDownloadedOnlyOnce() {
        var requestCounts = new Dictionary<string, int>();
        var client = new LiveBenchClient(BASE_URL, (url, _) => {
            requestCounts[url] = requestCounts.GetValueOrDefault(url) + 1;
            return Task.FromResult(url switch {
                BASE_URL => pageHtml,
                BUNDLE_URL => bundleJavascript,
                LATEST_TABLE_URL => "model,score\nnew-model,1",
                _ => throw new InvalidOperationException(url)
            });
        });

        await client.FetchSnapshotAsync(CancellationToken.None);
        await client.FetchSnapshotAsync(CancellationToken.None);

        Assert.Equal(2, requestCounts[BASE_URL]);
        Assert.Equal(1, requestCounts[BUNDLE_URL]);
        Assert.Equal(2, requestCounts[LATEST_TABLE_URL]);
    }

    [Fact]
    public void ReleaseParserIgnoresShorterUnrelatedDateArrays() {
        const string javascript = "const unrelated=[\"2099-12-31\"];" +
                                  "const releases=[\"2025-01-01\",\"2026-06-25\"];";

        DateOnly release = LeaderboardMonitorLogic.ParseLatestLiveBenchRelease(javascript);

        Assert.Equal(new DateOnly(2026, 6, 25), release);
    }

    [Fact]
    public void OlderReleaseIsRejected() {
        DateOnly currentRelease = new(2026, 6, 25);
        DateOnly staleRelease = new(2025, 1, 1);

        Assert.Throws<InvalidDataException>(() =>
            LeaderboardMonitorLogic.EnsureReleaseDoesNotRegress(staleRelease, currentRelease));
    }

    [Fact]
    public void NotificationBatchIsCappedAtFiveEntries() {
        int[] pending = Enumerable.Range(1, 32).ToArray();

        IReadOnlyList<int> batch = LeaderboardMonitorLogic.TakeNotificationBatch(pending, 5);

        Assert.Equal([1, 2, 3, 4, 5], batch);
    }

    [Fact]
    public void NullStateCollectionsAreRepaired() {
        const string json = """
                            {
                              "LiveBenchInitialized": true,
                              "LiveBenchEntries": null,
                              "DeepSweInitialized": true,
                              "DeepSweEntries": null
                            }
                            """;

        LeaderboardMonitorState state = LeaderboardMonitorLogic.DeserializeState(json);

        Assert.Empty(state.LiveBenchEntries);
        Assert.Empty(state.DeepSweEntries);
    }
}

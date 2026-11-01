using System.Net;
using System.Text.RegularExpressions;
using Disqord;
using Disqord.Bot.Hosting;
using Microsoft.Extensions.Logging;
using Disqord.Gateway;
using Disqord.Rest;
using Qmmands;

namespace LymdunetteBot.Services;

public class StreamerNotificationService : DiscordBotService {
    readonly SemaphoreSlim _semaphore = new(1, 1);
    HttpClient? _httpClient;
    string _lastDjName = string.Empty;

    // Last thread seen on the SSE stream. null until the first "thread" event is received,
    // so that connecting to the stream does not announce a thread that was already up.
    RadioThread? _lastThread;

    const ulong CHANNEL_ID = 1368365834166075512;

    const string BASE_URL = "https://r-a-d.io";
    // Force the theme used to render the SSE HTML: holiday and DJ themes take priority over the default one
    // and some of them do not render the DJ name, which ExtractDjName relies on.
    const string SSE_URL = "https://r-a-d.io/v1/sse?theme=default-dark";
    const int RECONNECT_DELAY_MS = 1500;
    // Stay under Discord's upload limit for bots
    const int MAX_THREAD_IMAGE_BYTES = 8 * 1024 * 1024;

    static readonly HttpClient ImageClient = new() { Timeout = TimeSpan.FromSeconds(30) };

    protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
        await StartListening(stoppingToken);
    }

    async Task StartListening(CancellationToken cancellationToken) {
        Logger.LogInformation("Starting SSE listener..");

        try {
            await _semaphore.WaitAsync(cancellationToken);

            if (_httpClient  != null) {
                Logger.LogWarning("SSE listener already running");
                return;
            }

            _httpClient = new HttpClient();

            _ = ListenForEventsWithReconnectionAsync(cancellationToken);
        } catch (Exception ex) {
            Logger.LogError(ex, "Error while trying to start SSE listener");
        } finally {
            _semaphore.Release();
        }
    }

    async Task ListenForEventsWithReconnectionAsync(CancellationToken cancellationToken) {
        while (!cancellationToken.IsCancellationRequested) {
            try {
                using HttpResponseMessage response =
                    await _httpClient!.GetAsync(SSE_URL, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                response.EnsureSuccessStatusCode();

                await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                using var reader = new StreamReader(stream);

                string eventData = string.Empty;
                string eventName = string.Empty;

                while (!reader.EndOfStream && !cancellationToken.IsCancellationRequested) {
                    string? line = await reader.ReadLineAsync(cancellationToken);
                    if (line != null) {
                        if (line.StartsWith("event:")) {
                            eventName = line.Substring(6).Trim();
                            eventData = string.Empty; // reset for next event
                        } else if (line.StartsWith("data:")) {
                            eventData += line.Substring(5).Trim() + "\n"; // save all lines
                        } else if (string.IsNullOrWhiteSpace(line) && !string.IsNullOrEmpty(eventData)) {
                            // empty line means end of event
                            await HandleEvent(eventName, eventData.Trim());
                            eventData = string.Empty;
                        }
                    }
                }
            } catch (OperationCanceledException) {
                Logger.LogInformation("SSE listener cancelled");
                break;
            } catch (Exception ex) {
                Logger.LogError(ex, "Error during SSE listener");
                await Task.Delay(RECONNECT_DELAY_MS, cancellationToken);
            }
        }

        await StopListening();
    }

    async Task HandleEvent(string eventName, string data) {
        if (eventName == "streamer") {
            Logger.LogInformation("New streamer detected: {StreamerData}", data);

            string djName = ExtractDjName(data);
            if (djName != _lastDjName) {
                _lastDjName = djName;

                string? imageUrl = ExtractImageUrl(data);
                await SendDiscordMessage(djName, imageUrl);
            }
        } else if (eventName == "thread") {
            await HandleThreadEvent(data);
        }
    }

    async Task HandleThreadEvent(string data) {
        RadioThread thread = ParseThread(data);
        RadioThread? previous = _lastThread;
        _lastThread = thread;

        // First event after startup: only remember the current thread.
        // Same thread re-sent (e.g. after an SSE reconnection): nothing to announce.
        // Thread removed: nothing to announce either.
        if (previous == null || thread == previous || thread.Kind == RadioThreadKind.None) {
            return;
        }

        Logger.LogInformation("Radio thread changed: {Thread}", thread);

        bool isUpdate = previous.Kind != RadioThreadKind.None;
        await SendThreadNotification(thread, isUpdate);
    }

    async Task SendThreadNotification(RadioThread thread, bool isUpdate) {
        try {
            var embed = new LocalEmbed()
                .WithTitle(isUpdate ? "Thread mis à jour" : "Nouveau thread")
                .WithColor(Color.Orange);

            // Always show the thread value: Discord leaves an image-only embed empty when it cannot load the image.
            embed.WithDescription(Truncate(thread.Value, Discord.Limits.Message.Embed.MaxDescriptionLength));

            if (thread.Kind != RadioThreadKind.Text) {
                embed.WithUrl(thread.Value);
            }

            if (thread.Kind == RadioThreadKind.Image) {
                // Upload the image ourselves so that Discord does not have to fetch it from the image host.
                LocalAttachment? attachment = await DownloadThreadImage(thread.Value);
                if (attachment != null) {
                    try {
                        embed.WithImageUrl($"attachment://{attachment.FileName.Value}");
                        await Bot.SendMessageAsync(CHANNEL_ID, new LocalMessage().WithEmbeds(embed).WithAttachments(attachment));
                        return;
                    } catch (Exception ex) {
                        Logger.LogWarning(ex, "Could not upload thread image {ImageUrl}, falling back to its URL", thread.Value);
                    }
                }

                embed.WithImageUrl(thread.Value);
            }

            await Bot.SendMessageAsync(CHANNEL_ID, new LocalMessage().WithEmbeds(embed));
        } catch (Exception ex) {
            Logger.LogError(ex, "Error sending thread notification to Discord");
        }
    }

    async Task<LocalAttachment?> DownloadThreadImage(string imageUrl) {
        try {
            using HttpResponseMessage response = await ImageClient.GetAsync(imageUrl, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();

            string? mediaType = response.Content.Headers.ContentType?.MediaType;
            if (mediaType == null || !mediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)) {
                Logger.LogWarning("Thread image {ImageUrl} is not an image ({MediaType})", imageUrl, mediaType);
                return null;
            }

            if (response.Content.Headers.ContentLength > MAX_THREAD_IMAGE_BYTES) {
                Logger.LogWarning("Thread image {ImageUrl} is too large to be uploaded ({Size} bytes)",
                    imageUrl, response.Content.Headers.ContentLength);
                return null;
            }

            byte[] bytes = await response.Content.ReadAsByteArrayAsync();
            if (bytes.Length > MAX_THREAD_IMAGE_BYTES) {
                Logger.LogWarning("Thread image {ImageUrl} is too large to be uploaded ({Size} bytes)", imageUrl, bytes.Length);
                return null;
            }

            // attachment:// only accepts simple file names
            string subtype = mediaType["image/".Length..].ToLowerInvariant();
            string extension = subtype switch {
                "jpeg" => "jpg",
                "png" or "gif" or "webp" or "avif" => subtype,
                _ => "png"
            };

            return LocalAttachment.Bytes(bytes, $"thread.{extension}");
        } catch (Exception ex) {
            Logger.LogWarning(ex, "Could not download thread image {ImageUrl}", imageUrl);
            return null;
        }
    }

    static string Truncate(string value, int maxLength) {
        return value.Length <= maxLength ? value : value[..(maxLength - 1)] + "…";
    }

    /// <summary>
    /// Parses the HTML of the SSE "thread" event.
    /// The thread is a free-form string set by the DJ: a link, "image:" followed by an image URL, or plain text,
    /// which r/a/dio still renders as a (percent-encoded) link.
    /// </summary>
    /// <example>
    /// Link thread:  &lt;div id="thread" ...&gt;&lt;a class="has-text-link" href="https://boards.4chan.org/a/thread/1" target="_blank"&gt;Thread up!&lt;/a&gt;&lt;/div&gt;
    /// Text thread:  &lt;div id="thread" ...&gt;&lt;a class="has-text-link" href="Merry%20Christmas!" target="_blank"&gt;Thread up!&lt;/a&gt;&lt;/div&gt;
    /// Image thread: &lt;div id="thread" ...&gt;&lt;img src="https://example.com/image.png" alt=""&gt;&lt;/div&gt;
    /// No thread:    &lt;div id="thread" ...&gt;There is no thread up.&lt;/div&gt;
    /// AFK streamer: empty data
    /// </example>
    static RadioThread ParseThread(string data) {
        Match threadMatch = Regex.Match(data, @"<div id=""thread""[^>]*>(.*?)</div>", RegexOptions.Singleline);
        if (!threadMatch.Success) {
            return RadioThread.None;
        }

        string threadHtml = threadMatch.Groups[1].Value;

        Match linkMatch = Regex.Match(threadHtml, @"<a\b[^>]*\bhref=""([^""]*)""");
        if (linkMatch.Success) {
            return CreateThread(RadioThreadKind.Link, linkMatch.Groups[1].Value);
        }

        Match imageMatch = Regex.Match(threadHtml, @"<img\b[^>]*\bsrc=""([^""]*)""");
        if (imageMatch.Success) {
            return CreateThread(RadioThreadKind.Image, imageMatch.Groups[1].Value);
        }

        return RadioThread.None;
    }

    static RadioThread CreateThread(RadioThreadKind kind, string rawValue) {
        string value = WebUtility.HtmlDecode(rawValue).Trim();
        if (string.IsNullOrEmpty(value)) {
            return RadioThread.None;
        }

        if (value.StartsWith("//")) {
            value = "https:" + value;
        } else if (value.StartsWith('/')) {
            value = BASE_URL + value;
        }

        if (!IsHttpUrl(value)) {
            // Not a URL: plain text that the template percent-encoded as a link
            value = Uri.UnescapeDataString(value).Trim();
            return string.IsNullOrEmpty(value) ? RadioThread.None : new RadioThread(RadioThreadKind.Text, value);
        }

        return new RadioThread(kind, value);
    }

    static bool IsHttpUrl(string value) {
        return Uri.TryCreate(value, UriKind.Absolute, out Uri? uri)
               && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
    }

    enum RadioThreadKind {
        None,
        Link,
        Image,
        Text
    }

    record RadioThread(RadioThreadKind Kind, string Value) {
        public static readonly RadioThread None = new(RadioThreadKind.None, string.Empty);
    }

    async Task SendDiscordMessage(string djName, string? imageUrl) {
        try {
            var embed = new LocalEmbed()
                .WithTitle("Évènement détecté")
                .WithDescription($"Nouveau streamer en direct : {djName}")
                .WithColor(Color.Orange);

            if (!string.IsNullOrWhiteSpace(imageUrl)) {
                embed.WithImageUrl(imageUrl);
            }

            await Bot.SendMessageAsync(CHANNEL_ID, new LocalMessage().WithEmbeds(embed));
        } catch (Exception ex) {
            Logger.LogError(ex, "Error sending streamer notification to Discord");
        }
    }

    string ExtractDjName(string data) {
        Match djNameMatch = Regex.Match(data, @"<div id=""dj-name""[^>]*>(.+?)</div>");
        return djNameMatch.Success ? djNameMatch.Groups[1].Value : "DJ inconnu";
    }

    string? ExtractImageUrl(string data) {
        Match imageMatch = Regex.Match(data, @"<img src=""(/api/dj-image/[^""]+)""");
        if (imageMatch.Success) {
            return $"{BASE_URL}{imageMatch.Groups[1].Value}";
        }

        return null;
    }

    async Task StopListening() {
        await _semaphore.WaitAsync();

        try {
            if (_httpClient != null) {
                _httpClient.Dispose();
                _httpClient = null;
            }

            Logger.LogInformation("SSE listener stopped");
        } catch (Exception ex) {
            Logger.LogError(ex, "Error while trying to stop SSE listener");
            Results.Failure("Une erreur est survenue lors de l'arrêt de l'écouteur SSE.");
        } finally {
            _semaphore.Release();
        }
    }

    protected override ValueTask OnReady(ReadyEventArgs e) {
        Logger.LogInformation("StreamerNotificationService Ready fired!");
        return default;
    }
}

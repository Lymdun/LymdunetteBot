using System.Net;
using System.Text.Json;
using LymdunetteBot.Services;
using Microsoft.Extensions.Logging.Abstractions;

int passed = 0;
foreach (string? language in new string?[] { "en", "fr", "EN", "fr-CA", "ja", "es", "zh-CN", "und", "zxx", "", null }) {
    bool translate = language is "ja" or "es" or "zh-CN";
    await Check($"language {language ?? "null"}", "https://x.com/alice/status/123",
        JsonSerializer.Serialize(new { tweet = new { lang = language } }),
        "https://fixupx.com/alice/status/123" + (translate ? "/en" : ""), 1);
}

await Check("URL variants and duplicate posts",
    "See (https://x.com/alice/status/123?s=20), https://x.com/alice/status/123?s=20 and X.COM/i/web/status/123/photo/2/fr#media",
    """{"tweet":{"lang":"ja"}}""",
    "See (https://fixupx.com/alice/status/123/en?s=20), https://fixupx.com/alice/status/123/en?s=20 and https://fixupx.com/i/web/status/123/photo/2/en#media", 1);
await Check("profiles and mobile URLs", "x.com/alice https://mobile.x.com/bob/status/456/ https://www.x.com/carol/status/789/video/1",
    """{"tweet":{"lang":"fr"}}""",
    "https://fixupx.com/alice https://fixupx.com/bob/status/456 https://fixupx.com/carol/status/789/video/1", 2);
await Check("unrelated domains", "https://notx.com/a/status/123 https://x.com.evil.org/a/status/123 https://example.org/x.com/a/status/123 https://fixupx.com/a/status/123 person@x.com/a/status/123",
    "{}", "https://notx.com/a/status/123 https://x.com.evil.org/a/status/123 https://example.org/x.com/a/status/123 https://fixupx.com/a/status/123 person@x.com/a/status/123", 0);
foreach (string body in new[] { "{}", "null", "{\"tweet\":null}", "{\"tweet\":{\"lang\":123}}", "not JSON" }) {
    await Check("missing or invalid metadata", "https://x.com/a/status/123", body, "https://fixupx.com/a/status/123", 1);
}
await Check("HTTP failure", "https://x.com/a/status/123", "{}", "https://fixupx.com/a/status/123", 1, HttpStatusCode.ServiceUnavailable);
await Check("timeout", "https://x.com/a/status/123", "{}", "https://fixupx.com/a/status/123", 1, error: new TaskCanceledException());
await Check("network failure", "https://x.com/a/status/123", "{}", "https://fixupx.com/a/status/123", 1, error: new HttpRequestException());
await Check("formatting and mentions", "<@123> **hello**\n[post](https://x.com/a/status/123)! <https://x.com/a/status/123>",
    """{"tweet":{"lang":"en"}}""", "<@123> **hello**\n[post](https://fixupx.com/a/status/123)! <https://fixupx.com/a/status/123>", 1);
await Check("spoiler link", "Spoiler: ||https://x.com/a/status/123||",
    """{"tweet":{"lang":"ja"}}""", "Spoiler: ||https://fixupx.com/a/status/123/en||", 1);
foreach (var text in new[] { "short", new string('a', 2000), string.Concat(Enumerable.Repeat("hello world\n", 400)) }) {
    var chunks = XLinkService.SplitContent(text);
    if (chunks.Any(c => c.Length > 2000) || string.Concat(chunks) != text) throw new Exception("Message splitting lost content");
    passed++;
}
bool rejected = false;
try { XLinkService.SplitContent(new string('a', 2001)); }
catch (InvalidOperationException) { rejected = true; }
if (!rejected) throw new Exception("Oversized unbroken content should be kept in the original message");
passed++;
Console.WriteLine($"Passed {passed} X link checks.");

async Task Check(string name, string input, string body, string expected, int requests,
    HttpStatusCode status = HttpStatusCode.OK, Exception? error = null) {
    var handler = new FakeHandler(body, status, error);
    using var client = new HttpClient(handler);
    var converter = new XLinkConverter(client, NullLogger<XLinkConverter>.Instance);
    var actual = await converter.ConvertAsync(input);
    if (actual != expected || handler.Requests != requests) {
        throw new Exception($"{name}: expected [{expected}] / {requests} requests; got [{actual}] / {handler.Requests}");
    }
    passed++;
}

class FakeHandler(string body, HttpStatusCode status, Exception? error) : HttpMessageHandler {
    public int Requests { get; private set; }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) {
        if (request.RequestUri?.Host != "api.fxtwitter.com" || !request.RequestUri.AbsolutePath.StartsWith("/status/"))
            throw new Exception("Unexpected language API URL");
        Requests++;
        return error != null
            ? Task.FromException<HttpResponseMessage>(error)
            : Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
    }
}

using Disqord;
using Disqord.Bot;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LymdunetteBot;

public class LymdunetteDiscordBot(
    IOptions<DiscordBotConfiguration> options,
    ILogger<LymdunetteDiscordBot> logger,
    IServiceProvider services,
    DiscordClient client)
    : DiscordBot(options, logger, services, client);

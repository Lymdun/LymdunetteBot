using Disqord;
using Disqord.Bot.Hosting;
using Disqord.Rest;
using Microsoft.Extensions.Logging;

namespace LymdunetteBot.Services;

public class XLinkService(XLinkConverter converter) : DiscordBotService {
    const string WebhookName = "Lymdunette X links";
    static readonly HttpClient AttachmentClient = new() { Timeout = TimeSpan.FromSeconds(60) };
    readonly SemaphoreSlim webhookLock = new(1, 1);

    protected override async ValueTask OnMessageReceived(BotMessageReceivedEventArgs e) {
        if (e.GuildId == null || e.Message.Author.IsBot || e.Message is not IUserMessage message
            || message.WebhookId != null || string.IsNullOrWhiteSpace(message.Content)) return;

        var attachments = new List<LocalAttachment>();
        string originalContent = message.Content;
        var originalEditedAt = message.EditedAt;
        try {
            string content = await converter.ConvertAsync(originalContent);
            if (content == originalContent) return;

            // These message features cannot be faithfully copied by this webhook.
            if (message.Stickers.Count > 0 || message.Poll != null || message.Components.Count > 0) {
                Logger.LogWarning("Keeping X message {MessageId}: contains stickers, a poll, or components", message.Id);
                return;
            }

            // Webhooks cannot send real replies, so name the replied-to user and link their message instead.
            var replyMentions = LocalAllowedMentions.None;
            if (message.Reference?.MessageId is { } replyId) {
                string link = $"https://discord.com/channels/{e.GuildId}/{message.Reference.ChannelId}/{replyId}";
                var replied = message.ReferencedMessage.HasValue ? message.ReferencedMessage.Value : null;
                string target = replied == null ? string.Empty
                    : replied.WebhookId != null ? $"**{replied.Author.Name}** "
                    : $"<@{replied.Author.Id}> ";
                content = $"-# ↪ Replying to {target}{link}\n{content}";

                // Ping only when the original reply did, as Discord would.
                if (replied != null && replied.WebhookId == null
                    && message.MentionedUsers.Any(user => user.Id == replied.Author.Id))
                    replyMentions = new LocalAllowedMentions().WithParsedMentions(ParsedMentions.None)
                        .WithUserIds(replied.Author.Id);
            }
            var chunks = SplitContent(content);
            var channel = await Client.FetchChannelAsync(e.ChannelId);
            var thread = channel as IThreadChannel;
            Snowflake webhookChannelId = thread?.ChannelId ?? e.ChannelId;
            var webhook = await GetWebhookAsync(webhookChannelId);

            foreach (var attachment in message.Attachments) {
                var bytes = await AttachmentClient.GetByteArrayAsync(attachment.Url);
                var copy = LocalAttachment.Bytes(bytes, attachment.FileName);
                if (attachment.Description != null) copy.Description = attachment.Description;
                attachments.Add(copy);
            }

            var member = e.Member ?? message.Author as IMember;
            string name = member?.Nick ?? message.Author.GlobalName ?? message.Author.Name;
            string avatar = member?.GetGuildAvatarUrl() ?? message.Author.GetAvatarUrl();
            for (int i = 0; i < chunks.Count; i++) {
                var repost = new LocalWebhookMessage {
                    Content = chunks[i],
                    AuthorName = name,
                    AuthorAvatarUrl = avatar,
                    AllowedMentions = i == 0 ? replyMentions : LocalAllowedMentions.None,
                    Attachments = i == 0 ? attachments : new List<LocalAttachment>()
                };
                var sent = await Client.ExecuteWebhookAsync(webhook.Id, webhook.Token!, repost,
                    threadId: thread?.Id, wait: true);
                if (sent == null) throw new InvalidOperationException("Webhook did not confirm the repost.");
            }

            // Delete only once Discord has confirmed all text and attachments were posted.
            var latest = await Client.FetchMessageAsync(e.ChannelId, message.Id) as IUserMessage;
            if (latest == null) return;
            if (latest.Content != originalContent || latest.EditedAt != originalEditedAt) {
                Logger.LogWarning("Keeping X message {MessageId}: it was edited during reposting", message.Id);
                return;
            }
            await Client.DeleteMessageAsync(e.ChannelId, message.Id);
            e.ProcessCommands = false;
        } catch (Exception ex) {
            Logger.LogError(ex, "Could not replace X message {MessageId}", e.MessageId);
        } finally {
            foreach (var attachment in attachments) attachment.Dispose();
        }
    }

    async Task<IWebhook> GetWebhookAsync(Snowflake channelId) {
        // Serialize discovery/creation so concurrent messages do not create duplicate webhooks.
        await webhookLock.WaitAsync();
        try {
            var webhooks = await Client.FetchChannelWebhooksAsync(channelId);
            return webhooks.FirstOrDefault(w => w.Name == WebhookName
                && w.Creator?.Id == Client.CurrentUser.Id && w.Token != null)
                ?? await Client.CreateWebhookAsync(channelId, WebhookName);
        } finally {
            webhookLock.Release();
        }
    }

    public static IReadOnlyList<string> SplitContent(string content) {
        var chunks = new List<string>();
        while (content.Length > 2000) {
            int split = content.LastIndexOfAny([' ', '\n', '\t'], 1999);
            if (split < 0) throw new InvalidOperationException("Cannot repost an unbroken URL or text longer than Discord's message limit.");
            chunks.Add(content[..(split + 1)]);
            content = content[(split + 1)..];
        }
        if (content.Length > 0) chunks.Add(content);
        return chunks;
    }
}

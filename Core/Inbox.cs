using System.Security.Cryptography;
using System.Text;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;
using MailKit.Security;
using MimeKit;

namespace CalendarTodo;

public sealed class Inbox(Store store)
{
    public static async Task Test(string account, string password, CancellationToken token)
    {
        using var client = await Connect(account, password, token);
    }

    public async Task<int> Sync(string account, string password, CancellationToken token, Action<string>? progress = null)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromMinutes(2));
        token = deadline.Token;
        progress?.Invoke($"正在连接 {MailProvider.ForAddress(account)?.Name}…");
        using var client = await Connect(account, password, token);
        var folder = client.Inbox;
        var now = Beijing.Now;
        var cursor = store.GetOrResetCursor(account, folder.UidValidity, now);
        if (cursor.Uid == uint.MaxValue)
            return 0;
        SearchQuery filter = SearchQuery.Uids(new UniqueIdRange(new UniqueId(cursor.Uid + 1), UniqueId.MaxValue));
        if (cursor.Uid == 0)
            filter = filter.And(SearchQuery.DeliveredAfter(cursor.ScanStart.UtcDateTime.Date.AddDays(-1)));
        progress?.Invoke("正在查找新邮件…");
        var uids = (await folder.SearchAsync(filter, token)).Where(uid => uid.Id > cursor.Uid).OrderBy(uid => uid.Id).ToArray();
        var count = 0;

        foreach (var batch in uids.Chunk(50))
        {
            progress?.Invoke($"正在检查邮件 {count}/{uids.Length}…");
            var summaries = await folder.FetchAsync(batch,
                MessageSummaryItems.UniqueId | MessageSummaryItems.InternalDate |
                MessageSummaryItems.Envelope | MessageSummaryItems.BodyStructure, token);
            foreach (var summary in summaries.OrderBy(summary => summary.UniqueId.Id))
            {
                token.ThrowIfCancellationRequested();
                cursor = cursor with { Uid = summary.UniqueId.Id };
                count++;
                var received = summary.InternalDate ?? now;
                if (received < cursor.ScanStart || CanSkip(summary))
                    continue;
                progress?.Invoke($"正在读取邮件 {count}/{uids.Length}…");
                var mail = await folder.GetMessageAsync(summary.UniqueId, token);
                var identity = string.IsNullOrWhiteSpace(mail.MessageId)
                    ? $"{mail.From}\n{mail.Date:o}\n{mail.Subject}\n{mail.TextBody}\n{mail.HtmlBody}\n{received:o}"
                    : $"{mail.From.Mailboxes.FirstOrDefault()?.Address}\n{mail.MessageId}";
                var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
                var source = SourceFor(folder, summary.UniqueId, mail);
                var suggestions = MailParser.Parse(mail, received).Select(item => item with { Source = source }).ToArray();
                token.ThrowIfCancellationRequested();
                store.Import(account, cursor, key, suggestions, now);
            }
            token.ThrowIfCancellationRequested();
            store.AdvanceCursor(account, cursor);
        }
        return count;
    }

    public static async Task<IReadOnlyList<OriginalMail>> ReadOriginals(Schedule schedule, string password, CancellationToken token)
    {
        using var client = await Connect(schedule.Account, password, token);
        var folder = client.Inbox;
        var source = schedule.Source;
        if (source is not null && source.Validity == folder.UidValidity)
        {
            try
            {
                var direct = await folder.GetMessageAsync(new UniqueId(source.Uid), token);
                if (source.MessageId is null || direct.MessageId == source.MessageId)
                    return [Readable(source, direct)];
            }
            catch (MessageNotFoundException) { }
        }

        if (source?.MessageId is { } id)
        {
            var matches = await folder.SearchAsync(SearchQuery.HeaderContains("Message-ID", id), token);
            foreach (var uid in matches)
            {
                var mail = await folder.GetMessageAsync(uid, token);
                if (mail.MessageId == id)
                    return [Readable(SourceFor(folder, uid, mail), mail)];
            }
        }

        // Stored links did not exist before version 1.1. Search only the original sender and
        // require a unique event match before opening an older email.
        if (string.IsNullOrWhiteSpace(schedule.Sender) && source?.MessageId is null)
            return [];
        // Coremail can return zero results for FROM and HEADER even for known mail.
        // Compare the relevant fields locally against recent envelope metadata.
        var recent = SearchQuery.DeliveredAfter(Beijing.Now.AddDays(-90).UtcDateTime.Date);
        var candidates = await folder.SearchAsync(recent, token);
        var found = new List<OriginalMail>();
        foreach (var batch in candidates.OrderByDescending(uid => uid.Id).Take(500).Chunk(50))
        {
            var summaries = await folder.FetchAsync(batch, MessageSummaryItems.UniqueId | MessageSummaryItems.InternalDate |
                MessageSummaryItems.Envelope | MessageSummaryItems.BodyStructure, token);
            foreach (var summary in summaries)
            {
                token.ThrowIfCancellationRequested();
                if (source?.MessageId is { } messageId)
                {
                    if (summary.Envelope?.MessageId != messageId)
                        continue;
                    var original = await folder.GetMessageAsync(summary.UniqueId, token);
                    if (original.MessageId == messageId)
                        return [Readable(SourceFor(folder, summary.UniqueId, original), original)];
                    continue;
                }
                if (summary.Envelope?.From.Mailboxes.Any(address =>
                    string.Equals(address.Address, schedule.Sender, StringComparison.OrdinalIgnoreCase)) != true || CanSkip(summary))
                    continue;
                var mail = await folder.GetMessageAsync(summary.UniqueId, token);
                if (!MailParser.Parse(mail, summary.InternalDate ?? Beijing.Now).Any(item => item.When == schedule.When &&
                    item.Kind == schedule.Kind && item.Sender == schedule.Sender &&
                    string.Equals(item.Title, schedule.Title, StringComparison.OrdinalIgnoreCase)))
                    continue;
                var candidate = SourceFor(folder, summary.UniqueId, mail);
                if (candidate.MessageId is null || found.All(item => item.Source.MessageId != candidate.MessageId))
                    found.Add(Readable(candidate, mail));
                if (found.Count == 8)
                    break;
            }
            if (found.Count == 8)
                break;
        }
        return found.OrderByDescending(item => item.Date).ToArray();
    }

    private static MailSource SourceFor(IMailFolder folder, UniqueId uid, MimeMessage mail) =>
        new(folder.UidValidity, uid.Id, mail.MessageId, mail.Subject ?? "");

    private static OriginalMail Readable(MailSource source, MimeMessage mail) =>
        new(source, mail.From.ToString(), mail.Date.ToOffset(Beijing.Offset), MailParser.ReadableBody(mail));

    private static bool CanSkip(IMessageSummary summary) =>
        summary.Envelope is { } envelope && summary.Body is not null &&
        !MailParser.IsScheduleSubject(envelope.Subject) &&
        !summary.BodyParts.Any(part => part.ContentType.IsMimeType("text", "calendar") ||
            ((part.ContentDisposition?.FileName ?? part.ContentType.Name)
                ?.EndsWith(".ics", StringComparison.OrdinalIgnoreCase) ?? false));

    private static async Task<ImapClient> Connect(string account, string password, CancellationToken token)
    {
        var client = new ImapClient { Timeout = 25_000 };
        try
        {
            var provider = MailProvider.ForAddress(account) ?? throw new ArgumentException("不支持的邮箱地址", nameof(account));
            await client.ConnectAsync(provider.Host, 993, SecureSocketOptions.SslOnConnect, token);
            if (client.Capabilities.HasFlag(ImapCapabilities.Id))
                await client.IdentifyAsync(new ImapImplementation { Name = "CalendarTodo", Version = "1.0" }, token);
            await client.AuthenticateAsync(account, provider.NormalizeSecret(password), token);
            await client.Inbox.OpenAsync(FolderAccess.ReadOnly, token);
            return client;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }
}

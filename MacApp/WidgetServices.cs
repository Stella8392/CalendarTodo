using Avalonia.Threading;

namespace CalendarTodo.Mac;

public sealed partial class WidgetWindow
{
    private readonly HashSet<string> blockedAccounts = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? syncCancellation;
    private Task? syncRunning;
    private DateTimeOffset nextAttempt;
    private bool notifying;

    internal async Task Sync(bool manual = false)
    {
        if (closing || dialogOpen) return;
        if (syncRunning is not null) { if (manual) await StopSync(); return; }
        var addresses = store.Accounts().Where(address => manual || AccountDue(address)).ToArray();
        if (addresses.Length == 0)
        {
            IdleStatus();
            if (manual && store.Accounts().Count == 0) await Settings();
            return;
        }
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        syncCancellation = cancellation;
        syncRunning = completed.Task;
        syncButton.Content = "取消";
        var successful = 0;
        try
        {
            foreach (var address in addresses)
            {
                cancellation.Token.ThrowIfCancellationRequested();
                try
                {
                    var secret = Keychain.Read(address);
                    if (string.IsNullOrEmpty(secret)) throw new MailKit.Security.AuthenticationException();
                    await Task.Run(() => new Inbox(store).Sync(address, secret, cancellation.Token, stage =>
                        Dispatcher.UIThread.Post(() =>
                        {
                            if (!closing && syncCancellation == cancellation && !cancellation.IsCancellationRequested)
                                syncStatus.Text = $"{MailProvider.ForAddress(address)?.Name} · {stage}";
                        })), cancellation.Token);
                    blockedAccounts.Remove(address);
                    if (DailyMailSync.IsDue(Beijing.Now, null, SyncTime())) store.Set($"daily:{address}", Beijing.DayKey(Beijing.Now));
                    store.Set($"mail-status:{address}", $"已同步 {Beijing.Now:M/d HH:mm}");
                    successful++;
                }
                catch (Exception error) when (!cancellation.IsCancellationRequested)
                {
                    if (error is MailKit.Security.AuthenticationException) blockedAccounts.Add(address);
                    store.Set($"mail-status:{address}", ConnectionError(error));
                }
            }
            syncStatus.Text = $"已同步 {successful}/{addresses.Length} 个邮箱";
        }
        catch (OperationCanceledException) { syncStatus.Text = "已取消同步"; }
        catch (Exception error) { syncStatus.Text = ConnectionError(error); }
        finally
        {
            syncCancellation = null;
            syncRunning = null;
            syncButton.Content = "同步";
            completed.TrySetResult();
            if (!closing) Refresh();
        }
    }

    private async Task StopSync()
    {
        syncCancellation?.Cancel();
        if (syncRunning is { } running) await running;
    }
    private bool AccountDue(string address) => !blockedAccounts.Contains(address) && DailyMailSync.IsDue(Beijing.Now, store.Get($"daily:{address}"), SyncTime());
    private TimeSpan SyncTime() => TimeSpan.TryParseExact(store.Get("daily-sync-time"), @"hh\:mm", System.Globalization.CultureInfo.InvariantCulture, out var time) ? time : DailyMailSync.DefaultTime;
    private void IdleStatus() => syncStatus.Text = $"{store.Accounts().Count} 个邮箱 · 每日 {SyncTime():hh\\:mm} 同步";

    private async Task Tick()
    {
        if (closing) return;
        if (lastToday != Beijing.Now.Date)
        {
            if (selectedDay == lastToday) selectedDay = Beijing.Now.Date;
            lastToday = Beijing.Now.Date;
            Refresh();
        }
        if (OperatingSystem.IsMacOS() && !notifying)
        {
            notifying = true;
            try
            {
                foreach (var reminder in store.Due(Beijing.Now))
                {
                    if (await MacSystem.Notify(reminder.Title, $"{reminder.When:M/d HH:mm} · 北京时间")) store.MarkSent(reminder);
                }
            }
            finally { notifying = false; }
        }
        if (!dialogOpen && syncRunning is null && Beijing.Now >= nextAttempt && store.Accounts().Any(AccountDue))
        {
            nextAttempt = Beijing.Now.AddMinutes(5);
            await Sync();
        }
    }

    private static string ConnectionError(Exception error) => error switch
    {
        MailKit.Security.AuthenticationException => "授权失效，请在设置中更新授权",
        MailKit.Security.SslHandshakeException => "邮箱安全连接验证失败",
        MailKit.Net.Imap.ImapCommandException => "邮箱拒绝 IMAP 请求，请检查服务设置",
        OperationCanceledException => "连接超时，可稍后重试",
        System.IO.IOException or System.Net.Sockets.SocketException => "连接失败，可稍后重试",
        _ => "操作未完成，请检查钥匙串权限和邮箱设置"
    };
}

using System;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace CalendarTodo;

public sealed partial class WidgetWindow
{
    private bool notificationsRegistered;
    private CancellationTokenSource? syncCancellation;
    private Task? syncCompleted;
    private DateTimeOffset nextDailyAttempt;

    private async Task Settings()
    {
        if (accountDialog)
            return;

        accountDialog = true;
        var previous = store.Get("account");
        var account = new TextBox { Header = "163 邮箱", PlaceholderText = "name@163.com", Text = previous ?? "", MaxLength = 128 };
        var password = new PasswordBox { Header = "客户端授权码", MaxLength = 128 };
        var startup = new CheckBox { Content = "登录 Windows 后启动", IsChecked = StartupEnabled() };
        var syncTime = new TimePicker
        {
            Header = "每日同步时间（北京时间）", Language = "zh-CN",
            ClockIdentifier = "24HourClock", MinuteIncrement = 1, SelectedTime = ScheduledSyncTime()
        };
        var message = Label("在 163 网页版的设置中开启 IMAP，生成客户端授权码。", 12, Muted);
        message.TextWrapping = TextWrapping.Wrap;
        var status = Label("", 12, Accent);
        status.TextWrapping = TextWrapping.Wrap;
        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(message);
        panel.Children.Add(account);
        panel.Children.Add(password);
        panel.Children.Add(syncTime);
        panel.Children.Add(startup);
        panel.Children.Add(status);
        var dialog = Dialog("设置", new ScrollViewer { Content = panel, MaxHeight = 270 }, "测试并绑定");
        dialog.SecondaryButtonText = "保存设置";
        if (!string.IsNullOrEmpty(previous))
        {
            var disconnect = Button("解除邮箱绑定");
            panel.Children.Add(disconnect);
            disconnect.Click += async (_, _) =>
            {
                dialog.IsPrimaryButtonEnabled = false;
                dialog.IsSecondaryButtonEnabled = false;
                disconnect.IsEnabled = false;
                try
                {
                    status.Text = "正在停止同步…";
                    await StopSync();
                    Credentials.Delete(previous);
                    store.Set("account", "");
                    syncStatus.Text = "163 邮箱未绑定";
                    requiresAuth = false;
                    dialog.Hide();
                }
                catch (Exception)
                {
                    status.Text = "无法删除 Windows 凭据。";
                }
                finally
                {
                    dialog.IsPrimaryButtonEnabled = true;
                    dialog.IsSecondaryButtonEnabled = true;
                    disconnect.IsEnabled = true;
                }
            };
        }
        var bound = false;
        dialog.PrimaryButtonClick += async (_, args) =>
        {
            var address = account.Text.Trim().ToLowerInvariant();
            if (!Regex.IsMatch(address, @"^[^@\s]+@163\.com$") || string.IsNullOrWhiteSpace(password.Password))
            {
                args.Cancel = true;
                status.Text = "请输入个人 @163.com 邮箱和客户端授权码。";
                return;
            }
            var deferral = args.GetDeferral();
            dialog.IsPrimaryButtonEnabled = false;
            dialog.IsSecondaryButtonEnabled = false;
            panel.IsHitTestVisible = false;
            try
            {
                if (syncCancellation is not null)
                {
                    status.Text = "正在停止上次同步…";
                    await StopSync();
                }
                status.Text = "正在测试 IMAP 连接…";
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                timeout.CancelAfter(TimeSpan.FromSeconds(35));
                var code = password.Password.Trim();
                await Inbox.Test(address, code, timeout.Token);
                if (lifetime.IsCancellationRequested)
                    return;
                Credentials.Save(address, code);
                if (!string.IsNullOrEmpty(previous) && previous != address)
                    Credentials.Delete(previous);
                store.Set("account", address);
                SavePreferences();
                requiresAuth = false;
                bound = true;
            }
            catch (Exception error)
            {
                args.Cancel = true;
                status.Text = ConnectionError(error);
            }
            finally
            {
                panel.IsHitTestVisible = true;
                dialog.IsPrimaryButtonEnabled = true;
                dialog.IsSecondaryButtonEnabled = true;
                deferral.Complete();
            }
        };
        dialog.SecondaryButtonClick += (_, _) => SavePreferences();
        try
        {
            await dialog.ShowAsync();
        }
        finally
        {
            password.Password = "";
            accountDialog = false;
        }
        if (bound)
        {
            nextDailyAttempt = default;
            ShowIdleSyncStatus();
        }

        void SavePreferences()
        {
            SetStartup(startup.IsChecked == true);
            if (syncTime.SelectedTime is { } time && time != ScheduledSyncTime())
            {
                store.Set("daily-sync-time", time.ToString(@"hh\:mm", CultureInfo.InvariantCulture));
                nextDailyAttempt = default;
                if (syncCancellation is null)
                    ShowIdleSyncStatus();
            }
        }
    }

    private bool StartupEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue("CalendarTodo") is string;
    }

    private static void SetStartup(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled)
            key.SetValue("CalendarTodo", $"\"{Environment.ProcessPath}\"");
        else
            key.DeleteValue("CalendarTodo", false);
    }

    private async Task Sync(bool manual = false)
    {
        if (exiting || accountDialog || (requiresAuth && !manual))
            return;
        if (syncCancellation is not null)
        {
            if (manual)
                await StopSync();
            return;
        }
        var account = store.Get("account");
        if (string.IsNullOrEmpty(account))
        {
            syncStatus.Text = "163 邮箱未绑定";
            if (manual && AppWindow.IsVisible)
                await Settings();
            return;
        }
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        syncCancellation = cancellation;
        syncCompleted = completed.Task;
        syncButton.Content = "取消";
        AutomationProperties.SetName(syncButton, "取消本次同步");
        SetSyncStatus("正在连接 163 邮箱…");
        try
        {
            var password = Credentials.Read(account);
            if (string.IsNullOrEmpty(password))
                throw new MailKit.Security.AuthenticationException();
            var before = store.Schedules().ToDictionary(item => item.Id);
            var checkedCount = await Task.Run(() => inbox.Sync(account, password, cancellation.Token, stage =>
                DispatcherQueue.TryEnqueue(() =>
                {
                    if (syncCancellation == cancellation && !cancellation.IsCancellationRequested)
                        SetSyncStatus(stage);
                })), cancellation.Token);
            if (exiting)
                return;
            foreach (var item in store.Schedules())
                if (before.TryGetValue(item.Id, out var old) && (old.When != item.When || old.Cancelled != item.Cancelled))
                    ClearDelivered(item.Id);
            requiresAuth = false;
            var finished = Beijing.Now;
            if (DailyMailSync.IsDue(finished, null, ScheduledSyncTime()))
                store.Set($"daily:{account}", Beijing.DayKey(finished));
            var result = checkedCount == 0 ? $"已同步 {finished:HH:mm} · 无新邮件" :
                $"已同步 {finished:HH:mm} · 检查 {checkedCount} 封";
            SetSyncStatus(result);
        }
        catch (Exception error)
        {
            if (exiting)
                return;
            requiresAuth = error is MailKit.Security.AuthenticationException;
            SetSyncStatus(cancellation.IsCancellationRequested ? "已取消同步" :
                error is OperationCanceledException ? "同步超时，稍后继续" : ConnectionError(error));
        }
        finally
        {
            syncCancellation = null;
            syncCompleted = null;
            if (!exiting)
            {
                syncButton.Content = "同步";
                AutomationProperties.SetName(syncButton, "立即同步邮箱");
            }
            completed.TrySetResult();
        }
        if (!exiting)
        {
            Refresh();
            Tick();
        }
    }

    private Task TryDailySync()
    {
        if (exiting || accountDialog || syncCancellation is not null || requiresAuth)
            return Task.CompletedTask;
        var now = Beijing.Now;
        var account = store.Get("account");
        if (string.IsNullOrEmpty(account) || now < nextDailyAttempt ||
            !DailyMailSync.IsDue(now, store.Get($"daily:{account}"), ScheduledSyncTime()))
            return Task.CompletedTask;
        nextDailyAttempt = now.AddMinutes(5);
        return Sync();
    }

    private void ShowIdleSyncStatus()
    {
        var account = store.Get("account");
        SetSyncStatus(string.IsNullOrEmpty(account) ? "163 邮箱未绑定" :
            $"每日 {ScheduledSyncTime():hh\\:mm} 自动同步");
    }

    private TimeSpan ScheduledSyncTime() => TimeSpan.TryParseExact(store.Get("daily-sync-time"), @"hh\:mm",
        CultureInfo.InvariantCulture, out var time) ? time : DailyMailSync.DefaultTime;

    private async Task StopSync()
    {
        var completed = syncCompleted;
        if (completed is null)
            return;
        SetSyncStatus("正在停止同步…");
        syncCancellation?.Cancel();
        await completed;
    }

    private void SetSyncStatus(string text)
    {
        syncStatus.Text = text;
        ToolTipService.SetToolTip(syncStatus, text);
    }

    private static string ConnectionError(Exception error) => error switch
    {
        MailKit.Security.AuthenticationException => "授权失效，请在设置中重新绑定",
        MailKit.Net.Imap.ImapCommandException => "163 拒绝 IMAP 请求，请检查网页邮箱的 IMAP 设置",
        MailKit.Security.SslHandshakeException => "无法验证 163 邮箱的安全连接",
        OperationCanceledException => "连接超时，请稍后重试",
        System.IO.IOException or System.Net.Sockets.SocketException => "连接失败，将在下次同步重试",
        System.ComponentModel.Win32Exception => "Windows 凭据访问失败，请在设置中重试",
        _ => "同步未完成，请稍后重试"
    };

    private async Task Review()
    {
        if (accountDialog)
            return;
        accountDialog = true;
        try
        {
            while (store.PendingItems().FirstOrDefault() is { } pending)
            {
                var item = pending.Suggestion;
                var content = new StackPanel { Spacing = 8 };
                var title = Label(item.Title, 15, strong: true);
                title.TextWrapping = TextWrapping.Wrap;
                content.Children.Add(title);
                var details = Label($"{item.Sender}\n{item.Reason}\n{item.Snippet}", 12, Muted);
                details.TextWrapping = TextWrapping.Wrap;
                content.Children.Add(details);
                var input = new TextBox
                {
                    Header = "时间（北京时间）",
                    PlaceholderText = "yyyy-MM-dd HH:mm",
                    Text = item.When?.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) ?? ""
                };
                if (item.Action != MailAction.Cancel)
                    content.Children.Add(input);
                var candidates = item.Action == MailAction.Add ? [] : store.ReviewTargets(pending.Id);
                var target = new ComboBox
                {
                    Header = "原日程", PlaceholderText = candidates.Count == 0 ? "没有已导入的日程" : "选择要更新的日程",
                    ItemsSource = candidates.Select(entry => $"{entry.When:M/d HH:mm} · {entry.Title}").ToArray(),
                    HorizontalAlignment = HorizontalAlignment.Stretch
                };
                if (item.Action != MailAction.Add)
                    content.Children.Add(target);
                var status = Label("", 11, Accent);
                content.Children.Add(status);
                var dialog = Dialog("确认邮件日程", content, item.Action == MailAction.Cancel ? "确认取消" : "确认保存");
                dialog.SecondaryButtonText = "忽略";
                dialog.PrimaryButtonClick += (_, args) =>
                {
                    DateTimeOffset? when = null;
                    if (item.Action != MailAction.Cancel)
                    {
                        if (!DateTime.TryParseExact(input.Text.Trim(), "yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture,
                            DateTimeStyles.None, out var date))
                        {
                            status.Text = "请按 yyyy-MM-dd HH:mm 输入时间。";
                            args.Cancel = true;
                            return;
                        }
                        when = Beijing.At(date);
                    }
                    if (item.Action != MailAction.Add && target.SelectedIndex < 0)
                    {
                        status.Text = "请选择原日程。";
                        args.Cancel = true;
                        return;
                    }
                    var id = target.SelectedIndex < 0 ? (long?)null : candidates[target.SelectedIndex].Id;
                    if (!store.Resolve(pending.Id, when, id, ignore: false))
                    {
                        args.Cancel = true;
                        status.Text = "该日程当前无法更新。";
                    }
                    else if (id.HasValue)
                        ClearDelivered(id.Value);
                };
                dialog.SecondaryButtonClick += (_, _) => store.Resolve(pending.Id, null, null, ignore: true);
                if (await dialog.ShowAsync() == ContentDialogResult.None)
                    break;
                Refresh();
            }
        }
        finally { accountDialog = false; }
    }

    private ContentDialog Dialog(string title, object content, string primary) => new()
    {
        XamlRoot = root.XamlRoot, RequestedTheme = ElementTheme.Light, Title = title,
        Content = content, PrimaryButtonText = primary, CloseButtonText = "关闭"
    };

    private void RegisterNotifications()
    {
        try
        {
            AppNotificationManager.Default.NotificationInvoked += NotificationInvoked;
            AppNotificationManager.Default.Register();
            notificationsRegistered = true;
        }
        catch (Exception)
        {
            AppNotificationManager.Default.NotificationInvoked -= NotificationInvoked;
        }
    }

    private void NotificationInvoked(AppNotificationManager _, AppNotificationActivatedEventArgs args) =>
        DispatcherQueue.TryEnqueue(ShowWidget);

    private void UnregisterNotifications()
    {
        if (!notificationsRegistered)
            return;
        AppNotificationManager.Default.NotificationInvoked -= NotificationInvoked;
        try { AppNotificationManager.Default.Unregister(); }
        catch (Exception) { /* Windows may already be shutting down. */ }
    }

    private void ClearDelivered(long id)
    {
        if (notificationsRegistered)
            _ = AppNotificationManager.Default.RemoveByGroupAsync($"s{id}");
    }

    private void Tick()
    {
        if (exiting)
            return;
        var now = Beijing.Now;
        if (lastDay != now.Date)
        {
            if (selected is null)
                month = now.Date.AddDays(1 - now.Day);
            lastDay = now.Date;
            Refresh();
        }
        foreach (var reminder in store.Due(now))
        {
            var dateLabel = reminder.Kind == EventKind.Deadline ? "截止" : "开始";
            var details = $"{reminder.When:M 月 d 日 HH:mm} {dateLabel} · 北京时间";
            try
            {
                if (!notificationsRegistered)
                    throw new InvalidOperationException();
                var notification = new AppNotificationBuilder()
                    .AddArgument("event", reminder.EventId.ToString(CultureInfo.InvariantCulture))
                    .AddText(reminder.Title)
                    .AddText(details)
                    .SetTag($"m{reminder.Lead}")
                    .SetGroup($"s{reminder.EventId}")
                    .BuildNotification();
                AppNotificationManager.Default.Show(notification);
            }
            catch (Exception)
            {
                tray.ShowBalloonTip(5000, reminder.Title, details, Forms.ToolTipIcon.Info);
            }
            store.MarkSent(reminder);
        }
    }
}

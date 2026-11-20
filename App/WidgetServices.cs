using System;
using System.Globalization;
using System.Linq;
using System.Collections.Generic;
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
    private readonly HashSet<string> accountsRequiringAuth = new(StringComparer.OrdinalIgnoreCase);
    private bool notificationsRegistered;
    private CancellationTokenSource? syncCancellation;
    private Task? syncCompleted;
    private DateTimeOffset nextDailyAttempt;

    private async Task Settings()
    {
        if (accountDialog) return;
        accountDialog = true;
        var channel = new ComboBox
        {
            Header = "邮箱渠道", ItemsSource = MailProvider.All.Select(item => item.Name).ToArray(),
            SelectedIndex = 0, HorizontalAlignment = HorizontalAlignment.Stretch
        };
        var account = new TextBox { Header = "邮箱地址", PlaceholderText = "填写完整邮箱地址", MaxLength = 128 };
        var password = new PasswordBox { Header = "客户端授权码", MaxLength = 128 };
        var instructions = Label("", 12, Muted);
        instructions.TextWrapping = TextWrapping.Wrap;
        var help = new HyperlinkButton { Content = "查看官方授权说明", Padding = new Thickness(0) };
        var boundAccounts = new StackPanel { Spacing = 8 };
        var startup = new CheckBox { Content = "登录 Windows 后启动", IsChecked = StartupEnabled() };
        var syncTime = new TimePicker
        {
            Header = "每日同步时间（北京时间）", Language = "zh-CN",
            ClockIdentifier = "24HourClock", MinuteIncrement = 1, SelectedTime = ScheduledSyncTime()
        };
        var status = Label("", 12, Accent);
        status.TextWrapping = TextWrapping.Wrap;
        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(Label("已绑定邮箱", 14, strong: true));
        panel.Children.Add(boundAccounts);
        panel.Children.Add(channel);
        panel.Children.Add(instructions);
        panel.Children.Add(help);
        panel.Children.Add(account);
        panel.Children.Add(password);
        panel.Children.Add(syncTime);
        panel.Children.Add(startup);
        panel.Children.Add(status);
        channel.SelectionChanged += (_, _) => UpdateChannel();
        UpdateChannel();
        var dialog = Dialog("邮箱与设置", new ScrollViewer { Content = panel, MaxHeight = 340 }, "测试并添加 / 更新");
        dialog.SecondaryButtonText = "保存设置";
        RefreshAccounts();
        dialog.PrimaryButtonClick += async (_, args) =>
        {
            var address = account.Text.Trim().ToLowerInvariant();
            var provider = MailProvider.All[channel.SelectedIndex];
            if (MailProvider.ForAddress(address) != provider || string.IsNullOrWhiteSpace(password.Password))
            {
                args.Cancel = true;
                status.Text = $"请输入该渠道的完整邮箱地址和{(provider.Id == "gmail" ? "应用专用密码" : "客户端授权码")}。";
                return;
            }
            var deferral = args.GetDeferral();
            dialog.IsPrimaryButtonEnabled = false;
            dialog.IsSecondaryButtonEnabled = false;
            panel.IsHitTestVisible = false;
            try
            {
                await StopSync();
                status.Text = "正在测试 IMAP 连接…";
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                timeout.CancelAfter(TimeSpan.FromSeconds(35));
                var code = provider.NormalizeSecret(password.Password);
                await Inbox.Test(address, code, timeout.Token);
                if (lifetime.IsCancellationRequested) return;
                Credentials.Save(address, code);
                store.BindAccount(address);
                store.Set($"mail-status:{address}", "已绑定，可同步");
                accountsRequiringAuth.Remove(address);
                SavePreferences();
                password.Password = "";
                status.Text = "已保存。可以继续添加其他邮箱，或关闭设置后点同步。";
                RefreshAccounts();
                args.Cancel = true;
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
        try { await dialog.ShowAsync(); }
        finally
        {
            password.Password = "";
            accountDialog = false;
            nextDailyAttempt = default;
            ShowIdleSyncStatus();
        }

        void UpdateChannel()
        {
            var provider = MailProvider.All[channel.SelectedIndex];
            instructions.Text = provider.Instructions;
            password.Header = provider.Id == "gmail" ? "Google 应用专用密码" : "客户端授权码";
            help.NavigateUri = new Uri(provider.HelpUrl);
        }

        void RefreshAccounts()
        {
            boundAccounts.Children.Clear();
            var addresses = store.Accounts();
            if (addresses.Count == 0) boundAccounts.Children.Add(Label("尚未绑定邮箱", 12, Muted));
            foreach (var address in addresses)
            {
                var entry = new StackPanel { Spacing = 4 };
                var label = Label($"{MailProvider.ForAddress(address)?.Name} · {address}\n{store.Get($"mail-status:{address}") ?? "待同步"}", 12);
                label.TextWrapping = TextWrapping.Wrap;
                entry.Children.Add(label);
                var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
                var edit = Button("更新授权");
                edit.Click += (_, _) =>
                {
                    channel.SelectedIndex = MailProvider.All.ToList().FindIndex(item => item == MailProvider.ForAddress(address));
                    account.Text = address;
                    password.Password = "";
                };
                var remove = Button("解除绑定");
                var confirmed = false;
                remove.Click += async (_, _) =>
                {
                    if (!confirmed)
                    {
                        confirmed = true;
                        remove.Content = "确认解除";
                        status.Text = "解除后停止同步此账号，已导入日程会保留。再次点击确认解除。";
                        return;
                    }
                    panel.IsHitTestVisible = false;
                    dialog.IsPrimaryButtonEnabled = false;
                    dialog.IsSecondaryButtonEnabled = false;
                    dialog.CloseButtonText = "";
                    try
                    {
                        await StopSync();
                        Credentials.Delete(address);
                        store.UnbindAccount(address);
                        accountsRequiringAuth.Remove(address);
                        status.Text = "已解除绑定。";
                        RefreshAccounts();
                    }
                    catch (Exception error) { status.Text = ConnectionError(error); }
                    finally
                    {
                        panel.IsHitTestVisible = true;
                        dialog.IsPrimaryButtonEnabled = true;
                        dialog.IsSecondaryButtonEnabled = true;
                        dialog.CloseButtonText = "关闭";
                    }
                };
                actions.Children.Add(edit);
                actions.Children.Add(remove);
                entry.Children.Add(actions);
                boundAccounts.Children.Add(entry);
            }
        }

        void SavePreferences()
        {
            SetStartup(startup.IsChecked == true);
            if (syncTime.SelectedTime is { } time)
                store.Set("daily-sync-time", time.ToString(@"hh\:mm", CultureInfo.InvariantCulture));
            nextDailyAttempt = default;
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
        if (exiting || accountDialog) return;
        if (syncCancellation is not null)
        {
            if (manual) await StopSync();
            return;
        }
        var accounts = store.Accounts().Where(address => manual || IsAccountDue(address, Beijing.Now)).ToArray();
        if (accounts.Length == 0)
        {
            ShowIdleSyncStatus();
            if (manual && AppWindow.IsVisible && store.Accounts().Count == 0) await Settings();
            return;
        }
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        syncCancellation = cancellation;
        syncCompleted = completed.Task;
        syncButton.Content = "取消";
        AutomationProperties.SetName(syncButton, "取消本次同步");
        var successful = 0;
        var attempted = 0;
        var checkedCount = 0;
        try
        {
            foreach (var address in accounts)
            {
                cancellation.Token.ThrowIfCancellationRequested();
                var provider = MailProvider.ForAddress(address);
                SetSyncStatus($"正在同步 {provider?.Name} · {++attempted}/{accounts.Length}");
                try
                {
                    var secret = Credentials.Read(address);
                    if (string.IsNullOrEmpty(secret)) throw new MailKit.Security.AuthenticationException();
                    var before = store.Schedules().Where(item => item.Account == address).ToDictionary(item => item.Id);
                    checkedCount += await Task.Run(() => inbox.Sync(address, secret, cancellation.Token, stage =>
                        DispatcherQueue.TryEnqueue(() =>
                        {
                            if (syncCancellation == cancellation && !cancellation.IsCancellationRequested)
                                SetSyncStatus($"{provider?.Name} · {stage}");
                        })), cancellation.Token);
                    foreach (var item in store.Schedules())
                        if (before.TryGetValue(item.Id, out var old) && (old.When != item.When || old.Cancelled != item.Cancelled))
                            ClearDelivered(item.Id);
                    accountsRequiringAuth.Remove(address);
                    var finished = Beijing.Now;
                    if (DailyMailSync.IsDue(finished, null, ScheduledSyncTime()))
                        store.Set($"daily:{address}", Beijing.DayKey(finished));
                    store.Set($"mail-status:{address}", $"已同步 {finished:M/d HH:mm}");
                    successful++;
                }
                catch (Exception error) when (!cancellation.IsCancellationRequested)
                {
                    if (error is MailKit.Security.AuthenticationException) accountsRequiringAuth.Add(address);
                    store.Set($"mail-status:{address}", ConnectionError(error));
                }
            }
            SetSyncStatus(successful == accounts.Length
                ? $"已同步 {successful} 个邮箱 · 检查 {checkedCount} 封"
                : $"已同步 {successful}/{accounts.Length} 个邮箱 · 请在设置查看失败原因");
        }
        catch (OperationCanceledException) { if (!exiting) SetSyncStatus("已取消同步"); }
        catch (Exception error) { if (!exiting) SetSyncStatus(cancellation.IsCancellationRequested ? "已取消同步" : ConnectionError(error)); }
        finally
        {
            syncCancellation = null;
            syncCompleted = null;
            if (!exiting)
            {
                syncButton.Content = "同步";
                AutomationProperties.SetName(syncButton, "立即同步邮箱");
                Refresh();
                Tick();
            }
            completed.TrySetResult();
        }
    }

    private bool IsAccountDue(string address, DateTimeOffset now) =>
        !accountsRequiringAuth.Contains(address) &&
        DailyMailSync.IsDue(now, store.Get($"daily:{address}"), ScheduledSyncTime());

    private Task TryDailySync()
    {
        if (exiting || accountDialog || syncCancellation is not null || Beijing.Now < nextDailyAttempt ||
            !store.Accounts().Any(address => IsAccountDue(address, Beijing.Now)))
            return Task.CompletedTask;
        nextDailyAttempt = Beijing.Now.AddMinutes(5);
        return Sync();
    }

    private void ShowIdleSyncStatus()
    {
        var count = store.Accounts().Count;
        SetSyncStatus(count == 0 ? "邮箱未绑定" : $"已绑定 {count} 个 · 每日 {ScheduledSyncTime():hh\\:mm} 同步");
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
        MailKit.Net.Imap.ImapCommandException => "邮箱拒绝 IMAP 请求，请检查网页邮箱的 IMAP 设置",
        MailKit.Security.SslHandshakeException => "无法验证邮箱的安全连接",
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
                var details = Label($"来源邮箱 · {pending.Account}\n{item.Sender}\n{item.Reason}\n{item.Snippet}", 12, Muted);
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
                var scope = ScopePicker(false);
                target.SelectionChanged += (_, _) => scope.Visibility = target.SelectedIndex >= 0 && store.RuleFor(candidates[target.SelectedIndex].Id, true) is not null
                    ? Visibility.Visible : Visibility.Collapsed;
                content.Children.Add(scope);
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
                    try
                    {
                        if (!store.Resolve(pending.Id, when, id, ignore: false, scope: (EditScope)scope.SelectedIndex))
                        { args.Cancel = true; status.Text = "该日程当前无法更新。"; }
                        else if (id.HasValue) ClearDelivered(id.Value);
                    }
                    catch (Microsoft.Data.Sqlite.SqliteException)
                    { args.Cancel = true; status.Text = "目标日期已有已编辑或完成的记录，请调整范围后重试。"; }
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
        store.EnsureOccurrences(now.Date.AddDays(-1), now.Date.AddDays(3), now);
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

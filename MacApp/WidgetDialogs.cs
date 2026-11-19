using System.Diagnostics;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Layout;

namespace CalendarTodo.Mac;

public sealed partial class WidgetWindow
{
    private bool dialogOpen;
    private static Window Dialog(string title, Control content) => new()
    {
        Title = title, Width = 410, Height = 490, MinWidth = 360, MinHeight = 300,
        WindowStartupLocation = WindowStartupLocation.CenterOwner,
        Content = new ScrollViewer { Content = content, Margin = new Avalonia.Thickness(16) }
    };

    private async Task<bool> Confirm(string title, string text, Window? owner = null)
    {
        var message = Label(text, 13);
        message.TextWrapping = Avalonia.Media.TextWrapping.Wrap;
        var window = Dialog(title, message);
        window.Height = 210;
        window.Content = Column(message, Horizontal(ActionButton("确认", () => window.Close(true)), ActionButton("取消", () => window.Close(false))));
        return await window.ShowDialog<bool>(owner ?? this);
    }

    private async Task AddSchedule()
    {
        if (dialogOpen) return;
        dialogOpen = true;
        try
        {
            var title = new TextBox { Watermark = "日程名称", MaxLength = 180 };
            var date = new DatePicker { SelectedDate = Beijing.Now, HorizontalAlignment = HorizontalAlignment.Stretch };
            var time = new TimePicker { SelectedTime = Beijing.Now.TimeOfDay, ClockIdentifier = "24HourClock", MinuteIncrement = 1 };
            var status = Label("", 12);
            var window = Dialog("添加日程", title);
            window.Content = new ScrollViewer { Margin = new Avalonia.Thickness(16), Content = Column(title, Label("日期和时间（北京时间）"), date, time, status,
                ActionButton("添加", () =>
                {
                    if (string.IsNullOrWhiteSpace(title.Text) || date.SelectedDate is not { } day || time.SelectedTime is not { } chosenTime)
                    { status.Text = "请输入名称并选择日期、时间"; return; }
                    var when = Beijing.At(day.Date + chosenTime);
                    store.AddSchedule(title.Text.Trim(), when);
                    selectedDay = when.Date;
                    month = selectedDay.AddDays(1 - selectedDay.Day);
                    window.Close();
                })) };
            await window.ShowDialog(this);
            Refresh();
        }
        finally { dialogOpen = false; }
    }

    private async Task Settings()
    {
        if (dialogOpen) return;
        dialogOpen = true;
        try
        {
            await StopSync();
            var channel = new ComboBox { ItemsSource = MailProvider.All.Select(item => item.Name).ToArray(), SelectedIndex = 0 };
            var address = new TextBox { Watermark = "完整邮箱账号，例如 yourname@qq.com", MaxLength = 128 };
            var secret = new TextBox { Watermark = "客户端授权码 / 应用专用密码", PasswordChar = '●', MaxLength = 128 };
            var instruction = Label("", 12);
            instruction.TextWrapping = Avalonia.Media.TextWrapping.Wrap;
            var status = Label("", 12);
            status.TextWrapping = Avalonia.Media.TextWrapping.Wrap;
            var accounts = new StackPanel { Spacing = 8 };
            var time = new TimePicker { SelectedTime = SyncTime(), ClockIdentifier = "24HourClock" };
            var startup = new CheckBox { Content = "登录 Mac 后启动", IsChecked = MacSystem.StartupEnabled };
            var panel = Column(Label("已绑定邮箱"), accounts, Label("添加或更新邮箱"), channel, instruction,
                ActionButton("查看官方授权说明", () => MacSystem.Open(channel.SelectedIndex == 1
                    ? "https://wx.mail.qq.com/list/readtemplate?name=app_intro.html#/agreement/authorizationCode"
                    : MailProvider.All[channel.SelectedIndex].HelpUrl)),
                Label("邮箱地址填写完整邮箱账号"), address, secret, Label("每日同步时间（北京时间）"), time, startup, status);
            var window = Dialog("邮箱与设置", panel);
            var busy = false;
            window.Closing += (_, args) => args.Cancel = busy;
            channel.SelectionChanged += (_, _) => instruction.Text = MailProvider.All[channel.SelectedIndex].Instructions;
            instruction.Text = MailProvider.All[0].Instructions;
            void RefreshAccounts()
            {
                accounts.Children.Clear();
                foreach (var account in store.Accounts())
                {
                    var text = Label($"{account}\n{store.Get($"mail-status:{account}") ?? "待同步"}", 12);
                    text.TextWrapping = Avalonia.Media.TextWrapping.Wrap;
                    accounts.Children.Add(Column(text, Horizontal(ActionButton("更新授权", () =>
                    {
                        address.Text = account;
                        channel.SelectedIndex = MailProvider.All.ToList().IndexOf(MailProvider.ForAddress(account)!);
                        secret.Text = "";
                    }), ActionButton("解除绑定", async () =>
                    {
                        if (!await Confirm("解除绑定", "解除此邮箱绑定并移除本机授权凭据？已导入的日程会保留。", window)) return;
                        try { Keychain.Delete(account); store.UnbindAccount(account); blockedAccounts.Remove(account); RefreshAccounts(); }
                        catch (Exception) { status.Text = "无法移除钥匙串凭据，请重试"; }
                    }))));
                }
            }
            RefreshAccounts();
            panel.Children.Add(ActionButton("测试并添加 / 更新", async () =>
            {
                var account = address.Text?.Trim().ToLowerInvariant() ?? "";
                var provider = MailProvider.All[channel.SelectedIndex];
                if (MailProvider.ForAddress(account) != provider || string.IsNullOrWhiteSpace(secret.Text))
                { status.Text = "请填写所选渠道的完整邮箱账号和授权凭据"; return; }
                busy = true;
                panel.IsEnabled = false;
                status.Text = "正在测试 IMAP 连接…";
                try
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                    timeout.CancelAfter(TimeSpan.FromSeconds(35));
                    var password = provider.NormalizeSecret(secret.Text);
                    await Inbox.Test(account, password, timeout.Token);
                    Keychain.Save(account, password);
                    store.BindAccount(account);
                    blockedAccounts.Remove(account);
                    store.Set($"mail-status:{account}", "已绑定，可同步");
                    secret.Text = "";
                    status.Text = "已保存，可以继续添加其他邮箱";
                    RefreshAccounts();
                }
                catch (Exception error) { status.Text = ConnectionError(error); }
                finally { panel.IsEnabled = true; busy = false; }
            }));
            panel.Children.Add(ActionButton("保存设置", () =>
            {
                try
                {
                    MacSystem.SetStartup(startup.IsChecked == true);
                    if (time.SelectedTime is { } chosen) store.Set("daily-sync-time", chosen.ToString(@"hh\:mm"));
                    window.Close();
                }
                catch (Exception) { status.Text = "无法保存登录启动设置，请检查目录权限"; }
            }));
            await window.ShowDialog(this);
            secret.Text = "";
            IdleStatus();
        }
        finally { dialogOpen = false; }
    }

    private async Task Review()
    {
        if (dialogOpen) return;
        dialogOpen = true;
        try
        {
            var pending = store.PendingItems().FirstOrDefault();
            if (pending is null) { await Confirm("待确认", "当前没有待确认的邮件"); return; }
            var suggestion = pending.Suggestion;
            var details = Label($"{suggestion.Title}\n{pending.Account}\n{suggestion.Reason}\n{suggestion.Snippet}", 12);
            details.TextWrapping = Avalonia.Media.TextWrapping.Wrap;
            var date = new DatePicker { SelectedDate = suggestion.When ?? Beijing.Now };
            var time = new TimePicker { SelectedTime = (suggestion.When ?? Beijing.Now).TimeOfDay, ClockIdentifier = "24HourClock" };
            var candidates = store.ReviewTargets(pending.Id);
            var target = new ComboBox { PlaceholderText = "选择原日程", ItemsSource = candidates.Select(item => $"{item.When:M/d HH:mm} · {item.Title}").ToArray() };
            target.IsVisible = suggestion.Action != MailAction.Add;
            date.IsVisible = time.IsVisible = suggestion.Action != MailAction.Cancel;
            var status = Label("", 12);
            var window = Dialog("确认邮件日程", details);
            window.Content = new ScrollViewer { Margin = new Avalonia.Thickness(16), Content = Column(details, date, time, target, status, Horizontal(
                ActionButton("保存", () =>
                {
                    if (suggestion.Action != MailAction.Add && target.SelectedIndex < 0) { status.Text = "请选择原日程"; return; }
                    if (suggestion.Action != MailAction.Cancel && (date.SelectedDate is null || time.SelectedTime is null)) { status.Text = "请选择日期和时间"; return; }
                    DateTimeOffset? when = suggestion.Action == MailAction.Cancel ? null : Beijing.At(date.SelectedDate!.Value.Date + time.SelectedTime!.Value);
                    if (store.Resolve(pending.Id, when, target.SelectedIndex < 0 ? null : candidates[target.SelectedIndex].Id, false)) window.Close();
                    else status.Text = "无法更新该日程，请重新确认";
                }), ActionButton("忽略", () => { store.Resolve(pending.Id, null, null, true); window.Close(); }))) };
            await window.ShowDialog(this);
            Refresh();
        }
        finally { dialogOpen = false; }
    }

    private async Task Mailboxes()
    {
        if (dialogOpen) return;
        dialogOpen = true;
        try
        {
            List<MailboxHome> links;
            try { links = JsonSerializer.Deserialize<List<MailboxHome>>(store.Get("mailbox-homepages") ?? "null") ?? MailProvider.All.Select(item => new MailboxHome(item.Name, item.Homepage)).ToList(); }
            catch (JsonException) { links = []; }
            var entries = new StackPanel { Spacing = 8 };
            var name = new TextBox { Watermark = "邮箱名称", MaxLength = 40 };
            var url = new TextBox { Watermark = "邮箱网页链接，例如 https://mail.qq.com/" };
            var status = Label("", 12);
            void RefreshLinks()
            {
                entries.Children.Clear();
                foreach (var link in links.ToArray())
                    entries.Children.Add(Row(ActionButton(link.Name, () => MacSystem.Open(link.Address)), ActionButton("删除", () =>
                    { links.Remove(link); store.Set("mailbox-homepages", JsonSerializer.Serialize(links)); RefreshLinks(); })));
            }
            RefreshLinks();
            var window = Dialog("打开邮箱与管理网页链接", Column(entries, name, url, status, ActionButton("添加链接", () =>
            {
                if (!MailboxHome.TryCreate(name.Text ?? "", url.Text ?? "", out var link)) { status.Text = "请输入名称和有效的 http / https 网页链接"; return; }
                links.Add(link!);
                store.Set("mailbox-homepages", JsonSerializer.Serialize(links));
                name.Text = url.Text = "";
                RefreshLinks();
            })));
            await window.ShowDialog(this);
        }
        finally { dialogOpen = false; }
    }

    private async Task Feedback()
    {
        if (dialogOpen) return;
        dialogOpen = true;
        try
        {
            if (await Confirm("产品反馈", "反馈将提交到公开的 GitHub Issues。请检查内容，避免填写邮箱、授权码和私人日程。"))
                MacSystem.Open("https://github.com/Stella8392/CalendarTodo/issues/new?body=" + Uri.EscapeDataString("Mac 预览版 1.4.0-preview.1\n\n反馈内容：\n\n复现步骤：\n"));
        }
        finally { dialogOpen = false; }
    }
}

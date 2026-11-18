using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.System;

namespace CalendarTodo;

public sealed partial class WidgetWindow
{
    private const string MailboxesSetting = "mailbox-homepages";

    private Button OpenMailboxButton()
    {
        var menu = new MenuFlyout();
        var button = Button("打开邮箱 ▾", "打开邮箱菜单");
        button.Flyout = menu;
        RefreshMenu();
        return button;

        void RefreshMenu()
        {
            menu.Items.Clear();
            foreach (var mailbox in Mailboxes())
            {
                var item = new MenuFlyoutItem { Text = mailbox.Name };
                item.Click += async (_, _) => await OpenMailbox(mailbox);
                menu.Items.Add(item);
            }
            if (menu.Items.Count > 0)
                menu.Items.Add(new MenuFlyoutSeparator());
            var manage = new MenuFlyoutItem { Text = "管理邮箱链接…" };
            manage.Click += async (_, _) =>
            {
                await ManageMailboxes();
                RefreshMenu();
            };
            menu.Items.Add(manage);
        }
    }

    private List<MailboxHome> Mailboxes()
    {
        if (store.Get(MailboxesSetting) is { } saved)
        {
            try { return JsonSerializer.Deserialize<List<MailboxHome>>(saved) ?? []; }
            catch (JsonException) { }
        }
        return MailProvider.All.Select(provider => new MailboxHome(provider.Name, provider.Homepage)).ToList();
    }

    private async Task OpenMailbox(MailboxHome mailbox)
    {
        try
        {
            if (MailboxHome.TryCreate(mailbox.Name, mailbox.Address, out var validated) &&
                await Launcher.LaunchUriAsync(new Uri(validated!.Address)))
                return;
        }
        catch (COMException) { }
        if (accountDialog)
            return;
        accountDialog = true;
        try
        {
            var notice = Label("无法打开此邮箱主页。请检查已保存的链接和 Windows 默认浏览器。", 12);
            notice.TextWrapping = TextWrapping.Wrap;
            await Dialog("打开邮箱", notice, "").ShowAsync();
        }
        finally { accountDialog = false; }
    }

    private async Task ManageMailboxes()
    {
        if (accountDialog)
            return;
        accountDialog = true;
        try
        {
            var mailboxes = Mailboxes();
            var selection = new ComboBox { Header = "邮箱", HorizontalAlignment = HorizontalAlignment.Stretch };
            var name = new TextBox { Header = "显示名称", PlaceholderText = "例如 QQ 邮箱", MaxLength = 40 };
            var address = new TextBox { Header = "邮箱主页链接", PlaceholderText = "https://mail.example.com/", MaxLength = 2048 };
            var status = Label("", 11, Accent);
            status.TextWrapping = TextWrapping.Wrap;
            var fields = new StackPanel { Spacing = 8 };
            fields.Children.Add(selection);
            fields.Children.Add(name);
            fields.Children.Add(address);
            fields.Children.Add(status);
            var dialog = Dialog("管理邮箱链接", fields, "保存");
            dialog.SecondaryButtonText = "删除";
            selection.SelectionChanged += (_, _) =>
            {
                var index = selection.SelectedIndex - 1;
                var entry = index >= 0 && index < mailboxes.Count ? mailboxes[index] : null;
                name.Text = entry?.Name ?? "";
                address.Text = entry?.Address ?? "";
                dialog.IsSecondaryButtonEnabled = entry is not null;
                status.Text = "";
            };
            ReloadChoices();
            dialog.PrimaryButtonClick += (_, args) =>
            {
                var index = selection.SelectedIndex - 1;
                if (!MailboxHome.TryCreate(name.Text, address.Text, out var entry))
                {
                    status.Text = "请输入名称和以 https:// 或 http:// 开头的主页链接。";
                    args.Cancel = true;
                    return;
                }
                if (mailboxes.Where((_, i) => i != index).Any(item =>
                    string.Equals(item.Name, entry!.Name, StringComparison.OrdinalIgnoreCase)))
                {
                    status.Text = "这个邮箱名称已存在，请使用其他名称。";
                    args.Cancel = true;
                    return;
                }
                if (index < 0)
                    mailboxes.Add(entry!);
                else
                    mailboxes[index] = entry!;
                Save();
            };
            dialog.SecondaryButtonClick += (_, args) =>
            {
                var index = selection.SelectedIndex - 1;
                if (index >= 0 && index < mailboxes.Count)
                {
                    mailboxes.RemoveAt(index);
                    Save();
                    ReloadChoices();
                }
                args.Cancel = true;
            };
            await dialog.ShowAsync();

            void Save() => store.Set(MailboxesSetting, JsonSerializer.Serialize(mailboxes));

            void ReloadChoices()
            {
                selection.ItemsSource = new[] { "＋ 新增邮箱" }.Concat(mailboxes.Select(item => item.Name)).ToArray();
                selection.SelectedIndex = 0;
            }
        }
        finally { accountDialog = false; }
    }
}

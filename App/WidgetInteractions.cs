using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.System;

namespace CalendarTodo;

public sealed partial class WidgetWindow
{
    private MenuFlyout DeleteMenu(string kind, string title, Action delete)
    {
        var menu = new MenuFlyout();
        var item = new MenuFlyoutItem { Text = $"删除{kind}" };
        menu.Items.Add(item);
        item.Click += async (_, _) =>
        {
            if (accountDialog) return;
            accountDialog = true;
            try
            {
                var message = Label($"删除「{title}」？", 13);
                message.TextWrapping = TextWrapping.Wrap;
                if (await Dialog($"删除{kind}", message, "删除").ShowAsync() != ContentDialogResult.Primary)
                    return;
                delete();
                Refresh();
            }
            finally { accountDialog = false; }
        };
        return menu;
    }

    private Button FeedbackButton()
    {
        var button = Button("反馈", "提供产品反馈");
        button.Click += async (_, _) =>
        {
            if (accountDialog) return;
            accountDialog = true;
            try
            {
                var message = Label("反馈会发布到公开的 GitHub Issues。请在浏览器内检查内容后提交，避免填写邮箱、授权码或私人日程。", 13);
                message.TextWrapping = TextWrapping.Wrap;
                if (await Dialog("产品反馈", message, "打开反馈页面").ShowAsync() != ContentDialogResult.Primary)
                    return;
                var version = typeof(WidgetWindow).Assembly.GetName().Version?.ToString(3);
                var body = Uri.EscapeDataString($"应用版本：{version}\n\n反馈内容：\n\n复现步骤（如适用）：\n");
                try
                {
                    if (await Launcher.LaunchUriAsync(new Uri($"https://github.com/Stella8392/CalendarTodo/issues/new?body={body}")))
                        return;
                }
                catch (COMException) { }
                await Dialog("产品反馈", Label("无法打开默认浏览器。", 13), "").ShowAsync();
            }
            finally { accountDialog = false; }
        };
        return button;
    }

    private async Task AddSchedule()
    {
        if (accountDialog)
            return;
        accountDialog = true;
        try
        {
            var now = Beijing.Now;
            var title = new TextBox { Header = "日程名称", MaxLength = 180 };
            var date = new DatePicker
            {
                Header = "日期（北京时间）", Language = "zh-CN", HorizontalAlignment = HorizontalAlignment.Stretch,
                CalendarIdentifier = Windows.Globalization.CalendarIdentifiers.Gregorian,
                SelectedDate = new DateTimeOffset(now.Date.AddHours(12))
            };
            var time = new TimePicker
            {
                Header = "时间（24 小时制）", Language = "zh-CN", HorizontalAlignment = HorizontalAlignment.Stretch,
                ClockIdentifier = Windows.Globalization.ClockIdentifiers.TwentyFourHour,
                MinuteIncrement = 1, SelectedTime = new TimeSpan(now.Hour, now.Minute, 0)
            };
            var status = Label("", 11, Accent);
            var content = new StackPanel { Spacing = 8 };
            content.Children.Add(title);
            content.Children.Add(date);
            content.Children.Add(time);
            content.Children.Add(status);
            var dialog = Dialog("添加日程", content, "添加");
            dialog.PrimaryButtonClick += (_, args) =>
            {
                var name = title.Text.Trim();
                if (name.Length == 0 || date.SelectedDate is not { } chosenDay || time.SelectedTime is not { } chosenTime)
                {
                    status.Text = "请输入日程名称，并选择日期和时间。";
                    args.Cancel = true;
                    return;
                }
                var when = Beijing.At(chosenDay.LocalDateTime.Date + chosenTime);
                store.AddSchedule(name, when);
                selected = when.Date;
                month = when.Date.AddDays(1 - when.Day);
            };
            if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            {
                Refresh();
                Tick();
            }
        }
        finally { accountDialog = false; }
    }
}

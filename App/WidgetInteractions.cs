using System;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace CalendarTodo;

public sealed partial class WidgetWindow
{
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

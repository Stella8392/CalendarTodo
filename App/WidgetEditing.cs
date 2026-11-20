using System;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace CalendarTodo;

public sealed partial class WidgetWindow
{
    private MenuFlyout ItemMenu(Schedule? schedule, Todo? task, Action<Func<Task>>? defer = null)
    {
        var menu = new MenuFlyout();
        void Add(string text, Func<Task> action)
        {
            var item = new MenuFlyoutItem { Text = text };
            item.Click += async (_, _) => { if (defer is not null) defer(action); else await action(); };
            menu.Items.Add(item);
        }
        Add("编辑事项…", () => EditEntry(schedule, task));
        Add(schedule is null && task?.ScheduleId is null ? "调整执行日期…" : "调整日期与时间…", () => EditEntry(schedule, task));
        Add("设置重复…", () => SetRepeat(schedule, task));
        Add("删除…", () => DeleteEntry(schedule, task));
        return menu;
    }

    private Schedule? LinkedSchedule(Todo? task) => task?.ScheduleId is { } id ? store.Schedules().FirstOrDefault(item => item.Id == id) : null;
    private static ComboBox ScopePicker(bool visible) => new()
    {
        Header = "影响范围", ItemsSource = new[] { "仅此次", "此次及以后" }, SelectedIndex = 0,
        Visibility = visible ? Visibility.Visible : Visibility.Collapsed, HorizontalAlignment = HorizontalAlignment.Stretch
    };

    private async Task EditEntry(Schedule? schedule, Todo? task)
    {
        if (accountDialog) return;
        accountDialog = true;
        try
        {
            schedule ??= LinkedSchedule(task);
            var isSchedule = schedule is not null;
            var id = schedule?.Id ?? task!.Id;
            var when = schedule?.When ?? task?.Day ?? Beijing.Now;
            var title = new TextBox { Header = "事项名称", Text = schedule?.Title ?? task!.Title, MaxLength = 180 };
            var date = new DatePicker { Header = "日期（北京时间）", SelectedDate = new DateTimeOffset(when.Date.AddHours(12)), Language = "zh-CN" };
            var time = new TimePicker { Header = "时间", ClockIdentifier = "24HourClock", SelectedTime = new TimeSpan(when.Hour, when.Minute, 0), Visibility = isSchedule ? Visibility.Visible : Visibility.Collapsed };
            var priority = PriorityPicker(task?.Priority ?? store.Todos(null, true).FirstOrDefault(item => item.ScheduleId == id)?.Priority ?? Priority.Medium);
            var scope = ScopePicker(store.RuleFor(id, isSchedule) is not null);
            var carried = isSchedule && store.Todos(null, true).Any(item => item.ScheduleId == id && item.MovedTo is not null);
            var carry = new ComboBox
            {
                Header = "此日程的待办已顺延，改期时如何处理？", ItemsSource = new[] { "同步调整当前未完成待办", "仅调整日程记录" },
                SelectedIndex = 0, Visibility = carried ? Visibility.Visible : Visibility.Collapsed
            };
            var error = Label("", 12, Accent);
            error.TextWrapping = TextWrapping.Wrap;
            var collision = new CheckBox { Content = "确认保留同一天的两次安排", Visibility = Visibility.Collapsed };
            date.SelectedDateChanged += (_, _) => { collision.IsChecked = false; collision.Visibility = Visibility.Collapsed; };
            scope.SelectionChanged += (_, _) => { collision.IsChecked = false; collision.Visibility = Visibility.Collapsed; };
            var panel = new StackPanel { Spacing = 9 };
            foreach (var child in new UIElement[] { title, date, time, Label("优先级", 12), priority, scope, carry, collision, error }) panel.Children.Add(child);
            var dialog = Dialog("编辑事项", new ScrollViewer { Content = panel, MaxHeight = 380 }, "保存");
            dialog.PrimaryButtonClick += (_, args) =>
            {
                if (string.IsNullOrWhiteSpace(title.Text) || date.SelectedDate is not { } day || (isSchedule && time.SelectedTime is null))
                { args.Cancel = true; error.Text = "请输入名称并选择日期、时间。"; return; }
                try
                {
                    var updated = Beijing.At(day.Date + (isSchedule ? time.SelectedTime!.Value : TimeSpan.Zero));
                    if (isSchedule && scope.SelectedIndex == 0 && updated.Date != when.Date && store.RelatedSchedulesOnDate(id, updated.Date) > 0 && collision.IsChecked != true)
                    { args.Cancel = true; collision.Visibility = Visibility.Visible; error.Text = "目标日期已有同系列日程。确认后将保留两次安排。"; return; }
                    store.EditItem(id, isSchedule, title.Text, (Priority)priority.SelectedIndex, updated,
                        (EditScope)scope.SelectedIndex, carry.SelectedIndex == 0);
                    if (isSchedule) ClearDelivered(id);
                    selected = updated.Date;
                    month = updated.Date.AddDays(1 - updated.Day);
                }
                catch (Exception exception) when (exception is ArgumentException or Microsoft.Data.Sqlite.SqliteException)
                { args.Cancel = true; error.Text = "无法保存，请检查日期。同系列目标日期可能已有已编辑或完成的事项。"; }
            };
            if (await dialog.ShowAsync() == ContentDialogResult.Primary) Refresh();
        }
        finally { accountDialog = false; }
    }

    private async Task SetRepeat(Schedule? schedule, Todo? task)
    {
        if (accountDialog) return;
        accountDialog = true;
        try
        {
            schedule ??= LinkedSchedule(task);
            var id = schedule?.Id ?? task!.Id;
            var isSchedule = schedule is not null;
            var rule = store.RuleFor(id, isSchedule);
            var original = (schedule?.When ?? task?.Day ?? Beijing.Now).Date;
            var frequency = new ComboBox { Header = "重复", ItemsSource = new[] { "不重复", "每天", "每周" }, SelectedIndex = (int)(rule?.Frequency ?? RepeatFrequency.None) };
            var weekdays = new CheckBox[7];
            var week = new Grid { ColumnSpacing = 6, RowSpacing = 3 };
            for (var column = 0; column < 4; column++) week.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            week.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            week.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            for (var index = 0; index < 7; index++)
            {
                weekdays[index] = new CheckBox { Content = "周" + "一二三四五六日"[index], MinWidth = 0,
                    IsChecked = ((rule?.Weekdays ?? RepeatRule.DayBit(original.DayOfWeek)) & (1 << index)) != 0 };
                Grid.SetRow(weekdays[index], index / 4); Grid.SetColumn(weekdays[index], index % 4); week.Children.Add(weekdays[index]);
            }
            var start = new DatePicker { Header = "生效日期（此前历史保留）", SelectedDate = new DateTimeOffset((original < Beijing.Now.Date ? Beijing.Now.Date : original).AddHours(12)), Language = "zh-CN" };
            var note = Label(isSchedule ? "日程与关联待办共用此重复规则。" : "未完成项到下一个重复日期时顺延并合并。", 12, Muted);
            note.TextWrapping = TextWrapping.Wrap;
            var error = Label("", 12, Accent);
            error.TextWrapping = TextWrapping.Wrap;
            void ShowWeek() => week.Visibility = frequency.SelectedIndex == 2 ? Visibility.Visible : Visibility.Collapsed;
            frequency.SelectionChanged += (_, _) => ShowWeek();
            ShowWeek();
            var panel = new StackPanel { Spacing = 10 };
            foreach (var child in new UIElement[] { frequency, week, start, note, error }) panel.Children.Add(child);
            var dialog = Dialog("设置重复", panel, "保存");
            dialog.PrimaryButtonClick += (_, args) =>
            {
                var mask = Enumerable.Range(0, 7).Where(index => weekdays[index].IsChecked == true).Sum(index => 1 << index);
                if (start.SelectedDate is not { } day || (frequency.SelectedIndex == 2 && mask == 0))
                { args.Cancel = true; error.Text = "请选择生效日期，每周重复至少选择一个星期。"; return; }
                try
                {
                    store.ConfigureRepeat(id, isSchedule, new RepeatRule((RepeatFrequency)frequency.SelectedIndex, mask, day.Date));
                    if (isSchedule) ClearDelivered(id);
                }
                catch (Exception exception) when (exception is ArgumentException or Microsoft.Data.Sqlite.SqliteException)
                { args.Cancel = true; error.Text = "无法更新，目标日期可能已有该系列的已编辑或完成记录。"; }
            };
            if (await dialog.ShowAsync() == ContentDialogResult.Primary) Refresh();
        }
        finally { accountDialog = false; }
    }

    private async Task DeleteEntry(Schedule? schedule, Todo? task)
    {
        if (accountDialog) return;
        accountDialog = true;
        try
        {
            var id = schedule?.Id ?? task!.Id;
            var isSchedule = schedule is not null;
            var scope = ScopePicker(store.RuleFor(id, isSchedule) is not null);
            var panel = new StackPanel { Spacing = 10 };
            var message = Label($"删除「{schedule?.Title ?? task!.Title}」？", 13);
            message.TextWrapping = TextWrapping.Wrap;
            panel.Children.Add(message);
            panel.Children.Add(scope);
            if (await Dialog("删除事项", panel, "删除").ShowAsync() != ContentDialogResult.Primary) return;
            if (scope.SelectedIndex == 1) store.DeleteFollowing(id, isSchedule);
            else if (isSchedule) store.DeleteSchedule(id);
            else store.DeleteTodo(id);
            if (isSchedule) ClearDelivered(id);
            Refresh();
        }
        finally { accountDialog = false; }
    }

    private async Task AddTodoForDate(DateTime day)
    {
        if (accountDialog) return;
        accountDialog = true;
        try
        {
            var title = new TextBox { Header = "待办名称", MaxLength = 180 };
            var priority = PriorityPicker(Priority.Medium);
            var date = new DatePicker { Header = "执行日期", SelectedDate = new DateTimeOffset(day.AddHours(12)), Language = "zh-CN" };
            var panel = new StackPanel { Spacing = 10 };
            panel.Children.Add(title); panel.Children.Add(date); panel.Children.Add(priority);
            var dialog = Dialog("添加待办", panel, "添加");
            dialog.PrimaryButtonClick += (_, args) =>
            {
                if (string.IsNullOrWhiteSpace(title.Text) || date.SelectedDate is not { } chosen) { args.Cancel = true; return; }
                store.AddTodo(title.Text.Trim(), (Priority)priority.SelectedIndex, Beijing.At(chosen.Date));
            };
            if (await dialog.ShowAsync() == ContentDialogResult.Primary) Refresh();
        }
        finally { accountDialog = false; }
    }
}

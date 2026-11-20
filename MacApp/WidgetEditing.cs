using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;

namespace CalendarTodo.Mac;

public sealed partial class WidgetWindow
{
    private ContextMenu ItemMenu(Schedule? schedule, Todo? task, Action<Func<Task>>? defer = null)
    {
        var items = new List<MenuItem>();
        void Add(string title, Func<Task> action)
        {
            var item = new MenuItem { Header = title };
            item.Click += async (_, _) => { if (defer is not null) defer(action); else await action(); };
            items.Add(item);
        }
        Add("编辑事项…", () => EditEntry(schedule, task));
        Add(schedule is null && task?.ScheduleId is null ? "调整执行日期…" : "调整日期与时间…", () => EditEntry(schedule, task));
        Add("设置重复…", () => SetRepeat(schedule, task));
        Add("删除…", () => DeleteEntry(schedule, task));
        return new ContextMenu { ItemsSource = items };
    }

    private Schedule? LinkedSchedule(Todo? task) => task?.ScheduleId is { } id ? store.Schedules().FirstOrDefault(item => item.Id == id) : null;
    private static ComboBox ScopePicker(bool enabled) => new()
    {
        ItemsSource = new[] { "仅此次", "此次及以后" }, SelectedIndex = 0, IsVisible = enabled,
        HorizontalAlignment = HorizontalAlignment.Stretch
    };

    private async Task EditEntry(Schedule? schedule, Todo? task)
    {
        if (dialogOpen) return;
        dialogOpen = true;
        try
        {
            schedule ??= LinkedSchedule(task);
            var isSchedule = schedule is not null;
            var id = schedule?.Id ?? task!.Id;
            var when = schedule?.When ?? task?.Day ?? Beijing.Now;
            var title = new TextBox { Text = schedule?.Title ?? task!.Title, MaxLength = 180 };
            var date = new DatePicker { SelectedDate = when };
            var time = new TimePicker { SelectedTime = new TimeSpan(when.Hour, when.Minute, 0), ClockIdentifier = "24HourClock", IsVisible = isSchedule };
            var priority = PriorityPicker(task?.Priority ?? store.Todos(null, true).FirstOrDefault(item => item.ScheduleId == id)?.Priority ?? Priority.Medium);
            var scope = ScopePicker(store.RuleFor(id, isSchedule) is not null);
            var carried = isSchedule && store.Todos(null, true).Any(item => item.ScheduleId == id && item.MovedTo is not null);
            var carry = new ComboBox { ItemsSource = new[] { "同步调整当前未完成待办", "仅调整日程记录" }, SelectedIndex = 0, IsVisible = carried };
            var error = Label("", 12);
            error.TextWrapping = Avalonia.Media.TextWrapping.Wrap;
            var collision = new CheckBox { Content = "确认保留同一天的两次安排", IsVisible = false };
            date.PropertyChanged += (_, args) => { if (args.Property == DatePicker.SelectedDateProperty) { collision.IsChecked = false; collision.IsVisible = false; } };
            scope.SelectionChanged += (_, _) => { collision.IsChecked = false; collision.IsVisible = false; };
            var panel = Column(Label("事项名称"), title, Label("日期和时间（北京时间）"), date, time, Label("优先级"), priority, scope, carry, collision, error);
            var window = Dialog("编辑事项", panel);
            panel.Children.Add(ActionButton("保存", () =>
            {
                if (string.IsNullOrWhiteSpace(title.Text) || date.SelectedDate is not { } chosen || (isSchedule && time.SelectedTime is null))
                { error.Text = "请输入名称并选择日期、时间"; return; }
                try
                {
                    var updated = Beijing.At(chosen.Date + (isSchedule ? time.SelectedTime!.Value : TimeSpan.Zero));
                    if (isSchedule && scope.SelectedIndex == 0 && updated.Date != when.Date && store.RelatedSchedulesOnDate(id, updated.Date) > 0 && collision.IsChecked != true)
                    { collision.IsVisible = true; error.Text = "目标日期已有同系列日程。确认后将保留两次安排。"; return; }
                    store.EditItem(id, isSchedule, title.Text, (Priority)priority.SelectedIndex, updated, (EditScope)scope.SelectedIndex, carry.SelectedIndex == 0);
                    selectedDay = updated.Date;
                    month = updated.Date.AddDays(1 - updated.Day);
                    window.Close();
                }
                catch (Exception exception) when (exception is ArgumentException or Microsoft.Data.Sqlite.SqliteException)
                { error.Text = "无法保存，请检查日期。同系列目标日期可能已有已编辑或完成事项。"; }
            }));
            await window.ShowDialog(this);
            Refresh();
        }
        finally { dialogOpen = false; }
    }

    private async Task SetRepeat(Schedule? schedule, Todo? task)
    {
        if (dialogOpen) return;
        dialogOpen = true;
        try
        {
            schedule ??= LinkedSchedule(task);
            var id = schedule?.Id ?? task!.Id;
            var isSchedule = schedule is not null;
            var rule = store.RuleFor(id, isSchedule);
            var original = (schedule?.When ?? task?.Day ?? Beijing.Now).Date;
            var frequency = new ComboBox { ItemsSource = new[] { "不重复", "每天", "每周" }, SelectedIndex = (int)(rule?.Frequency ?? RepeatFrequency.None) };
            var checks = new CheckBox[7];
            var week = new WrapPanel { Orientation = Orientation.Horizontal };
            for (var index = 0; index < 7; index++)
            {
                checks[index] = new CheckBox { Content = "周" + "一二三四五六日"[index], Margin = new Thickness(0, 0, 8, 0),
                    IsChecked = ((rule?.Weekdays ?? RepeatRule.DayBit(original.DayOfWeek)) & (1 << index)) != 0 };
                week.Children.Add(checks[index]);
            }
            var start = new DatePicker { SelectedDate = new DateTimeOffset((original < Beijing.Now.Date ? Beijing.Now.Date : original).AddHours(12)) };
            var note = Label(isSchedule ? "日程和关联待办共用此规则" : "未完成项到下一重复日期顺延并合并", 12);
            var error = Label("", 12);
            error.TextWrapping = Avalonia.Media.TextWrapping.Wrap;
            week.IsVisible = frequency.SelectedIndex == 2;
            frequency.SelectionChanged += (_, _) => week.IsVisible = frequency.SelectedIndex == 2;
            var panel = Column(Label("重复频率"), frequency, week, Label("生效日期（此前历史保留）"), start, note, error);
            var window = Dialog("设置重复", panel);
            panel.Children.Add(ActionButton("保存", () =>
            {
                var mask = Enumerable.Range(0, 7).Where(index => checks[index].IsChecked == true).Sum(index => 1 << index);
                if (start.SelectedDate is not { } day || (frequency.SelectedIndex == 2 && mask == 0))
                { error.Text = "请选择日期，每周重复至少选择一个星期"; return; }
                try
                {
                    store.ConfigureRepeat(id, isSchedule, new RepeatRule((RepeatFrequency)frequency.SelectedIndex, mask, day.Date));
                    window.Close();
                }
                catch (Exception exception) when (exception is ArgumentException or Microsoft.Data.Sqlite.SqliteException)
                { error.Text = "无法更新，目标日期可能已有已编辑或完成记录。"; }
            }));
            await window.ShowDialog(this);
            Refresh();
        }
        finally { dialogOpen = false; }
    }

    private async Task DeleteEntry(Schedule? schedule, Todo? task)
    {
        if (dialogOpen) return;
        dialogOpen = true;
        try
        {
            var id = schedule?.Id ?? task!.Id;
            var scope = ScopePicker(store.RuleFor(id, schedule is not null) is not null);
            var panel = Column(Label($"删除「{schedule?.Title ?? task!.Title}」？", 13), scope);
            var window = Dialog("删除事项", panel);
            window.Height = 260;
            panel.Children.Add(ActionButton("删除", () => window.Close(true)));
            panel.Children.Add(ActionButton("取消", () => window.Close(false)));
            if (!await window.ShowDialog<bool>(this)) return;
            if (scope.SelectedIndex == 1) store.DeleteFollowing(id, schedule is not null);
            else if (schedule is not null) store.DeleteSchedule(id);
            else store.DeleteTodo(id);
            Refresh();
        }
        finally { dialogOpen = false; }
    }

    private async Task AddTodoForDate(DateTime day)
    {
        if (dialogOpen) return;
        dialogOpen = true;
        try
        {
            var title = new TextBox { Watermark = "待办名称", MaxLength = 180 };
            var date = new DatePicker { SelectedDate = new DateTimeOffset(day.AddHours(12)) };
            var priority = PriorityPicker(Priority.Medium);
            var panel = Column(title, Label("执行日期"), date, priority);
            var window = Dialog("添加待办", panel);
            panel.Children.Add(ActionButton("添加", () =>
            {
                if (string.IsNullOrWhiteSpace(title.Text) || date.SelectedDate is not { } chosen) return;
                store.AddTodo(title.Text.Trim(), (Priority)priority.SelectedIndex, Beijing.At(chosen.Date));
                window.Close();
            }));
            await window.ShowDialog(this);
            Refresh();
        }
        finally { dialogOpen = false; }
    }
}

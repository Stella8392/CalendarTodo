using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace CalendarTodo.Mac;

public sealed partial class WidgetWindow
{
    private bool monthExpanded;
    private readonly Button monthToggle = new() { Content = "月览" };
    private readonly Grid monthOverview = new() { IsVisible = false, RowDefinitions = new RowDefinitions("Auto,*") };
    private readonly Grid monthCells = new() { ColumnDefinitions = new ColumnDefinitions("*,*,*,*,*,*,*") };
    private readonly TextBlock overviewHeading = Label("", 18);
    private (int Regular, int Overflow) monthCapacity;

    private (int Regular, int Overflow) MonthCapacities()
    {
        var height = Math.Max(0, (monthCells.Bounds.Height - 20) / 6);
        return (Math.Clamp((int)((height - 28) / 26), 0, 4), Math.Clamp((int)((height - 49) / 26), 0, 4));
    }
    private void BuildMonthOverview()
    {
        monthToggle.Click += (_, _) => ToggleMonth();
        Add(monthOverview, Row(overviewHeading, Horizontal(
            ActionButton("‹", () => { month = month.AddMonths(-1); Refresh(); }),
            ActionButton("今天", () => { month = Beijing.Now.Date.AddDays(1 - Beijing.Now.Day); selectedDay = Beijing.Now.Date; Refresh(); }),
            ActionButton("›", () => { month = month.AddMonths(1); Refresh(); }))), 0);
        Add(monthOverview, monthCells, 1);
        Grid.SetRowSpan(monthOverview, 4);
        Add(layout, monthOverview, 1);
        monthCells.SizeChanged += (_, _) =>
        {
            var count = MonthCapacities();
            if (monthExpanded && count != monthCapacity) RefreshMonthOverview();
        };
    }

    private void ToggleMonth()
    {
        SaveLayout();
        monthExpanded = !monthExpanded;
        store.Set("mac-month-expanded", monthExpanded ? "1" : "0");
        monthToggle.Content = monthExpanded ? "收起" : "月览";
        foreach (var control in layout.Children.Where(control => control != monthOverview && Grid.GetRow(control) is >= 1 and <= 4))
            control.IsVisible = !monthExpanded;
        monthOverview.IsVisible = monthExpanded;
        layout.RowDefinitions[1].Height = monthExpanded ? new GridLength(1, GridUnitType.Star) : GridLength.Auto;
        layout.RowDefinitions[2].Height = monthExpanded ? new GridLength(0) : new GridLength(Number("mac-agenda-weight", 1), GridUnitType.Star);
        layout.RowDefinitions[3].Height = new GridLength(monthExpanded ? 0 : 6);
        layout.RowDefinitions[4].Height = monthExpanded ? new GridLength(0) : new GridLength(1.2, GridUnitType.Star);
        MinWidth = monthExpanded ? 760 : 360;
        MinHeight = monthExpanded ? 500 : 540;
        var prefix = monthExpanded ? "mac-month-" : "mac-";
        Width = Number(prefix + "width", monthExpanded ? 1000 : 410);
        Height = Number(prefix + "height", monthExpanded ? 720 : 700);
        RestorePosition();
        Refresh();
    }

    private void RefreshMonthOverview()
    {
        if (!monthExpanded) return;
        overviewHeading.Text = month.ToString("yyyy 年 M 月");
        var first = month.AddDays(-(((int)month.DayOfWeek + 6) % 7));
        var entries = store.MonthEntries(first, first.AddDays(42));
        monthCells.Children.Clear();
        monthCells.RowDefinitions.Clear();
        monthCells.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        for (var row = 0; row < 6; row++) monthCells.RowDefinitions.Add(new RowDefinition());
        for (var col = 0; col < 7; col++)
        {
            var title = Label("星期" + "一二三四五六日"[col], 12);
            Grid.SetColumn(title, col); monthCells.Children.Add(title);
        }
        monthCapacity = MonthCapacities();
        for (var index = 0; index < 42; index++)
        {
            var day = first.AddDays(index);
            var items = entries.Where(entry => entry.Day == day).ToArray();
            var date = ActionButton(day.Day.ToString(), async () => { selectedDay = day; await ShowMonthDay(day); });
            date.Padding = new Thickness(3, 0);
            date.MinHeight = date.Height = 20;
            date.Background = day == Beijing.Now.Date ? Brush.Parse("#C4D7F7") : Brushes.Transparent;
            var count = items.Count(item => item.Schedule is not null);
            var countLabel = Label(count > 0 ? count.ToString() : "", 10);
            countLabel.Foreground = Brushes.Crimson;
            var contents = Column(Row(date, countLabel));
            contents.Spacing = 3;
            contents.Margin = new Thickness(0);
            var capacity = items.Length <= monthCapacity.Regular ? monthCapacity.Regular : monthCapacity.Overflow;
            foreach (var item in items.Take(capacity)) contents.Children.Add(MonthCard(item, Refresh));
            if (items.Length > capacity)
            {
                var more = ActionButton($"＋{items.Length - capacity}", () => ShowMonthDay(day));
                more.Height = more.MinHeight = 18;
                more.Padding = new Thickness(3, 0);
                contents.Children.Add(more);
            }
            var cell = new Border
            {
                Child = contents, Padding = new Thickness(4), Background = Brush.Parse("#CFFFFFFF"),
                BorderBrush = Brush.Parse("#B8C6D6"), BorderThickness = new Thickness(0, 0, 1, 1),
                Opacity = day.Month == month.Month ? 1 : 0.5
            };
            var addTodo = new MenuItem { Header = "添加待办…" };
            addTodo.Click += async (_, _) => await AddTodoForDate(day);
            var addSchedule = new MenuItem { Header = "添加日程…" };
            addSchedule.Click += async (_, _) => await AddScheduleForDate(day);
            cell.ContextMenu = new ContextMenu { ItemsSource = new[] { addTodo, addSchedule } };
            Grid.SetColumn(cell, index % 7); Grid.SetRow(cell, index / 7 + 1); monthCells.Children.Add(cell);
        }
    }

    private Control MonthCard(MonthEntry entry, Action changed, Action<MonthEntry>? edit = null, Action<Func<Task>>? defer = null)
    {
        var title = Label((entry.Schedule is { } schedule ? $"{schedule.When:HH:mm} " : "") + entry.Title, 11);
        title.TextTrimming = TextTrimming.CharacterEllipsis;
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        if (entry.Todo is { } task)
        {
            var check = new CheckBox { IsChecked = task.Done, MinWidth = 0, MinHeight = 22, Padding = new Thickness(0), IsEnabled = task.MovedTo is null };
            Avalonia.Automation.AutomationProperties.SetName(check, "完成 " + entry.Title);
            if (task.Done) { title.TextDecorations = TextDecorations.Strikethrough; title.Foreground = Brushes.DimGray; }
            if (task.MovedTo is { } moved) { title.Text += $" ↪ {moved[5..]}"; title.Foreground = Brushes.DimGray; }
            check.IsCheckedChanged += (_, _) => { store.SetDone(task.Id, check.IsChecked == true); Avalonia.Threading.Dispatcher.UIThread.Post(changed); };
            row.Children.Add(check);
        }
        var button = ActionButton("", async () => { if (edit is not null) edit(entry); else await EditEntry(entry.Schedule, entry.Todo); });
        button.Content = title; button.Padding = new Thickness(1, 0); button.Height = button.MinHeight = 23;
        button.HorizontalAlignment = HorizontalAlignment.Stretch;
        Grid.SetColumn(button, 1); row.Children.Add(button);
        row.ContextMenu = ItemMenu(entry.Schedule, entry.Todo, defer);
        ToolTip.SetTip(row, entry.Title + (entry.Todo?.MovedTo is { } target ? $"\n已顺延至 {target}" : ""));
        return new Border { Child = row, BorderBrush = new[] { Brushes.ForestGreen, Brushes.Goldenrod, Brushes.Crimson }[(int)entry.Priority],
            BorderThickness = new Thickness(3, 0, 0, 0), CornerRadius = new CornerRadius(3) };
    }

    private async Task ShowMonthDay(DateTime day)
    {
        if (dialogOpen) return;
        dialogOpen = true;
        Func<Task>? next = null;
        try
        {
            var list = new StackPanel { Spacing = 7 };
            var panel = Column(Horizontal(ActionButton("添加待办", () => { next = () => AddTodoForDate(day); currentDayWindow!.Close(); }),
                ActionButton("添加日程", () => { next = () => AddScheduleForDate(day); currentDayWindow!.Close(); })), list);
            var window = Dialog(day.ToString("M 月 d 日"), panel);
            currentDayWindow = window;
            void Fill()
            {
                list.Children.Clear();
                foreach (var item in store.MonthEntries(day, day.AddDays(1)))
                    list.Children.Add(MonthCard(item, () => { Refresh(); Fill(); },
                        entry => { next = () => EditEntry(entry.Schedule, entry.Todo); window.Close(); },
                        action => { next = action; window.Close(); }));
                if (list.Children.Count == 0) list.Children.Add(Label("当天暂无事项", 13));
            }
            Fill();
            await window.ShowDialog(this);
        }
        finally { dialogOpen = false; currentDayWindow = null; }
        if (next is not null) await next();
    }
    private Window? currentDayWindow;
}

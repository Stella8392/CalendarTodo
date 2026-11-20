using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI.Text;

namespace CalendarTodo;

public sealed partial class WidgetWindow
{
    private bool monthExpanded;
    private readonly Button monthToggle = Button("月览", "展开月度预览");
    private readonly Grid monthOverview = new() { Visibility = Visibility.Collapsed, RowSpacing = 7 };
    private readonly Grid monthCells = new() { RowSpacing = 1, ColumnSpacing = 1 };
    private readonly TextBlock overviewHeading = Label("", 18, strong: true);
    private (int Regular, int Overflow) monthCapacity;

    private (int Regular, int Overflow) MonthCapacities()
    {
        var height = Math.Max(0, (monthCells.ActualHeight - 20) / 6);
        return (Math.Clamp((int)((height - 28) / 26), 0, 4), Math.Clamp((int)((height - 49) / 26), 0, 4));
    }
    private void BuildMonthOverview()
    {
        monthToggle.Click += (_, _) => ToggleMonth();
        monthOverview.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        monthOverview.RowDefinitions.Add(new RowDefinition());
        var navigation = Row(5);
        var previous = Button("‹", "月视图上个月");
        var today = Button("今天", "月视图回到今天");
        var next = Button("›", "月视图下个月");
        previous.Click += (_, _) => Navigate(-1);
        next.Click += (_, _) => Navigate(1);
        today.Click += (_, _) => { month = Beijing.Now.Date.AddDays(1 - Beijing.Now.Day); selected = null; Refresh(); };
        navigation.Children.Add(previous); navigation.Children.Add(today); navigation.Children.Add(next);
        monthOverview.Children.Add(Split(overviewHeading, navigation));
        Grid.SetRow(monthCells, 1);
        monthOverview.Children.Add(monthCells);
        Grid.SetRow(monthOverview, 1);
        Grid.SetRowSpan(monthOverview, 4);
        root.Children.Add(monthOverview);
        monthCells.SizeChanged += (_, _) =>
        {
            var capacity = MonthCapacities();
            if (monthExpanded && capacity != monthCapacity) RefreshMonthOverview();
        };
    }

    private void ToggleMonth()
    {
        SavePosition();
        monthExpanded = !monthExpanded;
        store.Set("month-expanded", monthExpanded ? "1" : "0");
        monthToggle.Content = monthExpanded ? "收起" : "月览";
        foreach (var element in root.Children.OfType<FrameworkElement>().Where(element => element != monthOverview && Grid.GetRow(element) is >= 1 and <= 4))
            element.Visibility = monthExpanded ? Visibility.Collapsed : Visibility.Visible;
        monthOverview.Visibility = monthExpanded ? Visibility.Visible : Visibility.Collapsed;
        root.RowDefinitions[1].Height = monthExpanded ? new GridLength(1, GridUnitType.Star) : GridLength.Auto;
        agendaRow.Height = new GridLength(monthExpanded ? 0 : preferredAgendaHeight);
        root.RowDefinitions[3].Height = new GridLength(monthExpanded ? 0 : DividerHeight);
        root.RowDefinitions[4].Height = monthExpanded ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        FitWindow();
        Refresh();
    }

    private void RefreshMonthOverview()
    {
        if (!monthExpanded) return;
        overviewHeading.Text = month.ToString("yyyy 年 M 月", Chinese);
        var first = month.AddDays(-(((int)month.DayOfWeek + 6) % 7));
        var entries = store.MonthEntries(first, first.AddDays(42));
        monthCapacity = MonthCapacities();
        monthCells.Children.Clear(); monthCells.RowDefinitions.Clear(); monthCells.ColumnDefinitions.Clear();
        for (var col = 0; col < 7; col++)
        {
            monthCells.ColumnDefinitions.Add(new ColumnDefinition());
            var label = Label("星期" + "一二三四五六日"[col], 12, Muted);
            Grid.SetColumn(label, col); monthCells.Children.Add(label);
        }
        monthCells.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        for (var row = 0; row < 6; row++) monthCells.RowDefinitions.Add(new RowDefinition());
        for (var index = 0; index < 42; index++)
        {
            var day = first.AddDays(index);
            var items = entries.Where(entry => entry.Day == day).ToArray();
            var contents = new StackPanel { Spacing = 3, Padding = new Thickness(4) };
            var date = Button(day.Day.ToString(Chinese), $"查看 {day:M月d日} 全部事项");
            date.Padding = new Thickness(3, 0, 3, 0); date.MinHeight = 20;
            date.Background = day == Beijing.Now.Date ? Accent : new SolidColorBrush(Colors.Transparent);
            date.Foreground = day == Beijing.Now.Date ? new SolidColorBrush(Colors.White) : Ink;
            date.Click += async (_, _) => { selected = day; await ShowMonthDay(day); };
            var dateHeader = new Grid(); dateHeader.Children.Add(date);
            var count = items.Count(entry => entry.Schedule is not null);
            if (count > 0) dateHeader.Children.Add(CalendarCount(count));
            contents.Children.Add(dateHeader);
            var capacity = items.Length <= monthCapacity.Regular ? monthCapacity.Regular : monthCapacity.Overflow;
            foreach (var item in items.Take(capacity)) contents.Children.Add(MonthCard(item, Refresh));
            if (items.Length > capacity)
            {
                var more = Button($"＋{items.Length - capacity}", "展开当天完整列表");
                more.Padding = new Thickness(3, 0, 3, 0); more.MinHeight = 18;
                more.Click += async (_, _) => await ShowMonthDay(day);
                contents.Children.Add(more);
            }
            var cell = new Border { Child = contents, BorderBrush = Line, BorderThickness = new Thickness(0, 0, 1, 1),
                Background = Tile, Opacity = day.Month == month.Month ? 1 : 0.5 };
            var add = new MenuFlyout();
            var addTodo = new MenuFlyoutItem { Text = "添加待办…" };
            var addSchedule = new MenuFlyoutItem { Text = "添加日程…" };
            addTodo.Click += async (_, _) => await AddTodoForDate(day);
            addSchedule.Click += async (_, _) => await AddSchedule(day);
            add.Items.Add(addTodo); add.Items.Add(addSchedule); cell.ContextFlyout = add;
            Grid.SetRow(cell, index / 7 + 1); Grid.SetColumn(cell, index % 7); monthCells.Children.Add(cell);
        }
    }

    private FrameworkElement MonthCard(MonthEntry entry, Action changed, Action<MonthEntry>? edit = null, Action<Func<Task>>? defer = null)
    {
        var title = Label((entry.Schedule is { } schedule ? $"{schedule.When:HH:mm} " : "") + entry.Title, 11);
        title.TextTrimming = TextTrimming.CharacterEllipsis;
        var panel = new Grid { ColumnSpacing = 2 };
        panel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); panel.ColumnDefinitions.Add(new ColumnDefinition());
        if (entry.Todo is { } task)
        {
            var check = new CheckBox { IsChecked = task.Done, MinWidth = 0, MinHeight = 22, Padding = new Thickness(0),
                IsEnabled = task.MovedTo is null, VerticalAlignment = VerticalAlignment.Center };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(check, "完成 " + entry.Title);
            if (task.Done) { title.TextDecorations = TextDecorations.Strikethrough; title.Foreground = Muted; }
            if (task.MovedTo is { } moved) { title.Text += $" ↪ {moved[5..]}"; title.Foreground = Muted; }
            check.Checked += (_, _) => { store.SetDone(task.Id, true); DispatcherQueue.TryEnqueue(() => changed()); };
            check.Unchecked += (_, _) => { store.SetDone(task.Id, false); DispatcherQueue.TryEnqueue(() => changed()); };
            panel.Children.Add(check);
        }
        var button = Button("", "编辑 " + entry.Title);
        button.Content = title; button.Padding = new Thickness(1, 0, 1, 0); button.MinHeight = 23;
        button.HorizontalAlignment = HorizontalAlignment.Stretch; button.HorizontalContentAlignment = HorizontalAlignment.Left;
        button.Click += async (_, _) => { if (edit is not null) edit(entry); else await EditEntry(entry.Schedule, entry.Todo); };
        Grid.SetColumn(button, 1); panel.Children.Add(button);
        panel.ContextFlyout = ItemMenu(entry.Schedule, entry.Todo, defer);
        ToolTipService.SetToolTip(panel, entry.Title + (entry.Todo?.MovedTo is { } target ? $"\n已顺延至 {target}" : ""));
        return new Border { Child = panel, Background = Tile, BorderBrush = Priorities[(int)entry.Priority], BorderThickness = new Thickness(3, 0, 0, 0), CornerRadius = new CornerRadius(3) };
    }

    private async Task ShowMonthDay(DateTime day)
    {
        if (accountDialog) return;
        accountDialog = true;
        MonthEntry? editing = null;
        Func<Task>? deferred = null;
        ContentDialogResult result;
        try
        {
            var list = new StackPanel { Spacing = 7 };
            var dialog = Dialog(day.ToString("M 月 d 日", Chinese), new ScrollViewer { Content = list, MaxHeight = 360 }, "添加待办");
            dialog.SecondaryButtonText = "添加日程";
            void Fill()
            {
                list.Children.Clear();
                var items = store.MonthEntries(day, day.AddDays(1));
                foreach (var item in items) list.Children.Add(MonthCard(item, () => { Refresh(); Fill(); }, entry => { editing = entry; dialog.Hide(); }, action => { deferred = action; dialog.Hide(); }));
                if (items.Count == 0) list.Children.Add(Label("当天暂无事项", 13, Muted));
            }
            Fill();
            result = await dialog.ShowAsync();
        }
        finally { accountDialog = false; }
        if (deferred is not null) await deferred();
        else if (editing is not null) await EditEntry(editing.Schedule, editing.Todo);
        else if (result == ContentDialogResult.Primary) await AddTodoForDate(day);
        else if (result == ContentDialogResult.Secondary) await AddSchedule(day);
    }
}

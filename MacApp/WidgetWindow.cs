using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Threading;

namespace CalendarTodo.Mac;

public sealed partial class WidgetWindow : Window
{
    private readonly Store store;
    private readonly Grid calendar = new() { ColumnDefinitions = new ColumnDefinitions("*,*,*,*,*,*,*") };
    private readonly StackPanel agenda = new() { Spacing = 5 };
    private readonly StackPanel todos = new() { Spacing = 5 };
    private readonly TextBlock monthLabel = Label("");
    private readonly TextBlock agendaLabel = Label("日程提醒");
    private readonly TextBlock todoLabel = Label("今日待办");
    private readonly TextBlock syncStatus = Label("");
    private readonly Button syncButton = new() { Content = "同步" };
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(30) };
    private readonly CancellationTokenSource lifetime = new();
    private DateTime month = Beijing.Now.Date.AddDays(1 - Beijing.Now.Day);
    private DateTime selectedDay = Beijing.Now.Date;
    private DateTime lastToday = Beijing.Now.Date;
    private Grid layout = null!;
    private bool closing;

    public WidgetWindow(Store store)
    {
        this.store = store;
        Title = "日历与待办";
        Width = Number("mac-width", 410);
        Height = Number("mac-height", 700);
        MinWidth = 360;
        MinHeight = 540;
        CanResize = true;
        Topmost = store.Get("mac-topmost") == "1";
        Background = Brush.Parse("#E8EFF4F9");
        TransparencyLevelHint = [WindowTransparencyLevel.AcrylicBlur, WindowTransparencyLevel.Blur];
        Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://CalendarTodo.Mac/Assets/CalendarTodo.ico")));
        Content = Build();
        Opened += (_, _) => { RestorePosition(); if (store.Get("mac-month-expanded") == "1") ToggleMonth(); };
        Closing += async (_, args) =>
        {
            if (closing) return;
            args.Cancel = true;
            closing = true;
            timer.Stop();
            lifetime.Cancel();
            if (syncRunning is not null) await syncRunning;
            SaveLayout();
            Close();
        };
        timer.Tick += async (_, _) =>
        {
            try { await Tick(); }
            catch (Exception) { syncStatus.Text = "后台操作未完成，可稍后重试"; }
        };
        timer.Start();
        Refresh();
        IdleStatus();
    }

    private Control Build()
    {
        layout = new Grid { Margin = new Thickness(12), RowDefinitions = new RowDefinitions("Auto,Auto,*,6,1.2*,Auto,Auto") };
        layout.RowDefinitions[2].Height = new GridLength(Number("mac-agenda-weight", 1), GridUnitType.Star);
        var title = Label("日历与待办", 18);
        title.PointerPressed += (_, e) => { if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) BeginMoveDrag(e); };
        var pin = ActionButton("置顶", () => { Topmost = !Topmost; store.Set("mac-topmost", Topmost ? "1" : "0"); });
        var header = Row(title, Horizontal(monthToggle, pin, ActionButton("设置", Settings), ActionButton("隐藏", () => { SaveLayout(); Hide(); })));
        Add(layout, header, 0);
        var monthControls = Row(monthLabel, Horizontal(
            ActionButton("‹", () => { month = month.AddMonths(-1); Refresh(); }),
            ActionButton("今天", () => { selectedDay = Beijing.Now.Date; month = selectedDay.AddDays(1 - selectedDay.Day); Refresh(); }),
            ActionButton("›", () => { month = month.AddMonths(1); Refresh(); })));
        Add(layout, Column(monthControls, calendar), 1);
        var agendaArea = new Grid { RowDefinitions = new RowDefinitions("Auto,*"), MinHeight = 70 };
        Add(agendaArea, Row(agendaLabel, Horizontal(ActionButton("邮箱 ▾", Mailboxes), ActionButton("＋日程", AddSchedule), ActionButton("待确认", Review))), 0);
        Add(agendaArea, new ScrollViewer { Content = agenda, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto }, 1);
        Add(layout, agendaArea, 2);
        Add(layout, new GridSplitter { ResizeDirection = GridResizeDirection.Rows, HorizontalAlignment = HorizontalAlignment.Stretch, Background = Brush.Parse("#B8C6D6") }, 3);
        var todoArea = new Grid { RowDefinitions = new RowDefinitions("Auto,*"), MinHeight = 60 };
        Add(todoArea, todoLabel, 0);
        Add(todoArea, new ScrollViewer { Content = todos }, 1);
        Add(layout, todoArea, 4);
        var input = new TextBox { Watermark = "添加今日待办…", MaxLength = 180 };
        var priority = PriorityPicker(Priority.Medium);
        void AddTodo()
        {
            if (string.IsNullOrWhiteSpace(input.Text)) return;
            store.AddTodo(input.Text.Trim(), (Priority)priority.SelectedIndex);
            input.Text = "";
            Refresh();
        }
        input.KeyDown += (_, args) => { if (args.Key == Key.Enter) { AddTodo(); args.Handled = true; } };
        Add(layout, Row(input, Horizontal(priority, ActionButton("＋", AddTodo))), 5);
        syncStatus.FontSize = 11;
        syncButton.Click += async (_, _) => await Sync(manual: true);
        Add(layout, Row(syncStatus, Horizontal(syncButton, ActionButton("反馈", Feedback))), 6);
        BuildMonthOverview();
        return layout;
    }

    private void Refresh()
    {
        store.EnsureOccurrences(month.AddDays(-7), month.AddMonths(1).AddDays(7));
        var schedules = store.Schedules().Where(item => !item.Cancelled).ToArray();
        monthLabel.Text = month.ToString("yyyy 年 M 月", CultureInfo.InvariantCulture);
        calendar.Children.Clear();
        calendar.RowDefinitions.Clear();
        for (var row = 0; row < 7; row++) calendar.RowDefinitions.Add(new RowDefinition(26, GridUnitType.Pixel));
        for (var col = 0; col < 7; col++)
        {
            var day = Label("一二三四五六日"[col].ToString(), 12);
            day.HorizontalAlignment = HorizontalAlignment.Center;
            Grid.SetColumn(day, col);
            calendar.Children.Add(day);
        }
        var first = month.AddDays(-(((int)month.DayOfWeek + 6) % 7));
        for (var index = 0; index < 42; index++)
        {
            var date = first.AddDays(index);
            var contents = new Grid();
            var number = Label(date.Day.ToString(), 12);
            number.HorizontalAlignment = HorizontalAlignment.Center;
            contents.Children.Add(number);
            var count = schedules.Count(item => item.When.Date == date);
            if (count > 0)
            {
                contents.Children.Add(new Border
                {
                    Child = new TextBlock { Text = count.ToString(), FontSize = 9, Foreground = Brushes.Red, HorizontalAlignment = HorizontalAlignment.Center },
                    BorderBrush = Brushes.Red, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8),
                    MinWidth = 14, Height = 14, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top
                });
            }
            var button = ActionButton("", () => { selectedDay = date; Refresh(); });
            button.Content = contents;
            button.Padding = new Thickness(1);
            button.HorizontalAlignment = HorizontalAlignment.Stretch;
            button.HorizontalContentAlignment = HorizontalAlignment.Stretch;
            button.Background = date == selectedDay ? Brush.Parse("#C4D7F7") : Brushes.Transparent;
            button.Opacity = date.Month == month.Month ? 1 : 0.45;
            Grid.SetRow(button, index / 7 + 1);
            Grid.SetColumn(button, index % 7);
            calendar.Children.Add(button);
        }
        agendaLabel.Text = $"{selectedDay:M/d} 日程";
        agenda.Children.Clear();
        foreach (var item in schedules.Where(item => item.When.Date == selectedDay))
        {
            var title = Label(item.Title, 13);
            title.TextTrimming = TextTrimming.CharacterEllipsis;
            ToolTip.SetTip(title, $"{item.Title}\n{item.Account}");
            var row = Row(title, Label($"{item.When:HH:mm} {(item.Kind == EventKind.Deadline ? "截止" : "开始")}", 11));
            row.ContextMenu = ItemMenu(item, null);
            agenda.Children.Add(Tile(row));
        }
        if (agenda.Children.Count == 0) agenda.Children.Add(Label("当天暂无日程", 12));
        var tasks = store.Todos(Beijing.Now);
        RefreshMonthOverview();
        todoLabel.Text = $"今日待办 · 未完成 {tasks.Count(item => !item.Done && !item.Cancelled)}";
        todos.Children.Clear();
        foreach (var item in tasks)
        {
            var check = new CheckBox { IsChecked = item.Done, VerticalAlignment = VerticalAlignment.Center };
            check.IsCheckedChanged += (_, _) => { store.SetDone(item.Id, check.IsChecked == true); Dispatcher.UIThread.Post(Refresh); };
            var title = Label(item.Title, 13);
            title.TextTrimming = TextTrimming.CharacterEllipsis;
            if (item.Done) title.TextDecorations = TextDecorations.Strikethrough;
            if (item.Done || item.Cancelled) title.Foreground = Brushes.DimGray;
            var priority = PriorityPicker(item.Priority);
            priority.SelectionChanged += (_, _) => { if (priority.SelectedIndex >= 0) { store.SetPriority(item.Id, (Priority)priority.SelectedIndex); Dispatcher.UIThread.Post(Refresh); } };
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), ColumnSpacing = 6 };
            row.Children.Add(check);
            Grid.SetColumn(title, 1);
            row.Children.Add(title);
            Grid.SetColumn(priority, 2);
            row.Children.Add(priority);
            row.ContextMenu = ItemMenu(null, item);
            var tile = Tile(row);
            tile.BorderBrush = item.Done ? Brushes.DimGray : new[] { Brushes.ForestGreen, Brushes.Goldenrod, Brushes.Crimson }[(int)item.Priority];
            tile.BorderThickness = new Thickness(4, 0, 0, 0);
            todos.Children.Add(tile);
        }
    }

    private double Number(string key, double fallback) => double.TryParse(store.Get(key), CultureInfo.InvariantCulture, out var value) && double.IsFinite(value) && value > 0 ? value : fallback;
    private void SaveLayout()
    {
        var prefix = monthExpanded ? "mac-month-" : "mac-";
        store.Set(prefix + "width", Width.ToString(CultureInfo.InvariantCulture));
        store.Set(prefix + "height", Height.ToString(CultureInfo.InvariantCulture));
        store.Set(prefix + "x", Position.X.ToString(CultureInfo.InvariantCulture));
        store.Set(prefix + "y", Position.Y.ToString(CultureInfo.InvariantCulture));
        if (!monthExpanded) store.Set("mac-agenda-weight", layout.RowDefinitions[2].Height.Value.ToString(CultureInfo.InvariantCulture));
    }
    private void RestorePosition()
    {
        if (Screens.Primary is not { } primary) return;
        var prefix = monthExpanded ? "mac-month-" : "mac-";
        var x = int.TryParse(store.Get(prefix + "x"), out var savedX) ? savedX : primary.WorkingArea.X + 30;
        var y = int.TryParse(store.Get(prefix + "y"), out var savedY) ? savedY : primary.WorkingArea.Y + 30;
        var screen = Screens.All.FirstOrDefault(item => item.WorkingArea.Contains(new PixelPoint(x + 30, y + 30))) ?? primary;
        Width = Math.Clamp(Width, MinWidth, Math.Max(MinWidth, screen.WorkingArea.Width / screen.Scaling - 40));
        Height = Math.Clamp(Height, MinHeight, Math.Max(MinHeight, screen.WorkingArea.Height / screen.Scaling - 40));
        var right = Math.Max(screen.WorkingArea.X, screen.WorkingArea.Right - (int)Math.Ceiling(Width * screen.Scaling));
        var bottom = Math.Max(screen.WorkingArea.Y, screen.WorkingArea.Bottom - (int)Math.Ceiling(Height * screen.Scaling));
        Position = new PixelPoint(Math.Clamp(x, screen.WorkingArea.X, right), Math.Clamp(y, screen.WorkingArea.Y, bottom));
    }
    private static TextBlock Label(string text, double size = 14) => new() { Text = text, FontSize = size, VerticalAlignment = VerticalAlignment.Center };
    private static Button ActionButton(string title, Action action)
    {
        var button = new Button { Content = title, Padding = new Thickness(7, 4), VerticalAlignment = VerticalAlignment.Center };
        button.Click += (_, _) => action();
        return button;
    }
    private static Button ActionButton(string title, Func<Task> action)
    {
        var button = new Button { Content = title, Padding = new Thickness(7, 4) };
        button.Click += async (_, _) => await action();
        return button;
    }
    private static StackPanel Column(params Control[] controls)
    {
        var panel = new StackPanel { Spacing = 7, Margin = new Thickness(0, 3) };
        foreach (var control in controls) panel.Children.Add(control);
        return panel;
    }
    private static StackPanel Horizontal(params Control[] controls)
    {
        var panel = Column(controls);
        panel.Orientation = Orientation.Horizontal;
        return panel;
    }
    private static Grid Row(Control left, Control right)
    {
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 6 };
        row.Children.Add(left);
        Grid.SetColumn(right, 1);
        row.Children.Add(right);
        return row;
    }
    private static Border Tile(Control child) => new() { Child = child, Background = Brush.Parse("#BFFFFFFF"), CornerRadius = new CornerRadius(6), Padding = new Thickness(6, 4) };
    private static ComboBox PriorityPicker(Priority priority) => new() { ItemsSource = new[] { "低", "中", "高" }, SelectedIndex = (int)priority, MinWidth = 58 };
    private static void Add(Grid grid, Control control, int row) { Grid.SetRow(control, row); grid.Children.Add(control); }
}

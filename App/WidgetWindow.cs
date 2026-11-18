using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.Win32;
using Windows.Graphics;
using Windows.UI.Text;
using Forms = System.Windows.Forms;
using VirtualKey = Windows.System.VirtualKey;

namespace CalendarTodo;

public sealed partial class WidgetWindow : Window
{
    private const string Name = "日历与待办";
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private static readonly CultureInfo Chinese = CultureInfo.GetCultureInfo("zh-CN");
    private static readonly SolidColorBrush Ink = Color(0xFF, 0x17, 0x27, 0x3E);
    private static readonly SolidColorBrush Muted = Color(0xFF, 0x49, 0x59, 0x6D);
    private static readonly SolidColorBrush Accent = Color(0xFF, 0x20, 0x4C, 0x9E);
    private static readonly SolidColorBrush Line = Color(0x40, 0x65, 0x7C, 0x9C);
    private static readonly SolidColorBrush Tile = Color(0xA8, 0xFF, 0xFF, 0xFF);
    private static readonly SolidColorBrush[] Priorities =
        [Color(0xFF, 0x2E, 0x87, 0x61), Color(0xFF, 0xDB, 0xA8, 0x32), Color(0xFF, 0xD4, 0x4D, 0x5A)];

    private readonly Store store;
    private readonly Inbox inbox;
    private readonly CancellationTokenSource lifetime = new();
    private readonly Forms.NotifyIcon tray;
    private readonly System.Drawing.Icon? applicationIcon;
    private readonly OverlappedPresenter presenter;
    private readonly IntPtr handle;
    private readonly Grid root = new() { RequestedTheme = ElementTheme.Light };
    private readonly Grid days = new() { ColumnSpacing = 3, RowSpacing = 1 };
    private readonly StackPanel events = new() { Spacing = 5 };
    private readonly ScrollViewer agendaScroll = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly StackPanel todos = new() { Spacing = 5 };
    private readonly TextBlock monthLabel = Label("", 17, strong: true);
    private readonly TextBlock eventHeading = Label("日程提醒", 15, strong: true);
    private readonly TextBlock todoCount = Label("", 12, Muted);
    private readonly TextBlock syncStatus = Label("", 11, Muted);
    private readonly Button syncButton = Button("同步", "立即同步邮箱");
    private readonly Button pin = Button("置顶", "切换窗口置顶");
    private readonly Button review = Button("待确认", "检查需要确认的邮件");
    private readonly TextBox newTodo = new()
    {
        PlaceholderText = "添加待办…", MaxLength = 180, MinWidth = 0, CornerRadius = new CornerRadius(7),
        Background = Tile, BorderBrush = Line, BorderThickness = new Thickness(1)
    };
    private readonly ComboBox newPriority = PriorityPicker(Priority.Medium);
    private readonly DispatcherQueueTimer syncTimer, reminderTimer, positionTimer;
    private DateTime month = Beijing.Now.Date.AddDays(1 - Beijing.Now.Day);
    private DateTime? selected;
    private DateTime lastDay = Beijing.Now.Date;
    private DateTime? displayedAgendaDay;
    private static readonly SolidColorBrush CountRed = Color(0xFF, 0xB4, 0x1F, 0x32);
    private static readonly SolidColorBrush CountBackground = new(Colors.White);
    private bool exiting, accountDialog;

    public WidgetWindow(Store store)
    {
        this.store = store;
        inbox = new Inbox(store);
        Title = Name;
        handle = WinRT.Interop.WindowNative.GetWindowHandle(this);
        presenter = (OverlappedPresenter)AppWindow.Presenter;
        presenter.SetBorderAndTitleBar(false, false);
        presenter.IsResizable = false;
        presenter.IsMaximizable = false;
        presenter.IsMinimizable = false;
        presenter.IsAlwaysOnTop = store.Get("topmost") == "1";
        UpdatePin();
        SystemBackdrop = new DesktopAcrylicBackdrop();
        applicationIcon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath!);
        Content = Build();
        if (applicationIcon is not null)
        {
            SendMessage(handle, 0x0080, IntPtr.Zero, applicationIcon.Handle);
            SendMessage(handle, 0x0080, new IntPtr(1), applicationIcon.Handle);
        }

        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("显示日历与待办", null, (_, _) => ShowWidget());
        menu.Items.Add("同步 / 取消同步", null, async (_, _) => await Sync(manual: true));
        menu.Items.Add("退出", null, (_, _) => ExitWidget());
        tray = new Forms.NotifyIcon
        {
            Icon = applicationIcon ?? System.Drawing.SystemIcons.Application, Text = Name,
            ContextMenuStrip = menu, Visible = true
        };
        tray.MouseClick += (_, args) => { if (args.Button == Forms.MouseButtons.Left) ShowWidget(); };
        tray.BalloonTipClicked += (_, _) => ShowWidget();

        positionTimer = DispatcherQueue.CreateTimer();
        positionTimer.Interval = TimeSpan.FromMilliseconds(350);
        positionTimer.IsRepeating = false;
        positionTimer.Tick += (_, _) => SavePosition();
        AppWindow.Changed += (_, args) =>
        {
            if (args.DidPositionChange || args.DidSizeChange)
            {
                positionTimer.Stop();
                positionTimer.Start();
            }
            if (args.DidSizeChange)
                RoundWindow();
        };
        AppWindow.Closing += (_, args) =>
        {
            if (!exiting)
            {
                args.Cancel = true;
                DispatcherQueue.TryEnqueue(ExitWidget);
            }
        };
        SystemEvents.DisplaySettingsChanged += DisplayChanged;
        if (int.TryParse(store.Get("x"), out var x) && int.TryParse(store.Get("y"), out var y))
            AppWindow.Move(new PointInt32(x, y));
        else
        {
            var area = DisplayArea.Primary.WorkArea;
            AppWindow.Move(new PointInt32(area.X + area.Width - (int)(360 * Scale), area.Y + 32));
        }
        FitWindow();

        RegisterNotifications();
        syncTimer = DispatcherQueue.CreateTimer();
        syncTimer.Interval = TimeSpan.FromSeconds(20);
        syncTimer.Tick += async (_, _) => await TryDailySync();
        syncTimer.Start();
        reminderTimer = DispatcherQueue.CreateTimer();
        reminderTimer.Interval = TimeSpan.FromSeconds(15);
        reminderTimer.Tick += (_, _) => Tick();
        reminderTimer.Start();
        ShowIdleSyncStatus();
        Refresh();
        DispatcherQueue.TryEnqueue(async () =>
        {
            Tick();
            await TryDailySync();
        });
    }

    private UIElement Build()
    {
        root.Padding = new Thickness(12, 9, 12, 9);
        root.BorderThickness = new Thickness(1);
        root.BorderBrush = Line;
        root.CornerRadius = new CornerRadius(16);
        root.Background = Color(0xA5, 0xE8, 0xEF, 0xF8);
        // Windows supplies an opaque Acrylic fallback when transparency is disabled.
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(agendaRow);
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(DividerHeight) });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var heading = Row(5);
        heading.Background = new SolidColorBrush(Colors.Transparent);
        heading.Children.Add(new SymbolIcon(Symbol.Accept) { Foreground = Accent, Width = 18, Height = 18 });
        var titleText = Label(Name, 14, strong: true);
        titleText.VerticalAlignment = VerticalAlignment.Center;
        heading.Children.Add(titleText);
        var dragArea = new Border { Background = new SolidColorBrush(Colors.Transparent), Child = heading, Height = 27 };
        ConfigureWindowDrag(dragArea);
        var tools = Row(4);
        pin.Click += (_, _) =>
        {
            presenter.IsAlwaysOnTop = !presenter.IsAlwaysOnTop;
            store.Set("topmost", presenter.IsAlwaysOnTop ? "1" : "0");
            UpdatePin();
        };
        var settings = Button("设置");
        settings.Click += async (_, _) => await Settings();
        var hide = Button("隐藏");
        hide.Click += (_, _) => HideWidget();
        var close = Button("×", "退出程序");
        close.FontSize = 17;
        close.Width = 28;
        close.Height = 27;
        close.Padding = new Thickness(0);
        close.Click += (_, _) => ExitWidget();
        tools.Children.Add(pin);
        tools.Children.Add(settings);
        tools.Children.Add(hide);
        tools.Children.Add(close);
        var header = Split(dragArea, tools);
        root.Children.Add(header);

        var calendar = new StackPanel { Spacing = 3, Margin = new Thickness(0, 6, 0, 5) };
        var navigation = Row(4);
        var previous = Button("‹", "上个月");
        var today = Button("今天");
        var next = Button("›", "下个月");
        previous.Click += (_, _) => Navigate(-1);
        next.Click += (_, _) => Navigate(1);
        today.Click += (_, _) =>
        {
            selected = null;
            month = Beijing.Now.Date.AddDays(1 - Beijing.Now.Day);
            RefreshScheduleViews();
            agendaScroll.ChangeView(null, 0, null, true);
        };
        navigation.Children.Add(previous);
        navigation.Children.Add(today);
        navigation.Children.Add(next);
        calendar.Children.Add(Split(monthLabel, navigation));
        calendar.Children.Add(days);
        Grid.SetRow(calendar, 1);
        root.Children.Add(calendar);

        var agenda = new Grid { RowSpacing = 4, BorderBrush = Line, BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(0, 4, 0, 0) };
        agenda.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        agenda.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        review.Click += async (_, _) => await Review();
        var addEvent = Button("＋ 日程", "添加日程");
        addEvent.Click += async (_, _) => await AddSchedule();
        var agendaTools = Row(4);
        agendaTools.Children.Add(OpenMailboxButton());
        agendaTools.Children.Add(addEvent);
        agendaTools.Children.Add(review);
        eventHeading.TextTrimming = TextTrimming.CharacterEllipsis;
        agenda.Children.Add(Split(eventHeading, agendaTools));
        agendaScroll.Content = events;
        Grid.SetRow(agendaScroll, 1);
        agenda.Children.Add(agendaScroll);
        Grid.SetRow(agenda, 2);
        root.Children.Add(agenda);

        var taskArea = new Grid { RowSpacing = 5, BorderThickness = new Thickness(0, 1, 0, 0), BorderBrush = Line,
            Padding = new Thickness(0, 5, 0, 5) };
        taskArea.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        taskArea.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        taskArea.Children.Add(Split(Label("今日待办", 15, strong: true), todoCount));
        var taskScroll = new ScrollViewer { Content = todos, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Grid.SetRow(taskScroll, 1);
        taskArea.Children.Add(taskScroll);
        Grid.SetRow(taskArea, 4);
        root.Children.Add(taskArea);

        var footer = Section();
        var entry = new Grid { ColumnSpacing = 6 };
        entry.ColumnDefinitions.Add(new ColumnDefinition());
        entry.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        entry.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        AutomationProperties.SetName(newTodo, "新待办标题");
        entry.Children.Add(newTodo);
        Grid.SetColumn(newPriority, 1);
        entry.Children.Add(newPriority);
        var add = Button("＋", "添加待办");
        add.Height = 32;
        add.Background = Accent;
        add.Foreground = new SolidColorBrush(Colors.White);
        add.Click += (_, _) => AddTodo();
        newTodo.KeyDown += (_, args) => { if (args.Key == VirtualKey.Enter) { AddTodo(); args.Handled = true; } };
        Grid.SetColumn(add, 2);
        entry.Children.Add(add);
        footer.Children.Add(entry);
        syncButton.Click += async (_, _) => await Sync(manual: true);
        var footerActions = Row(4);
        footerActions.Children.Add(syncButton);
        footerActions.Children.Add(FeedbackButton());
        var syncRow = Split(syncStatus, footerActions);
        syncStatus.TextTrimming = TextTrimming.CharacterEllipsis;
        footer.Children.Add(syncRow);
        Grid.SetRow(footer, 5);
        root.Children.Add(footer);
        var divider = ConfigureAgendaHeight(header, calendar, footer);
        Grid.SetRow(divider, 3);
        root.Children.Add(divider);
        return AddResizeEdges();
    }

    private void Navigate(int delta)
    {
        month = month.AddMonths(delta);
        selected = month;
        RefreshScheduleViews();
    }

    private void Refresh()
    {
        RefreshScheduleViews();
        RefreshTodos();
    }

    private void RefreshScheduleViews()
    {
        var today = Beijing.Now.Date;
        var day = selected ?? today;
        var schedules = store.Schedules().Where(item => !item.Cancelled).ToLookup(item => item.When.Date);
        RefreshCalendar(today, day, schedules);
        RefreshAgenda(day, schedules[day]);
    }

    private void RefreshCalendar(DateTime today, DateTime selectedDay, ILookup<DateTime, Schedule> schedules)
    {
        monthLabel.Text = month.ToString("yyyy 年 M 月", Chinese);
        days.Children.Clear();
        days.ColumnDefinitions.Clear();
        days.RowDefinitions.Clear();
        for (var col = 0; col < 7; col++)
        {
            days.ColumnDefinitions.Add(new ColumnDefinition());
            var day = Label("一二三四五六日"[col].ToString(), 11, Muted);
            day.HorizontalAlignment = HorizontalAlignment.Center;
            Grid.SetColumn(day, col);
            days.Children.Add(day);
        }
        var first = month.AddDays(-(((int)month.DayOfWeek + 6) % 7));
        var weeks = (month.Subtract(first).Days + DateTime.DaysInMonth(month.Year, month.Month) + 6) / 7;
        for (var row = 0; row <= weeks; row++)
            days.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        for (var i = 0; i < weeks * 7; i++)
        {
            var date = first.AddDays(i);
            var isSelected = date == selectedDay;
            var count = schedules[date].Count();
            var number = Label(date.Day.ToString(Chinese), 12, isSelected ? new SolidColorBrush(Colors.White) : Muted, date == today);
            var cell = new Grid();
            number.HorizontalAlignment = HorizontalAlignment.Center;
            number.VerticalAlignment = VerticalAlignment.Center;
            cell.Children.Add(number);
            if (count > 0)
                cell.Children.Add(CalendarCount(count));
            var button = Button("", date.ToString("M 月 d 日", Chinese) + (count > 0 ? $"，{count} 条日程" : ""));
            button.Content = cell;
            button.Tag = date;
            button.Background = isSelected ? Accent : new SolidColorBrush(Colors.Transparent);
            button.BorderBrush = date == today && !isSelected ? Accent : new SolidColorBrush(Colors.Transparent);
            button.Opacity = date.Month == month.Month ? 1 : 0.52;
            button.Height = 21;
            button.Padding = new Thickness(0);
            button.HorizontalAlignment = HorizontalAlignment.Stretch;
            button.HorizontalContentAlignment = HorizontalAlignment.Stretch;
            button.VerticalContentAlignment = VerticalAlignment.Stretch;
            button.Click += (_, _) =>
            {
                selected = date;
                month = date.AddDays(1 - date.Day);
                RefreshScheduleViews();
            };
            Grid.SetRow(button, i / 7 + 1);
            Grid.SetColumn(button, i % 7);
            days.Children.Add(button);
        }

    }

    private static Border CalendarCount(int count)
    {
        var number = Label(count.ToString(Chinese), 9, CountRed, strong: true);
        number.HorizontalAlignment = HorizontalAlignment.Center;
        number.VerticalAlignment = VerticalAlignment.Center;
        return new Border
        {
            Child = number, MinWidth = 13, Height = 13, Padding = new Thickness(1, 0, 1, 0),
            Margin = new Thickness(0, 0, 1, 0), CornerRadius = new CornerRadius(7),
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top,
            BorderBrush = CountRed, BorderThickness = new Thickness(1), Background = CountBackground
        };
    }

    private void RefreshAgenda(DateTime day, IEnumerable<Schedule> schedules)
    {
        eventHeading.Text = selected is null ? "日程提醒" : $"{day:M/d} 日程";
        var dayChanged = displayedAgendaDay != day;
        displayedAgendaDay = day;
        events.Children.Clear();
        foreach (var item in schedules)
        {
            var type = item.Kind == EventKind.Deadline ? "截止" : "开始";
            var title = Label(item.Title, 12, strong: true);
            title.TextTrimming = TextTrimming.CharacterEllipsis;
            ToolTipService.SetToolTip(title, string.IsNullOrEmpty(item.Account) ? item.Title : $"{item.Title}\n来源邮箱 · {item.Account}");
            var row = Split(title, Label($"{item.When.ToString("M/d HH:mm", Chinese)} {type}", 10, Accent));
            row.Padding = new Thickness(6, 2, 6, 2);
            row.Background = Tile;
            row.CornerRadius = new CornerRadius(6);
            row.ContextFlyout = DeleteMenu("日程", item.Title, () =>
            {
                store.DeleteSchedule(item.Id);
                ClearDelivered(item.Id);
            });
            events.Children.Add(row);
        }
        if (events.Children.Count == 0)
            events.Children.Add(Label("当天暂无日程", 12, Muted));
        if (dayChanged)
            agendaScroll.ChangeView(null, 0, null, true);
        var pendingCount = store.PendingItems().Count;
        review.IsEnabled = pendingCount > 0;
        review.Content = pendingCount == 0 ? "待确认" : $"待确认 {pendingCount}";
    }

    private void RefreshTodos()
    {
        var items = store.Todos(Beijing.Now);
        todoCount.Text = $"未完成 {items.Count(item => !item.Done && !item.Cancelled)}";
        todos.Children.Clear();
        foreach (var item in items)
        {
            var muted = item.Done || item.Cancelled;
            var grid = new Grid { Background = Tile, CornerRadius = new CornerRadius(7), MinHeight = 32 };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(4) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.Children.Add(new Border
            {
                Background = muted ? Line : Priorities[(int)item.Priority],
                CornerRadius = new CornerRadius(7, 0, 0, 7)
            });
            var check = new CheckBox
            {
                IsChecked = item.Done, MinWidth = 0, Padding = new Thickness(0),
                Margin = new Thickness(6, 0, 2, 0), VerticalAlignment = VerticalAlignment.Center
            };
            AutomationProperties.SetName(check, $"完成 {item.Title}");
            void Toggle(object _, RoutedEventArgs args)
            {
                store.SetDone(item.Id, check.IsChecked == true);
                DispatcherQueue.TryEnqueue(RefreshTodos);
            }
            check.Checked += Toggle;
            check.Unchecked += Toggle;
            Grid.SetColumn(check, 1);
            grid.Children.Add(check);
            var label = Label(item.Title, 13, muted ? Muted : Ink);
            label.TextTrimming = TextTrimming.CharacterEllipsis;
            if (item.Done)
                label.TextDecorations = TextDecorations.Strikethrough;
            ToolTipService.SetToolTip(label, item.Title);
            label.VerticalAlignment = VerticalAlignment.Center;
            var description = item.Cancelled ? "已取消" : item.When is { } when
                ? $"{(item.FromMail ? "邮件导入" : "日程")} · {when:HH:mm}" : "";
            var labels = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            labels.Children.Add(label);
            if (description.Length > 0)
                labels.Children.Add(Label(description, 10, Muted));
            Grid.SetColumn(labels, 2);
            grid.Children.Add(labels);
            var priority = PriorityPicker(item.Priority);
            priority.Margin = new Thickness(4, 2, 5, 2);
            priority.SelectionChanged += (_, _) =>
            {
                if (priority.SelectedIndex < 0)
                    return;
                store.SetPriority(item.Id, (Priority)priority.SelectedIndex);
                DispatcherQueue.TryEnqueue(RefreshTodos);
            };
            Grid.SetColumn(priority, 3);
            grid.Children.Add(priority);
            grid.ContextFlyout = DeleteMenu("待办", item.Title, () => store.DeleteTodo(item.Id));
            todos.Children.Add(grid);
        }
        if (items.Count == 0)
            todos.Children.Add(Label("今天暂无待办", 12, Muted));
    }

    private void AddTodo()
    {
        var title = newTodo.Text.Trim();
        if (title.Length == 0)
            return;
        store.AddTodo(title, (Priority)newPriority.SelectedIndex);
        newTodo.Text = "";
        newPriority.SelectedIndex = (int)Priority.Medium;
        RefreshTodos();
        newTodo.Focus(FocusState.Programmatic);
    }

    private void UpdatePin() => pin.Content = presenter.IsAlwaysOnTop ? "已置顶" : "置顶";

    private void DisplayChanged(object? sender, EventArgs args) => DispatcherQueue.TryEnqueue(FitWindow);

    private void FitWindow()
    {
        var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest).WorkArea;
        var scale = Scale;
        var oldWidth = int.TryParse(store.Get("width"), out var savedWidth);
        var oldHeight = int.TryParse(store.Get("height"), out var savedHeight);
        var width = Math.Min(area.Width, (int)(Math.Max(MinimumWidth, oldWidth ? savedWidth : 338) * scale));
        var height = Math.Min(area.Height, oldHeight
            ? (int)(Math.Max(MinimumHeight, savedHeight) * scale) : Math.Min((int)(500 * scale), (int)(area.Height * 0.73)));
        var x = Math.Clamp(AppWindow.Position.X, area.X, area.X + area.Width - width);
        var y = Math.Clamp(AppWindow.Position.Y, area.Y, area.Y + area.Height - height);
        AppWindow.MoveAndResize(new RectInt32(x, y, width, height));
        RoundWindow();
    }

    private void RoundWindow()
    {
        var size = AppWindow.Size;
        var radius = Math.Max(16, (int)(32 * GetDpiForWindow(handle) / 96.0));
        var region = CreateRoundRectRgn(0, 0, size.Width + 1, size.Height + 1, radius, radius);
        if (region != IntPtr.Zero && SetWindowRgn(handle, region, true) == 0)
            DeleteObject(region);
    }

    private void SavePosition()
    {
        store.Set("x", AppWindow.Position.X.ToString(CultureInfo.InvariantCulture));
        store.Set("y", AppWindow.Position.Y.ToString(CultureInfo.InvariantCulture));
        store.Set("width", ((int)Math.Round(AppWindow.Size.Width / Scale)).ToString(CultureInfo.InvariantCulture));
        store.Set("height", ((int)Math.Round(AppWindow.Size.Height / Scale)).ToString(CultureInfo.InvariantCulture));
    }

    public void ShowWidget()
    {
        AppWindow.Show();
        Activate();
    }

    private void HideWidget()
    {
        SavePosition();
        AppWindow.Hide();
    }

    private void ExitWidget()
    {
        if (exiting)
            return;
        SavePosition();
        exiting = true;
        lifetime.Cancel();
        syncTimer.Stop();
        reminderTimer.Stop();
        positionTimer.Stop();
        SystemEvents.DisplaySettingsChanged -= DisplayChanged;
        UnregisterNotifications();
        tray.Visible = false;
        tray.ContextMenuStrip?.Dispose();
        tray.Dispose();
        Close();
        applicationIcon?.Dispose();
        Application.Current.Exit();
    }

    private static SolidColorBrush Color(byte a, byte r, byte g, byte b) =>
        new(Windows.UI.Color.FromArgb(a, r, g, b));

    private static TextBlock Label(string text, double size, Brush? foreground = null, bool strong = false) =>
        new() { Text = text, FontSize = size, Foreground = foreground ?? Ink,
            FontWeight = strong ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal };

    private static StackPanel Row(double spacing) => new() { Orientation = Orientation.Horizontal, Spacing = spacing };
    private static StackPanel Section() => new()
    {
        Spacing = 5, BorderBrush = Line, BorderThickness = new Thickness(0, 1, 0, 0),
        Padding = new Thickness(0, 4, 0, 3)
    };

    private static Grid Split(FrameworkElement start, FrameworkElement end)
    {
        var grid = new Grid { ColumnSpacing = 6 };
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(end, 1);
        start.VerticalAlignment = VerticalAlignment.Center;
        grid.Children.Add(start);
        grid.Children.Add(end);
        return grid;
    }

    private static Button Button(string text, string? name = null)
    {
        var button = new Button
        {
            Content = text, FontSize = 12, Padding = new Thickness(7, 3, 7, 3), MinWidth = 0, MinHeight = 0,
            Background = Tile, Foreground = Ink, BorderBrush = Line, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6)
        };
        if (name is not null)
            AutomationProperties.SetName(button, name);
        return button;
    }

    private static ComboBox PriorityPicker(Priority priority)
    {
        var picker = new ComboBox { ItemsSource = new[] { "低", "中", "高" }, SelectedIndex = (int)priority,
            FontSize = 12, MinWidth = 0, VerticalAlignment = VerticalAlignment.Center, Background = Tile,
            BorderBrush = Line, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6) };
        AutomationProperties.SetName(picker, "优先级");
        return picker;
    }

    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern int SetWindowRgn(IntPtr window, IntPtr region, bool redraw);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateRoundRectRgn(int left, int top, int right, int bottom, int width, int height);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr handle);
}

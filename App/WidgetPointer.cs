using System;
using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using Windows.System;

namespace CalendarTodo;

public sealed partial class WidgetWindow
{
    private const int MinimumWidth = 315;
    private const int MinimumHeight = 420;
    private const double DividerHeight = 12;
    private const double MinimumAgendaHeight = 54;
    private const double ReservedTodoHeight = 48;
    private readonly RowDefinition agendaRow = new();
    private double preferredAgendaHeight;
    private double maximumAgendaHeight;
    private double Scale => GetDpiForWindow(handle) is > 0 and var dpi ? dpi / 96.0 : 1;

    private void ConfigureWindowDrag(Border title)
    {
        PointInt32 position = default;
        TrackMouseDrag(title, _ => position = AppWindow.Position,
            (x, y) => AppWindow.Move(new PointInt32(position.X + x, position.Y + y)),
            KeepWindowVisible);
        ToolTipService.SetToolTip(title, "按住左键拖动窗口，松开即可放下");
    }

    private void KeepWindowVisible()
    {
        var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest).WorkArea;
        var size = AppWindow.Size;
        var position = AppWindow.Position;
        var x = Math.Clamp(position.X, area.X, area.X + Math.Max(0, area.Width - size.Width));
        var y = Math.Clamp(position.Y, area.Y, area.Y + Math.Max(0, area.Height - size.Height));
        if (x != position.X || y != position.Y)
            AppWindow.Move(new PointInt32(x, y));
    }

    private Grid AddResizeEdges()
    {
        var frame = new Grid();
        frame.Children.Add(root);
        var edges = new (ResizeEdge Edge, HorizontalAlignment H, VerticalAlignment V, InputSystemCursorShape Cursor, string Name)[]
        {
            (ResizeEdge.Left, HorizontalAlignment.Left, VerticalAlignment.Stretch, InputSystemCursorShape.SizeWestEast, "左边"),
            (ResizeEdge.Right, HorizontalAlignment.Right, VerticalAlignment.Stretch, InputSystemCursorShape.SizeWestEast, "右边"),
            (ResizeEdge.Top, HorizontalAlignment.Stretch, VerticalAlignment.Top, InputSystemCursorShape.SizeNorthSouth, "上边"),
            (ResizeEdge.Bottom, HorizontalAlignment.Stretch, VerticalAlignment.Bottom, InputSystemCursorShape.SizeNorthSouth, "下边"),
            (ResizeEdge.Left | ResizeEdge.Top, HorizontalAlignment.Left, VerticalAlignment.Top, InputSystemCursorShape.SizeNorthwestSoutheast, "左上角"),
            (ResizeEdge.Right | ResizeEdge.Top, HorizontalAlignment.Right, VerticalAlignment.Top, InputSystemCursorShape.SizeNortheastSouthwest, "右上角"),
            (ResizeEdge.Left | ResizeEdge.Bottom, HorizontalAlignment.Left, VerticalAlignment.Bottom, InputSystemCursorShape.SizeNortheastSouthwest, "左下角"),
            (ResizeEdge.Right | ResizeEdge.Bottom, HorizontalAlignment.Right, VerticalAlignment.Bottom, InputSystemCursorShape.SizeNorthwestSoutheast, "右下角")
        };
        foreach (var (edge, horizontal, vertical, cursor, name) in edges)
        {
            var corner = horizontal != HorizontalAlignment.Stretch && vertical != VerticalAlignment.Stretch;
            var grip = new MouseGrip(cursor)
            {
                HorizontalAlignment = horizontal,
                VerticalAlignment = vertical,
                Width = horizontal == HorizontalAlignment.Stretch ? double.NaN : corner ? 16 : 8,
                Height = vertical == VerticalAlignment.Stretch ? double.NaN : corner ? 16 : 8
            };
            if (edge == (ResizeEdge.Right | ResizeEdge.Bottom))
            {
                var mark = Label("◢", 10, Muted);
                mark.HorizontalAlignment = HorizontalAlignment.Center;
                mark.VerticalAlignment = VerticalAlignment.Center;
                ((Border)grip.Content).Child = mark;
            }
            AutomationProperties.SetName(grip, $"拖动{name}调整窗口");
            ConfigureEdgeDrag(grip, edge);
            frame.Children.Add(grip);
        }
        return frame;
    }

    private void ConfigureEdgeDrag(FrameworkElement grip, ResizeEdge edge)
    {
        var left = edge.HasFlag(ResizeEdge.Left);
        var right = edge.HasFlag(ResizeEdge.Right);
        var top = edge.HasFlag(ResizeEdge.Top);
        var bottom = edge.HasFlag(ResizeEdge.Bottom);
        RectInt32 original = default;
        RectInt32 work = default;
        int minWidth = 0, minHeight = 0;
        TrackMouseDrag(grip, _ =>
        {
            var position = AppWindow.Position;
            var size = AppWindow.Size;
            original = new RectInt32(position.X, position.Y, size.Width, size.Height);
            work = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest).WorkArea;
            minWidth = Math.Min(size.Width, (int)(MinimumWidth * Scale));
            minHeight = Math.Min(size.Height, (int)(MinimumHeight * Scale));
        }, (x, y) =>
        {
            var oldRight = original.X + original.Width;
            var oldBottom = original.Y + original.Height;
            var newLeft = left ? Math.Clamp(original.X + x, Math.Min(work.X, original.X), oldRight - minWidth) : original.X;
            var newRight = right ? Math.Clamp(oldRight + x, original.X + minWidth, Math.Max(oldRight, work.X + work.Width)) : oldRight;
            var newTop = top ? Math.Clamp(original.Y + y, Math.Min(work.Y, original.Y), oldBottom - minHeight) : original.Y;
            var newBottom = bottom ? Math.Clamp(oldBottom + y, original.Y + minHeight, Math.Max(oldBottom, work.Y + work.Height)) : oldBottom;
            AppWindow.MoveAndResize(new RectInt32(newLeft, newTop, newRight - newLeft, newBottom - newTop));
        });
    }

    private FrameworkElement ConfigureAgendaHeight(FrameworkElement header, FrameworkElement calendar, FrameworkElement footer)
    {
        preferredAgendaHeight = double.TryParse(store.Get("agenda-height"), NumberStyles.Float,
            CultureInfo.InvariantCulture, out var saved) && double.IsFinite(saved) ? saved : 96;
        agendaRow.Height = new GridLength(preferredAgendaHeight > MinimumAgendaHeight ? preferredAgendaHeight : MinimumAgendaHeight);

        var divider = new MouseGrip(InputSystemCursorShape.SizeNorthSouth)
        {
            Height = DividerHeight, IsTabStop = true
        };
        var mark = new Border
        {
            Width = 36, Height = 3, Background = Line, CornerRadius = new CornerRadius(2),
            VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center
        };
        ((Border)divider.Content).Child = mark;
        AutomationProperties.SetName(divider, "调整日程提醒高度");
        ToolTipService.SetToolTip(divider, "上下拖动调整日程提醒高度");

        foreach (var element in new[] { root, header, calendar, footer })
            element.SizeChanged += (_, _) => ApplyHeight();
        double originalHeight = 0;
        TrackMouseDrag(divider, _ => originalHeight = agendaRow.ActualHeight, (_, y) =>
        {
            preferredAgendaHeight = ClampHeight(originalHeight + y / Scale);
            ApplyHeight();
        }, SaveHeight);
        divider.KeyDown += (_, args) =>
        {
            if (args.Key != VirtualKey.Up && args.Key != VirtualKey.Down)
                return;
            preferredAgendaHeight = ClampHeight(agendaRow.ActualHeight + (args.Key == VirtualKey.Up ? -10 : 10));
            ApplyHeight();
            SaveHeight();
            args.Handled = true;
        };
        return divider;

        double ClampHeight(double height) =>
            Math.Clamp(height, Math.Min(MinimumAgendaHeight, maximumAgendaHeight), maximumAgendaHeight);

        void ApplyHeight()
        {
            if (root.ActualHeight <= 0)
                return;
            maximumAgendaHeight = Math.Max(0, root.ActualHeight - root.Padding.Top - root.Padding.Bottom -
                root.BorderThickness.Top - root.BorderThickness.Bottom - header.ActualHeight - calendar.ActualHeight -
                calendar.Margin.Top - calendar.Margin.Bottom - footer.ActualHeight - DividerHeight - ReservedTodoHeight);
            var height = ClampHeight(preferredAgendaHeight);
            if (Math.Abs(agendaRow.Height.Value - height) > 0.1)
                agendaRow.Height = new GridLength(height);
        }

        void SaveHeight() => store.Set("agenda-height", preferredAgendaHeight.ToString(CultureInfo.InvariantCulture));
    }

    private static void TrackMouseDrag(FrameworkElement element, Action<PointInt32> start,
        Action<int, int> move, Action? finish = null)
    {
        uint? pointer = null;
        PointInt32 origin = default;
        PointInt32 previous = default;
        var timer = element.DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(16);
        element.PointerPressed += (_, args) =>
        {
            if (pointer.HasValue || args.Pointer.PointerDeviceType != PointerDeviceType.Mouse ||
                !args.GetCurrentPoint(element).Properties.IsLeftButtonPressed || !GetCursorPos(out origin))
                return;
            pointer = args.Pointer.PointerId;
            previous = origin;
            element.CapturePointer(args.Pointer);
            start(origin);
            timer.Start();
            args.Handled = true;
        };
        // WinUI can drop pointer events while a border moves away from the original
        // hit area. Track the screen position only while the physical button is held.
        timer.Tick += (_, _) =>
        {
            if (GetAsyncKeyState(0x01) >= 0)
            {
                Stop();
                return;
            }
            if (!GetCursorPos(out var point) || (point.X == previous.X && point.Y == previous.Y))
                return;
            previous = point;
            move(point.X - origin.X, point.Y - origin.Y);
        };
        element.PointerReleased += (_, args) =>
        {
            if (pointer != args.Pointer.PointerId || args.GetCurrentPoint(element).Properties.IsLeftButtonPressed)
                return;
            Stop();
            args.Handled = true;
        };
        element.Unloaded += (_, _) => { if (pointer.HasValue) Stop(); };

        void Stop()
        {
            timer.Stop();
            pointer = null;
            element.ReleasePointerCaptures();
            finish?.Invoke();
        }
    }

    [Flags]
    private enum ResizeEdge { Left = 1, Top = 2, Right = 4, Bottom = 8 }

    private sealed class MouseGrip : ContentControl
    {
        public MouseGrip(InputSystemCursorShape cursor)
        {
            ProtectedCursor = InputSystemCursor.Create(cursor);
            HorizontalContentAlignment = HorizontalAlignment.Stretch;
            VerticalContentAlignment = VerticalAlignment.Stretch;
            IsTabStop = false;
            Content = new Border { Background = new SolidColorBrush(Colors.Transparent) };
        }
    }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out PointInt32 point);
    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int key);
}

using Avalonia.Controls.ApplicationLifetimes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace CalendarTodo.Mac;

public sealed partial class WidgetWindow
{
    internal async Task Verify(IClassicDesktopStyleApplicationLifetime desktop)
    {
        timer.Stop();
        var code = 0;
        var account = "calendartodo-qa-" + Guid.NewGuid().ToString("N") + "@gmail.com";
        try
        {
            await Task.Delay(600);
            if (!IsVisible || calendar.Children.Count != 49) throw new Exception("Calendar window did not render");
            Height = MinHeight;
            await Task.Delay(250);
            var footer = syncButton.TranslatePoint(new Avalonia.Point(0, syncButton.Bounds.Height), this);
            if (footer is null || footer.Value.Y > ClientSize.Height + 1) throw new Exception("Small window clips the sync controls");
            Height = 700;
            RestorePosition();
            await Task.Delay(250);
            store.AddSchedule("本机验证会议", Beijing.At(Beijing.Now.Date.AddHours(14)));
            store.AddTodo("本机验证待办", Priority.High);
            Refresh();
            if (store.Todos(Beijing.Now).Count != 2 || agenda.Children.Count != 1 || todos.Children.Count != 2) throw new Exception("Schedule and todo display failed");
            var compactWidth = Width;
            var compactHeight = Height;
            ToggleMonth();
            await Task.Delay(350);
            if (!monthExpanded || !monthOverview.IsVisible || monthCells.Children.Count != 49) throw new Exception("Expanded month did not render");
            if (store.MonthEntries(Beijing.Now.Date, Beijing.Now.Date.AddDays(1)).Count != 2) throw new Exception("Linked calendar entry was duplicated");
            var schedule = store.Schedules().Single();
            var operation = EditEntry(schedule, null);
            await Task.Delay(350);
            var editor = desktop.Windows.Single(window => window != this);
            var panel = (StackPanel)((ScrollViewer)editor.Content!).Content!;
            panel.Children.OfType<TextBox>().Single().Text = "改期会议";
            panel.Children.OfType<DatePicker>().Single().SelectedDate = new DateTimeOffset(Beijing.Now.Date.AddDays(1).AddHours(12));
            panel.Children.OfType<TimePicker>().Single().SelectedTime = new TimeSpan(10, 5, 0);
            panel.Children.OfType<Button>().Single().RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await operation.WaitAsync(TimeSpan.FromSeconds(5));
            schedule = store.Schedules().Single();
            if (schedule.When.Date != Beijing.Now.Date.AddDays(1) || schedule.When.Hour != 10 || schedule.When.Minute != 5)
                throw new Exception("Date/time editor did not update schedule");
            operation = SetRepeat(schedule, null);
            await Task.Delay(350);
            editor = desktop.Windows.Single(window => window != this);
            panel = (StackPanel)((ScrollViewer)editor.Content!).Content!;
            panel.Children.OfType<ComboBox>().Single().SelectedIndex = 1;
            panel.Children.OfType<Button>().Single().RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await operation.WaitAsync(TimeSpan.FromSeconds(5));
            if (store.RuleFor(schedule.Id, true)?.Frequency != RepeatFrequency.Daily) throw new Exception("Native repeat settings did not save");
            ToggleMonth();
            await Task.Delay(250);
            if (monthExpanded || Math.Abs(Width - compactWidth) > 1 || Math.Abs(Height - compactHeight) > 1)
                throw new Exception($"Compact window size did not restore: expected {compactWidth}x{compactHeight}, actual {Width}x{Height}");
            if (OperatingSystem.IsMacOS())
            {
                Keychain.Save(account, "qa-secret-one");
                if (Keychain.Read(account) != "qa-secret-one") throw new Exception("Keychain create/read failed");
                Keychain.Save(account, "qa-secret-two");
                if (Keychain.Read(account) != "qa-secret-two") throw new Exception("Keychain update failed");
                Keychain.Delete(account);
                if (Keychain.Read(account) is not null) throw new Exception("Keychain delete failed");
            }
            SaveLayout();
            Console.WriteLine(OperatingSystem.IsMacOS()
                ? "PASS macOS monthly expansion, merged cards, native date/time and repeat editors, compact bounds, local persistence and Keychain roundtrip"
                : "PASS Windows UI dry run: month view, date/time and repeat editors, compact bounds and local persistence; Keychain requires macOS");
        }
        catch (Exception error) { Console.Error.WriteLine(error); code = 1; }
        finally
        {
            if (OperatingSystem.IsMacOS()) Keychain.Delete(account);
            desktop.Shutdown(code);
        }
    }
}

using Avalonia.Controls.ApplicationLifetimes;
using Avalonia;

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
            store.AddSchedule("本机验证会议", Beijing.Now.AddHours(1));
            store.AddTodo("本机验证待办", Priority.High);
            Refresh();
            if (store.Todos(Beijing.Now).Count != 2 || agenda.Children.Count != 1 || todos.Children.Count != 2) throw new Exception("Schedule and todo display failed");
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
                ? "PASS macOS rendered calendar, linked schedule/todo, local persistence and Keychain roundtrip"
                : "PASS Windows UI dry run: rendered calendar, linked schedule/todo and local persistence; Keychain requires macOS");
        }
        catch (Exception error) { Console.Error.WriteLine(error); code = 1; }
        finally
        {
            if (OperatingSystem.IsMacOS()) Keychain.Delete(account);
            desktop.Shutdown(code);
        }
    }
}

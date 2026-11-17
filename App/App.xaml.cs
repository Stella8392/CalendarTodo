using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Microsoft.UI.Xaml;

namespace CalendarTodo;

public partial class App : Application
{
    private Mutex? instance;
    private EventWaitHandle? activation;
    private RegisteredWaitHandle? activationWait;
    private WidgetWindow? widget;

    public App() => InitializeComponent();

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            var customFolder = Environment.GetEnvironmentVariable("CALENDARTODO_DATA_DIR");
            var folder = string.IsNullOrWhiteSpace(customFolder)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CalendarTodo", "Data")
                : Path.TrimEndingDirectorySeparator(Path.GetFullPath(customFolder));
            var profile = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(folder.ToUpperInvariant())))[..16];
            instance = new Mutex(true, $@"Local\CalendarTodo.Widget.{profile}", out var first);
            activation = new EventWaitHandle(false, EventResetMode.AutoReset, $@"Local\CalendarTodo.Show.{profile}");
            if (!first)
            {
                activation.Set();
                Exit();
                return;
            }
            Directory.CreateDirectory(folder);
            widget = new WidgetWindow(new Store(Path.Combine(folder, "widget.db")));
            activationWait = ThreadPool.RegisterWaitForSingleObject(activation, (_, _) =>
                widget.DispatcherQueue.TryEnqueue(widget.ShowWidget), null, Timeout.Infinite, false);
            widget.ShowWidget();
        }
        catch (Exception error)
        {
            System.Windows.Forms.MessageBox.Show($"无法启动日历与待办。\n{error.Message}", "日历与待办");
            Exit();
        }
    }
}

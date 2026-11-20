using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Themes.Fluent;
using Avalonia.Styling;
using Avalonia.Platform;

namespace CalendarTodo.Mac;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (!OperatingSystem.IsMacOS() && !args.Contains("--ui-test"))
        {
            Console.Error.WriteLine("This application is for macOS.");
            return 1;
        }
        return AppBuilder.Configure<MacApplication>().UsePlatformDetect().StartWithClassicDesktopLifetime(args);
    }
}

public sealed class MacApplication : Application
{
    private FileStream? instanceLock;
    private TrayIcon? tray;

    public override void Initialize()
    {
        RequestedThemeVariant = ThemeVariant.Light;
        Styles.Add(new FluentTheme());
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var custom = Environment.GetEnvironmentVariable("CALENDARTODO_DATA_DIR");
            if ((desktop.Args?.Contains("--smoke-test") == true || desktop.Args?.Contains("--ui-test") == true) && string.IsNullOrWhiteSpace(custom))
            {
                Console.Error.WriteLine("Verification requires an isolated CALENDARTODO_DATA_DIR.");
                desktop.Shutdown(2);
                return;
            }
            var folder = string.IsNullOrWhiteSpace(custom)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Application Support", "CalendarTodo")
                : Path.GetFullPath(custom);
            Directory.CreateDirectory(folder);
            try { instanceLock = new FileStream(Path.Combine(folder, "instance.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) { desktop.Shutdown(); return; }
            var window = new WidgetWindow(new Store(Path.Combine(folder, "widget.db")));
            desktop.MainWindow = window;
            desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;
            var menu = new NativeMenu();
            var show = new NativeMenuItem("显示日历与待办");
            show.Click += (_, _) => { window.Show(); window.Activate(); };
            var sync = new NativeMenuItem("同步邮箱");
            sync.Click += async (_, _) => await window.Sync(manual: true);
            var quit = new NativeMenuItem("退出");
            quit.Click += (_, _) => window.Close();
            menu.Items.Add(show);
            menu.Items.Add(sync);
            menu.Items.Add(quit);
            tray = new TrayIcon
            {
                Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://CalendarTodo.Mac/Assets/CalendarTodo.ico"))),
                ToolTipText = "日历与待办", Menu = menu
            };
            TrayIcon.SetIcons(this, new TrayIcons { tray });
            desktop.Exit += (_, _) => { tray.Dispose(); instanceLock?.Dispose(); };
            if (desktop.Args?.Contains("--smoke-test") == true || desktop.Args?.Contains("--ui-test") == true)
                window.Opened += async (_, _) => await window.Verify(desktop);
        }
        base.OnFrameworkInitializationCompleted();
    }
}

using System.Diagnostics;
using System.Xml.Linq;

namespace CalendarTodo.Mac;

internal static class MacSystem
{
    private static string StartupFile => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "LaunchAgents", "org.calendartodo.widget.plist");
    public static bool StartupEnabled => OperatingSystem.IsMacOS() && File.Exists(StartupFile);
    public static void SetStartup(bool enabled)
    {
        if (!OperatingSystem.IsMacOS()) return;
        if (!enabled) { File.Delete(StartupFile); return; }
        Directory.CreateDirectory(Path.GetDirectoryName(StartupFile)!);
        var document = new XDocument(new XDeclaration("1.0", "UTF-8", null),
            new XElement("plist", new XAttribute("version", "1.0"), new XElement("dict",
                new XElement("key", "Label"), new XElement("string", "org.calendartodo.widget"),
                new XElement("key", "ProgramArguments"), new XElement("array", new XElement("string", Environment.ProcessPath!)),
                new XElement("key", "RunAtLoad"), new XElement("true"))));
        document.Save(StartupFile);
    }

    public static void Open(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != "https" && uri.Scheme != "http") || uri.UserInfo.Length > 0) return;
        Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
    }

    public static async Task<bool> Notify(string title, string message)
    {
        var start = new ProcessStartInfo("/usr/bin/osascript") { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true };
        start.ArgumentList.Add("-e");
        start.ArgumentList.Add("on run argv\n display notification (item 2 of argv) with title (item 1 of argv)\nend run");
        start.ArgumentList.Add(title);
        start.ArgumentList.Add(message);
        using var process = Process.Start(start)!;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            await process.WaitForExitAsync(timeout.Token);
            return process.ExitCode == 0;
        }
        catch (OperationCanceledException) { process.Kill(); return false; }
    }
}

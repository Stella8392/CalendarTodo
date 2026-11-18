namespace CalendarTodo;

public enum Priority { Low, Medium, High }
public enum EventKind { Interview, Exam, Deadline, Appointment }
public enum MailAction { Add, Reschedule, Cancel }

public sealed record Schedule(long Id, string Title, string Sender, EventKind Kind,
    DateTimeOffset When, bool Cancelled, string Account = "", MailSource? Source = null);
public sealed record Todo(long Id, string Title, Priority Priority, bool Done, long? ScheduleId,
    DateTimeOffset? When, bool Cancelled, bool FromMail = false);
public sealed record Suggestion(string Title, string Sender, EventKind Kind, MailAction Action,
    DateTimeOffset? When, string? CalendarUid, int? Sequence, string Snippet, string Reason, MailSource? Source = null);
public sealed record MailSource(uint Validity, uint Uid, string? MessageId, string Subject);
public sealed record OriginalMail(MailSource Source, string Sender, DateTimeOffset Date, string Body);
public sealed record Pending(long Id, Suggestion Suggestion, string Account = "");
public sealed record Reminder(long EventId, string Title, EventKind Kind, DateTimeOffset When, int Lead);
public sealed record Cursor(uint Validity, uint Uid, DateTimeOffset ScanStart);

public static class Beijing
{
    public static DateTimeOffset Now => DateTimeOffset.UtcNow.ToOffset(Offset);
    public static readonly TimeSpan Offset = TimeSpan.FromHours(8);
    public static DateTimeOffset At(DateTime value) => new(DateTime.SpecifyKind(value, DateTimeKind.Unspecified), Offset);
    public static string DayKey(DateTimeOffset value) =>
        value.ToOffset(Offset).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
}

public static class DailyMailSync
{
    public static readonly TimeSpan DefaultTime = TimeSpan.FromHours(20);

    public static bool IsDue(DateTimeOffset time, string? lastSuccessfulDay, TimeSpan? scheduledTime = null) =>
        time.ToOffset(Beijing.Offset).TimeOfDay >= (scheduledTime ?? DefaultTime) &&
        lastSuccessfulDay != Beijing.DayKey(time);
}

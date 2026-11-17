using CalendarTodo;
using MimeKit;

var now = Beijing.At(new DateTime(2026, 11, 16, 10, 0, 0));

List<Suggestion> Parse(string title, string body)
{
    var message = new MimeMessage { Subject = title, Body = new TextPart("plain") { Text = body } };
    message.From.Add(new MailboxAddress("招聘团队", "jobs@example.test"));
    return MailParser.Parse(message, now);
}

void Check(bool condition, string name)
{
    if (!condition)
        throw new InvalidOperationException(name);
    Console.WriteLine($"PASS {name}");
}

Check(!DailyMailSync.IsDue(Beijing.At(now.Date.AddHours(19).AddMinutes(59)), null), "daily sync does not start before 20:00");
Check(DailyMailSync.IsDue(Beijing.At(now.Date.AddHours(20)), null), "daily sync starts at 20:00 Beijing time");
Check(!DailyMailSync.IsDue(Beijing.At(now.Date.AddHours(22)), Beijing.DayKey(now)), "successful daily sync does not repeat");
Check(DailyMailSync.IsDue(Beijing.At(now.Date.AddDays(1).AddHours(21)), Beijing.DayKey(now)), "next day's missed 20:00 sync can catch up");
Check(MailboxHome.TryCreate("  示例邮箱  ", "https://mail.example.test/", out var mailbox) &&
    mailbox == new MailboxHome("示例邮箱", "https://mail.example.test/"), "mailbox home accepts and normalizes web links");
Check(!MailboxHome.TryCreate("命令", "file:///C:/example", out _) &&
    !MailboxHome.TryCreate("带凭据", "https://user:password@mail.example.test/", out _), "mailbox home rejects non-web and credential URLs");

var interview = Parse("星河科技面试邀请", "面试时间：11月17日 周二 下午2点30分").Single();
Check(interview.When == Beijing.At(new DateTime(2026, 11, 17, 14, 30, 0)), "Chinese interview date");
var deadline = Parse("云杉数据笔试通知", "请在截止时间 2026-11-18 20:00 前提交").Single();
Check(deadline.Kind == EventKind.Deadline && deadline.When?.Hour == 20, "deadline mentioned only in body");
var exam = Parse("远山在线测评", "开考时间：11月18日 17:00\n截止时间：11月18日 20:00");
Check(exam.Count == 2 && exam[0].When?.Hour == 17 && exam[1].When?.Hour == 20, "separate start and deadline");
var pending = Parse("蓝海面试邀请", "时间待定，请留意后续邮件。").Single();
Check(pending.When is null, "incomplete invitation");
Check(Parse("远山面试邀请", "11月19日 13:30 或 11月19日 15:30").Single().When is null, "ambiguous time");
Check(Parse("远山面试邀请", $"11月19日{new string(' ', 15000)}时间待定").Single().When is null,
    "long whitespace in email does not stall parsing");
Check(Parse("远山面试邀请", "Interview time: 2026-11-19 01:45 UTC-5").Single().When?.Hour == 14, "explicit UTC offset");
Check(Parse("远山面试邀请", "面试时间：11月19日 13:30-15:30").Single().When?.Hour == 13, "time range start");
Check(Parse("远山面试邀请", "Interview time: 2026-11-19T01:45-05:00").Single().When?.Hour == 14, "ISO timestamp offset");
Check(Parse("购物节优惠", "优惠截止 2026-11-18 20:00").Count == 0, "unrelated email");
var change = Parse("星河科技面试改期通知", "原时间：11月17日 14:30\n改为：11月18日 16:00").Single();
Check(change.Action == MailAction.Reschedule && change.When?.Day == 18, "new time in reschedule");
var cancel = Parse("星河科技面试取消通知", "原计划取消。").Single();
Check(cancel.Action == MailAction.Cancel, "cancellation detected");

var invite = new MimeMessage { Subject = "远山科技面试" };
invite.From.Add(new MailboxAddress("招聘团队", "jobs@example.test"));
var calendar = new TextPart("calendar")
{
    Text = """
        BEGIN:VCALENDAR
        VERSION:2.0
        PRODID:-//CalendarTodo//Verification//EN
        METHOD:REQUEST
        BEGIN:VEVENT
        UID:interview@example.test
        SEQUENCE:3
        DTSTAMP:20261115T162000Z
        DTSTART;TZID=America/New_York:20261119T063000
        SUMMARY:远山科技面试
        END:VEVENT
        END:VCALENDAR
        """
};
invite.Body = calendar;
var ics = MailParser.Parse(invite, now).Single();
Check(ics.Sequence == 3 && ics.CalendarUid == "interview@example.test" && ics.When?.Hour == 19, "ICS UID, revision and timezone");

var folder = Path.Combine(Path.GetTempPath(), "CalendarTodoVerify", Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(folder);
try
{
    const string account = "test@163.com";
    var path = Path.Combine(folder, "test.db");
    var store = new Store(path);
    var cursor = store.GetOrResetCursor(account, 15, now);
    uint uid = 0;
    Check(cursor.ScanStart == now.AddDays(-30), "30-day initial cursor");

    void Import(string key, Suggestion item) => store.Import(account, cursor with { Uid = ++uid }, key, [item], now);
    Import("same-email", interview);
    Import("same-email", interview);
    Import("near-duplicate", interview);
    Check(store.Schedules().Count == 1 && store.Todos().Count == 1, "repeat sync and equivalent mail deduplication");
    Check(store.GetOrResetCursor(account, 15, now).Uid == 3, "cursor advances for duplicate");

    var item = store.Todos().Single();
    store.AddTodo("准备项目介绍", Priority.Medium);
    store.SetPriority(item.Id, Priority.Low);
    store.SetDone(item.Id, true);
    Check(new Store(path).Todos().Last() is { Done: true, Priority: Priority.Low }, "checked item remains at bottom after reopen");
    store.SetDone(item.Id, false);
    Check(!store.Todos().Last().Done, "unchecked item restores state");

    Import("pending", pending);
    var itemToConfirm = store.PendingItems().Single();
    Check(store.Resolve(itemToConfirm.Id, now.AddDays(1), null, ignore: false), "manual time confirmation");
    Check(store.PendingItems().Count == 0 && store.Schedules().Count == 2, "confirmed item updates schedule");

    var eventId = store.Schedules().Single(entry => entry.Title == interview.Title).Id;
    var first = store.Due(interview.When!.Value.AddHours(-24)).Single(entry => entry.EventId == eventId);
    Check(first.Lead == 1440, "24-hour notification is due");
    store.MarkSent(first);
    Check(store.Due(interview.When.Value.AddHours(-23)).All(entry => entry.EventId != eventId), "notification deduplication");
    Check(store.Due(interview.When.Value.AddMinutes(-30)).Single(entry => entry.EventId == eventId).Lead == 30, "30-minute notification is due");
    Import("change", change);
    Check(store.Schedules().Single(entry => entry.Id == eventId).When == change.When &&
        store.Todos().Single(entry => entry.Id == item.Id).When == change.When, "one rescheduled calendar/todo pair");
    Check(store.Due(change.When!.Value.AddHours(-24)).Any(entry => entry.EventId == eventId && entry.Lead == 1440), "time change resets schedule");
    Import("cancel", cancel);
    Check(store.Schedules().Single(entry => entry.Id == eventId).Cancelled &&
        store.Due(change.When.Value.AddMinutes(-29)).All(entry => entry.EventId != eventId), "cancellation suppresses reminders");

    Import("old", interview with { When = now.AddMinutes(-1) });
    Check(store.Schedules().Count == 2, "past appointments skipped");
    Import("unmatched", change with { Sender = "other@example.test" });
    Check(store.PendingItems().Count == 1, "unmatched reschedule requires confirmation");
    Import("text-before-ics", ics with { CalendarUid = null, Sequence = null });
    Import("ics", ics);
    Check(store.Schedules().Count(entry => entry.Title == ics.Title) == 1, "plain mail and ICS share one appointment");
    Import("ics-reschedule", ics with { When = ics.When!.Value.AddHours(1), Sequence = 4 });
    Import("ics-stale", ics);
    Check(store.Schedules().Single(entry => entry.Title == ics.Title).When == ics.When!.Value.AddHours(1), "ICS lower sequence cannot undo reschedule");
    var beforeReset = store.GetOrResetCursor(account, 15, now);
    store.AdvanceCursor(account, beforeReset with { Uid = beforeReset.Uid + 8 });
    store.AdvanceCursor(account, beforeReset);
    Check(store.GetOrResetCursor(account, 15, now).Uid == beforeReset.Uid + 8, "skipped mail advances cursor without regressing");
    Check(store.GetOrResetCursor(account, 16, now).Uid == 0, "UID validity change resets cursor");
    store.AdvanceCursor(account, beforeReset);
    Check(store.GetOrResetCursor(account, 16, now).Uid == 0, "stale checkpoint cannot overwrite new UID validity");

    store.AddTodo("昨天的手动任务", Priority.Low, now.AddDays(-1));
    store.AddTodo("今天的手动任务", Priority.Medium, now);
    store.AddSchedule("自己添加的今日会议", now.AddHours(3));
    store.AddSchedule("自己添加的明日会议", now.AddDays(1));
    Check(store.Todos(now).Any(task => task.Title == "今天的手动任务") &&
        store.Todos(now).Any(task => task.Title == "自己添加的今日会议" && !task.FromMail) &&
        store.Todos(now).All(task => task.Title != "昨天的手动任务" && task.Title != "自己添加的明日会议"),
        "today uses Beijing date and hides tasks from other days");
    Check(store.Schedules().Any(item => item.Title == "自己添加的明日会议" && item.Account == ""),
        "future manual schedule remains on calendar");

    var source = new MailSource(16, 50, "message@example.test", "邮件原始主题");
    var newAppointment = interview with { Title = "带来源的新面试", When = now.AddHours(5), Source = source };
    store.Import(account, cursor with { Uid = 50, Validity = 16 }, "mail-source", [newAppointment], now);
    Check(new Store(path).Schedules().Single(item => item.Title == newAppointment.Title).Source == source &&
        store.Todos(now).Any(task => task.Title == newAppointment.Title && task.FromMail),
        "mail provenance survives reopening and today's mail task is visible");

    var interviewDay = Beijing.At(new DateTime(2026, 11, 18, 0, 0, 0));
    var interviews = new[]
    {
        interview with { Title = "18 日第一场面试", When = interviewDay.AddHours(10) },
        interview with { Title = "18 日第二场面试", When = interviewDay.AddHours(16) }
    };
    store.Import(account, cursor with { Uid = 51, Validity = 16 }, "two-interviews", interviews, now);
    var dayTodos = store.Todos(interviewDay.AddHours(23)).Where(task => task.Title.StartsWith("18 日")).ToArray();
    Check(dayTodos.Length == 2 && dayTodos.All(task => task.FromMail && task.ScheduleId.HasValue) &&
        store.Todos(interviewDay.AddTicks(-1)).All(task => !task.Title.StartsWith("18 日")) &&
        store.Todos(interviewDay.AddDays(1)).All(task => !task.Title.StartsWith("18 日")),
        "both imported interviews appear in today's todo only on their Beijing calendar date");
}
finally
{
    Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    Directory.Delete(folder, recursive: true);
}

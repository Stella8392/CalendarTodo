using CalendarTodo;

internal static class RecurrenceVerification
{
    private static void Check(bool condition, string name)
    {
        if (!condition) throw new InvalidOperationException(name);
        Console.WriteLine("PASS " + name);
    }

    public static void Run()
    {
        var folder = Path.Combine(Path.GetTempPath(), "CalendarTodoRecurrence", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var day = Beijing.At(new DateTime(2026, 11, 20, 9, 0, 0));
        try
        {
            var legacyPath = Path.Combine(folder, "legacy.db");
            using (var legacy = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=" + legacyPath))
            {
                legacy.Open();
                using var command = legacy.CreateCommand();
                command.CommandText = """
                    CREATE TABLE setting(key TEXT PRIMARY KEY,value TEXT NOT NULL);
                    CREATE TABLE bound_account(address TEXT PRIMARY KEY COLLATE NOCASE);
                    CREATE TABLE cursor(account TEXT PRIMARY KEY,validity INTEGER NOT NULL,uid INTEGER NOT NULL,scan_start INTEGER NOT NULL);
                    CREATE TABLE schedule(id INTEGER PRIMARY KEY,account TEXT NOT NULL,title TEXT NOT NULL,sender TEXT NOT NULL,kind INTEGER NOT NULL,
                      time INTEGER NOT NULL,calendar_uid TEXT,sequence INTEGER,cancelled INTEGER NOT NULL DEFAULT 0,mail TEXT);
                    CREATE TABLE todo(id INTEGER PRIMARY KEY,title TEXT NOT NULL,priority INTEGER NOT NULL,done INTEGER NOT NULL DEFAULT 0,
                      completed INTEGER,schedule_id INTEGER UNIQUE REFERENCES schedule(id),day TEXT);
                    CREATE TABLE processed(account TEXT NOT NULL,message_key TEXT NOT NULL,PRIMARY KEY(account,message_key));
                    CREATE TABLE pending(id INTEGER PRIMARY KEY,account TEXT NOT NULL,suggestion TEXT NOT NULL);
                    CREATE TABLE sent(schedule_id INTEGER REFERENCES schedule(id),time INTEGER NOT NULL,lead INTEGER NOT NULL,PRIMARY KEY(schedule_id,time,lead));
                    INSERT INTO bound_account VALUES('legacy@163.com');
                    INSERT INTO cursor VALUES('legacy@163.com',42,123,0);
                    INSERT INTO schedule(id,account,title,sender,kind,time) VALUES(7,'legacy@163.com','升级前面试','jobs@example.test',0,1795158000);
                    INSERT INTO todo(id,title,priority,done,completed,schedule_id) VALUES(8,'升级前面试',2,1,1795000000,7);
                    PRAGMA user_version=1;
                    """;
                command.ExecuteNonQuery();
            }
            var upgraded = new Store(legacyPath);
            Check(upgraded.Schedules().Single().Id == 7 && upgraded.Todos().Single() is { Id: 8, Done: true, Priority: Priority.High } &&
                upgraded.Accounts().Single() == "legacy@163.com" && upgraded.GetOrResetCursor("legacy@163.com", 42, day).Uid == 123 &&
                Directory.GetFiles(folder, "legacy.before-v2-*.db").Length == 1 && upgraded.RuleFor(7, true) is null,
                "v1 database migration creates a SQLite backup and preserves event IDs, completion, priority, accounts and sync cursor");
            var path = Path.Combine(folder, "daily.db");
            var store = new Store(path);
            store.AddTodo("每日练习", Priority.High, day);
            var root = store.Todos(day).Single();
            Check(store.RuleFor(root.Id, false) is null, "new todos default to no recurrence");
            store.ConfigureRepeat(root.Id, false, new RepeatRule(RepeatFrequency.Daily, 0, day.Date), day);
            store.EnsureOccurrences(day.Date, day.Date.AddDays(6), day);
            Check(store.Todos(day).Count == 1 && store.Todos(day.AddDays(1)).Count == 1 &&
                store.Todos(day, true).Single().MovedTo is null, "future preview does not roll today's outstanding todo");
            store.SetDone(root.Id, true);
            store.EnsureOccurrences(day.Date, day.Date.AddDays(6), day.AddDays(1));
            Check(store.Todos(day).Single().Done && !store.Todos(day.AddDays(1)).Single().Done, "completion only affects one repetition");
            var missed = store.Todos(day.AddDays(1)).Single();
            store.EnsureOccurrences(day.Date, day.Date.AddDays(6), day.AddDays(4));
            Check(store.Todos(day.AddDays(4)).Count == 1 &&
                store.Todos(day.AddDays(1)).Count == 0 &&
                store.Todos(day.AddDays(1), true).Single().MovedTo == Beijing.DayKey(day.AddDays(4)),
                "several missed daily tasks roll forward into one active todo with history");
            store = new Store(path);
            store.EnsureOccurrences(day.Date, day.Date.AddDays(6), day.AddDays(4));
            Check(store.Todos(day.AddDays(4)).Count == 1 && store.Todos(day).Single().Done,
                "reopening and repeated generation preserve completion and do not duplicate");
            var current = store.Todos(day.AddDays(4)).Single();
            store.DeleteTodo(current.Id);
            store.EnsureOccurrences(day.Date, day.Date.AddDays(6), day.AddDays(4));
            Check(store.Todos(day.AddDays(4)).Count == 0 && store.Todos(day.AddDays(5)).Count == 1,
                "deleting one repeated todo leaves a tombstone and preserves later occurrences");

            var weekly = new Store(Path.Combine(folder, "weekly.db"));
            weekly.AddSchedule("每周会议", day.AddHours(5));
            var meeting = weekly.Schedules().Single();
            var mask = RepeatRule.DayBit(DayOfWeek.Monday) | RepeatRule.DayBit(DayOfWeek.Wednesday) | RepeatRule.DayBit(DayOfWeek.Friday);
            weekly.ConfigureRepeat(meeting.Id, true, new RepeatRule(RepeatFrequency.Weekly, mask, day.Date), day);
            var month = weekly.MonthEntries(day.Date, day.Date.AddDays(7), day);
            Check(month.Count == 3 && month.All(item => item.Schedule is not null && item.Todo is not null),
                "weekly selected weekdays generate linked schedule/todo once per monthly entry");
            weekly.EnsureOccurrences(day.Date, day.Date.AddDays(7), day.AddDays(3));
            Check(weekly.Schedules().Single(item => item.Id == meeting.Id).When == meeting.When &&
                weekly.Todos(day.AddDays(3)).Count == 1 && weekly.Todos(day, true).Single().MovedTo == "2026-11-23",
                "carrying only moves the todo while historical schedule time stays fixed");
            weekly.DeleteSchedule(meeting.Id);
            weekly.EnsureOccurrences(day.Date, day.Date.AddDays(7), day.AddDays(3));
            Check(weekly.Schedules().All(item => item.Id != meeting.Id) && weekly.Schedules().Count == 2,
                "a deleted recurring schedule is not regenerated");

            var edit = new Store(Path.Combine(folder, "edit.db"));
            edit.AddSchedule("原会议", day.AddHours(5));
            var original = edit.Schedules().Single();
            edit.ConfigureRepeat(original.Id, true, new RepeatRule(RepeatFrequency.Daily, 0, day.Date), day);
            edit.EnsureOccurrences(day.Date, day.Date.AddDays(5), day);
            edit.EditItem(original.Id, true, "改名会议", Priority.Low, day.AddDays(1).AddHours(6));
            edit.EnsureOccurrences(day.Date, day.Date.AddDays(5), day);
            Check(edit.Schedules().Single(item => item.Id == original.Id).When == day.AddDays(1).AddHours(6) &&
                edit.Schedules().Count(item => item.When.Date == day.Date) == 0 &&
                edit.Schedules().Any(item => item.When.Date == day.Date.AddDays(2) && item.When.Hour == 14),
                "single occurrence reschedule keeps stable identity and later repetition times");
            edit.EditItem(original.Id, true, "后续会议", Priority.Medium, day.AddHours(7), EditScope.ThisAndFollowing);
            edit.EnsureOccurrences(day.Date, day.Date.AddDays(5), day);
            Check(edit.Schedules().Count == 5 && edit.Schedules().All(item => item.When.Hour == 16),
                "series reschedule replaces future planned occurrences without duplicating the split date");
            var stop = edit.Schedules().Single(item => item.When.Date == day.Date.AddDays(2));
            edit.ConfigureRepeat(stop.Id, true, new RepeatRule(RepeatFrequency.None, 0, day.Date.AddDays(2)), day);
            edit.EnsureOccurrences(day.Date, day.Date.AddDays(8), day);
            Check(edit.Schedules().Count == 3 && edit.RuleFor(stop.Id, true) is null,
                "switching to none keeps the current occurrence and stops future generation");

            var split = new Store(Path.Combine(folder, "split.db"));
            split.AddSchedule("每日安排", day);
            var splitRoot = split.Schedules().Single();
            split.ConfigureRepeat(splitRoot.Id, true, new RepeatRule(RepeatFrequency.Daily, 0, day.Date), day);
            split.EnsureOccurrences(day.Date, day.Date.AddDays(5), day);
            var completedFuture = split.Todos(day.AddDays(3)).Single();
            split.SetDone(completedFuture.Id, true);
            split.EditItem(splitRoot.Id, true, "新时间", Priority.Medium, day.AddDays(1).AddHours(1), EditScope.ThisAndFollowing);
            split.EnsureOccurrences(day.Date, day.Date.AddDays(5), day);
            Check(split.Schedules().All(item => item.When.Date != day.Date) && split.Todos(day.AddDays(3)).Single().Done &&
                split.Todos(day.AddDays(3)).Single().Id == completedFuture.Id,
                "a future split does not resurrect the old anchor and preserves explicitly completed occurrences");
            var carry = new Store(Path.Combine(folder, "carry.db"));
            carry.AddSchedule("顺延测试", day);
            var carryRoot = carry.Schedules().Single();
            carry.ConfigureRepeat(carryRoot.Id, true, new RepeatRule(RepeatFrequency.Daily, 0, day.Date), day);
            carry.EnsureOccurrences(day.Date, day.Date.AddDays(5), day.AddDays(1));
            var currentCarry = carry.Todos(day.AddDays(1)).Single();
            carry.EditItem(carryRoot.Id, true, "原日程改期", Priority.Medium, day.AddDays(3), moveCarriedTodo: false, at: day.AddDays(1));
            Check(carry.Todos(day.AddDays(1)).Any(item => item.Id == currentCarry.Id), "reschedule can leave an already carried todo on its current execution date");
            carry.EditItem(carryRoot.Id, true, "原日程再次改期", Priority.Medium, day.AddDays(3), moveCarriedTodo: true, at: day.AddDays(1));
            Check(carry.Todos(day.AddDays(1)).All(item => item.Id != currentCarry.Id) &&
                carry.Todos(day.AddDays(3)).Any(item => item.Id == currentCarry.Id),
                "reschedule can explicitly move the current carried todo without cloning it");
            var changedRule = new Store(Path.Combine(folder, "changed-rule.db"));
            changedRule.AddTodo("修改频率后的承接", Priority.Medium, day);
            var ruleRoot = changedRule.Todos(day).Single();
            changedRule.ConfigureRepeat(ruleRoot.Id, false, new RepeatRule(RepeatFrequency.Daily, 0, day.Date), day);
            changedRule.ConfigureRepeat(ruleRoot.Id, false,
                new RepeatRule(RepeatFrequency.Weekly, RepeatRule.DayBit(DayOfWeek.Monday), day.Date.AddDays(3)), day);
            changedRule.EnsureOccurrences(day.Date, day.Date.AddDays(10), day.AddDays(3));
            Check(changedRule.Todos(day.AddDays(3)).Count == 1 && changedRule.Todos(day).Count == 0 &&
                changedRule.Todos(day.AddDays(2), true).Single().MovedTo == "2026-11-23",
                "rule changes keep a shared family so unfinished tasks carry across the rule boundary");
            changedRule.DeleteFollowing(ruleRoot.Id, false);
            changedRule.EnsureOccurrences(day.Date, day.Date.AddDays(10), day.AddDays(7));
            Check(changedRule.Todos().Count == 0, "deleting following applies across all rule versions of a family");
            var linkedDelete = new Store(Path.Combine(folder, "linked-delete.db"));
            linkedDelete.AddSchedule("保留日程", day);
            var linkedEvent = linkedDelete.Schedules().Single();
            linkedDelete.ConfigureRepeat(linkedEvent.Id, true, new RepeatRule(RepeatFrequency.Daily, 0, day.Date), day);
            var linkedTask = linkedDelete.Todos(day).Single();
            linkedDelete.DeleteFollowing(linkedTask.Id, false);
            linkedDelete.EnsureOccurrences(day.Date, day.Date.AddDays(10), day);
            Check(linkedDelete.Todos().Count == 0 && linkedDelete.Schedules().Count == 10,
                "deleting current and following linked todos preserves schedules and suppresses future task regeneration");

            var mail = new Store(Path.Combine(folder, "mail.db"));
            var cursor = mail.GetOrResetCursor("test@163.com", 1, day);
            var suggestion = new Suggestion("公司面试", "jobs@example.test", EventKind.Interview, MailAction.Add,
                day.AddDays(1), "invite@example.test", 1, "", "");
            mail.Import("test@163.com", cursor with { Uid = 1 }, "original", [suggestion], day);
            var imported = mail.Schedules().Single();
            mail.EditItem(imported.Id, true, "我的面试名称", Priority.High, day.AddDays(2));
            mail.Import("test@163.com", cursor with { Uid = 2 }, "changed", [suggestion with { Action = MailAction.Reschedule, When = day.AddDays(3), Sequence = 2 }], day);
            Check(mail.Schedules().Single().When == day.AddDays(2) && mail.PendingItems().Count == 1,
                "mail update conflicts with manual overrides and enters review using original identity");
            var pending = mail.PendingItems().Single();
            Check(mail.Resolve(pending.Id, day.AddDays(3), imported.Id, false) && mail.Schedules().Single().When == day.AddDays(3),
                "confirmed incoming change applies to the same locally edited event");
            mail.ConfigureRepeat(imported.Id, true, new RepeatRule(RepeatFrequency.Daily, 0, day.Date.AddDays(3)), day);
            mail.EnsureOccurrences(day.Date, day.Date.AddDays(7), day);
            mail.Import("test@163.com", cursor with { Uid = 3 }, "cancel-one", [suggestion with { Action = MailAction.Cancel, When = null, Sequence = 3 }], day);
            pending = mail.PendingItems().Single();
            Check(mail.Resolve(pending.Id, null, imported.Id, false) && mail.Schedules().Any(item => item.Id != imported.Id && !item.Cancelled),
                "confirmed mail cancellation can affect only the selected recurring occurrence");
            mail.Import("test@163.com", cursor with { Uid = 4 }, "cancel-future", [suggestion with { Action = MailAction.Cancel, When = null, Sequence = 4 }], day);
            pending = mail.PendingItems().Single();
            Check(mail.Resolve(pending.Id, null, imported.Id, false, EditScope.ThisAndFollowing) &&
                mail.MonthEntries(day.Date, day.Date.AddDays(10), day).Count == 0,
                "confirmed mail cancellation can stop all following recurring schedules and reminders");
            var reminders = new Store(Path.Combine(folder, "reminders.db"));
            reminders.AddSchedule("重复提醒", day.AddDays(1));
            var reminderEvent = reminders.Schedules().Single();
            reminders.ConfigureRepeat(reminderEvent.Id, true, new RepeatRule(RepeatFrequency.Daily, 0, day.Date.AddDays(1)), day);
            var firstReminder = reminders.Due(day).Single(item => item.EventId == reminderEvent.Id && item.Lead == 1440);
            reminders.MarkSent(firstReminder);
            Check(reminders.Due(day).All(item => item.EventId != reminderEvent.Id), "each recurring occurrence's reminder is sent once");
            reminders.EditItem(reminderEvent.Id, true, "重复提醒改期", Priority.Medium, day.AddDays(2).AddHours(1));
            Check(reminders.Due(day).All(item => item.EventId != reminderEvent.Id) &&
                reminders.Due(day.AddDays(1).AddHours(1)).Any(item => item.EventId == reminderEvent.Id && item.Lead == 1440),
                "recurring reschedule invalidates the old time and enables reminders at the new time");
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(folder, true);
        }
    }
}

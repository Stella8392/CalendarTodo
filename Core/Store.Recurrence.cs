using System.Globalization;
using Microsoft.Data.Sqlite;

namespace CalendarTodo;

public sealed record MonthEntry(DateTime Day, Schedule? Schedule, Todo? Todo)
{
    public string Title => Schedule?.Title ?? Todo!.Title;
    public Priority Priority => Todo?.Priority ?? Priority.Medium;
    public bool Done => Todo?.Done == true;
}

public sealed partial class Store
{
    private sealed record Series(long Id, bool IsSchedule, string Title, Priority Priority, EventKind Kind,
        string Account, string Sender, string? Mail, RepeatRule Rule, int Seconds, long Family, DateTime? TodoEnd);

    private static DateTimeOffset DayAt(string day) => Beijing.At(DateTime.ParseExact(day, "yyyy-MM-dd", CultureInfo.InvariantCulture));
    private static string DayKey(DateTime day) => day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static List<Series> ReadSeries(SqliteConnection db, SqliteTransaction? transaction)
    {
        using var command = Command(db, transaction, "SELECT id,is_schedule,title,priority,kind,account,sender,mail,frequency,weekdays,starts,ends,seconds,coalesce(family_id,id),todo_ends FROM repeat_series ORDER BY starts,id");
        using var rows = command.ExecuteReader();
        var result = new List<Series>();
        while (rows.Read())
            result.Add(new Series(rows.GetInt64(0), rows.GetBoolean(1), rows.GetString(2), (Priority)rows.GetInt32(3),
                (EventKind)rows.GetInt32(4), rows.GetString(5), rows.GetString(6), rows.IsDBNull(7) ? null : rows.GetString(7),
                new RepeatRule((RepeatFrequency)rows.GetInt32(8), rows.GetInt32(9), DayAt(rows.GetString(10)).Date,
                    rows.IsDBNull(11) ? null : DayAt(rows.GetString(11)).Date), rows.GetInt32(12), rows.GetInt64(13),
                    rows.IsDBNull(14) ? null : DayAt(rows.GetString(14)).Date));
        return result;
    }

    public RepeatRule? RuleFor(long id, bool isSchedule)
    {
        using var db = Open();
        var sql = isSchedule ? "SELECT series_id FROM schedule WHERE id=$id" :
            "SELECT coalesce(s.series_id,t.series_id) FROM todo t LEFT JOIN schedule s ON s.id=t.schedule_id WHERE t.id=$id";
        using var command = Command(db, null, sql, ("$id", id));
        return command.ExecuteScalar() is long seriesId ? ReadSeries(db, null).FirstOrDefault(item => item.Id == seriesId)?.Rule : null;
    }

    public int RelatedSchedulesOnDate(long id, DateTime day)
    {
        using var db = Open();
        using var command = Command(db, null, """
            SELECT count(*) FROM schedule s JOIN repeat_series a ON a.id=s.series_id
              JOIN schedule origin ON origin.id=$id JOIN repeat_series b ON b.id=origin.series_id
            WHERE s.id<>$id AND s.deleted=0 AND s.cancelled=0 AND coalesce(a.family_id,a.id)=coalesce(b.family_id,b.id)
              AND date(coalesce(s.local_time,s.time),'unixepoch','+8 hours')=$day
            """, ("$id", id), ("$day", DayKey(day)));
        return Convert.ToInt32(command.ExecuteScalar());
    }

    public void EnsureOccurrences(DateTime from, DateTime until, DateTimeOffset? at = null)
    {
        if (until <= from || (until - from).TotalDays > 400) throw new ArgumentException("一次最多查询 400 天的安排。");
        var today = (at ?? Beijing.Now).ToOffset(Beijing.Offset).Date;
        using var db = Open();
        using var transaction = db.BeginTransaction();
        foreach (var series in ReadSeries(db, transaction))
        {
            foreach (var day in series.Rule.Dates(from, until)) Materialize(db, transaction, series, day);
            if (series.Rule.Latest(today) is not { } latest) continue;
            Materialize(db, transaction, series, latest);
            using var target = Command(db, transaction,
                """
                SELECT t.id,coalesce(t.day,t.occurrence_day) FROM todo t LEFT JOIN schedule s ON s.id=t.schedule_id
                WHERE t.series_id=$series AND t.occurrence_day=$day AND t.state=0 AND coalesce(s.cancelled,0)=0 AND coalesce(s.deleted,0)=0
                """,
                ("$series", series.Id), ("$day", DayKey(latest)));
            long targetId;
            string targetDay;
            using (var row = target.ExecuteReader())
            {
                if (!row.Read()) continue;
                targetId = row.GetInt64(0);
                targetDay = row.GetString(1);
            }
            Execute(db, transaction, """
                UPDATE todo SET state=1,moved_to=$targetDay
                WHERE series_id IN(SELECT id FROM repeat_series WHERE coalesce(family_id,id)=$family)
                  AND id<>$target AND state=0 AND done=0 AND occurrence_day<$day
                  AND coalesce(day,occurrence_day)<=$day
                  AND NOT EXISTS(SELECT 1 FROM schedule s WHERE s.id=todo.schedule_id AND (s.cancelled=1 OR s.deleted=1));
                """, ("$family", series.Family), ("$target", targetId), ("$targetDay", targetDay), ("$day", DayKey(latest)));
        }
        transaction.Commit();
    }

    private static void Materialize(SqliteConnection db, SqliteTransaction transaction, Series series, DateTime day)
    {
        var key = DayKey(day);
        long? schedule = null;
        if (series.IsSchedule)
        {
            Execute(db, transaction, """
                INSERT OR IGNORE INTO schedule(title,account,sender,kind,time,mail,series_id,occurrence_day,generated)
                VALUES($title,$account,$sender,$kind,$time,$mail,$series,$day,1);
                """, ("$title", series.Title), ("$account", series.Account), ("$sender", series.Sender),
                ("$kind", (int)series.Kind), ("$time", Beijing.At(day.AddSeconds(series.Seconds)).ToUnixTimeSeconds()),
                ("$mail", series.Mail), ("$series", series.Id), ("$day", key));
            using var command = Command(db, transaction, "SELECT id FROM schedule WHERE series_id=$series AND occurrence_day=$day AND deleted=0",
                ("$series", series.Id), ("$day", key));
            if (command.ExecuteScalar() is not long id) return;
            schedule = id;
        }
        if (series.TodoEnd is { } lastTodo && day > lastTodo) return;
        Execute(db, transaction, """
            INSERT OR IGNORE INTO todo(title,priority,schedule_id,day,series_id,occurrence_day)
            VALUES($title,$priority,$schedule,$day,$series,$day);
            """, ("$title", series.Title), ("$priority", (int)series.Priority), ("$schedule", schedule),
            ("$day", key), ("$series", series.Id));
    }

    public List<MonthEntry> MonthEntries(DateTime from, DateTime until, DateTimeOffset? now = null)
    {
        EnsureOccurrences(from, until, now);
        var schedules = Schedules().Where(item => !item.Cancelled).ToDictionary(item => item.Id);
        var tasks = Todos(null, includeMoved: true).Where(item => !item.Cancelled).ToList();
        var linked = tasks.Where(item => item.ScheduleId.HasValue).ToDictionary(item => item.ScheduleId!.Value);
        var result = new List<MonthEntry>();
        foreach (var item in schedules.Values.Where(item => item.When.Date >= from.Date && item.When.Date < until.Date))
        {
            linked.TryGetValue(item.Id, out var task);
            result.Add(new MonthEntry(item.When.Date, item, task?.Day?.Date == item.When.Date ? task : null));
        }
        foreach (var task in tasks.Where(item => item.Day?.Date >= from.Date && item.Day?.Date < until.Date))
            if (task.ScheduleId is not { } id || !schedules.TryGetValue(id, out var schedule) || task.Day!.Value.Date != schedule.When.Date)
                result.Add(new MonthEntry(task.Day!.Value.Date, null, task));
        return result.OrderBy(item => item.Day).ThenBy(item => item.Done || item.Todo?.MovedTo is not null)
            .ThenByDescending(item => item.Priority).ThenBy(item => item.Schedule?.When ?? DateTimeOffset.MaxValue).ToList();
    }

    public void ConfigureRepeat(long id, bool isSchedule, RepeatRule rule, DateTimeOffset? at = null)
    {
        rule.Validate();
        using var db = Open();
        using var transaction = db.BeginTransaction();
        ConfigureRepeat(db, transaction, id, isSchedule, rule);
        transaction.Commit();
        EnsureOccurrences(rule.Start.Date, rule.Start.Date.AddDays(1), at);
    }

    private static void ConfigureRepeat(SqliteConnection db, SqliteTransaction transaction, long id, bool isSchedule, RepeatRule rule, DateTime? splitAt = null)
    {
        if (!isSchedule)
        {
            using var linked = Command(db, transaction, "SELECT schedule_id FROM todo WHERE id=$id", ("$id", id));
            if (linked.ExecuteScalar() is long scheduleId) { id = scheduleId; isSchedule = true; }
        }
        var sql = isSchedule ? """
            SELECT coalesce(s.local_title,s.title),coalesce(t.priority,1),s.kind,s.account,s.sender,s.mail,
              coalesce(s.local_time,s.time),s.series_id,coalesce(s.occurrence_day,date(coalesce(s.local_time,s.time),'unixepoch','+8 hours'))
            FROM schedule s LEFT JOIN todo t ON t.schedule_id=s.id WHERE s.id=$id AND s.deleted=0 AND s.cancelled=0
            """ : "SELECT title,priority,3,'','',NULL,0,series_id,coalesce(occurrence_day,day) FROM todo WHERE id=$id AND state<>2";
        string title, account, sender, originalDay;
        string? mail;
        int priority, kind, seconds;
        long? previous;
        using (var command = Command(db, transaction, sql, ("$id", id)))
        using (var row = command.ExecuteReader())
        {
            if (!row.Read()) throw new ArgumentException("事项已不存在。");
            title = row.GetString(0); priority = row.GetInt32(1); kind = row.GetInt32(2);
            account = row.GetString(3); sender = row.GetString(4); mail = row.IsDBNull(5) ? null : row.GetString(5);
            seconds = isSchedule ? (int)FromUnix(row.GetInt64(6)).TimeOfDay.TotalSeconds : 0;
            previous = row.IsDBNull(7) ? null : row.GetInt64(7);
            originalDay = row.GetString(8);
        }
        var boundary = splitAt?.Date ?? rule.Start.Date;
        var moveRoot = previous is null || string.CompareOrdinal(originalDay, DayKey(boundary)) >= 0;
        if (previous is { } old)
        {
            StopSeries(db, transaction, old, boundary, isSchedule ? id : null, !isSchedule ? id : null);
            if (moveRoot)
                Execute(db, transaction, isSchedule
                    ? "UPDATE schedule SET series_id=NULL,occurrence_day=NULL WHERE id=$id; UPDATE todo SET series_id=NULL,occurrence_day=NULL WHERE schedule_id=$id;"
                    : "UPDATE todo SET series_id=NULL,occurrence_day=NULL WHERE id=$id;", ("$id", id));
        }
        if (rule.Frequency == RepeatFrequency.None) return;
        Execute(db, transaction, """
            INSERT INTO repeat_series(is_schedule,title,priority,kind,account,sender,mail,frequency,weekdays,starts,ends,seconds)
            VALUES($scheduled,$title,$priority,$kind,$account,$sender,$mail,$frequency,$weekdays,$start,$end,$seconds);
            """, ("$scheduled", isSchedule), ("$title", title), ("$priority", priority), ("$kind", kind), ("$account", account),
            ("$sender", sender), ("$mail", mail), ("$frequency", (int)rule.Frequency), ("$weekdays", rule.Weekdays),
            ("$start", DayKey(rule.Start)), ("$end", rule.End is { } end ? DayKey(end) : null), ("$seconds", seconds));
        using var lastId = Command(db, transaction, "SELECT last_insert_rowid()");
        var series = (long)lastId.ExecuteScalar()!;
        long family = series;
        if (previous is { } ancestor)
        {
            using var familyQuery = Command(db, transaction, "SELECT coalesce(family_id,id) FROM repeat_series WHERE id=$id", ("$id", ancestor));
            family = (long)familyQuery.ExecuteScalar()!;
        }
        Execute(db, transaction, "UPDATE repeat_series SET family_id=$family WHERE id=$id", ("$family", family), ("$id", series));
        Execute(db, transaction, "UPDATE repeat_series SET todo_ends=(SELECT min(todo_ends) FROM repeat_series WHERE coalesce(family_id,id)=$family) WHERE id=$id",
            ("$family", family), ("$id", series));
        if (previous is { } prior)
            Execute(db, transaction, """
                UPDATE schedule SET series_id=$series WHERE series_id IN(SELECT id FROM repeat_series WHERE coalesce(family_id,id)=$family)
                  AND occurrence_day>=$boundary AND id<>$keep
                  AND (edited=1 OR EXISTS(SELECT 1 FROM todo WHERE schedule_id=schedule.id AND (done=1 OR edited=1)));
                UPDATE todo SET series_id=$series WHERE series_id IN(SELECT id FROM repeat_series WHERE coalesce(family_id,id)=$family) AND occurrence_day>=$boundary
                  AND id<>$keepTodo AND coalesce(schedule_id,-1)<>$keepSchedule
                  AND (done=1 OR edited=1 OR schedule_id IN (SELECT id FROM schedule WHERE series_id=$series));
                """, ("$series", series), ("$family", family), ("$boundary", DayKey(boundary)),
                ("$keep", isSchedule ? id : -1), ("$keepTodo", isSchedule ? -1 : id), ("$keepSchedule", isSchedule ? id : -2));
        if (moveRoot) Execute(db, transaction, isSchedule ? """
            UPDATE schedule SET series_id=$series,occurrence_day=$day WHERE id=$id;
            UPDATE todo SET series_id=$series,occurrence_day=$day,day=coalesce(day,$day) WHERE schedule_id=$id;
            """ : "UPDATE todo SET series_id=$series,occurrence_day=$day WHERE id=$id;",
            ("$series", series), ("$day", previous.HasValue ? DayKey(rule.Start) : originalDay), ("$id", id));
    }

    private static void StopSeries(SqliteConnection db, SqliteTransaction transaction, long seriesId, DateTime boundary,
        long? keepSchedule = null, long? keepTodo = null)
    {
        using var familyQuery = Command(db, transaction, "SELECT coalesce(family_id,id) FROM repeat_series WHERE id=$id", ("$id", seriesId));
        var family = (long)familyQuery.ExecuteScalar()!;
        Execute(db, transaction, """
            UPDATE repeat_series SET ends=$end WHERE coalesce(family_id,id)=$family AND (ends IS NULL OR ends>$end);
            UPDATE schedule SET deleted=1 WHERE series_id IN(SELECT id FROM repeat_series WHERE coalesce(family_id,id)=$family) AND occurrence_day>=$boundary AND id<>coalesce($schedule,-1)
              AND NOT EXISTS(SELECT 1 FROM todo WHERE schedule_id=schedule.id AND (done=1 OR edited=1));
            UPDATE todo SET state=2 WHERE series_id IN(SELECT id FROM repeat_series WHERE coalesce(family_id,id)=$family) AND occurrence_day>=$boundary
              AND id<>coalesce($todo,-1) AND coalesce(schedule_id,-1)<>coalesce($schedule,-2) AND done=0 AND edited=0;
            """, ("$end", DayKey(boundary.AddDays(-1))), ("$family", family), ("$boundary", DayKey(boundary)),
            ("$schedule", keepSchedule), ("$todo", keepTodo));
    }

    public void DeleteFollowing(long id, bool isSchedule)
    {
        var schedule = isSchedule ? Schedules().FirstOrDefault(item => item.Id == id) : null;
        var task = !isSchedule ? Todos(null, true).FirstOrDefault(item => item.Id == id) : null;
        var series = schedule?.SeriesId ?? task?.SeriesId;
        var day = schedule?.OriginalDay ?? task?.OriginalDay;
        if (series is null || day is null) { if (isSchedule) DeleteSchedule(id); else DeleteTodo(id); return; }
        using var db = Open();
        using var transaction = db.BeginTransaction();
        if (!isSchedule && task?.ScheduleId is not null)
        {
            Execute(db, transaction, """
                UPDATE repeat_series SET todo_ends=$end WHERE coalesce(family_id,id)=
                  (SELECT coalesce(family_id,id) FROM repeat_series WHERE id=$series) AND (todo_ends IS NULL OR todo_ends>$end);
                UPDATE todo SET state=2,edited=1 WHERE series_id IN(
                  SELECT id FROM repeat_series WHERE coalesce(family_id,id)=(SELECT coalesce(family_id,id) FROM repeat_series WHERE id=$series)) AND occurrence_day>=$day;
                """, ("$series", series), ("$day", day), ("$end", DayKey(DayAt(day).Date.AddDays(-1))));
            transaction.Commit();
            return;
        }
        StopSeries(db, transaction, series.Value, DayAt(day).Date);
        Execute(db, transaction, """
            UPDATE schedule SET deleted=1,edited=1 WHERE series_id IN(
              SELECT id FROM repeat_series WHERE coalesce(family_id,id)=(SELECT coalesce(family_id,id) FROM repeat_series WHERE id=$series)) AND occurrence_day>=$day;
            UPDATE todo SET state=2,edited=1 WHERE series_id IN(
              SELECT id FROM repeat_series WHERE coalesce(family_id,id)=(SELECT coalesce(family_id,id) FROM repeat_series WHERE id=$series)) AND occurrence_day>=$day;
            """, ("$series", series), ("$day", day));
        transaction.Commit();
    }

    public void EditItem(long id, bool isSchedule, string title, Priority priority, DateTimeOffset when,
        EditScope scope = EditScope.ThisOccurrence, bool moveCarriedTodo = true, DateTimeOffset? at = null)
    {
        title = title.Trim();
        if (title.Length is 0 or > 180 || !Enum.IsDefined(priority)) throw new ArgumentException("请输入有效的事项名称和优先级。");
        when = when.ToOffset(Beijing.Offset);
        var repeat = RuleFor(id, isSchedule);
        var original = isSchedule ? Schedules().FirstOrDefault(item => item.Id == id)?.When.Date : Todos(null, true).FirstOrDefault(item => item.Id == id)?.Day?.Date;
        if (original is null) throw new ArgumentException("事项已不存在。");
        using var db = Open();
        using var transaction = db.BeginTransaction();
        if (isSchedule)
        {
            using var exists = Command(db, transaction, "SELECT 1 FROM schedule WHERE id=$id AND deleted=0 AND cancelled=0", ("$id", id));
            if (exists.ExecuteScalar() is null) throw new ArgumentException("该日程已取消或删除，请刷新列表。");
            Execute(db, transaction, """
                UPDATE schedule SET local_title=$title,local_time=$time,edited=1 WHERE id=$id;
                UPDATE todo SET title=$title,priority=$priority,day=$day,edited=1 WHERE schedule_id=$id AND state=0;
                DELETE FROM sent WHERE schedule_id=$id;
                """, ("$title", title), ("$time", when.ToUnixTimeSeconds()), ("$priority", (int)priority), ("$day", Beijing.DayKey(when)), ("$id", id));
            if (moveCarriedTodo)
                Execute(db, transaction, """
                    UPDATE todo SET day=$day,edited=1 WHERE id=(
                      SELECT active.id FROM todo active JOIN repeat_series a ON a.id=active.series_id
                        JOIN todo old ON old.schedule_id=$id JOIN repeat_series o ON o.id=old.series_id
                      WHERE old.schedule_id=$id AND old.state=1 AND active.state=0 AND active.done=0
                        AND coalesce(a.family_id,a.id)=coalesce(o.family_id,o.id)
                        AND coalesce(active.day,active.occurrence_day)<=$today
                      ORDER BY active.occurrence_day DESC LIMIT 1);
                    """, ("$day", Beijing.DayKey(when)), ("$id", id), ("$today", Beijing.DayKey(at ?? Beijing.Now)));
        }
        else
            Execute(db, transaction, "UPDATE todo SET title=$title,priority=$priority,day=$day,edited=1 WHERE id=$id",
                ("$title", title), ("$priority", (int)priority), ("$day", Beijing.DayKey(when)), ("$id", id));
        if (scope == EditScope.ThisAndFollowing && repeat is not null)
        {
            var weekdays = repeat.Weekdays;
            if (repeat.Frequency == RepeatFrequency.Weekly && when.Date != original.Value)
                weekdays = (weekdays & ~RepeatRule.DayBit(original.Value.DayOfWeek)) | RepeatRule.DayBit(when.DayOfWeek);
            ConfigureRepeat(db, transaction, id, isSchedule, repeat with { Start = when.Date, End = null, Weekdays = weekdays },
                isSchedule ? DayAt(SchedulesOriginalDay(db, transaction, id)).Date : original.Value);
        }
        transaction.Commit();
    }

    private static string SchedulesOriginalDay(SqliteConnection db, SqliteTransaction transaction, long id)
    {
        using var command = Command(db, transaction, "SELECT coalesce(occurrence_day,date(time,'unixepoch','+8 hours')) FROM schedule WHERE id=$id", ("$id", id));
        return (string)command.ExecuteScalar()!;
    }
}

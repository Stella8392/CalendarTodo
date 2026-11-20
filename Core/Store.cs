using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace CalendarTodo;

public sealed partial class Store
{
    private readonly string connectionString;

    public Store(string path)
    {
        connectionString = new SqliteConnectionStringBuilder { DataSource = path, ForeignKeys = true }.ToString();
        using var db = Open();
        using var command = Command(db, null, """
            PRAGMA journal_mode = WAL;
            CREATE TABLE IF NOT EXISTS setting (key TEXT PRIMARY KEY, value TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS bound_account (address TEXT PRIMARY KEY COLLATE NOCASE);
            CREATE TABLE IF NOT EXISTS cursor (account TEXT PRIMARY KEY, validity INTEGER NOT NULL,
              uid INTEGER NOT NULL, scan_start INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS schedule (id INTEGER PRIMARY KEY, account TEXT NOT NULL,
              title TEXT NOT NULL, sender TEXT NOT NULL, kind INTEGER NOT NULL, time INTEGER NOT NULL,
              calendar_uid TEXT, sequence INTEGER, cancelled INTEGER NOT NULL DEFAULT 0);
            CREATE INDEX IF NOT EXISTS schedule_calendar ON schedule(account, calendar_uid);
            CREATE TABLE IF NOT EXISTS todo (id INTEGER PRIMARY KEY, title TEXT NOT NULL,
              priority INTEGER NOT NULL, done INTEGER NOT NULL DEFAULT 0, completed INTEGER,
              schedule_id INTEGER UNIQUE REFERENCES schedule(id));
            CREATE TABLE IF NOT EXISTS processed (account TEXT NOT NULL, message_key TEXT NOT NULL,
              PRIMARY KEY(account, message_key));
            CREATE TABLE IF NOT EXISTS pending (id INTEGER PRIMARY KEY, account TEXT NOT NULL,
              suggestion TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS sent (schedule_id INTEGER REFERENCES schedule(id),
              time INTEGER NOT NULL, lead INTEGER NOT NULL, PRIMARY KEY(schedule_id,time,lead));
            """);
        command.ExecuteNonQuery();
        using (var existingVersion = Command(db, null, "PRAGMA user_version"))
        {
            if (Convert.ToInt64(existingVersion.ExecuteScalar()) == 1)
            {
                using var backup = new SqliteConnection(new SqliteConnectionStringBuilder
                {
                    DataSource = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!,
                        Path.GetFileNameWithoutExtension(path) + ".before-v2-" + Guid.NewGuid().ToString("N") + ".db")
                }.ToString());
                backup.Open();
                db.BackupDatabase(backup);
            }
        }
        using var migration = db.BeginTransaction();
        using var version = Command(db, migration, "PRAGMA user_version");
        var schemaVersion = Convert.ToInt64(version.ExecuteScalar());
        if (schemaVersion < 1)
        {
            Execute(db, migration, """
                ALTER TABLE schedule ADD COLUMN mail TEXT;
                ALTER TABLE todo ADD COLUMN day TEXT;
                UPDATE todo SET day=$today WHERE schedule_id IS NULL;
                PRAGMA user_version=1;
                """, ("$today", Beijing.DayKey(Beijing.Now)));
        }
        if (schemaVersion < 2)
            Execute(db, migration, """
                CREATE TABLE repeat_series(id INTEGER PRIMARY KEY, is_schedule INTEGER NOT NULL,
                  title TEXT NOT NULL, priority INTEGER NOT NULL, kind INTEGER NOT NULL,
                  account TEXT NOT NULL, sender TEXT NOT NULL, mail TEXT,
                  frequency INTEGER NOT NULL, weekdays INTEGER NOT NULL,
                  starts TEXT NOT NULL, ends TEXT, seconds INTEGER NOT NULL, family_id INTEGER, todo_ends TEXT);
                ALTER TABLE schedule ADD COLUMN series_id INTEGER REFERENCES repeat_series(id);
                ALTER TABLE schedule ADD COLUMN occurrence_day TEXT;
                ALTER TABLE schedule ADD COLUMN local_title TEXT;
                ALTER TABLE schedule ADD COLUMN local_time INTEGER;
                ALTER TABLE schedule ADD COLUMN deleted INTEGER NOT NULL DEFAULT 0;
                ALTER TABLE schedule ADD COLUMN edited INTEGER NOT NULL DEFAULT 0;
                ALTER TABLE schedule ADD COLUMN generated INTEGER NOT NULL DEFAULT 0;
                ALTER TABLE todo ADD COLUMN series_id INTEGER REFERENCES repeat_series(id);
                ALTER TABLE todo ADD COLUMN occurrence_day TEXT;
                ALTER TABLE todo ADD COLUMN state INTEGER NOT NULL DEFAULT 0;
                ALTER TABLE todo ADD COLUMN moved_to TEXT;
                ALTER TABLE todo ADD COLUMN edited INTEGER NOT NULL DEFAULT 0;
                CREATE UNIQUE INDEX schedule_occurrence ON schedule(series_id,occurrence_day) WHERE series_id IS NOT NULL;
                CREATE UNIQUE INDEX todo_occurrence ON todo(series_id,occurrence_day) WHERE series_id IS NOT NULL;
                PRAGMA user_version=2;
                """);
        Execute(db, migration, """
            INSERT OR IGNORE INTO bound_account(address) SELECT value FROM setting WHERE key='account' AND value<>'';
            DELETE FROM setting WHERE key='account';
            """);
        migration.Commit();
    }

    private SqliteConnection Open()
    {
        var db = new SqliteConnection(connectionString);
        db.Open();
        return db;
    }

    private static SqliteCommand Command(SqliteConnection db, SqliteTransaction? transaction,
        string sql, params (string Name, object? Value)[] parameters)
    {
        var command = db.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return command;
    }

    private static int Execute(SqliteConnection db, SqliteTransaction? transaction, string sql,
        params (string Name, object? Value)[] parameters)
    {
        using var command = Command(db, transaction, sql, parameters);
        return command.ExecuteNonQuery();
    }

    public List<string> Accounts()
    {
        using var db = Open();
        using var command = Command(db, null, "SELECT address FROM bound_account ORDER BY address");
        using var rows = command.ExecuteReader();
        var result = new List<string>();
        while (rows.Read()) result.Add(rows.GetString(0));
        return result;
    }

    public void BindAccount(string address)
    {
        address = address.Trim().ToLowerInvariant();
        if (MailProvider.ForAddress(address) is null) throw new ArgumentException("不支持的邮箱地址", nameof(address));
        using var db = Open();
        Execute(db, null, "INSERT OR IGNORE INTO bound_account(address) VALUES($address)", ("$address", address));
    }

    public void UnbindAccount(string address)
    {
        using var db = Open();
        Execute(db, null, "DELETE FROM bound_account WHERE address=$address", ("$address", address));
    }

    public string? Get(string key)
    {
        using var db = Open();
        using var command = Command(db, null, "SELECT value FROM setting WHERE key=$key", ("$key", key));
        return command.ExecuteScalar() as string;
    }

    public void Set(string key, string value)
    {
        using var db = Open();
        Execute(db, null, "INSERT INTO setting VALUES($key,$value) ON CONFLICT(key) DO UPDATE SET value=excluded.value",
            ("$key", key), ("$value", value));
    }

    public List<Schedule> Schedules()
    {
        using var db = Open();
        using var command = Command(db, null, """
            SELECT id,coalesce(local_title,title),sender,kind,coalesce(local_time,time),cancelled,account,mail,
              series_id,occurrence_day FROM schedule WHERE deleted=0 ORDER BY coalesce(local_time,time),id
            """);
        using var rows = command.ExecuteReader();
        var result = new List<Schedule>();
        while (rows.Read())
            result.Add(new Schedule(rows.GetInt64(0), rows.GetString(1), rows.GetString(2),
                (EventKind)rows.GetInt32(3), FromUnix(rows.GetInt64(4)), rows.GetBoolean(5),
                rows.GetString(6), rows.IsDBNull(7) ? null : JsonSerializer.Deserialize<MailSource>(rows.GetString(7)),
                rows.IsDBNull(8) ? null : rows.GetInt64(8), rows.IsDBNull(9) ? null : rows.GetString(9)));
        return result;
    }

    public List<Todo> Todos(DateTimeOffset? day = null, bool includeMoved = false)
    {
        var midnight = day is { } date ? Beijing.At(date.ToOffset(Beijing.Offset).Date) : (DateTimeOffset?)null;
        using var db = Open();
        using var command = Command(db, null, """
            SELECT t.id,coalesce(s.local_title,s.title,t.title),t.priority,t.done,t.schedule_id,coalesce(s.local_time,s.time),
              coalesce(s.cancelled,0), coalesce(s.account<>'',0),
              coalesce(t.day,date(coalesce(s.local_time,s.time),'unixepoch','+8 hours')),
              t.series_id,t.occurrence_day,t.moved_to
            FROM todo t LEFT JOIN schedule s ON t.schedule_id=s.id
            WHERE t.state<>2 AND (s.id IS NULL OR s.deleted=0) AND ($moved OR t.state=0) AND
              ($day IS NULL OR coalesce(t.day,date(coalesce(s.local_time,s.time),'unixepoch','+8 hours'))=$day)
            ORDER BY t.done, coalesce(s.cancelled,0),
              CASE WHEN t.done=0 THEN t.priority END DESC,
              CASE WHEN t.done=0 THEN coalesce(s.local_time,s.time,9223372036854775807) END,
              t.completed DESC, t.id
            """, ("$day", midnight is { } local ? Beijing.DayKey(local) : null),
            ("$moved", includeMoved));
        using var rows = command.ExecuteReader();
        var result = new List<Todo>();
        while (rows.Read())
            result.Add(new Todo(rows.GetInt64(0), rows.GetString(1), (Priority)rows.GetInt32(2),
                rows.GetBoolean(3), rows.IsDBNull(4) ? null : rows.GetInt64(4),
                rows.IsDBNull(5) ? null : FromUnix(rows.GetInt64(5)), rows.GetBoolean(6), rows.GetBoolean(7),
                rows.IsDBNull(8) ? null : DayAt(rows.GetString(8)), rows.IsDBNull(9) ? null : rows.GetInt64(9),
                rows.IsDBNull(10) ? null : rows.GetString(10), rows.IsDBNull(11) ? null : rows.GetString(11)));
        return result;
    }

    public void AddTodo(string title, Priority priority, DateTimeOffset? day = null)
    {
        using var db = Open();
        Execute(db, null, "INSERT INTO todo(title,priority,day) VALUES($title,$priority,$day)",
            ("$title", title), ("$priority", (int)priority), ("$day", Beijing.DayKey(day ?? Beijing.Now)));
    }

    public void AddSchedule(string title, DateTimeOffset date)
    {
        using var db = Open();
        using var transaction = db.BeginTransaction();
        Execute(db, transaction, """
            INSERT INTO schedule(title,account,sender,kind,time) VALUES($title,'','',$kind,$time);
            INSERT INTO todo(title,priority,schedule_id) VALUES($title,1,last_insert_rowid());
            """, ("$title", title), ("$kind", (int)EventKind.Appointment), ("$time", date.ToUnixTimeSeconds()));
        transaction.Commit();
    }

    public void AttachMail(Schedule schedule, MailSource source)
    {
        using var db = Open();
        Execute(db, null, """
            UPDATE schedule SET mail=$mail WHERE id=$id AND account=$account AND time=$time
            """, ("$id", schedule.Id), ("$account", schedule.Account), ("$time", schedule.When.ToUnixTimeSeconds()),
            ("$mail", JsonSerializer.Serialize(source)));
    }

    public void SetDone(long id, bool done)
    {
        using var db = Open();
        Execute(db, null, "UPDATE todo SET done=$done,completed=$completed,edited=1 WHERE id=$id AND state=0",
            ("$id", id), ("$done", done), ("$completed", done ? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() : null));
    }

    public void DeleteTodo(long id)
    {
        using var db = Open();
        Execute(db, null, """
            UPDATE todo SET state=2,edited=1 WHERE id=$id AND series_id IS NOT NULL;
            DELETE FROM todo WHERE id=$id AND series_id IS NULL;
            """, ("$id", id));
    }

    public void DeleteSchedule(long id)
    {
        using var db = Open();
        using var transaction = db.BeginTransaction();
        using (var recurring = Command(db, transaction, "SELECT series_id FROM schedule WHERE id=$id", ("$id", id)))
        {
            if (recurring.ExecuteScalar() is long)
            {
                Execute(db, transaction, "UPDATE schedule SET deleted=1,edited=1 WHERE id=$id; UPDATE todo SET state=2,edited=1 WHERE schedule_id=$id;", ("$id", id));
                transaction.Commit();
                return;
            }
        }
        Execute(db, transaction, """
            DELETE FROM sent WHERE schedule_id=$id;
            DELETE FROM todo WHERE schedule_id=$id;
            DELETE FROM schedule WHERE id=$id;
            """, ("$id", id));
        transaction.Commit();
    }

    public void SetPriority(long id, Priority priority)
    {
        using var db = Open();
        Execute(db, null, "UPDATE todo SET priority=$priority,edited=1 WHERE id=$id",
            ("$id", id), ("$priority", (int)priority));
    }

    public Cursor GetOrResetCursor(string account, uint validity, DateTimeOffset now)
    {
        using var db = Open();
        using var transaction = db.BeginTransaction();
        Cursor? existing = null;
        using (var command = Command(db, transaction, "SELECT validity,uid,scan_start FROM cursor WHERE account=$account", ("$account", account)))
        using (var row = command.ExecuteReader())
            if (row.Read())
                existing = new Cursor((uint)row.GetInt64(0), (uint)row.GetInt64(1), FromUnix(row.GetInt64(2)));

        var cursor = existing?.Validity == validity ? existing : new Cursor(validity, 0, now.AddDays(-30));
        if (cursor != existing)
            SaveCursor(db, transaction, account, cursor);
        transaction.Commit();
        return cursor;
    }

    private static void SaveCursor(SqliteConnection db, SqliteTransaction transaction, string account, Cursor cursor) =>
        Execute(db, transaction, """
            INSERT INTO cursor VALUES($account,$validity,$uid,$scan)
            ON CONFLICT(account) DO UPDATE SET validity=excluded.validity,uid=excluded.uid,scan_start=excluded.scan_start
            """, ("$account", account), ("$validity", (long)cursor.Validity),
            ("$uid", (long)cursor.Uid), ("$scan", cursor.ScanStart.ToUnixTimeSeconds()));

    public void AdvanceCursor(string account, Cursor cursor)
    {
        using var db = Open();
        Execute(db, null, """
            UPDATE cursor SET uid=$uid WHERE account=$account AND validity=$validity AND uid<$uid
            """, ("$account", account), ("$validity", (long)cursor.Validity), ("$uid", (long)cursor.Uid));
    }

    public void Import(string account, Cursor cursor, string messageKey, IEnumerable<Suggestion> suggestions, DateTimeOffset now)
    {
        using var db = Open();
        using var transaction = db.BeginTransaction();
        if (Execute(db, transaction, "INSERT OR IGNORE INTO processed VALUES($account,$message)",
            ("$account", account), ("$message", messageKey)) != 0)
        {
            foreach (var suggestion in suggestions)
            {
                if (suggestion.Action == MailAction.Add && suggestion.When <= now)
                    continue;
                if (Apply(db, transaction, account, suggestion, null))
                    continue;
                Execute(db, transaction, "INSERT INTO pending(account,suggestion) VALUES($account,$suggestion)",
                    ("$account", account), ("$suggestion", JsonSerializer.Serialize(suggestion)));
            }
        }
        SaveCursor(db, transaction, account, cursor);
        transaction.Commit();
    }

    private static bool Apply(SqliteConnection db, SqliteTransaction transaction,
        string account, Suggestion suggestion, long? selectedId, EditScope scope = EditScope.ThisOccurrence)
    {
        if ((suggestion.Action != MailAction.Cancel && suggestion.When is null) ||
            (suggestion.Action != MailAction.Add && selectedId is null && string.IsNullOrWhiteSpace(suggestion.Sender)))
            return false;

        var matches = new List<(long Id, int? Seq, string? CalendarUid)>();
        using (var command = Command(db, transaction, """
            SELECT id,sequence,calendar_uid FROM schedule WHERE account=$account AND deleted=0 AND ($id IS NOT NULL OR generated=0) AND
            (($id IS NOT NULL AND id=$id) OR ($id IS NULL AND (
              ($ical IS NOT NULL AND calendar_uid=$ical AND sender=$sender) OR
              (title=$title COLLATE NOCASE AND sender=$sender AND kind=$kind AND
                (($ical IS NULL AND ($action<>0 OR time=$time)) OR
                 ($ical IS NOT NULL AND calendar_uid IS NULL AND $action=0 AND time=$time))))))
            """, ("$account", account), ("$id", selectedId), ("$ical", suggestion.CalendarUid),
            ("$title", suggestion.Title), ("$sender", suggestion.Sender), ("$kind", (int)suggestion.Kind),
            ("$action", (int)suggestion.Action), ("$time", suggestion.When?.ToUnixTimeSeconds())))
        using (var rows = command.ExecuteReader())
            while (rows.Read())
                matches.Add((rows.GetInt64(0), rows.IsDBNull(1) ? null : rows.GetInt32(1),
                    rows.IsDBNull(2) ? null : rows.GetString(2)));

        if (suggestion.CalendarUid is not null && matches.Any(match => match.CalendarUid == suggestion.CalendarUid))
            matches.RemoveAll(match => match.CalendarUid != suggestion.CalendarUid);

        if (matches.Count > 1 || (matches.Count == 0 && suggestion.Action != MailAction.Add))
            return false;

        if (matches.Count == 0)
        {
            Execute(db, transaction, """
                INSERT INTO schedule(account,title,sender,kind,time,calendar_uid,sequence,mail)
                VALUES($account,$title,$sender,$kind,$time,$ical,$sequence,$mail);
                INSERT INTO todo(title,priority,schedule_id) VALUES($title,2,last_insert_rowid());
                """, ("$account", account), ("$title", suggestion.Title), ("$sender", suggestion.Sender),
                ("$kind", (int)suggestion.Kind), ("$time", suggestion.When!.Value.ToUnixTimeSeconds()),
                ("$ical", suggestion.CalendarUid), ("$sequence", suggestion.Sequence),
                ("$mail", suggestion.Source is null ? null : JsonSerializer.Serialize(suggestion.Source)));
            return true;
        }

        var match = matches[0];
        if (selectedId is null && ((suggestion.Action == MailAction.Add && suggestion.CalendarUid is null) ||
            (match.Seq is int previous && suggestion.Sequence is int incoming &&
                (incoming < previous || (incoming == previous && suggestion.Action != MailAction.Cancel)))))
        {
            if (suggestion.Source is { } source && suggestion.Action == MailAction.Add)
                Execute(db, transaction, "UPDATE schedule SET mail=$mail WHERE id=$id AND time=$time AND mail IS NULL",
                    ("$id", match.Id), ("$time", suggestion.When?.ToUnixTimeSeconds()),
                    ("$mail", JsonSerializer.Serialize(source)));
            return true;
        }

        using (var local = Command(db, transaction, "SELECT local_time,local_title,series_id FROM schedule WHERE id=$id", ("$id", match.Id)))
        using (var row = local.ExecuteReader())
            if (row.Read() && selectedId is null && (!row.IsDBNull(0) || !row.IsDBNull(1) || !row.IsDBNull(2)))
                return false;

        Execute(db, transaction, """
            UPDATE schedule SET time=CASE WHEN $cancelled THEN time ELSE coalesce($time,time) END,
              sequence=coalesce($sequence,sequence), calendar_uid=coalesce(calendar_uid,$ical),
              mail=coalesce($mail,mail), cancelled=$cancelled,
              local_time=CASE WHEN $confirmed THEN NULL ELSE local_time END,
              edited=CASE WHEN $confirmed THEN 1 ELSE edited END WHERE id=$id
            """, ("$time", suggestion.When?.ToUnixTimeSeconds()), ("$sequence", suggestion.Sequence),
            ("$ical", suggestion.CalendarUid), ("$cancelled", suggestion.Action == MailAction.Cancel), ("$id", match.Id),
            ("$mail", suggestion.Source is null ? null : JsonSerializer.Serialize(suggestion.Source)), ("$confirmed", selectedId.HasValue));
        if (selectedId.HasValue)
        {
            using var seriesCommand = Command(db, transaction, "SELECT series_id,occurrence_day FROM schedule WHERE id=$id", ("$id", match.Id));
            long? seriesId;
            string? day;
            using (var row = seriesCommand.ExecuteReader())
            {
                row.Read();
                seriesId = row.IsDBNull(0) ? null : row.GetInt64(0);
                day = row.IsDBNull(1) ? null : row.GetString(1);
            }
            if (seriesId is { } series && suggestion.Action == MailAction.Cancel && scope == EditScope.ThisAndFollowing)
            {
                StopSeries(db, transaction, series, DayAt(day!).Date, match.Id);
                Execute(db, transaction, """
                    UPDATE schedule SET cancelled=1 WHERE occurrence_day>=$day AND series_id IN(
                      SELECT id FROM repeat_series WHERE coalesce(family_id,id)=(SELECT coalesce(family_id,id) FROM repeat_series WHERE id=$series));
                    """, ("$day", day), ("$series", series));
            }
            else if (seriesId is not null && suggestion.When is { } updated && scope == EditScope.ThisAndFollowing)
            {
                var rule = ReadSeries(db, transaction).Single(item => item.Id == seriesId).Rule;
                var weekdays = rule.Weekdays;
                if (rule.Frequency == RepeatFrequency.Weekly)
                    weekdays = (weekdays & ~RepeatRule.DayBit(DayAt(day!).DayOfWeek)) | RepeatRule.DayBit(updated.DayOfWeek);
                ConfigureRepeat(db, transaction, match.Id, true, rule with { Start = updated.Date, End = null, Weekdays = weekdays }, DayAt(day!).Date);
            }
            Execute(db, transaction, "UPDATE todo SET day=$day,edited=1 WHERE schedule_id=$id AND state=0",
                ("$day", suggestion.When is { } date ? Beijing.DayKey(date) : day), ("$id", match.Id));
        }
        return true;
    }

    public List<Pending> PendingItems()
    {
        using var db = Open();
        using var command = Command(db, null, "SELECT id,suggestion,account FROM pending ORDER BY id");
        using var rows = command.ExecuteReader();
        var result = new List<Pending>();
        while (rows.Read())
            result.Add(new Pending(rows.GetInt64(0), JsonSerializer.Deserialize<Suggestion>(rows.GetString(1))!, rows.GetString(2)));
        return result;
    }

    public List<Schedule> ReviewTargets(long pendingId)
    {
        using var db = Open();
        using var command = Command(db, null, """
            SELECT s.id,coalesce(s.local_title,s.title),s.sender,s.kind,coalesce(s.local_time,s.time),s.cancelled FROM schedule s
            JOIN pending p ON p.account=s.account WHERE p.id=$id AND s.deleted=0 ORDER BY s.cancelled,coalesce(s.local_time,s.time) DESC
            """, ("$id", pendingId));
        using var rows = command.ExecuteReader();
        var result = new List<Schedule>();
        while (rows.Read())
            result.Add(new Schedule(rows.GetInt64(0), rows.GetString(1), rows.GetString(2),
                (EventKind)rows.GetInt32(3), FromUnix(rows.GetInt64(4)), rows.GetBoolean(5)));
        return result;
    }

    public bool Resolve(long id, DateTimeOffset? when, long? targetId, bool ignore, EditScope scope = EditScope.ThisOccurrence)
    {
        using var db = Open();
        using var transaction = db.BeginTransaction();
        if (!ignore)
        {
            string account;
            Suggestion suggestion;
            using (var command = Command(db, transaction, "SELECT account,suggestion FROM pending WHERE id=$id", ("$id", id)))
            using (var row = command.ExecuteReader())
            {
                if (!row.Read())
                    return false;
                account = row.GetString(0);
                suggestion = JsonSerializer.Deserialize<Suggestion>(row.GetString(1))!;
            }

            if (!Apply(db, transaction, account, suggestion with { When = when }, targetId, scope))
                return false;
        }
        Execute(db, transaction, "DELETE FROM pending WHERE id=$id", ("$id", id));
        transaction.Commit();
        return true;
    }

    public List<Reminder> Due(DateTimeOffset now)
    {
        EnsureOccurrences(now.Date.AddDays(-1), now.Date.AddDays(3), now);
        using var db = Open();
        using var command = Command(db, null, """
            WITH leads(minutes) AS (VALUES (1440),(30))
            SELECT s.id,coalesce(s.local_title,s.title),s.kind,coalesce(s.local_time,s.time),l.minutes FROM schedule s CROSS JOIN leads l
            WHERE NOT s.cancelled AND s.deleted=0 AND coalesce(s.local_time,s.time)>$now AND coalesce(s.local_time,s.time)-l.minutes*60<=$now
              AND (l.minutes=30 OR coalesce(s.local_time,s.time)-1800>$now)
              AND NOT EXISTS (SELECT 1 FROM sent r WHERE r.schedule_id=s.id AND r.time=coalesce(s.local_time,s.time) AND r.lead=l.minutes)
            """, ("$now", now.ToUnixTimeSeconds()));
        using var rows = command.ExecuteReader();
        var result = new List<Reminder>();
        while (rows.Read())
            result.Add(new Reminder(rows.GetInt64(0), rows.GetString(1), (EventKind)rows.GetInt32(2),
                FromUnix(rows.GetInt64(3)), rows.GetInt32(4)));
        return result;
    }

    public void MarkSent(Reminder reminder)
    {
        using var db = Open();
        Execute(db, null, "INSERT OR IGNORE INTO sent VALUES($id,$time,$lead)",
            ("$id", reminder.EventId), ("$time", reminder.When.ToUnixTimeSeconds()), ("$lead", reminder.Lead));
    }

    private static DateTimeOffset FromUnix(long value) =>
        DateTimeOffset.FromUnixTimeSeconds(value).ToOffset(Beijing.Offset);
}

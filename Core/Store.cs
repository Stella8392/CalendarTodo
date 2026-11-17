using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace CalendarTodo;

public sealed class Store
{
    private readonly string connectionString;

    public Store(string path)
    {
        connectionString = new SqliteConnectionStringBuilder { DataSource = path, ForeignKeys = true }.ToString();
        using var db = Open();
        using var command = Command(db, null, """
            PRAGMA journal_mode = WAL;
            CREATE TABLE IF NOT EXISTS setting (key TEXT PRIMARY KEY, value TEXT NOT NULL);
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
        using var migration = db.BeginTransaction();
        using var version = Command(db, migration, "PRAGMA user_version");
        if (Convert.ToInt64(version.ExecuteScalar()) < 1)
        {
            Execute(db, migration, """
                ALTER TABLE schedule ADD COLUMN mail TEXT;
                ALTER TABLE todo ADD COLUMN day TEXT;
                UPDATE todo SET day=$today WHERE schedule_id IS NULL;
                PRAGMA user_version=1;
                """, ("$today", Beijing.DayKey(Beijing.Now)));
        }
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
            SELECT id,title,sender,kind,time,cancelled,account,mail FROM schedule ORDER BY time,id
            """);
        using var rows = command.ExecuteReader();
        var result = new List<Schedule>();
        while (rows.Read())
            result.Add(new Schedule(rows.GetInt64(0), rows.GetString(1), rows.GetString(2),
                (EventKind)rows.GetInt32(3), FromUnix(rows.GetInt64(4)), rows.GetBoolean(5),
                rows.GetString(6), rows.IsDBNull(7) ? null : JsonSerializer.Deserialize<MailSource>(rows.GetString(7))));
        return result;
    }

    public List<Todo> Todos(DateTimeOffset? day = null)
    {
        var midnight = day is { } date ? Beijing.At(date.ToOffset(Beijing.Offset).Date) : (DateTimeOffset?)null;
        using var db = Open();
        using var command = Command(db, null, """
            SELECT t.id,coalesce(s.title,t.title),t.priority,t.done,t.schedule_id,s.time,
              coalesce(s.cancelled,0), coalesce(s.account<>'',0)
            FROM todo t LEFT JOIN schedule s ON t.schedule_id=s.id
            WHERE $day IS NULL OR (s.id IS NULL AND t.day=$day) OR (s.time >= $start AND s.time < $end)
            ORDER BY t.done, coalesce(s.cancelled,0),
              CASE WHEN t.done=0 THEN t.priority END DESC,
              CASE WHEN t.done=0 THEN coalesce(s.time,9223372036854775807) END,
              t.completed DESC, t.id
            """, ("$day", midnight is { } local ? Beijing.DayKey(local) : null),
            ("$start", midnight?.ToUnixTimeSeconds()), ("$end", midnight?.AddDays(1).ToUnixTimeSeconds()));
        using var rows = command.ExecuteReader();
        var result = new List<Todo>();
        while (rows.Read())
            result.Add(new Todo(rows.GetInt64(0), rows.GetString(1), (Priority)rows.GetInt32(2),
                rows.GetBoolean(3), rows.IsDBNull(4) ? null : rows.GetInt64(4),
                rows.IsDBNull(5) ? null : FromUnix(rows.GetInt64(5)), rows.GetBoolean(6), rows.GetBoolean(7)));
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
        Execute(db, null, "UPDATE todo SET done=$done,completed=$completed WHERE id=$id",
            ("$id", id), ("$done", done), ("$completed", done ? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() : null));
    }

    public void SetPriority(long id, Priority priority)
    {
        using var db = Open();
        Execute(db, null, "UPDATE todo SET priority=$priority WHERE id=$id",
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
        string account, Suggestion suggestion, long? selectedId)
    {
        if ((suggestion.Action != MailAction.Cancel && suggestion.When is null) ||
            (suggestion.Action != MailAction.Add && selectedId is null && string.IsNullOrWhiteSpace(suggestion.Sender)))
            return false;

        var matches = new List<(long Id, int? Seq, string? CalendarUid)>();
        using (var command = Command(db, transaction, """
            SELECT id,sequence,calendar_uid FROM schedule WHERE account=$account AND
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

        Execute(db, transaction, """
            UPDATE schedule SET time=CASE WHEN $cancelled THEN time ELSE coalesce($time,time) END,
              sequence=coalesce($sequence,sequence), calendar_uid=coalesce(calendar_uid,$ical),
              mail=coalesce($mail,mail), cancelled=$cancelled WHERE id=$id
            """, ("$time", suggestion.When?.ToUnixTimeSeconds()), ("$sequence", suggestion.Sequence),
            ("$ical", suggestion.CalendarUid), ("$cancelled", suggestion.Action == MailAction.Cancel), ("$id", match.Id),
            ("$mail", suggestion.Source is null ? null : JsonSerializer.Serialize(suggestion.Source)));
        return true;
    }

    public List<Pending> PendingItems()
    {
        using var db = Open();
        using var command = Command(db, null, "SELECT id,suggestion FROM pending ORDER BY id");
        using var rows = command.ExecuteReader();
        var result = new List<Pending>();
        while (rows.Read())
            result.Add(new Pending(rows.GetInt64(0), JsonSerializer.Deserialize<Suggestion>(rows.GetString(1))!));
        return result;
    }

    public List<Schedule> ReviewTargets(long pendingId)
    {
        using var db = Open();
        using var command = Command(db, null, """
            SELECT s.id,s.title,s.sender,s.kind,s.time,s.cancelled FROM schedule s
            JOIN pending p ON p.account=s.account WHERE p.id=$id ORDER BY s.cancelled,s.time DESC
            """, ("$id", pendingId));
        using var rows = command.ExecuteReader();
        var result = new List<Schedule>();
        while (rows.Read())
            result.Add(new Schedule(rows.GetInt64(0), rows.GetString(1), rows.GetString(2),
                (EventKind)rows.GetInt32(3), FromUnix(rows.GetInt64(4)), rows.GetBoolean(5)));
        return result;
    }

    public bool Resolve(long id, DateTimeOffset? when, long? targetId, bool ignore)
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

            if (!Apply(db, transaction, account, suggestion with { When = when }, targetId))
                return false;
        }
        Execute(db, transaction, "DELETE FROM pending WHERE id=$id", ("$id", id));
        transaction.Commit();
        return true;
    }

    public List<Reminder> Due(DateTimeOffset now)
    {
        using var db = Open();
        using var command = Command(db, null, """
            WITH leads(minutes) AS (VALUES (1440),(30))
            SELECT s.id,s.title,s.kind,s.time,l.minutes FROM schedule s CROSS JOIN leads l
            WHERE NOT s.cancelled AND s.time>$now AND s.time-l.minutes*60<=$now
              AND (l.minutes=30 OR s.time-1800>$now)
              AND NOT EXISTS (SELECT 1 FROM sent r WHERE r.schedule_id=s.id AND r.time=s.time AND r.lead=l.minutes)
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

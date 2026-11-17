using System.Globalization;
using System.Text.RegularExpressions;
using HtmlAgilityPack;
using MimeKit;

namespace CalendarTodo;

public static partial class MailParser
{
    private const string ExamWords = "笔试|在线测评|written test|assessment|coding test";
    private const string InterviewWords = "面试|[一二三四初终]面|interview";
    private const string DeadlineWords = "截止|最后提交|deadline|due by";
    private const string CancelWords = "取消|撤销|cancel(?:led|ed|lation)?";
    private const string ChangeWords = "改期|改约|调期|调整|变更|更新|延期|提前|延长|推迟|reschedul\\w*|postpon\\w*";

    public static bool IsScheduleSubject(string? subject) => Classify(subject ?? "") is not null;
    public static string ReadableBody(MimeMessage mail) => VisibleBody(mail, forParsing: false);

    public static List<Suggestion> Parse(MimeMessage mail, DateTimeOffset received)
    {
        var subject = Limit(Whitespace().Replace(mail.Subject ?? "", " ").Trim(), 200);
        var sender = mail.From.Mailboxes.FirstOrDefault()?.Address.ToLowerInvariant() ?? "";
        var body = VisibleBody(mail);
        var snippet = Limit(Whitespace().Replace(body, " ").Trim(), 280);
        var result = new List<Suggestion>();
        var calendarError = false;

        foreach (var part in mail.BodyParts.OfType<MimePart>().Where(p =>
            p.ContentType.IsMimeType("text", "calendar") ||
            (p.FileName?.EndsWith(".ics", StringComparison.OrdinalIgnoreCase) ?? false)))
        {
            try
            {
                using var bytes = new MemoryStream();
                if (part.Content is null)
                    continue;
                part.Content.DecodeTo(bytes);
                if (bytes.Length > 256_000)
                    continue;
                bytes.Position = 0;
                using var reader = new StreamReader(bytes);
                var calendar = Ical.Net.Calendar.Load(reader.ReadToEnd());
                if (calendar is null)
                    continue;
                var method = calendar.Method?.ToUpperInvariant();
                if (method is "REPLY" or "COUNTER" or "DECLINECOUNTER" or "REFRESH")
                    continue;

                foreach (var item in calendar.Events)
                {
                    var summary = Limit(item.Summary ?? subject, 200);
                    var kind = Classify(summary) ?? Classify(subject);
                    if (kind is null)
                        continue;
                    var action = method == "CANCEL" || string.Equals(item.Status, "CANCELLED", StringComparison.OrdinalIgnoreCase)
                        ? MailAction.Cancel : Action(summary + " " + subject);
                    DateTimeOffset? when = null;
                    if (item.DtStart is { HasTime: true } start)
                        when = start.TzId is null ? Beijing.At(start.Value) : new DateTimeOffset(start.AsUtc).ToOffset(Beijing.Offset);
                    if (when is null && action != MailAction.Cancel)
                        calendarError = true;
                    if (item.RecurrenceIdentifier is not null || item.RecurrenceRule is not null)
                        continue;
                    result.Add(new Suggestion(Title(summary), sender, kind.Value, action, when,
                        string.IsNullOrWhiteSpace(item.Uid) ? null : item.Uid, item.Sequence, snippet, Reason(action, when)));
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // Malformed or unsupported calendar data still allows the plain-text path.
                calendarError = true;
            }
        }

        if (result.Count != 0 && !calendarError)
            return result.Distinct().ToList();

        var textKind = Classify(subject);
        if (textKind is null)
            return result;

        var textAction = Action(subject);
        if (textAction == MailAction.Add)
        {
            var heading = body.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault(line =>
                Classify(line) is not null && line.Length <= 120);
            if (heading is not null)
                textAction = Action(heading);
        }

        if (textKind == EventKind.Exam && Match(body, DeadlineWords) &&
            (!Match(body, @"面试时间|笔试时间|开始时间|开考|start time") || textAction != MailAction.Add))
            textKind = EventKind.Deadline;
        var time = textAction == MailAction.Cancel ? null : FindTime(subject, body, received, textKind.Value, textAction);
        if (result.Count != 0)
        {
            if (result.Count == 1 && result[0].When is null && time is not null)
                result[0] = result[0] with { When = time, Reason = Reason(result[0].Action, time) };
            return result;
        }

        result.Add(new Suggestion(Title(subject), sender, textKind.Value, textAction, time, null, null, snippet, Reason(textAction, time)));
        if (textKind == EventKind.Exam && textAction == MailAction.Add && Match(body, DeadlineWords))
        {
            var deadline = FindTime(subject, body, received, EventKind.Deadline, textAction);
            if (deadline.HasValue && deadline != time)
                result.Add(new Suggestion(Title(subject), sender, EventKind.Deadline, textAction, deadline, null, null, snippet, ""));
        }
        return result;
    }

    private static EventKind? Classify(string text)
    {
        if (Match(text, ExamWords))
            return Match(text, DeadlineWords) ? EventKind.Deadline : EventKind.Exam;
        if (Match(text, InterviewWords))
            return EventKind.Interview;
        return null;
    }

    private static MailAction Action(string text)
    {
        if (Match(text, CancelWords))
            return MailAction.Cancel;
        return Match(text, ChangeWords) ? MailAction.Reschedule : MailAction.Add;
    }

    private static bool Match(string text, string pattern) =>
        Regex.IsMatch(text, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static string Reason(MailAction action, DateTimeOffset? time) =>
        time is null && action != MailAction.Cancel ? "邮件缺少明确的日期和时间"
            : action == MailAction.Add ? "请确认日程" : "请选择对应的原日程";

    private static string Title(string subject)
    {
        var clean = Regex.Replace(subject, @"^(?:(?:re|fw|fwd)\s*[:：]\s*)+", "", RegexOptions.IgnoreCase);
        clean = Regex.Replace(clean, "通知|邀请|" + CancelWords + "|" + ChangeWords, "", RegexOptions.IgnoreCase);
        clean = clean.Trim(' ', ':', '：', '【', '】', '[', ']', '-', '—', '（', '）');
        return string.IsNullOrWhiteSpace(clean) ? "招聘日程" : clean;
    }

    private static string VisibleBody(MimeMessage mail, bool forParsing = true)
    {
        var text = mail.TextBody;
        if (string.IsNullOrWhiteSpace(text) && mail.HtmlBody is { } html)
        {
            var doc = new HtmlDocument();
            doc.LoadHtml(Limit(html, 256_000));
            foreach (var node in doc.DocumentNode.SelectNodes("//script|//style|//blockquote|//head") ?? Enumerable.Empty<HtmlNode>())
                node.Remove();
            foreach (var node in doc.DocumentNode.SelectNodes("//br|//p|//div|//tr|//li") ?? Enumerable.Empty<HtmlNode>())
                node.AppendChild(doc.CreateTextNode("\n"));
            text = HtmlEntity.DeEntitize(doc.DocumentNode.InnerText);
        }
        if (!forParsing)
            return Limit(text ?? "", 256_000);
        return string.Join('\n', Limit(text ?? "", 40_000).Replace("\r", "").Split('\n')
            .TakeWhile(line => !Match(line.Trim(), @"^[- ]*(?:原始邮件|original message|forwarded message)"))
            .Where(line => !line.TrimStart().StartsWith('>')));
    }

    private static DateTimeOffset? FindTime(string subject, string body, DateTimeOffset received,
        EventKind kind, MailAction action)
    {
        var lines = (subject + "\n" + body).Replace('：', ':').Split('\n');
        var candidates = new List<(DateTimeOffset When, int Rank)>();
        foreach (var line in lines)
        {
            var rank = 0;
            if (kind == EventKind.Deadline)
            {
                if (Match(line, DeadlineWords))
                    rank = 2;
                else if (Match(body, DeadlineWords) && !Match(line, @"(?:最新|新)时间|改为"))
                    continue;
            }
            if (kind != EventKind.Deadline && Match(line, @"面试时间|笔试时间|开始时间|开考|start(?: time)?"))
                rank = 2;
            if (kind != EventKind.Deadline && Match(line, DeadlineWords))
                continue;
            var newTime = Match(line, @"改为|调整(?:到|为)|变更为|最新时间|新时间|延期至|推迟至|提前至|延长至|rescheduled to|new time");
            if (action == MailAction.Reschedule && newTime)
                rank = 3;
            if (action == MailAction.Reschedule && !newTime && Match(line, @"原时间|原定|original(?:ly)?|previous"))
                continue;

            var normalized = Whitespace().Replace(line, " ").Trim();
            try
            {
                foreach (Match match in DateTimePattern().Matches(normalized))
                    if (ParseDate(match, normalized, received) is { } date)
                        candidates.Add((date, rank));
            }
            catch (RegexMatchTimeoutException)
            {
                return null;
            }
        }

        var rankMax = candidates.Count == 0 ? 0 : candidates.Max(c => c.Rank);
        var dates = candidates.Where(c => c.Rank == rankMax).Select(c => c.When).Distinct().Take(2).ToArray();
        return dates.Length == 1 ? dates[0] : null;
    }

    private static DateTimeOffset? ParseDate(Match match, string line, DateTimeOffset received)
    {
        int Part(string name) => int.TryParse(match.Groups[name].Value, out var value) ? value : 0;
        var year = Part("year");
        var receivedInBeijing = received.ToOffset(Beijing.Offset);
        if (year == 0)
            year = receivedInBeijing.Year;
        var hour = Part("hour");
        var half = match.Groups["half"].Value;
        if (half is "下午" or "晚上" or "中午" || half.Equals("pm", StringComparison.OrdinalIgnoreCase))
        {
            if (hour < 12)
                hour += 12;
        }
        else if ((half is "上午" or "凌晨" || half.Equals("am", StringComparison.OrdinalIgnoreCase)) && hour == 12)
            hour = 0;

        try
        {
            var day = new DateTime(year, Part("month"), Part("day"), hour, Part("minute"), 0);
            // A month/day just after New Year can belong to the next year.
            if (Part("year") == 0 && day < receivedInBeijing.DateTime.AddMonths(-6))
                day = day.AddYears(1);
            var suffix = line[(match.Index + match.Length)..];
            var zone = TimeZonePattern().Match(suffix);
            if (!zone.Success && match.Value.Contains('T'))
                zone = IsoOffset().Match(suffix);
            if (!zone.Success)
                zone = TimeZonePattern().Match(line[..match.Index]);
            if (!zone.Success)
                return Match(line, @"\b(?:PST|PDT|EST|EDT|CST|CDT|MST|MDT|CET|CEST|IST|BST)\b|美东|美西|欧洲中部")
                    ? null : Beijing.At(day);
            if (zone.Value is "北京时间" or "中国标准时间")
                return Beijing.At(day);
            var offset = TimeSpan.Zero;
            if (zone.Groups["sign"].Success)
            {
                offset = TimeSpan.FromHours(int.Parse(zone.Groups["zhour"].Value, CultureInfo.InvariantCulture));
                if (int.TryParse(zone.Groups["zminute"].Value, out var minutes))
                {
                    if (minutes > 59)
                        return null;
                    offset += TimeSpan.FromMinutes(minutes);
                }
                if (zone.Groups["sign"].Value == "-")
                    offset = -offset;
            }
            return new DateTimeOffset(day, offset).ToOffset(Beijing.Offset);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static string Limit(string value, int length) => value.Length <= length ? value : value[..length];

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    [GeneratedRegex(@"(?<!\d)(?:(?<year>20\d{2})\s*[-/.年]\s*)?(?<month>\d{1,2})\s*[-/.月]\s*(?<day>\d{1,2})(?:日|号)?\s*(?:[（(][^)）]{1,12}[）)]|(?:周|星期)[一二三四五六日天])?\s*T?\s*(?<half>上午|下午|中午|晚上|凌晨)?\s*(?<hour>\d{1,2})(?::(?<minute>\d{2})(?::\d{2})?|[点时](?:(?<minute>\d{1,2})分?)?)\s*(?(half)|(?<half>am|pm)?)(?!\d)", RegexOptions.IgnoreCase, 250)]
    private static partial Regex DateTimePattern();

    [GeneratedRegex(@"北京时间|中国标准时间|\b(?:UTC|GMT)\s*(?<sign>[+-])(?<zhour>\d{1,2})(?::?(?<zminute>\d{2}))?\b|\b(?:UTC|GMT)\b|^Z\b", RegexOptions.IgnoreCase)]
    private static partial Regex TimeZonePattern();

    [GeneratedRegex(@"^(?<sign>[+-])(?<zhour>\d{2}):?(?<zminute>\d{2})\b")]
    private static partial Regex IsoOffset();
}

namespace CalendarTodo;

public sealed record RepeatRule(RepeatFrequency Frequency, int Weekdays, DateTime Start, DateTime? End = null)
{
    public static int DayBit(DayOfWeek day) => 1 << (((int)day + 6) % 7);
    public bool Includes(DateTime date) => date.Date >= Start.Date &&
        (End is null || date.Date <= End.Value.Date) && (Frequency switch
        {
            RepeatFrequency.Daily => true,
            RepeatFrequency.Weekly => (Weekdays & DayBit(date.DayOfWeek)) != 0,
            _ => false
        });

    public IEnumerable<DateTime> Dates(DateTime from, DateTime until)
    {
        for (var day = from.Date > Start.Date ? from.Date : Start.Date;
             day < until.Date && (End is null || day <= End.Value.Date); day = day.AddDays(1))
            if (Includes(day)) yield return day;
    }

    public DateTime? Latest(DateTime today)
    {
        var day = End is { } end && end.Date < today.Date ? end.Date : today.Date;
        for (var offset = 0; offset < 7 && day >= Start.Date; offset++, day = day.AddDays(-1))
            if (Includes(day)) return day;
        return null;
    }

    public string Description => Frequency switch
    {
        RepeatFrequency.Daily => "每天",
        RepeatFrequency.Weekly => "每周" + string.Join("、", Enumerable.Range(0, 7)
            .Where(index => (Weekdays & (1 << index)) != 0).Select(index => "一二三四五六日"[index])),
        _ => "不重复"
    };

    public void Validate()
    {
        if (!Enum.IsDefined(Frequency) || (Frequency == RepeatFrequency.Weekly && (Weekdays & 127) == 0) ||
            (Weekdays & ~127) != 0 || (End is { } end && end.Date < Start.Date))
            throw new ArgumentException("请选择有效的重复频率、星期和生效日期。");
    }
}

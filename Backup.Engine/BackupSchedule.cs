namespace Backup.Engine;

public enum ScheduleFrequency { Daily, Weekly, Monthly }

public sealed record BackupSchedule
{
    public ScheduleFrequency Frequency { get; init; } = ScheduleFrequency.Daily;
    public DateOnly StartDate { get; init; } = DateOnly.FromDateTime(DateTime.Today);
    public int Interval { get; init; } = 1;
    public string[] Times { get; init; } = ["02:00"];
    public DayOfWeek[] WeekDays { get; init; } = [DayOfWeek.Monday];
    public int[] MonthDays { get; init; } = [1];
    public bool LastDayOfMonth { get; init; }
    public int[] Months { get; init; } = Enumerable.Range(1, 12).ToArray();

    public BackupSchedule Validate()
    {
        if (!Enum.IsDefined(Frequency) || Interval < 1 || Interval > (Frequency == ScheduleFrequency.Weekly ? 52 : 365) || Frequency == ScheduleFrequency.Monthly && Interval != 1)
            throw new ArgumentException("スケジュールの繰り返し間隔が範囲外です。");
        if (Times is null || Times.Length is < 1 or > 24 || Times.Any(t => !TimeOnly.TryParseExact(t, "HH:mm", out _)))
            throw new ArgumentException("実行時刻は HH:mm 形式で1〜24個指定してください。");
        if (WeekDays is null || WeekDays.Any(d => !Enum.IsDefined(d)) || (Frequency == ScheduleFrequency.Weekly && WeekDays.Length == 0))
            throw new ArgumentException("毎週実行する曜日を1つ以上選択してください。");
        if (MonthDays is null || Months is null || MonthDays.Any(d => d is < 1 or > 31) || Months.Any(m => m is < 1 or > 12) ||
            (Frequency == ScheduleFrequency.Monthly && (Months.Length == 0 || (!LastDayOfMonth && !MonthDays.Any(d => Months.Any(m => d <= DateTime.DaysInMonth(2000, m)))))))
            throw new ArgumentException("毎月実行する月と有効な日付（または月末）を指定してください。");
        return this with { Times = Times.Distinct().Order().ToArray(), WeekDays = WeekDays.Distinct().Order().ToArray(), MonthDays = MonthDays.Distinct().Order().ToArray(), Months = Months.Distinct().Order().ToArray() };
    }

    public DateTime? NextOccurrence(DateTime after)
    {
        var validated = Validate();
        var first = DateOnly.FromDateTime(after) > StartDate ? DateOnly.FromDateTime(after) : StartDate;
        for (var offset = 0; offset < 366 * 8; offset++)
        {
            var date = first.AddDays(offset);
            if (!Matches(date)) continue;
            foreach (var time in validated.Times)
            {
                var next = date.ToDateTime(TimeOnly.ParseExact(time, "HH:mm"));
                if (next > after) return next;
            }
        }
        return null;
    }

    private bool Matches(DateOnly date) => Frequency switch
    {
        ScheduleFrequency.Daily => (date.DayNumber - StartDate.DayNumber) % Interval == 0,
        // Task Scheduler weekly intervals are anchored to the Sunday of StartBoundary's week.
        ScheduleFrequency.Weekly => ((date.DayNumber - (int)date.DayOfWeek - (StartDate.DayNumber - (int)StartDate.DayOfWeek)) / 7) % Interval == 0 && WeekDays.Contains(date.DayOfWeek),
        ScheduleFrequency.Monthly => Months.Contains(date.Month) && (MonthDays.Contains(date.Day) || LastDayOfMonth && date.Day == DateTime.DaysInMonth(date.Year, date.Month)),
        _ => false
    };
}

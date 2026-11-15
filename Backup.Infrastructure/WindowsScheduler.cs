using System.Runtime.InteropServices;
using System.Security.Principal;
using Backup.Engine;

namespace Backup.Infrastructure;

public static class WindowsScheduler
{
    public static string FindCli()
    {
        foreach (var path in new[] { Path.Combine(AppContext.BaseDirectory, "Backup.Cli.exe"), Path.Combine(AppContext.BaseDirectory, "cli", "Backup.Cli.exe") })
            if (File.Exists(path)) return path;
        throw new FileNotFoundException("Backup.Cli.exeがありません。scripts/publish.ps1で配布用フォルダを作成してください。");
    }

    public static void Register(BackupTask task, string dataDirectory, string cliPath, string sevenZipPath, string? password = null)
    {
        task = task.Validate();
        if (!File.Exists(cliPath)) throw new FileNotFoundException("CLIの実行ファイルがありません。", cliPath);
        var type = Type.GetTypeFromProgID("Schedule.Service") ?? throw new PlatformNotSupportedException("タスクスケジューラが利用できません。");
        dynamic service = Activator.CreateInstance(type)!;
        try
        {
            service.Connect();
            dynamic folder = service.GetFolder(@"\");
            var name = "BackupTool-" + task.Id;
            if (task.EffectiveSchedule is null)
            {
                try { folder.DeleteTask(name, 0); } catch (COMException ex) when ((uint)ex.HResult == 0x80070002) { }
                return;
            }
            dynamic definition = service.NewTask(0);
            definition.RegistrationInfo.Description = $"BackupTool: {task.Name}";
            definition.Settings.Enabled = true;
            definition.Settings.StartWhenAvailable = true;
            definition.Settings.MultipleInstances = 2; // IgnoreNew
            definition.Settings.DisallowStartIfOnBatteries = false;
            definition.Settings.StopIfGoingOnBatteries = false;
            definition.Settings.ExecutionTimeLimit = "PT0S";
            var user = WindowsIdentity.GetCurrent().Name;
            var logonType = password is null ? 3 : 1; // InteractiveToken / Password (network access)
            definition.Principal.UserId = user;
            definition.Principal.LogonType = logonType;
            AddTriggers(definition, task.EffectiveSchedule);
            dynamic action = definition.Actions.Create(0);
            action.Path = Path.GetFullPath(cliPath);
            action.Arguments = $"run --task-id {task.Id} --data {Quote(dataDirectory)} --7zip {Quote(sevenZipPath)}";
            action.WorkingDirectory = Path.GetDirectoryName(cliPath);
            folder.RegisterTaskDefinition(name, definition, 6, user, password, logonType, null);
        }
        finally { Marshal.FinalReleaseComObject(service); }
    }

    // Also used by the integration tests to inspect definitions without registering OS tasks.
    public static void AddTriggers(dynamic definition, BackupSchedule schedule)
    {
        schedule = schedule.Validate();
        foreach (var time in schedule.Times)
        {
            dynamic trigger = definition.Triggers.Create(schedule.Frequency switch { ScheduleFrequency.Daily => 2, ScheduleFrequency.Weekly => 3, ScheduleFrequency.Monthly => 4, _ => throw new ArgumentException("スケジュールの種類が不正です。") });
            trigger.StartBoundary = schedule.StartDate.ToDateTime(TimeOnly.ParseExact(time, "HH:mm")).ToString("yyyy-MM-ddTHH:mm:ss");
            switch (schedule.Frequency)
            {
                case ScheduleFrequency.Daily: trigger.DaysInterval = schedule.Interval; break;
                case ScheduleFrequency.Weekly:
                    trigger.WeeksInterval = schedule.Interval;
                    trigger.DaysOfWeek = (short)schedule.WeekDays.Aggregate(0, (bits, day) => bits | 1 << (int)day);
                    break;
                case ScheduleFrequency.Monthly:
                    trigger.MonthsOfYear = (short)schedule.Months.Aggregate(0, (bits, month) => bits | 1 << (month - 1));
                    trigger.DaysOfMonth = schedule.MonthDays.Aggregate(0, (bits, day) => bits | 1 << (day - 1));
                    trigger.RunOnLastDayOfMonth = schedule.LastDayOfMonth;
                    break;
            }
        }
    }

    private static string Quote(string value)
    {
        if (value.Contains('"')) throw new ArgumentException("パスに引用符は使用できません。");
        return "\"" + value.TrimEnd('\\') + "\"";
    }
}

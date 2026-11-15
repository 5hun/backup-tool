using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using Backup.Engine;
using Backup.Infrastructure;
using Microsoft.Win32;

namespace Backup.Gui;

public partial class MainWindow : Window
{
    private readonly BackupApplication application = new();
    private BackupTask editing = new();
    private CancellationTokenSource? cancellation;
    private List<HistoryRow> history = [];
    private bool initialized;
    public MainWindow()
    {
        InitializeComponent();
        initialized = true;
        LoadEditor(editing);
        Refresh();
    }

    private void Refresh()
    {
        var tasks = application.Store.GetTasks(); var runs = application.Store.GetRuns();
        TasksGrid.ItemsSource = tasks.Select(t => new TaskRow(t, runs.FirstOrDefault(r => r.TaskId == t.Id))).ToList();
        history = runs.Select(r => new HistoryRow(r, tasks.FirstOrDefault(t => t.Id == r.TaskId)?.Name ?? r.TaskId)).ToList();
        ApplyFilter();
    }
    private void ApplyFilter()
    {
        var filter = FilterBox.Text.Trim();
        HistoryGrid.ItemsSource = history.Where(r => $"{r.Name} {r.Started:yyyy/MM/dd HH:mm:ss} {r.Status} {r.Error}".Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();
    }
    private void Filter_Changed(object sender, TextChangedEventArgs e) { if (initialized) ApplyFilter(); }
    private void Refresh_Click(object sender, RoutedEventArgs e) => Guard(Refresh);
    private void New_Click(object sender, RoutedEventArgs e) { if (cancellation is null) { TasksGrid.SelectedItem = null; LoadEditor(new()); } }
    private void Tasks_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (cancellation is null && TasksGrid.SelectedItem is TaskRow row) LoadEditor(row.Task);
    }
    private void LoadEditor(BackupTask task)
    {
        editing = task;
        NameBox.Text = task.Name; SourceBox.Text = task.Source; DestinationBox.Text = task.Destination;
        DepthBox.Text = task.UnitDepth.ToString();
        UncompressedBox.Text = string.Join(Environment.NewLine, task.UncompressedFolders);
        LegacyModeText.Visibility = task.Mode == BackupMode.Copy ? Visibility.Visible : Visibility.Collapsed;
        UncompressedBox.IsEnabled = task.Mode != BackupMode.Copy;
        ParallelBox.Text = task.ParallelUnits.ToString(); ThreadsBox.Text = task.CopyThreads.ToString();
        FileRetriesBox.Text = task.FileRetries.ToString(); FileWaitBox.Text = task.FileWaitSeconds.ToString();
        NetworkRetriesBox.Text = task.NetworkRetries.ToString(); NetworkWaitBox.Text = task.NetworkWaitSeconds.ToString();
        RetentionBox.Text = task.RetentionDays.ToString(); CompressionBox.Text = task.CompressionLevel.ToString();
        var schedule = task.EffectiveSchedule ?? new BackupSchedule();
        FrequencyBox.SelectedIndex = task.EffectiveSchedule is null ? 0 : (int)schedule.Frequency + 1;
        StartDatePicker.SelectedDate = schedule.StartDate.ToDateTime(TimeOnly.MinValue);
        TimesBox.Text = string.Join(", ", schedule.Times); IntervalBox.Text = schedule.Interval.ToString();
        foreach (var (day, box) in DayBoxes()) box.IsChecked = schedule.WeekDays.Contains(day);
        MonthDaysBox.Text = string.Join(",", schedule.MonthDays); MonthsBox.Text = string.Join(",", schedule.Months);
        LastDayBox.IsChecked = schedule.LastDayOfMonth;
        UpdateScheduleFields(); PasswordBox.Clear();
    }
    private BackupTask Save()
    {
        var updated = (editing with
        {
            Name = NameBox.Text.Trim(), Source = SourceBox.Text.Trim(), Destination = DestinationBox.Text.Trim(),
            UnitDepth = int.Parse(DepthBox.Text),
            UncompressedFolders = UncompressedBox.Text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            ParallelUnits = int.Parse(ParallelBox.Text), CopyThreads = int.Parse(ThreadsBox.Text),
            FileRetries = int.Parse(FileRetriesBox.Text), FileWaitSeconds = int.Parse(FileWaitBox.Text),
            NetworkRetries = int.Parse(NetworkRetriesBox.Text), NetworkWaitSeconds = int.Parse(NetworkWaitBox.Text),
            RetentionDays = int.Parse(RetentionBox.Text), CompressionLevel = int.Parse(CompressionBox.Text),
            DailyAt = null, Schedule = ReadSchedule()
        }).Validate();
        application.Store.SaveTask(updated); editing = updated; Refresh(); return updated;
    }
    private void Save_Click(object sender, RoutedEventArgs e) => Guard(() => { Save(); StatusText.Text = "設定を保存しました。時刻を変更した場合はスケジュールも反映してください。"; });
    private void Schedule_Click(object sender, RoutedEventArgs e) => Guard(() =>
    {
        var task = Save();
        if (task.EffectiveSchedule is not null && UnattendedBox.IsChecked == true && PasswordBox.Password.Length == 0) throw new ArgumentException("Windowsアカウントのパスワードを入力してください（PINではありません）。");
        try
        {
            WindowsScheduler.Register(task, application.Store.DataDirectory, WindowsScheduler.FindCli(), application.SevenZipPath,
                UnattendedBox.IsChecked == true ? PasswordBox.Password : null);
            StatusText.Text = "Windowsタスクスケジューラに設定を反映しました。";
        }
        finally { PasswordBox.Clear(); }
    });
    private async void Run_Click(object sender, RoutedEventArgs e)
    {
        if (cancellation is not null) return;
        try
        {
            var task = Save();
            cancellation = new(); Editor.IsEnabled = false; SaveButton.IsEnabled = false; ScheduleButton.IsEnabled = false; RunButton.IsEnabled = false; CancelButton.IsEnabled = true;
            var progress = new Progress<RunProgress>(p => { StatusText.Text = $"{p.Completed}/{p.Total} {p.Message}"; ProgressBar.Maximum = Math.Max(1, p.Total); ProgressBar.Value = p.Completed; });
            var token = cancellation.Token;
            var run = await Task.Run(() => application.CreateRunner().RunAsync(task.Id, progress, token));
            StatusText.Text = $"{StatusLabel(run.Status)} — 完了 {run.Completed} / 失敗 {run.Failed} {run.Error}";
        }
        catch (Exception ex) { ShowError(ex); }
        finally { cancellation?.Dispose(); cancellation = null; Editor.IsEnabled = true; SaveButton.IsEnabled = true; ScheduleButton.IsEnabled = true; RunButton.IsEnabled = true; CancelButton.IsEnabled = false; Guard(Refresh); }
    }
    private void Cancel_Click(object sender, RoutedEventArgs e) { cancellation?.Cancel(); StatusText.Text = "中断処理中…"; }
    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (cancellation is not null) { e.Cancel = true; cancellation.Cancel(); StatusText.Text = "中断処理後に画面を閉じてください。"; }
    }
    private void History_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (HistoryGrid.SelectedItem is HistoryRow row) Guard(() => UnitsGrid.ItemsSource = application.Store.GetUnits(row.Run.Id));
    }
    private void SourceBrowse_Click(object sender, RoutedEventArgs e) => Browse(SourceBox);
    private void DestinationBrowse_Click(object sender, RoutedEventArgs e) => Browse(DestinationBox);
    private void AddUncompressed_Click(object sender, RoutedEventArgs e) => Guard(() =>
    {
        var source = SafePaths.Normalize(SourceBox.Text.Trim());
        var dialog = new OpenFolderDialog { InitialDirectory = source, Multiselect = true };
        if (dialog.ShowDialog(this) != true) return;
        var additions = dialog.FolderNames.Select(path =>
        {
            if (!SafePaths.Contains(source, path) || path.Equals(source, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("バックアップ元の配下にあるフォルダを選択してください。");
            return System.IO.Path.GetRelativePath(source, path);
        }).ToArray();
        UncompressedBox.Text = string.Join(Environment.NewLine, UncompressedBox.Text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Concat(additions).Distinct(StringComparer.OrdinalIgnoreCase));
    });
    private void ClearUncompressed_Click(object sender, RoutedEventArgs e) => UncompressedBox.Clear();
    private (DayOfWeek Day, CheckBox Box)[] DayBoxes() => [(DayOfWeek.Monday, MondayBox), (DayOfWeek.Tuesday, TuesdayBox), (DayOfWeek.Wednesday, WednesdayBox), (DayOfWeek.Thursday, ThursdayBox), (DayOfWeek.Friday, FridayBox), (DayOfWeek.Saturday, SaturdayBox), (DayOfWeek.Sunday, SundayBox)];
    private BackupSchedule? ReadSchedule()
    {
        if (FrequencyBox.SelectedIndex == 0) return null;
        var frequency = (ScheduleFrequency)(FrequencyBox.SelectedIndex - 1);
        static string[] Parts(string text) => text.Split([',', '、', '，', ';', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return new BackupSchedule
        {
            Frequency = frequency,
            StartDate = DateOnly.FromDateTime(StartDatePicker.SelectedDate ?? throw new ArgumentException("スケジュールの開始日を指定してください。")),
            Times = Parts(TimesBox.Text), Interval = frequency == ScheduleFrequency.Monthly ? 1 : int.Parse(IntervalBox.Text),
            WeekDays = DayBoxes().Where(x => x.Box.IsChecked == true).Select(x => x.Day).ToArray(),
            MonthDays = frequency == ScheduleFrequency.Monthly ? Parts(MonthDaysBox.Text).Select(int.Parse).ToArray() : [1],
            Months = frequency == ScheduleFrequency.Monthly ? Parts(MonthsBox.Text).Select(int.Parse).ToArray() : Enumerable.Range(1, 12).ToArray(),
            LastDayOfMonth = LastDayBox.IsChecked == true
        }.Validate();
    }
    private void Frequency_Changed(object sender, SelectionChangedEventArgs e) { if (initialized) UpdateScheduleFields(); }
    private void UpdateScheduleFields()
    {
        ScheduleFields.Visibility = FrequencyBox.SelectedIndex == 0 ? Visibility.Collapsed : Visibility.Visible;
        IntervalPanel.Visibility = FrequencyBox.SelectedIndex is 1 or 2 ? Visibility.Visible : Visibility.Collapsed;
        IntervalLabel.Text = FrequencyBox.SelectedIndex == 2 ? "何週ごとに実行するか" : "何日ごとに実行するか";
        WeekDaysPanel.Visibility = FrequencyBox.SelectedIndex == 2 ? Visibility.Visible : Visibility.Collapsed;
        MonthDaysPanel.Visibility = FrequencyBox.SelectedIndex == 3 ? Visibility.Visible : Visibility.Collapsed;
    }
    private void Browse(TextBox box) { var dialog = new OpenFolderDialog(); if (dialog.ShowDialog(this) == true) box.Text = dialog.FolderName; }
    private void OpenRunLog_Click(object sender, RoutedEventArgs e) { if (HistoryGrid.SelectedItem is HistoryRow row) Guard(() => Open(row.Run.LogPath)); }
    private void OpenUnitLog_Click(object sender, RoutedEventArgs e) { if (UnitsGrid.SelectedItem is UnitResult row) Guard(() => Open(row.LogPath)); }
    private void OpenLogFolder_Click(object sender, RoutedEventArgs e) { if (HistoryGrid.SelectedItem is HistoryRow row) Guard(() => Open(System.IO.Path.GetDirectoryName(row.Run.LogPath)!)); }
    private static void Open(string path) => Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    private void Guard(Action action) { try { action(); } catch (Exception ex) { ShowError(ex); } }
    private void ShowError(Exception exception) { StatusText.Text = exception.Message; MessageBox.Show(this, exception.Message, "Backup Tool", MessageBoxButton.OK, MessageBoxImage.Error); }
    public static string StatusLabel(ResultStatus status) => status switch
    {
        ResultStatus.Running => "実行中", ResultStatus.Success => "成功", ResultStatus.Warning => "警告あり",
        ResultStatus.PartialFailure => "一部失敗", ResultStatus.Failed => "失敗", ResultStatus.Cancelled => "中断",
        ResultStatus.Interrupted => "異常終了", ResultStatus.Skipped => "変更なし", _ => status.ToString()
    };
    private sealed record TaskRow(BackupTask Task, RunRecord? Last)
    {
        public string Name => Task.Name;
        public string Destination => Task.Destination;
        public BackupMode Mode => Task.Mode;
        public string LastStatus => Last is null ? "未実行" : StatusLabel(Last.Status);
        public string LastRun => Last?.Started.ToString("MM/dd HH:mm") ?? "—";
        public string NextRun => Task.EffectiveSchedule?.NextOccurrence(DateTime.Now)?.ToString("MM/dd HH:mm") ?? "なし";
    }
    private sealed record HistoryRow(RunRecord Run, string Name)
    {
        public DateTimeOffset Started => Run.Started;
        public string Status => StatusLabel(Run.Status);
        public string Duration => Run.Finished is { } end ? (end - Run.Started).ToString(@"hh\:mm\:ss") : "実行中";
        public int Completed => Run.Completed;
        public int Failed => Run.Failed;
        public string? Error => Run.Error;
    }
}

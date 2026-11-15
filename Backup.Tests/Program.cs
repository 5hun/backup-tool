using System.IO.Compression;
using System.Security.Cryptography;
using Backup.Engine;
using Backup.Infrastructure;

// Deliberately uses the real Windows executables and a disposable, isolated workspace.
var root = new DirectoryInfo(AppContext.BaseDirectory);
while (root is not null && !File.Exists(Path.Combine(root.FullName, "BackupTool.sln"))) root = root.Parent;
if (root is null) throw new InvalidOperationException("リポジトリが見つかりません。");
var suitePath = Path.Combine(root.FullName, "artifacts", "tests", DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..6]);
Directory.CreateDirectory(suitePath);
var tests = new List<(string Name, Func<Task> Execute)>();
var sevenZip = new BackupApplication(Path.Combine(suitePath, "discovery")).SevenZipPath;
if (!File.Exists(sevenZip)) throw new FileNotFoundException("scripts/setup-7zip.ps1を実行してください。", sevenZip);

tests.Add(("ZIP: 浅いファイル・空フォルダ・階層分割・変更なし", async () =>
{
    var f = Fixture("zip-depth", BackupMode.Zip, 2);
    Write(f.Source, "root.txt", "root"); Write(f.Source, "A/direct.txt", "direct"); Write(f.Source, "A/B/data.txt", "data");
    Directory.CreateDirectory(Path.Combine(f.Source, "A/B/empty")); Directory.CreateDirectory(Path.Combine(f.Source, "shallow"));
    var run = await f.Run(); Equal(ResultStatus.Success, run.Status);
    Equal("root", ReadZip(f.Destination, "_files.zip", "root.txt"));
    Equal("direct", ReadZip(f.Destination, "A/_files.zip", "direct.txt"));
    Equal("data", ReadZip(f.Destination, "A/B.zip", "data.txt"));
    using (var zip = ZipFile.OpenRead(Path.Combine(f.Destination, "A/B.zip"))) True(zip.Entries.Any(e => e.FullName.TrimEnd('/') == "empty"), "空フォルダがありません。");
    var timestamp = File.GetLastWriteTimeUtc(Path.Combine(f.Destination, "A/B.zip"));
    run = await f.Run(); Equal(ResultStatus.Success, run.Status);
    True(f.Store.GetUnits(run.Id).All(x => x.Status == ResultStatus.Skipped), "変更なしユニットをスキップしていません。");
    Equal(timestamp, File.GetLastWriteTimeUtc(Path.Combine(f.Destination, "A/B.zip")));
}));
tests.Add(("ZIP: 差分更新・古い日時への復元・削除・消滅ユニット", async () =>
{
    var f = Fixture("zip-update");
    Write(f.Source, "A/keep.txt", "keep"); Write(f.Source, "A/change.txt", "original"); Write(f.Source, "A/remove.txt", "remove"); Write(f.Source, "Gone/x", "gone");
    Equal(ResultStatus.Success, (await f.Run()).Status);
    Write(f.Source, "A/change.txt", "updated with old timestamp");
    File.SetLastWriteTimeUtc(Path.Combine(f.Source, "A/change.txt"), DateTime.UtcNow.AddDays(-10));
    File.Delete(Path.Combine(f.Source, "A/remove.txt")); Directory.Delete(Path.Combine(f.Source, "Gone"), true);
    Equal(ResultStatus.Success, (await f.Run()).Status);
    Equal("updated with old timestamp", ReadZip(f.Destination, "A.zip", "change.txt"));
    Equal("keep", ReadZip(f.Destination, "A.zip", "keep.txt"));
    using (var zip = ZipFile.OpenRead(Path.Combine(f.Destination, "A.zip"))) True(zip.GetEntry("remove.txt") is null, "削除したエントリが残っています。");
    True(!File.Exists(Path.Combine(f.Destination, "Gone.zip")), "消滅ユニットが残っています。");
}));
tests.Add(("ZIP: 読取失敗時の旧ZIP保持・他ユニット継続・削除停止", async () =>
{
    var f = Fixture("zip-locked");
    Write(f.Source, "A/data", "old"); Write(f.Source, "B/data", "old"); Write(f.Source, "Gone/data", "old");
    Equal(ResultStatus.Success, (await f.Run()).Status);
    var hash = Hash(Path.Combine(f.Destination, "A.zip"));
    Write(f.Source, "A/data", "new locked content"); Write(f.Source, "B/data", "new valid content"); Directory.Delete(Path.Combine(f.Source, "Gone"), true);
    using (var held = new FileStream(Path.Combine(f.Source, "A/data"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
    {
        var result = await f.Run(); Equal(ResultStatus.PartialFailure, result.Status);
        Equal(hash, Hash(Path.Combine(f.Destination, "A.zip")));
        Equal("new valid content", ReadZip(f.Destination, "B.zip", "data"));
        True(File.Exists(Path.Combine(f.Destination, "Gone.zip")), "失敗時に削除されました。");
    }
    Equal(ResultStatus.Success, (await f.Run()).Status);
    Equal("new locked content", ReadZip(f.Destination, "A.zip", "data"));
}));
tests.Add(("ZIP: 全エントリ削除後も有効な空ZIP", async () =>
{
    var f = Fixture("zip-empty"); Write(f.Source, "A/data", "old"); await f.Run();
    File.Delete(Path.Combine(f.Source, "A/data")); Equal(ResultStatus.Success, (await f.Run()).Status);
    using var zip = ZipFile.OpenRead(Path.Combine(f.Destination, "A.zip")); Equal(0, zip.Entries.Count);
}));
tests.Add(("ZIP: 同一サイズ・同一秒内の変更も反映", async () =>
{
    var f = Fixture("zip-precise-time"); Write(f.Source, "A/data", "aaaa");
    var time = new DateTime(2026, 11, 1, 1, 1, 0, DateTimeKind.Utc);
    File.SetLastWriteTimeUtc(Path.Combine(f.Source, "A/data"), time.AddMilliseconds(100));
    Equal(ResultStatus.Success, (await f.Run()).Status);
    Write(f.Source, "A/data", "bbbb"); File.SetLastWriteTimeUtc(Path.Combine(f.Source, "A/data"), time.AddMilliseconds(200));
    Equal(ResultStatus.Success, (await f.Run()).Status); Equal("bbbb", ReadZip(f.Destination, "A.zip", "data"));
}));
tests.Add(("COPY: Robocopy差分更新・削除・空フォルダ・管理ファイル保持", async () =>
{
    var f = Fixture("copy-sync", BackupMode.Copy, 2);
    Write(f.Source, "root.txt", "root"); Write(f.Source, "A/shallow.txt", "shallow"); Write(f.Source, "A/B/data", "before"); Write(f.Source, "Gone/x", "gone");
    Directory.CreateDirectory(Path.Combine(f.Source, "empty"));
    Equal(ResultStatus.Success, (await f.Run()).Status);
    Write(f.Source, "A/B/data", "after new size"); File.Delete(Path.Combine(f.Source, "root.txt")); Directory.Delete(Path.Combine(f.Source, "Gone"), true);
    Equal(ResultStatus.Success, (await f.Run()).Status);
    Equal("after new size", File.ReadAllText(Path.Combine(f.Destination, "A/B/data")));
    Equal("shallow", File.ReadAllText(Path.Combine(f.Destination, "A/shallow.txt")));
    True(!Directory.Exists(Path.Combine(f.Destination, "Gone")) && !File.Exists(Path.Combine(f.Destination, "root.txt")), "削除同期に失敗しました。");
    True(Directory.Exists(Path.Combine(f.Destination, "empty")), "空フォルダがありません。");
    True(File.Exists(Path.Combine(f.Destination, ".backup-tool.owner.json")), "所有情報が削除されました。");
    Equal(ResultStatus.Success, (await f.Run()).Status);
}));
tests.Add(("COPY: 一部読取失敗でも他の更新を継続し削除を停止", async () =>
{
    var f = Fixture("copy-locked", BackupMode.Copy);
    Write(f.Source, "A/locked", "old"); Write(f.Source, "B/good", "old"); Write(f.Source, "remove", "preserve"); await f.Run();
    Write(f.Source, "A/locked", "new locked"); Write(f.Source, "B/good", "new successful"); File.Delete(Path.Combine(f.Source, "remove"));
    using var held = new FileStream(Path.Combine(f.Source, "A/locked"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    Equal(ResultStatus.PartialFailure, (await f.Run()).Status);
    Equal("new successful", File.ReadAllText(Path.Combine(f.Destination, "B/good")));
    True(File.Exists(Path.Combine(f.Destination, "remove")), "コピー失敗時に削除しました。");
}));
tests.Add(("安全性: 元が参照不能なら既存データを削除しない", async () =>
{
    var f = Fixture("missing-source", BackupMode.Copy); Write(f.Source, "data", "safe"); await f.Run();
    Directory.Move(f.Source, f.Source + "-offline");
    Equal(ResultStatus.Failed, (await f.Run()).Status); Equal("safe", File.ReadAllText(Path.Combine(f.Destination, "data")));
}));
tests.Add(("安全性: 保存先の既存ユーザーデータを引き継がない", async () =>
{
    var f = Fixture("unowned"); Directory.CreateDirectory(f.Destination); Write(f.Destination, "personal", "preserve");
    Equal(ResultStatus.Failed, (await f.Run()).Status); Equal("preserve", File.ReadAllText(Path.Combine(f.Destination, "personal")));
}));
tests.Add(("安全性: 同一・包含・他タスクとの保存先重複を拒否", () =>
{
    var f = Fixture("overlap");
    Throws<ArgumentException>(() => (f.Task with { Destination = f.Source }).Validate());
    Throws<ArgumentException>(() => (f.Task with { Destination = Path.Combine(f.Source, "backup") }).Validate());
    Throws<ArgumentException>(() => f.Store.SaveTask(f.Task with { Id = Guid.NewGuid().ToString("N") }));
    Throws<ArgumentException>(() => f.Store.SaveTask(f.Task with { Source = f.Store.DataDirectory }));
    return Task.CompletedTask;
}));
tests.Add(("安全性: ZIP出力名の衝突を検出して停止", async () =>
{
    var f = Fixture("collision"); Write(f.Source, "_files/data", "collision"); Write(f.Source, "root", "root");
    Equal(ResultStatus.Failed, (await f.Run()).Status);
    True(!File.Exists(Path.Combine(f.Destination, "_files.zip")), "衝突したZIPが作成されました。");
}));
tests.Add(("安全性: 実行ロックがあると二重実行を拒否", async () =>
{
    var f = Fixture("locked-run");
    using var held = new FileStream(Path.Combine(f.Store.DataDirectory, "execution.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    await ThrowsAsync<IOException>(() => f.Run());
}));
tests.Add(("安全性: 別ストアから同じ保存先を更新しない", async () =>
{
    var f = Fixture("ownership"); Write(f.Source, "A/data", "safe"); await f.Run();
    var otherStore = new SqliteStore(Path.Combine(suitePath, "other-db"));
    var other = f.Task with { Id = Guid.NewGuid().ToString("N") }; otherStore.SaveTask(other);
    var scanner = new SourceScanner();
    var runner = new BackupRunner(otherStore, scanner, new BackupBackend(sevenZip, scanner), otherStore.DataDirectory);
    Equal(ResultStatus.Failed, (await runner.RunAsync(other.Id)).Status);
    Equal("safe", ReadZip(f.Destination, "A.zip", "data"));
}));
tests.Add(("障害: 中断を記録し次回に再実行", async () =>
{
    var f = Fixture("cancelled"); Write(f.Source, "A/data", "content");
    using var cts = new CancellationTokenSource(); cts.Cancel();
    var scanner = new SourceScanner();
    var runner = new BackupRunner(f.Store, scanner, new BackupBackend(sevenZip, scanner), f.Store.DataDirectory);
    Equal(ResultStatus.Cancelled, (await runner.RunAsync(f.Task.Id, ct: cts.Token)).Status);
    Equal(ResultStatus.Success, (await f.Run()).Status);
}));
tests.Add(("履歴: 異常終了を回復しユニット詳細を保存", async () =>
{
    var f = Fixture("recover");
    var previous = new RunRecord(Guid.NewGuid().ToString("N"), f.Task.Id, DateTimeOffset.Now.AddMinutes(-5), null, ResultStatus.Running, Path.Combine(f.Store.DataDirectory, "orphan.log"), null, 0, 0);
    f.Store.StartRun(previous);
    var result = await f.Run(); Equal(ResultStatus.Success, result.Status);
    Equal(ResultStatus.Interrupted, f.Store.GetRuns().Single(r => r.Id == previous.Id).Status);
    True(f.Store.GetUnits(result.Id).Count > 0, "ユニット履歴がありません。");
    True(File.Exists(Path.Combine(Path.GetDirectoryName(result.LogPath)!, "summary.json")), "サマリーがありません。");
}));
tests.Add(("処理順: 未完了ユニットを優先", () =>
{
    var units = Enumerable.Range(0, 20).Select(i => new BackupUnit(i.ToString(), "", "", true)).ToList();
    var states = new[] { new UnitState("7", "", "", ResultStatus.Failed), new UnitState("12", "", "", ResultStatus.Running) };
    for (var i = 0; i < 20; i++) True(UnitPlanner.Order(units, states).Take(2).All(u => u.Key is "7" or "12"), "未完了ユニットが優先されません。");
    return Task.CompletedTask;
}));
tests.Add(("削除前確認: 再出現したファイル・フォルダを削除しない", () =>
{
    var f = Fixture("reappeared", BackupMode.Copy); Write(f.Source, "exists/file", "keep");
    Throws<IOException>(() => SourceScanner.ConfirmAbsent(f.Source, "exists"));
    Throws<IOException>(() => SourceScanner.ConfirmAbsent(f.Source, "exists/file"));
    SourceScanner.ConfirmAbsent(f.Source, "absent/file"); return Task.CompletedTask;
}));
tests.Add(("パス: ドライブルート包含とUNC共有ルートを検証", () =>
{
    True(SafePaths.Contains(@"C:\", @"C:\Data\Backup"), "ドライブルートの包含判定に失敗しました。");
    var f = Fixture("root-validation");
    Throws<ArgumentException>(() => (f.Task with { Source = Path.GetPathRoot(f.Source)! }).Validate());
    Throws<ArgumentException>(() => (f.Task with { Destination = @"\\server\share" }).Validate());
    return Task.CompletedTask;
}));
tests.Add(("ZIP: 日本語・空白・メタ文字を含むパス", async () =>
{
    var f = Fixture("日本語 空白;path"); Write(f.Source, "フォルダ [1]/@data [2].txt", "日本語コンテンツ");
    Equal(ResultStatus.Success, (await f.Run()).Status);
    Equal("日本語コンテンツ", ReadZip(f.Destination, "フォルダ [1].zip", "@data [2].txt"));
}));
tests.Add(("リンク: 元のジャンクションを除外し保存先のジャンクションを拒否", async () =>
{
    var f = Fixture("junctions", BackupMode.Copy); Write(f.Source, "A/data", "safe");
    var external = Path.Combine(suitePath, "junction-target"); Directory.CreateDirectory(external); Write(external, "outside", "do not touch");
    await CreateJunction(Path.Combine(f.Source, "link"), external);
    Equal(ResultStatus.Success, (await f.Run()).Status);
    True(!Directory.Exists(Path.Combine(f.Destination, "link")), "元のジャンクションをコピーしました。");
    await CreateJunction(Path.Combine(f.Destination, "bad-link"), external);
    Equal(ResultStatus.Failed, (await f.Run()).Status);
    Equal("do not touch", File.ReadAllText(Path.Combine(external, "outside")));
}));

tests.Add(("混在: 深い非圧縮フォルダ・重複除外・差分と削除", async () =>
{
    var f = Fixture("mixed-deep");
    Write(f.Source, "A/direct.txt", "direct"); Write(f.Source, "A/Zipped/data", "zip");
    Write(f.Source, "A/Raw/data", "raw"); Write(f.Source, "A/Raw/remove", "remove"); Write(f.Source, "A/OtherRaw/data", "other");
    Write(f.Source, "A/Raw/.backup-tool.owner.json", "source user data");
    f.Store.SaveTask(f.Task with { UncompressedFolders = ["A/Raw", "A/Raw/Sub", "A/OtherRaw"] });
    Equal(2, f.Store.GetTask(f.Task.Id).UncompressedFolders.Length);
    Equal(ResultStatus.Success, (await f.Run()).Status);
    Equal("direct", ReadZip(f.Destination, "A/_files.zip", "direct.txt"));
    Equal("zip", ReadZip(f.Destination, "A/Zipped.zip", "data"));
    Equal("raw", File.ReadAllText(Path.Combine(f.Destination, "A/Raw/data")));
    True(!File.Exists(Path.Combine(f.Destination, "A.zip")), "非圧縮フォルダが親ZIPにも含まれています。");
    Write(f.Source, "A/Raw/data", "raw updated"); File.Delete(Path.Combine(f.Source, "A/Raw/remove")); Directory.Delete(Path.Combine(f.Source, "A/OtherRaw"), true);
    File.Delete(Path.Combine(f.Source, "A/Raw/.backup-tool.owner.json"));
    Equal(ResultStatus.Success, (await f.Run()).Status);
    Equal("raw updated", File.ReadAllText(Path.Combine(f.Destination, "A/Raw/data")));
    True(!File.Exists(Path.Combine(f.Destination, "A/Raw/remove")), "非圧縮ファイルが削除されません。");
    True(!File.Exists(Path.Combine(f.Destination, "A/Raw/.backup-tool.owner.json")), "管理マーカーと同名の利用者ファイルが削除されません。");
    True(!Directory.Exists(Path.Combine(f.Destination, "A/OtherRaw")), "消滅した非圧縮フォルダが残っています。");
}));
tests.Add(("混在: 既存タスクの例外追加・変更・解除で旧出力を整理", async () =>
{
    var f = Fixture("mixed-transition"); Write(f.Source, "A/root", "root"); Write(f.Source, "A/B/data", "payload"); Write(f.Source, "A/C/data", "sibling");
    Equal(ResultStatus.Success, (await f.Run()).Status);
    f.Store.SaveTask(f.Task with { UncompressedFolders = ["A"] });
    Equal(ResultStatus.Success, (await f.Run()).Status);
    True(!File.Exists(Path.Combine(f.Destination, "A.zip")), "旧ZIPが残っています。");
    Equal("payload", File.ReadAllText(Path.Combine(f.Destination, "A/B/data")));
    f.Store.SaveTask(f.Task with { UncompressedFolders = ["A/B"] });
    Equal(ResultStatus.Success, (await f.Run()).Status);
    Equal("root", ReadZip(f.Destination, "A/_files.zip", "root"));
    Equal("sibling", ReadZip(f.Destination, "A/C.zip", "data"));
    Equal("payload", File.ReadAllText(Path.Combine(f.Destination, "A/B/data")));
    True(!File.Exists(Path.Combine(f.Destination, "A/root")) && !Directory.Exists(Path.Combine(f.Destination, "A/C")), "旧非圧縮ファイルが残っています。");
    f.Store.SaveTask(f.Task);
    Equal(ResultStatus.Success, (await f.Run()).Status);
    Equal("payload", ReadZip(f.Destination, "A.zip", "B/data"));
    True(!Directory.Exists(Path.Combine(f.Destination, "A")), "旧混在出力が残っています。");
}));
tests.Add(("混在: 変換先が失敗した場合に旧ZIPを保持", async () =>
{
    var f = Fixture("mixed-transition-failure"); Write(f.Source, "A/data", "original"); await f.Run();
    var hash = Hash(Path.Combine(f.Destination, "A.zip"));
    f.Store.SaveTask(f.Task with { UncompressedFolders = ["A"] });
    Write(f.Source, "A/data", "locked changed data");
    using var held = new FileStream(Path.Combine(f.Source, "A/data"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    Equal(ResultStatus.PartialFailure, (await f.Run()).Status);
    Equal(hash, Hash(Path.Combine(f.Destination, "A.zip")));
}));
tests.Add(("混在: 出力名衝突と対象外の例外パスを拒否", async () =>
{
    var f = Fixture("mixed-collision"); Write(f.Source, "A/data", "zip"); Write(f.Source, "A.zip/data", "copy");
    f.Store.SaveTask(f.Task with { UncompressedFolders = ["A.zip"] });
    Equal(ResultStatus.Failed, (await f.Run()).Status);
    foreach (var invalid in new[] { "../outside", @"C:\outside", ".", "", "A/../B" })
        Throws<ArgumentException>(() => (f.Task with { UncompressedFolders = [invalid] }).Validate());
}));
tests.Add(("定期実行: 毎週複数曜日・複数時刻・隔週の次回予定", () =>
{
    var schedule = new BackupSchedule { Frequency = ScheduleFrequency.Weekly, StartDate = new(2026, 11, 16), WeekDays = [DayOfWeek.Monday, DayOfWeek.Wednesday, DayOfWeek.Friday], Times = ["18:00", "09:00"] };
    Equal(new DateTime(2026, 11, 16, 18, 0, 0), schedule.NextOccurrence(new(2026, 11, 16, 9, 0, 0)));
    Equal(new DateTime(2026, 11, 18, 9, 0, 0), schedule.NextOccurrence(new(2026, 11, 17, 10, 0, 0)));
    Equal(new DateTime(2026, 11, 30, 9, 0, 0), (schedule with { Interval = 2, WeekDays = [DayOfWeek.Monday] }).NextOccurrence(new(2026, 11, 17, 0, 0, 0)));
    Equal(new DateTime(2026, 11, 18, 9, 0, 0), (schedule with { Frequency = ScheduleFrequency.Daily, Interval = 2 }).NextOccurrence(new(2026, 11, 16, 18, 0, 0)));
    return Task.CompletedTask;
}));
tests.Add(("定期実行: 毎月の指定日・月末・閏年・実行月を処理", () =>
{
    var schedule = new BackupSchedule { Frequency = ScheduleFrequency.Monthly, StartDate = new(2026, 1, 1), MonthDays = [31], Times = ["02:00"] };
    Equal(new DateTime(2026, 3, 31, 2, 0, 0), schedule.NextOccurrence(new(2026, 2, 1)));
    var last = schedule with { MonthDays = [], LastDayOfMonth = true };
    Equal(new DateTime(2026, 2, 28, 2, 0, 0), last.NextOccurrence(new(2026, 2, 1)));
    Equal(new DateTime(2028, 2, 29, 2, 0, 0), last.NextOccurrence(new(2028, 2, 1)));
    Equal(new DateTime(2026, 6, 15, 2, 0, 0), (schedule with { MonthDays = [15], Months = [3, 6, 9, 12] }).NextOccurrence(new(2026, 4, 1)));
    Throws<ArgumentException>(() => (schedule with { Months = [2], MonthDays = [30] }).Validate());
    Throws<ArgumentException>(() => (schedule with { Times = ["25:00"] }).Validate());
    Throws<ArgumentException>(() => (schedule with { Frequency = ScheduleFrequency.Weekly, WeekDays = [] }).Validate());
    return Task.CompletedTask;
}));
tests.Add(("定期実行: 旧DailyAtを移行し解除可能", () =>
{
    var f = Fixture("schedule-migration"); f.Store.SaveTask(f.Task with { DailyAt = "03:30" });
    var saved = f.Store.GetTask(f.Task.Id);
    Equal(ScheduleFrequency.Daily, saved.EffectiveSchedule!.Frequency); Equal("03:30", saved.Schedule!.Times[0]);
    f.Store.SaveTask(saved with { Schedule = null, DailyAt = null });
    True(f.Store.GetTask(f.Task.Id).EffectiveSchedule is null, "スケジュールを解除できません。");
    return Task.CompletedTask;
}));
tests.Add(("定期実行: Windowsのトリガー定義を検証（登録なし）", () =>
{
    dynamic service = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service")!)!;
    try
    {
        service.Connect();
        foreach (var frequency in Enum.GetValues<ScheduleFrequency>())
        {
            dynamic definition = service.NewTask(0);
            try
            {
                var schedule = new BackupSchedule { Frequency = frequency, Times = ["02:00", "18:30"], WeekDays = [DayOfWeek.Monday, DayOfWeek.Friday], MonthDays = [1, 15], LastDayOfMonth = true };
                WindowsScheduler.AddTriggers(definition, schedule);
                Equal(2, (int)definition.Triggers.Count);
                dynamic trigger = definition.Triggers.Item(1);
                Equal(frequency switch { ScheduleFrequency.Daily => 2, ScheduleFrequency.Weekly => 3, _ => 4 }, (int)trigger.Type);
                if (frequency == ScheduleFrequency.Weekly) Equal(34, (int)trigger.DaysOfWeek);
                if (frequency == ScheduleFrequency.Monthly) { Equal(16385, (int)trigger.DaysOfMonth); True((bool)trigger.RunOnLastDayOfMonth, "月末設定がありません。"); Equal(4095, (int)trigger.MonthsOfYear); }
                var xml = System.Xml.Linq.XDocument.Parse((string)definition.XmlText);
                True(xml.Descendants().Count(x => x.Name.LocalName == "CalendarTrigger") == 2, "カレンダートリガーのXMLが不正です。");
            }
            finally { System.Runtime.InteropServices.Marshal.FinalReleaseComObject(definition); }
        }
    }
    finally { System.Runtime.InteropServices.Marshal.FinalReleaseComObject(service); }
    return Task.CompletedTask;
}));

var failures = 0;
foreach (var (name, execute) in tests)
{
    try { await execute(); Console.WriteLine($"PASS {name}"); }
    catch (Exception ex) { failures++; Console.Error.WriteLine($"FAIL {name}\n{ex}"); }
}
Console.WriteLine($"{tests.Count - failures}/{tests.Count} passed. Evidence: {suitePath}");
return failures == 0 ? 0 : 1;

FixtureData Fixture(string name, BackupMode mode = BackupMode.Zip, int depth = 1)
{
    var path = Path.Combine(suitePath, name); var source = Path.Combine(path, "source"); Directory.CreateDirectory(source);
    var app = new BackupApplication(Path.Combine(path, "data"), sevenZip);
    var task = new BackupTask { Name = name, Source = source, Destination = Path.Combine(path, "destination"), Mode = mode, UnitDepth = depth, FileRetries = 0, FileWaitSeconds = 0, NetworkRetries = 0, NetworkWaitSeconds = 0 };
    app.Store.SaveTask(task); return new(app, task);
}
static void Write(string rootPath, string relative, string content)
{
    var path = Path.Combine(rootPath, relative); Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, content);
}
static string ReadZip(string destination, string relative, string entry)
{
    using var zip = ZipFile.OpenRead(Path.Combine(destination, relative));
    using var reader = new StreamReader(zip.GetEntry(entry)?.Open() ?? throw new Exception($"ZIPエントリなし: {relative}/{entry}")); return reader.ReadToEnd();
}
static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
static async Task CreateJunction(string path, string target)
{
    var start = new System.Diagnostics.ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
    foreach (var argument in new[] { "/c", "mklink", "/J", path, target }) start.ArgumentList.Add(argument);
    using var process = System.Diagnostics.Process.Start(start)!;
    var output = await process.StandardOutput.ReadToEndAsync(); var error = await process.StandardError.ReadToEndAsync();
    await process.WaitForExitAsync();
    if (process.ExitCode != 0) throw new Exception($"Junction creation failed: {output} {error}");
}
static void True(bool condition, string message) { if (!condition) throw new Exception(message); }
static void Equal<T>(T expected, T actual) { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected {expected}, actual {actual}"); }
static void Throws<T>(Action action) where T : Exception
{
    try { action(); } catch (T) { return; } throw new Exception($"Expected {typeof(T).Name}");
}
static async Task ThrowsAsync<T>(Func<Task> action) where T : Exception
{
    try { await action(); } catch (T) { return; } throw new Exception($"Expected {typeof(T).Name}");
}
sealed record FixtureData(BackupApplication App, BackupTask Task)
{
    public string Source => Task.Source;
    public string Destination => Task.Destination;
    public SqliteStore Store => App.Store;
    public Task<RunRecord> Run() => App.CreateRunner().RunAsync(Task.Id);
}

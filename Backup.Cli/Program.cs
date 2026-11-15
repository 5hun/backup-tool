using System.Text.Json;
using System.Text.Json.Serialization;
using Backup.Engine;
using Backup.Infrastructure;

var json = new JsonSerializerOptions { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };
try
{
    if (args.Length == 0 || args[0] is "help" or "--help")
    {
        Console.WriteLine("""
            Backup.Cli task list
            Backup.Cli task import <task.json>
            Backup.Cli task export --task-id <id>
            Backup.Cli run --task-id <id>
            Backup.Cli history [--task-id <id>]
            Backup.Cli details --run-id <id>
            Backup.Cli schedule --task-id <id> [--unattended]
            共通: --data <ローカルデータフォルダ> --7zip <7za.exeのパス>
            --unattended: 現在のWindowsアカウントのパスワードを対話入力します。
            終了コード: 0=成功, 1=失敗/一部失敗, 2=設定エラー, 3=中断, 4=警告
            """);
        return 0;
    }
    var allowed = new HashSet<string> { "--task-id", "--run-id", "--data", "--7zip", "--unattended" };
    Dictionary<string, string> options = [];
    List<string> positional = [];
    for (var i = 1; i < args.Length; i++)
    {
        if (!args[i].StartsWith("--", StringComparison.Ordinal)) { positional.Add(args[i]); continue; }
        var option = args[i];
        if (!allowed.Contains(option) || options.ContainsKey(option)) throw new ArgumentException($"不正なオプション: {option}");
        if (option == "--unattended") options[option] = "true";
        else if (++i < args.Length) options[option] = args[i];
        else throw new ArgumentException($"値がありません: {option}");
    }
    string Required(string key) => options.GetValueOrDefault(key) ?? throw new ArgumentException($"{key} を指定してください。");
    var app = new BackupApplication(options.GetValueOrDefault("--data"), options.GetValueOrDefault("--7zip"));
    switch (args[0])
    {
        case "task" when positional.SequenceEqual(["list"]): Console.WriteLine(JsonSerializer.Serialize(app.Store.GetTasks(), json)); return 0;
        case "task" when positional.Count == 2 && positional[0] == "import":
            var task = JsonSerializer.Deserialize<BackupTask>(File.ReadAllText(positional[1]), json) ?? throw new ArgumentException("タスク定義が空です。");
            app.Store.SaveTask(task); Console.WriteLine(task.Id); return 0;
        case "task" when positional.SequenceEqual(["export"]): Console.WriteLine(JsonSerializer.Serialize(app.Store.GetTask(Required("--task-id")), json)); return 0;
        case "history": Console.WriteLine(JsonSerializer.Serialize(app.Store.GetRuns(options.GetValueOrDefault("--task-id")), json)); return 0;
        case "details": Console.WriteLine(JsonSerializer.Serialize(app.Store.GetUnits(Required("--run-id")), json)); return 0;
        case "schedule":
            string? password = null;
            if (options.ContainsKey("--unattended"))
            {
                if (Console.IsInputRedirected) throw new ArgumentException("パスワードは対話端末から入力してください。");
                Console.Write("Windowsアカウントのパスワード: ");
                var buffer = new List<char>();
                while (true)
                {
                    var key = Console.ReadKey(true);
                    if (key.Key == ConsoleKey.Enter) break;
                    if (key.Key == ConsoleKey.Backspace) { if (buffer.Count > 0) buffer.RemoveAt(buffer.Count - 1); }
                    else if (!char.IsControl(key.KeyChar)) buffer.Add(key.KeyChar);
                }
                Console.WriteLine(); password = new string(buffer.ToArray()); buffer.Clear();
            }
            WindowsScheduler.Register(app.Store.GetTask(Required("--task-id")), app.Store.DataDirectory, WindowsScheduler.FindCli(), app.SevenZipPath, password);
            Console.WriteLine("スケジュールを反映しました。"); return 0;
        case "run":
            using (var cancellation = new CancellationTokenSource())
            {
                Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
                var progress = new Progress<RunProgress>(p => Console.WriteLine($"{p.Completed}/{p.Total} {p.Message}"));
                var run = await app.CreateRunner().RunAsync(Required("--task-id"), progress, cancellation.Token);
                Console.WriteLine(JsonSerializer.Serialize(run, json));
                return run.Status switch { ResultStatus.Success => 0, ResultStatus.Warning => 4, ResultStatus.Cancelled => 3, _ => 1 };
            }
        default: throw new ArgumentException("コマンドが不正です。--helpを参照してください。");
    }
}
catch (Exception ex) when (ex is not OutOfMemoryException)
{
    Console.Error.WriteLine(ex.Message);
    return 2;
}

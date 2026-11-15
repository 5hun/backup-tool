# Backup Tool

Windows向けの単一世代ファイルバックアップツールです。.NET 10 / WPF / SQLiteを使用します。元ファイルの削除も保存先へ反映します。

## 開発・起動

Windows x64、.NET 10 SDKが必要です。7-Zipは管理者権限を使わず、このリポジトリの `.tools/7zip` に配置します。

```powershell
./scripts/setup-7zip.ps1
dotnet build BackupTool.sln
dotnet run --project Backup.Tests
./scripts/publish.ps1
./artifacts/app/Backup.Gui.exe
```

配布用フォルダは `artifacts/app` です（Release、デバッグシンボルなし、win-x64向けのみ）。デバッグ用は `./scripts/publish.ps1 -Configuration Debug` で `artifacts/app-debug` に.pdb付きで出力されます。7-Zipを含むフォルダ全体を配置してください。配布先では.NET 10 Desktop Runtime（x64）が必要です。開発用画面は `dotnet run --project Backup.Gui` でも起動できます。

## 使い方

1. GUIの「新規」で名前、元フォルダ、空の専用保存先、処理単位の階層を指定します。標準はZIP保存です。NASにはUNCパスを指定します。
2. 非圧縮で保存するフォルダは「フォルダを追加…」で複数選択します。相対パスを1行ずつ入力することもできます。指定したフォルダの配下も非圧縮になり、親ZIPには重複して含めません。圧縮レベルは「ZIP圧縮」欄で設定します。
3. 「設定を保存」でタスクを作成し、「実行」で開始します。「中断」は外部プロセスも停止します。
4. 「実行履歴」でユニットの結果を確認し、ログを開きます。RobocopyのUnicode詳細ログはユニットログと同じ場所の `.robocopy.log` です。
5. 定期実行で日ごと（N日間隔）、週ごと（複数曜日・N週間隔）、月ごと（複数日付・月末・実行月）を選びます。実行時刻は `02:00,18:00` のように複数指定できます。「保存してスケジュール反映」でWindowsへ登録します。「自動実行しない」を選んで反映すると解除します。31日等が存在しない月はスキップします。

通常のスケジュールは現在のWindowsユーザーがログオン中に実行します。GUIは起動不要です。「ログオフ中も実行する」では、現在のWindowsアカウントの**パスワード（PINではありません）**を入力します。パスワードはアプリのDBやコマンド引数に保存せず、Windowsのタスクスケジューラへ渡します。アカウントのバッチログオン権限、NAS接続権限が必要です。登録が権限不足で拒否された場合は、Windowsのタスクスケジューラ側で実行アカウントを設定してください。

設定・履歴・ログの既定保存先は `%LOCALAPPDATA%\BackupTool` です。`BACKUP_TOOL_DATA` 環境変数またはCLIの `--data` で変更できます。GUIとCLI、スケジュールで同じ保存先を利用してください。ログの既定保持期間は90日でタスクごとに変更できます。

## CLI

タスクのJSON例（Idを省略すると新規IDを発行）:

```json
{
  "Name": "ドキュメント",
  "Source": "D:\\Data",
  "Destination": "\\\\NAS\\Backup\\Data",
  "UnitDepth": 2,
  "UncompressedFolders": ["Videos", "DepartmentA/LargeFiles"],
  "ParallelUnits": 2,
  "CopyThreads": 8,
  "Schedule": {
    "Frequency": "Weekly",
    "StartDate": "2026-11-16",
    "Interval": 1,
    "WeekDays": ["Monday", "Wednesday", "Friday"],
    "Times": ["02:00", "18:00"]
  }
}
```

```powershell
./artifacts/app/Backup.Cli.exe task import ./task.json
./artifacts/app/Backup.Cli.exe task list
./artifacts/app/Backup.Cli.exe run --task-id <id>
./artifacts/app/Backup.Cli.exe history --task-id <id>
./artifacts/app/Backup.Cli.exe details --run-id <id>
./artifacts/app/Backup.Cli.exe schedule --task-id <id>
```

CLIの終了コードは `0` 成功、`1` バックアップ失敗/一部失敗、`2` 設定や起動のエラー、`3` 中断、`4` 警告です。`Ctrl+C` で中断できます。7-Zipの場所を変える場合は `BACKUP_TOOL_7ZIP` または `--7zip` を指定します。

## 保存ルール

- ZIP: 指定階層のフォルダを `相対パス.zip` として保存します。それより浅い各フォルダの直下ファイルは、その位置の `_files.zip` に保存します。例: 階層2なら `root.txt` → `_files.zip`、`A/note.txt` → `A/_files.zip`、`A/B/**` → `A/B.zip`。出力名が衝突する元フォルダ名 `_files` がある場合は、処理を停止して報告します。
- 非圧縮: 指定した例外フォルダを元のフォルダ構造でRobocopyにより差分コピーします。ZIPの設定階層より深い例外は、祖先の直下ファイルと他のサブフォルダを別ZIPに分けて保存します。
- 例外の追加・変更・解除は実行済みタスクでも可能です。新しい形式の保存が全ユニットで成功した後に、古い形式の出力を整理します。失敗時は旧出力を残します。初期版の `Mode: Copy` タスクもそのまま実行でき、旧 `DailyAt` 設定は日ごとのスケジュールへ移行します。
- ZIPの変更判定は相対パス・種類・サイズ・更新日時です。各ユニットの一覧を一時SQLiteへ逐次格納し、並べ替えて指紋を計算します。全ファイルをメモリに展開しません。
- 既存ZIPの圧縮済みエントリを再利用し、一時ZIPへ更新します。CRC検証と元データの再列挙後、同じ保存先フォルダ内で置換します。失敗時に旧ZIPを削除するフォールバックは行いません。
- 再解析ポイント（シンボリックリンク、ジャンクション等）は元では除外し、保存先に見つかった場合は処理を停止します。VSSは使用しません。
- 非圧縮の更新は直接上書きです。失敗したファイルの旧版保持は保証しません。
- 保存先は専用フォルダとして所有マーカーを置きます。初回に既存データがある保存先や、別タスクの保存先への書込みは拒否します。実行済みタスクの元・保存先・方式・階層を変える場合は、新しいタスクと空の保存先を使用してください。
- 列挙・ユニット更新に失敗した実行では削除全体を停止します。非圧縮の削除候補はSQLiteに保持し、元・先の列挙が完了してから、削除直前にも元の不在を確認します。
- 同じローカルDBを使用するタスクは同時実行せず、ユニット内並列処理を使用します。保存先にも排他ロックを置きます。

## 検証範囲

`Backup.Tests` はテスト用フォルダ `artifacts/tests` に実データを作成し、実際の7-Zip・Robocopyを実行する統合テストです。検証用データとログを残します。外部テストフレームワークは不要で、失敗時は終了コード1を返します。

ローカルでのテストはNAS/SMBの故障耐性を保証しません。実NASでの切断、容量不足、アクセス拒否、電源断と、大量ファイルの性能測定は受入試験として残しています。実装判断・制限の詳細は [docs/implementation.md](docs/implementation.md) を参照してください。

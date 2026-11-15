param()
$ErrorActionPreference = 'Stop'
$repoPath = Split-Path -Parent $PSScriptRoot
$appPath = Join-Path $repoPath 'artifacts/app/Backup.Gui.exe'
if (!(Test-Path -LiteralPath $appPath)) { throw 'Run scripts/publish.ps1 first.' }
$testPath = Join-Path $repoPath ('artifacts/gui-smoke/' + [guid]::NewGuid().ToString('N'))
$sourcePath = Join-Path $testPath 'source'
$destinationPath = Join-Path $testPath 'destination'
New-Item -ItemType Directory -Force (Join-Path $sourcePath 'Example') | Out-Null
New-Item -ItemType Directory -Force (Join-Path $sourcePath 'Raw') | Out-Null
Set-Content -LiteralPath (Join-Path $sourcePath 'Example/data.txt') -Value 'GUI smoke test' -Encoding utf8
Set-Content -LiteralPath (Join-Path $sourcePath 'Raw/raw.txt') -Value 'Uncompressed GUI smoke test' -Encoding utf8
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
$previousData = $env:BACKUP_TOOL_DATA
$env:BACKUP_TOOL_DATA = Join-Path $testPath 'data'
$process = $null
try {
    $process = Start-Process -FilePath $appPath -PassThru -WindowStyle Hidden
    if (!$process.WaitForInputIdle(15000)) { throw 'GUI did not become idle.' }
    $deadline = [datetime]::UtcNow.AddSeconds(15)
    do {
        $process.Refresh()
        if ($process.HasExited) { throw "GUI exited: $($process.ExitCode)" }
        if ($process.MainWindowHandle -ne 0) { break }
        Start-Sleep -Milliseconds 100
    } while ([datetime]::UtcNow -lt $deadline)
    if ($process.MainWindowHandle -eq 0) { throw 'GUI window was not created.' }
    $window = [System.Windows.Automation.AutomationElement]::FromHandle($process.MainWindowHandle)
    function Find-Element([string]$Value, [switch]$ByName) {
        $property = if ($ByName) { [System.Windows.Automation.AutomationElement]::NameProperty } else { [System.Windows.Automation.AutomationElement]::AutomationIdProperty }
        $condition = [System.Windows.Automation.PropertyCondition]::new($property, $Value)
        $element = $window.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
        if ($null -eq $element) { throw "GUI element not found: $Value" }
        return $element
    }
    if ($window.Current.Name -ne 'バックアップ管理') { throw 'Unexpected window title.' }
    foreach ($entry in @(@('NameBox','GUI smoke'), @('SourceBox',$sourcePath), @('DestinationBox',$destinationPath), @('UncompressedBox','Raw'))) {
        $element = Find-Element $entry[0]
        $pattern = $element.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
        $pattern.SetValue($entry[1])
    }
    function Select-Frequency([string]$Name) {
        $combo = Find-Element 'FrequencyBox'
        $combo.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Expand()
        $condition = [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::ListItem)
        $item = $combo.FindAll([System.Windows.Automation.TreeScope]::Descendants, $condition) | Where-Object { $_.Current.Name -eq $Name } | Select-Object -First 1
        if ($null -eq $item) { throw "Schedule choice not found: $Name" }
        $item.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
        $combo.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Collapse()
    }
    Select-Frequency '週ごと（曜日指定）'
    $friday = (Find-Element 'FridayBox').GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
    if ($friday.Current.ToggleState -ne [System.Windows.Automation.ToggleState]::On) { $friday.Toggle() }
    (Find-Element 'TimesBox').GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue('02:00,18:00')
    $save = Find-Element '設定を保存' -ByName
    $save.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    $cliPath = Join-Path $repoPath 'artifacts/app/Backup.Cli.exe'
    $saved = (& $cliPath task list --data $env:BACKUP_TOOL_DATA | ConvertFrom-Json)[0]
    if ($saved.Schedule.Frequency -ne 'Weekly' -or $saved.Schedule.Times.Count -ne 2 -or 'Friday' -notin $saved.Schedule.WeekDays) { throw 'Weekly schedule did not round-trip through the GUI.' }
    Select-Frequency '月ごと（日付・月末指定）'
    (Find-Element 'MonthDaysBox').GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue('15')
    $lastDay = (Find-Element 'LastDayBox').GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
    if ($lastDay.Current.ToggleState -ne [System.Windows.Automation.ToggleState]::On) { $lastDay.Toggle() }
    $save.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    $saved = (& $cliPath task list --data $env:BACKUP_TOOL_DATA | ConvertFrom-Json)[0]
    if ($saved.Schedule.Frequency -ne 'Monthly' -or !$saved.Schedule.LastDayOfMonth -or 15 -notin $saved.Schedule.MonthDays -or $saved.UncompressedFolders[0] -ne 'Raw') { throw 'Monthly schedule / exception list did not round-trip.' }
    $run = Find-Element 'RunButton'
    $run.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    $archivePath = Join-Path $destinationPath 'Example.zip'
    $deadline = [datetime]::UtcNow.AddSeconds(30)
    do {
        Start-Sleep -Milliseconds 100
        $status = (Find-Element 'StatusText').Current.Name
        if ($status -like '成功*' -and (Test-Path -LiteralPath $archivePath)) { break }
    } while ([datetime]::UtcNow -lt $deadline)
    if ($status -notlike '成功*' -or !(Test-Path -LiteralPath $archivePath)) { throw "GUI backup failed: $status" }
    if (!(Test-Path -LiteralPath (Join-Path $destinationPath 'Raw/raw.txt')) -or (Test-Path -LiteralPath (Join-Path $destinationPath 'Raw.zip'))) { throw 'GUI mixed backup failed.' }
    $historyTab = Find-Element '実行履歴' -ByName
    $historyTab.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    $historyGrid = Find-Element 'HistoryGrid'
    if ($null -eq $historyGrid) { throw 'History grid is missing.' }
    if ((Find-Element 'FilterBox').Current.BoundingRectangle.Width -lt 600) { throw 'History filter is too narrow.' }
    $window.GetCurrentPattern([System.Windows.Automation.TransformPattern]::Pattern).Resize(980,680)
    Start-Sleep -Milliseconds 150
    if ((Find-Element 'FilterBox').Current.BoundingRectangle.Width -lt 500) { throw 'History filter is too narrow at minimum window width.' }
    Write-Output "PASS native GUI, weekly/monthly settings, mixed backup, responsive history filter. Evidence: $testPath"
}
finally {
    $env:BACKUP_TOOL_DATA = $previousData
    if ($null -ne $process -and !$process.HasExited) {
        $null = $process.CloseMainWindow()
        if (!$process.WaitForExit(5000)) { Stop-Process -Id $process.Id }
    }
}

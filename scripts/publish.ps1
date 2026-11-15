param([ValidateSet('Debug','Release')][string]$Configuration = 'Release')
$ErrorActionPreference = 'Stop'
$repoPath = Split-Path -Parent $PSScriptRoot
$sevenZipPath = Join-Path $repoPath '.tools/7zip/x64/7za.exe'
if (!(Test-Path -LiteralPath $sevenZipPath)) { throw 'Run scripts/setup-7zip.ps1 first.' }
# Release is the distributable folder; Debug goes to a separate folder and keeps symbols.
$outputName = if ($Configuration -eq 'Release') { 'app' } else { 'app-debug' }
$outputPath = Join-Path $repoPath "artifacts/$outputName"
# Start from an empty folder so files from earlier publishes (e.g. other runtimes, .pdb) do not linger.
if (Test-Path -LiteralPath $outputPath) { Remove-Item -LiteralPath $outputPath -Recurse -Force }
$dotnetPath = (Get-Command dotnet -ErrorAction SilentlyContinue).Source
if (!$dotnetPath) { $dotnetPath = Join-Path $env:ProgramFiles 'dotnet/dotnet.exe' }
# -r win-x64: include only the Windows x64 native SQLite library instead of every platform under runtimes/.
$publishArgs = @('-c', $Configuration, '-r', 'win-x64', '--self-contained', 'false', '-o', $outputPath)
if ($Configuration -eq 'Release') { $publishArgs += @('-p:DebugType=None', '-p:DebugSymbols=false') }
foreach ($project in @('Backup.Cli', 'Backup.Gui')) {
    & $dotnetPath publish (Join-Path $repoPath "$project/$project.csproj") @publishArgs
    if ($LASTEXITCODE -ne 0) { throw "Publish failed: $project" }
}
$toolsOutputPath = Join-Path $outputPath 'tools/7zip'
New-Item -ItemType Directory -Force $toolsOutputPath | Out-Null
Copy-Item -LiteralPath $sevenZipPath -Destination $toolsOutputPath
Copy-Item -LiteralPath (Join-Path $repoPath '.tools/7zip/License.txt') -Destination $toolsOutputPath
Copy-Item -LiteralPath (Join-Path $repoPath 'THIRD-PARTY-NOTICES.md') -Destination $outputPath
Copy-Item -LiteralPath (Join-Path $repoPath 'LICENSE') -Destination $outputPath
Copy-Item -LiteralPath (Join-Path $repoPath 'README.md') -Destination $outputPath
Write-Output "Application: $outputPath/Backup.Gui.exe"

param([ValidateSet('Debug','Release')][string]$Configuration = 'Release')
$ErrorActionPreference = 'Stop'
$repoPath = Split-Path -Parent $PSScriptRoot
$sevenZipPath = Join-Path $repoPath '.tools/7zip/x64/7za.exe'
if (!(Test-Path -LiteralPath $sevenZipPath)) { throw 'Run scripts/setup-7zip.ps1 first.' }
$outputPath = Join-Path $repoPath 'artifacts/app'
$dotnetPath = (Get-Command dotnet -ErrorAction SilentlyContinue).Source
if (!$dotnetPath) { $dotnetPath = Join-Path $env:ProgramFiles 'dotnet/dotnet.exe' }
foreach ($project in @('Backup.Cli', 'Backup.Gui')) {
    & $dotnetPath publish (Join-Path $repoPath "$project/$project.csproj") -c $Configuration -o $outputPath --self-contained false
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

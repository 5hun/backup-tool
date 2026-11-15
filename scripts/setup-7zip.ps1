param()
$ErrorActionPreference = 'Stop'
$repoPath = Split-Path -Parent $PSScriptRoot
$toolsPath = Join-Path $repoPath '.tools'
New-Item -ItemType Directory -Force $toolsPath | Out-Null
$archivePath = Join-Path $toolsPath '7zip-extra.7z'
$extractorPath = Join-Path $toolsPath '7zr.exe'
# Pinned official binaries; hashes were recorded from the verified local download.
$downloads = @(
    @{ Url = 'https://github.com/ip7z/7zip/releases/download/26.06/7z2606-extra.7z'; Path = $archivePath; Hash = '396C180F12D2247C24B0B1FCD482C6FC2A2700F61211FFEDE1061DD971F5E104' },
    @{ Url = 'https://github.com/ip7z/7zip/releases/download/26.04/7zr.exe'; Path = $extractorPath; Hash = '256FECA8E274E5DA655E2A284FABAFD9F554365EB164862089DACD4E8276D282' }
)
foreach ($download in $downloads) {
    if (!(Test-Path -LiteralPath $download.Path)) { Invoke-WebRequest -Uri $download.Url -OutFile $download.Path }
    if ((Get-FileHash -LiteralPath $download.Path -Algorithm SHA256).Hash -ne $download.Hash) {
        throw "Checksum mismatch: $($download.Path)"
    }
}
$destinationPath = Join-Path $toolsPath '7zip'
& $extractorPath x $archivePath "-o$destinationPath" -y
if ($LASTEXITCODE -ne 0) { throw '7-Zip extraction failed.' }
& (Join-Path $destinationPath 'x64/7za.exe') i
if ($LASTEXITCODE -ne 0) { throw '7-Zip validation failed.' }

param(
    [int]$TimeoutSeconds = 90,
    [int]$Width = 0,
    [int]$Height = 0
)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$gameDirectory = 'E:\SteamLibrary\steamapps\common\Slay the Spire 2'
$gameExecutable = Join-Path $gameDirectory 'SlayTheSpire2.exe'
$logPath = Join-Path $env:APPDATA 'SlayTheSpire2\HomuraLog\logs\HomuraLog.log'

if (Get-Process SlayTheSpire2 -ErrorAction SilentlyContinue) {
    throw 'Slay the Spire 2 is already running. Close it before starting the visual smoke test.'
}

Push-Location $projectRoot
try {
    dotnet build --no-restore
    if ($LASTEXITCODE -ne 0) { throw "Build failed with exit code $LASTEXITCODE." }
}
finally {
    Pop-Location
}

$arguments = @('--enable-mods', '--homuralog-visual-smoke')
if ($Width -gt 0 -or $Height -gt 0) {
    if ($Width -lt 640 -or $Height -lt 360) { throw 'Width and Height must both specify a usable viewport.' }
    $arguments += "--homuralog-visual-size=${Width}x${Height}"
}

$startedAt = Get-Date
Start-Process -FilePath $gameExecutable `
    -ArgumentList $arguments `
    -WorkingDirectory $gameDirectory

$deadline = $startedAt.AddSeconds($TimeoutSeconds)
$completed = $false
do {
    Start-Sleep -Seconds 2
    if (Test-Path $logPath) {
        $match = Select-String -Path $logPath -Pattern 'Visual smoke check (passed|failed).*directory=' |
            Where-Object { $_.Line -match '^(.+?) \[INFO\]' -and [DateTimeOffset]$Matches[1] -ge $startedAt } |
            Select-Object -Last 1
        if ($match) {
            $directory = ($match.Line -split 'directory=', 2)[1].TrimEnd('.')
            if ($match.Line -match 'Visual smoke check failed') {
                throw "Visual smoke assertions failed. $($match.Line)"
            }
            Write-Output "Visual smoke screenshots: $directory"
            Get-ChildItem -LiteralPath $directory -Filter '*.png' | Select-Object FullName, Length, LastWriteTime
            $completed = $true
            break
        }
    }
} while ((Get-Date) -lt $deadline)

if (-not $completed) {
    throw "Visual smoke test did not finish within $TimeoutSeconds seconds. Inspect $logPath and the Godot log."
}

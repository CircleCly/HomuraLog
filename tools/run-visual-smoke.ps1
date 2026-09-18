param(
    [int]$TimeoutSeconds = 90
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

$startedAt = Get-Date
Start-Process -FilePath $gameExecutable `
    -ArgumentList '--enable-mods', '--homuralog-visual-smoke' `
    -WorkingDirectory $gameDirectory

$deadline = $startedAt.AddSeconds($TimeoutSeconds)
do {
    Start-Sleep -Seconds 2
    if (Test-Path $logPath) {
        $match = Select-String -Path $logPath -Pattern 'Visual smoke check captured screenshots directory=' |
            Where-Object { $_.Line -match '^(.+?) \[INFO\]' -and [DateTimeOffset]$Matches[1] -ge $startedAt } |
            Select-Object -Last 1
        if ($match) {
            $directory = ($match.Line -split 'directory=', 2)[1].TrimEnd('.')
            Write-Output "Visual smoke screenshots: $directory"
            Get-ChildItem -LiteralPath $directory -Filter '*.png' | Select-Object FullName, Length, LastWriteTime
            exit 0
        }
    }
} while ((Get-Date) -lt $deadline)

throw "Visual smoke test did not finish within $TimeoutSeconds seconds. Inspect $logPath and the Godot log."

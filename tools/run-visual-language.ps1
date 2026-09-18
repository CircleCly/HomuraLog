param(
    [ValidateSet('eng', 'zhs')]
    [string]$Language = 'eng',
    [int]$TimeoutSeconds = 90
)

$ErrorActionPreference = 'Stop'
$settingsRoot = Join-Path $env:APPDATA 'SlayTheSpire2\steam'
$settings = Get-ChildItem -LiteralPath $settingsRoot -Filter settings.save -Recurse -File |
    Where-Object { $_.FullName -notmatch '副本|backup' } |
    Sort-Object LastWriteTime -Descending |
    Select-Object -First 1
if (-not $settings) { throw "No active Steam settings.save was found below $settingsRoot." }
if (Get-Process SlayTheSpire2 -ErrorAction SilentlyContinue) {
    throw 'Slay the Spire 2 is already running. Close it before starting a language visual test.'
}

$backupPath = Join-Path ([IO.Path]::GetTempPath()) ("HomuraLog-settings-" + [Guid]::NewGuid().ToString('N') + '.save')
$beforeHash = (Get-FileHash -LiteralPath $settings.FullName -Algorithm SHA256).Hash
[IO.File]::Copy($settings.FullName, $backupPath, $false)

try {
    $content = [IO.File]::ReadAllText($settings.FullName)
    $pattern = '(?m)("language"\s*:\s*")[^"]+("\s*,)'
    $matches = [regex]::Matches($content, $pattern)
    if ($matches.Count -ne 1) {
        throw "Expected exactly one language property in $($settings.FullName); found $($matches.Count)."
    }
    $updated = [regex]::Replace($content, $pattern, "`$1$Language`$2", 1)
    [IO.File]::WriteAllText($settings.FullName, $updated, [Text.UTF8Encoding]::new($false))

    & (Join-Path $PSScriptRoot 'run-visual-smoke.ps1') -TimeoutSeconds $TimeoutSeconds
}
finally {
    Get-Process SlayTheSpire2 -ErrorAction SilentlyContinue | Stop-Process -Force
    $deadline = (Get-Date).AddSeconds(10)
    while ((Get-Process SlayTheSpire2 -ErrorAction SilentlyContinue) -and (Get-Date) -lt $deadline) {
        Start-Sleep -Milliseconds 250
    }
    [IO.File]::Copy($backupPath, $settings.FullName, $true)
    Remove-Item -LiteralPath $backupPath -Force
}

$afterHash = (Get-FileHash -LiteralPath $settings.FullName -Algorithm SHA256).Hash
if ($afterHash -ne $beforeHash) {
    throw "Settings restoration hash mismatch for $($settings.FullName)."
}
Write-Output "Restored settings byte-for-byte: $($settings.FullName)"

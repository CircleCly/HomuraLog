param(
    [ValidateRange(1280, 3840)]
    [int]$Width = 1600,
    [ValidateRange(720, 2160)]
    [int]$Height = 900,
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
    throw 'Slay the Spire 2 is already running. Close it before starting a resolution visual test.'
}

$backupPath = Join-Path ([IO.Path]::GetTempPath()) ("HomuraLog-settings-" + [Guid]::NewGuid().ToString('N') + '.save')
$beforeHash = (Get-FileHash -LiteralPath $settings.FullName -Algorithm SHA256).Hash
[IO.File]::Copy($settings.FullName, $backupPath, $false)

try {
    $result = & (Join-Path $PSScriptRoot 'run-visual-smoke.ps1') `
        -TimeoutSeconds $TimeoutSeconds -Width $Width -Height $Height
    $result | Write-Output
    $directoryLine = $result | Where-Object {
        $_ -is [string] -and $_.StartsWith('Visual smoke screenshots: ')
    } | Select-Object -Last 1
    if (-not $directoryLine) { throw 'Visual smoke output did not contain a screenshot directory.' }
    $directory = $directoryLine.Substring('Visual smoke screenshots: '.Length)
    $firstPng = Get-ChildItem -LiteralPath $directory -Filter '*.png' | Sort-Object Name | Select-Object -First 1
    if (-not $firstPng) { throw "No PNG screenshots were found in $directory." }
    $bytes = [IO.File]::ReadAllBytes($firstPng.FullName)
    if ($bytes.Length -lt 24) { throw "Invalid PNG file: $($firstPng.FullName)." }
    $pngWidth = [BitConverter]::ToUInt32([byte[]]@($bytes[19], $bytes[18], $bytes[17], $bytes[16]), 0)
    $pngHeight = [BitConverter]::ToUInt32([byte[]]@($bytes[23], $bytes[22], $bytes[21], $bytes[20]), 0)
    if ($pngWidth -ne $Width -or $pngHeight -ne $Height) {
        throw "Requested ${Width}x${Height}, but captured ${pngWidth}x${pngHeight}."
    }
    Write-Output "Verified screenshot dimensions: ${pngWidth}x${pngHeight}"
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

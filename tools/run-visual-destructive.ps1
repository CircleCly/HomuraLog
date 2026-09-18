param([int]$TimeoutSeconds = 180)

$ErrorActionPreference = 'Stop'
$userRoot = Join-Path $env:APPDATA 'SlayTheSpire2'
$timelineDirectory = Join-Path $userRoot 'HomuraLog\timelines-v1'
$expectedPrefix = [IO.Path]::GetFullPath((Join-Path $userRoot 'HomuraLog')) + [IO.Path]::DirectorySeparatorChar
$resolvedTimeline = [IO.Path]::GetFullPath($timelineDirectory)
if (-not $resolvedTimeline.StartsWith($expectedPrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Refusing to manage unexpected timeline directory: $resolvedTimeline"
}
if (Get-Process SlayTheSpire2 -ErrorAction SilentlyContinue) {
    throw 'Slay the Spire 2 is already running. Close it before starting the destructive visual test.'
}

$temporaryRoot = Join-Path ([IO.Path]::GetTempPath()) ("HomuraLog-destructive-" + [Guid]::NewGuid().ToString('N'))
$timelineBackup = Join-Path $temporaryRoot 'timelines-v1'
$saveBackup = Join-Path $temporaryRoot 'native-saves'
New-Item -ItemType Directory -Path $temporaryRoot | Out-Null
$timelineExisted = Test-Path -LiteralPath $timelineDirectory
$nativeSaves = @(Get-ChildItem (Join-Path $userRoot 'steam') -Recurse -Filter 'current_run.save' -File -ErrorAction SilentlyContinue)

function Get-FileState([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path)) { return '<missing>' }
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash
}

function Get-DirectoryState([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path)) { return @('<missing>') }
    $root = [IO.Path]::GetFullPath($Path).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    return @(Get-ChildItem -LiteralPath $Path -Recurse -File | ForEach-Object {
        $relative = $_.FullName.Substring($root.Length)
        "$relative`t$((Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash)"
    } | Sort-Object)
}

$nativeState = @{}
$timelineState = @(Get-DirectoryState $timelineDirectory)
try {
    if ($timelineExisted) { Copy-Item -LiteralPath $timelineDirectory -Destination $timelineBackup -Recurse }
    New-Item -ItemType Directory -Path $saveBackup | Out-Null
    for ($index = 0; $index -lt $nativeSaves.Count; $index++) {
        $nativeState[$nativeSaves[$index].FullName] = Get-FileState $nativeSaves[$index].FullName
        Copy-Item -LiteralPath $nativeSaves[$index].FullName -Destination (Join-Path $saveBackup "$index.save")
    }

    & (Join-Path $PSScriptRoot 'run-visual-smoke.ps1') -TimeoutSeconds $TimeoutSeconds -Destructive
    if ($LASTEXITCODE -ne 0) { throw "Destructive visual smoke failed with exit code $LASTEXITCODE." }
}
finally {
    Get-Process SlayTheSpire2 -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Milliseconds 500
    if (Test-Path -LiteralPath $timelineDirectory) {
        Remove-Item -LiteralPath $timelineDirectory -Recurse -Force
    }
    if ($timelineExisted) { Copy-Item -LiteralPath $timelineBackup -Destination $timelineDirectory -Recurse }
    for ($index = 0; $index -lt $nativeSaves.Count; $index++) {
        Copy-Item -LiteralPath (Join-Path $saveBackup "$index.save") -Destination $nativeSaves[$index].FullName -Force
    }

    foreach ($path in $nativeState.Keys) {
        $restored = Get-FileState $path
        if ($restored -ne $nativeState[$path]) { throw "Native save restoration hash mismatch: $path" }
    }
    $timelineDifference = @(Compare-Object $timelineState @(Get-DirectoryState $timelineDirectory) -SyncWindow 0)
    if ($timelineDifference.Count -ne 0) { throw 'HomuraLog timeline restoration hash mismatch.' }
    Remove-Item -LiteralPath $temporaryRoot -Recurse -Force
}

Write-Output 'Destructive visual test completed; timeline data and native run saves were restored.'

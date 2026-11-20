param(
    [int]$MinutesBack = 15,
    [string]$AppNamePattern = "*PulseIsland*"
)

$projectRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..\..\..")).Path
$cutoff = (Get-Date).AddMinutes(-$MinutesBack)
$targets = @(
    $env:APPDATA,
    $env:LOCALAPPDATA,
    $env:TEMP,
    [Environment]::GetFolderPath("MyDocuments")
) | Where-Object {
    $_ -and (Test-Path $_) -and -not $_.StartsWith($projectRoot, [System.StringComparison]::OrdinalIgnoreCase)
}

Write-Output "=== Auditing system user directories outside '$projectRoot' (cutoff: $cutoff) ==="
$violations = 0

foreach ($dir in $targets) {
    $namedHits = Get-ChildItem -Path $dir -Filter $AppNamePattern -Recurse -ErrorAction SilentlyContinue |
        Where-Object {
            $_.LastWriteTime -ge $cutoff -and
            $_.FullName -notmatch '\\Microsoft\\Windows\\Recent\\' -and
            $_.FullName -notmatch '\\Local\\CrashDumps\\'
        }
    foreach ($hit in $namedHits) {
        Write-Warning "[VIOLATION - Name Match] $($hit.FullName) ($($hit.LastWriteTime))"
        $violations++
    }
}

$dotnetExtractDir = Join-Path $env:LOCALAPPDATA "Temp\.net"
if (Test-Path $dotnetExtractDir) {
    $recentNet = Get-ChildItem -Path $dotnetExtractDir -Recurse -ErrorAction SilentlyContinue |
        Where-Object { $_.LastWriteTime -ge $cutoff }
    foreach ($item in $recentNet) {
        Write-Warning "[CHECK - .NET Bundle Extract] $($item.FullName) ($($item.LastWriteTime))"
        $violations++
    }
}

if ($violations -eq 0) {
    Write-Output "PASS: No PulseIsland files or .NET extractions detected in system user directories."
    exit 0
} else {
    Write-Error "FAIL: Detected $violations potential isolation violation(s) in system user directories."
    exit 1
}

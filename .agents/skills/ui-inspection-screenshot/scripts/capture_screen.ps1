param(
    [string]$OutName = "screenshot.png",
    [switch]$TopRegionOnly
)

Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

$projectRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..\..\..")).Path
$outDir = Join-Path $projectRoot "cache\screenshots"
if (-not (Test-Path $outDir)) {
    New-Item -ItemType Directory -Path $outDir -Force | Out-Null
}

$bounds = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
$height = if ($TopRegionOnly) { [Math]::Min(250, $bounds.Height) } else { $bounds.Height }

$bitmap = New-Object System.Drawing.Bitmap($bounds.Width, $height)
$graphics = [System.Drawing.Graphics]::FromImage($bitmap)
$graphics.CopyFromScreen($bounds.Location, [System.Drawing.Point]::Empty, (New-Object System.Drawing.Size($bounds.Width, $height)))

$outPath = Join-Path $outDir $OutName
$bitmap.Save($outPath, [System.Drawing.Imaging.ImageFormat]::Png)

$graphics.Dispose()
$bitmap.Dispose()

Write-Output "Saved screenshot to: $outPath"

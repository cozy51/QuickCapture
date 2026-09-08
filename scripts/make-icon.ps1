# Deterministic vector artwork rendered into a multi-resolution Windows icon.
# Run with Windows PowerShell -STA. No external drawing libraries are required.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName PresentationCore, WindowsBase
$root = Split-Path -Parent $PSScriptRoot
$assets = Join-Path $root 'src/QuickCapture/Assets'
New-Item -ItemType Directory -Force -Path $assets | Out-Null
New-Item -ItemType Directory -Force -Path (Join-Path $root 'docs') | Out-Null
$frames = @()
$sizes = @(16, 20, 24, 32, 40, 48, 64, 128, 256)
foreach ($size in $sizes) {
    $visual = New-Object System.Windows.Media.DrawingVisual
    $drawing = $visual.RenderOpen()
    $drawing.PushTransform((New-Object System.Windows.Media.ScaleTransform ($size/256.0), ($size/256.0)))
    $background = New-Object System.Windows.Media.LinearGradientBrush ([System.Windows.Media.ColorConverter]::ConvertFromString('#234D3D')), ([System.Windows.Media.ColorConverter]::ConvertFromString('#102A24')), 90
    $drawing.DrawRoundedRectangle($background, $null, (New-Object System.Windows.Rect 8,8,240,240), 50, 50)
    $mint = New-Object System.Windows.Media.SolidColorBrush ([System.Windows.Media.ColorConverter]::ConvertFromString('#BAFFD9'))
    $strokeWidth = if ($size -le 32) { 16 } else { 12 }
    $corners = New-Object System.Windows.Media.Pen $mint, $strokeWidth
    $corners.StartLineCap = 'Round'; $corners.EndLineCap = 'Round'; $corners.LineJoin = 'Round'
    $drawing.DrawGeometry($null, $corners, [System.Windows.Media.Geometry]::Parse('M 56,94 L 56,56 L 94,56 M 162,56 L 200,56 L 200,94 M 200,162 L 200,200 L 162,200 M 94,200 L 56,200 L 56,162'))
    $marker = New-Object System.Windows.Media.LinearGradientBrush ([System.Windows.Media.ColorConverter]::ConvertFromString('#D8FF65')), ([System.Windows.Media.ColorConverter]::ConvertFromString('#49DD86')), 90
    $drawing.DrawGeometry($marker, $null, [System.Windows.Media.Geometry]::Parse('M 88,149 L 140,97 Q 146,91 152,97 L 171,116 Q 177,122 171,128 L 119,180 Z'))
    $drawing.DrawGeometry([System.Windows.Media.Brushes]::White, $null, [System.Windows.Media.Geometry]::Parse('M 86,154 L 114,182 L 79,190 Z'))
    $drawing.DrawGeometry($null, (New-Object System.Windows.Media.Pen $mint, 5), [System.Windows.Media.Geometry]::Parse('M 137,108 L 158,129'))
    $drawing.Pop(); $drawing.Close()
    $bitmap = New-Object System.Windows.Media.Imaging.RenderTargetBitmap $size,$size,96,96,([System.Windows.Media.PixelFormats]::Pbgra32)
    $bitmap.Render($visual)
    $encoder = New-Object System.Windows.Media.Imaging.PngBitmapEncoder
    $encoder.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
    $memory = New-Object System.IO.MemoryStream
    $encoder.Save($memory)
    $frames += ,($memory.ToArray()); $memory.Dispose()
    if ($size -eq 256) { [System.IO.File]::WriteAllBytes((Join-Path $root 'docs/app-icon.png'), $frames[-1]) }
}
$file = [System.IO.File]::Create((Join-Path $assets 'QuickCapture.ico'))
$writer = New-Object System.IO.BinaryWriter $file
try {
    $writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$sizes.Count)
    $offset = 6 + 16 * $sizes.Count
    for ($index = 0; $index -lt $sizes.Count; $index++) {
        $dimension = if ($sizes[$index] -eq 256) { 0 } else { $sizes[$index] }
        $writer.Write([byte]$dimension); $writer.Write([byte]$dimension)
        $writer.Write([byte]0); $writer.Write([byte]0); $writer.Write([uint16]1); $writer.Write([uint16]32)
        $writer.Write([uint32]$frames[$index].Length); $writer.Write([uint32]$offset)
        $offset += $frames[$index].Length
    }
    foreach ($frame in $frames) { $writer.Write([byte[]]$frame) }
} finally { $writer.Dispose(); $file.Dispose() }
Write-Output 'Generated Assets/QuickCapture.ico and docs/app-icon.png'

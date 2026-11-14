# Draws SnapCull's icon at every size Windows and the Microsoft Store need:
#   packaging/Assets/*.png   tiles and logos for the MSIX package
#   src/SnapCull/SnapCull.ico  icon for the .exe and windows
# Run with Windows PowerShell (it uses WPF to draw):  powershell -File packaging/make-assets.ps1

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName PresentationCore, PresentationFramework, WindowsBase

$root = Split-Path -Parent $PSScriptRoot
$assets = Join-Path $PSScriptRoot 'Assets'
New-Item -ItemType Directory -Force $assets | Out-Null

# Draws the logo into a 256x256 design space, scaled to fit the given box.
function Draw-Logo([System.Windows.Media.DrawingContext]$dc, [double]$x, [double]$y, [double]$size) {
    $s = $size / 256.0
    $dc.PushTransform([System.Windows.Media.TranslateTransform]::new($x, $y))
    $dc.PushTransform([System.Windows.Media.ScaleTransform]::new($s, $s))

    $bg = [System.Windows.Media.LinearGradientBrush]::new(
        [System.Windows.Media.Color]::FromRgb(0x4F, 0xA3, 0xFF),
        [System.Windows.Media.Color]::FromRgb(0x1D, 0x4E, 0xD8),
        [System.Windows.Point]::new(0, 0), [System.Windows.Point]::new(1, 1))
    $dc.DrawRoundedRectangle($bg, $null, [System.Windows.Rect]::new(12, 12, 232, 232), 52, 52)

    $white = [System.Windows.Media.Brushes]::White
    $small = $size -le 24

    if (-not $small) {
        # Viewfinder corner brackets
        $pen = [System.Windows.Media.Pen]::new($white, 14)
        $pen.StartLineCap = 'Round'; $pen.EndLineCap = 'Round'; $pen.LineJoin = 'Round'
        $a = 52; $b = 204; $l = 38
        foreach ($c in @(@($a, $a, 1, 1), @($b, $a, -1, 1), @($a, $b, 1, -1), @($b, $b, -1, -1))) {
            $g = [System.Windows.Media.StreamGeometry]::new()
            $ctx = $g.Open()
            $ctx.BeginFigure([System.Windows.Point]::new($c[0], $c[1] + $c[3] * $l), $false, $false)
            $ctx.LineTo([System.Windows.Point]::new($c[0], $c[1]), $true, $true)
            $ctx.LineTo([System.Windows.Point]::new($c[0] + $c[2] * $l, $c[1]), $true, $true)
            $ctx.Close()
            $dc.DrawGeometry($null, $pen, $g)
        }
    }

    # Check mark: "pick the keeper"
    $checkPen = [System.Windows.Media.Pen]::new($white, $(if ($small) { 34 } else { 24 }))
    $checkPen.StartLineCap = 'Round'; $checkPen.EndLineCap = 'Round'; $checkPen.LineJoin = 'Round'
    $check = [System.Windows.Media.StreamGeometry]::new()
    $ctx = $check.Open()
    if ($small) {
        $ctx.BeginFigure([System.Windows.Point]::new(66, 132), $false, $false)
        $ctx.LineTo([System.Windows.Point]::new(108, 174), $true, $true)
        $ctx.LineTo([System.Windows.Point]::new(190, 86), $true, $true)
    } else {
        $ctx.BeginFigure([System.Windows.Point]::new(90, 130), $false, $false)
        $ctx.LineTo([System.Windows.Point]::new(118, 158), $true, $true)
        $ctx.LineTo([System.Windows.Point]::new(170, 102), $true, $true)
    }
    $ctx.Close()
    $dc.DrawGeometry($null, $checkPen, $check)

    $dc.Pop(); $dc.Pop()
}

# Renders a width x height PNG with the logo filling $fraction of the shorter side, centred.
function Save-Png([string]$path, [int]$width, [int]$height, [double]$fraction = 1.0) {
    $visual = [System.Windows.Media.DrawingVisual]::new()
    $dc = $visual.RenderOpen()
    $logo = [Math]::Min($width, $height) * $fraction
    Draw-Logo $dc (($width - $logo) / 2) (($height - $logo) / 2) $logo
    $dc.Close()
    $bitmap = [System.Windows.Media.Imaging.RenderTargetBitmap]::new($width, $height, 96, 96, [System.Windows.Media.PixelFormats]::Pbgra32)
    $bitmap.Render($visual)
    $encoder = [System.Windows.Media.Imaging.PngBitmapEncoder]::new()
    $encoder.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
    $stream = [System.IO.File]::Create($path)
    try { $encoder.Save($stream) } finally { $stream.Dispose() }
}

# MSIX tiles at 100% and 200% scale (Windows picks the right one via resources.pri).
foreach ($scale in 100, 200) {
    $f = $scale / 100
    Save-Png "$assets\Square44x44Logo.scale-$scale.png"   (44 * $f)  (44 * $f)
    Save-Png "$assets\Square150x150Logo.scale-$scale.png" (150 * $f) (150 * $f) 0.66
    Save-Png "$assets\Wide310x150Logo.scale-$scale.png"   (310 * $f) (150 * $f) 0.66
    Save-Png "$assets\StoreLogo.scale-$scale.png"         (50 * $f)  (50 * $f)
}
# Taskbar / Start / File Explorer icon sizes, with and without the plate.
foreach ($t in 16, 24, 32, 48, 256) {
    Save-Png "$assets\Square44x44Logo.targetsize-$t.png" $t $t
    Save-Png "$assets\Square44x44Logo.targetsize-$t`_altform-unplated.png" $t $t
}

# .ico for the exe: PNG-compressed entries (supported since Windows Vista).
$sizes = 16, 24, 32, 48, 64, 128, 256
$pngs = foreach ($t in $sizes) {
    $tmp = [System.IO.Path]::GetTempFileName()
    Save-Png $tmp $t $t
    $bytes = [System.IO.File]::ReadAllBytes($tmp)
    Remove-Item $tmp
    , $bytes
}
$ico = [System.IO.MemoryStream]::new()
$w = [System.IO.BinaryWriter]::new($ico)
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $dim = if ($sizes[$i] -ge 256) { 0 } else { $sizes[$i] }
    $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([uint16]1); $w.Write([uint16]32)
    $w.Write([uint32]$pngs[$i].Length); $w.Write([uint32]$offset)
    $offset += $pngs[$i].Length
}
foreach ($p in $pngs) { $w.Write($p) }
$w.Flush()
[System.IO.File]::WriteAllBytes((Join-Path $root 'src\SnapCull\SnapCull.ico'), $ico.ToArray())

# A large copy for the README and the Store listing.
Save-Png "$PSScriptRoot\logo-512.png" 512 512

Write-Host "Assets written to $assets and src\SnapCull\SnapCull.ico"

# Rasterises the NexusShot brand mark to the PNG and ICO sizes the app ships.
#
# The sibling Nexus apps generate icons with `cargo tauri icon icon-source.svg`. NexusShot has no
# Tauri toolchain, and adding one to rasterise a single SVG would be a heavier dependency than the
# icon is worth. So this script redraws icon-source.svg's geometry with System.Drawing, which the
# app already depends on. Keep the two in sync: icon-source.svg remains the design source of truth.
#
#   pwsh assets/icons/export-icons.ps1

param(
    [string]$OutDir = $PSScriptRoot
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

# Palette shared with the NexusSpace / NexusSearch / NexusVoice / NexusSend family.
$TileSteel   = '#3a4652'
$SplitLight  = '#53caf6'
$SplitMid    = '#46bae3'
$GlyphInk    = '#18222b'

function ConvertTo-Color([string]$Hex) {
    return [System.Drawing.ColorTranslator]::FromHtml($Hex)
}

function New-RoundedRectPath([float]$X, [float]$Y, [float]$W, [float]$H, [float]$R) {
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $R * 2
    $path.AddArc($X, $Y, $d, $d, 180, 90)
    $path.AddArc($X + $W - $d, $Y, $d, $d, 270, 90)
    $path.AddArc($X + $W - $d, $Y + $H - $d, $d, $d, 0, 90)
    $path.AddArc($X, $Y + $H - $d, $d, $d, 90, 90)
    $path.CloseFigure()
    return $path
}

# The SVG's `split` gradient puts two stops at offset 0.5, producing a hard edge along the 135°
# diagonal. GDI+ expresses that as a ColorBlend with coincident positions rather than as a plain
# two-colour LinearGradientBrush, which would interpolate smoothly across the whole tile.
function New-SplitBrush([float]$Size) {
    $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        (New-Object System.Drawing.PointF 0, 0),
        (New-Object System.Drawing.PointF $Size, $Size),
        (ConvertTo-Color $SplitLight),
        (ConvertTo-Color $TileSteel))

    $blend = New-Object System.Drawing.Drawing2D.ColorBlend 4
    $blend.Colors = @(
        (ConvertTo-Color $SplitLight),
        (ConvertTo-Color $SplitMid),
        (ConvertTo-Color $TileSteel),
        (ConvertTo-Color $TileSteel))
    # 0.4999/0.5 rather than 0.5/0.5: GDI+ rejects a ColorBlend whose positions are not strictly
    # increasing, so the hard stop is approximated by a sub-pixel ramp.
    $blend.Positions = @(0.0, 0.4999, 0.5, 1.0)
    $brush.InterpolationColors = $blend
    return $brush
}

# Draws the mark into a Size x Size bitmap. All geometry is authored in the SVG's 1024 viewBox and
# scaled down, so every exported size is the same drawing rather than a hand-tuned variant.
function New-IconBitmap([int]$Size) {
    $bmp = New-Object System.Drawing.Bitmap $Size, $Size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    try {
        $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $g.Clear([System.Drawing.Color]::Transparent)
        $g.ScaleTransform(($Size / 1024.0), ($Size / 1024.0))

        # Full-bleed rounded tile: x=32 w=960 rx=220, matching the sibling icon-source.svg files.
        $tile = New-RoundedRectPath 32 32 960 960 220
        try {
            $base = New-Object System.Drawing.SolidBrush (ConvertTo-Color $TileSteel)
            $g.FillPath($base, $tile)
            $base.Dispose()

            $split = New-SplitBrush 1024
            $g.FillPath($split, $tile)
            $split.Dispose()
        } finally {
            $tile.Dispose()
        }

        # Crop marks: offset corner brackets straddling the diagonal.
        $pen = New-Object System.Drawing.Pen ((ConvertTo-Color $GlyphInk), 84)
        $pen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
        $pen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
        $pen.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round
        try {
            # M300 520 V300 H520 -- corner on the diagonal
            $topLeft = New-Object System.Drawing.Drawing2D.GraphicsPath
            $topLeft.AddLine(300, 520, 300, 300)
            $topLeft.AddLine(300, 300, 520, 300)
            $g.DrawPath($pen, $topLeft)
            $topLeft.Dispose()

            # M504 724 H724 V504 -- corner on the diagonal
            $bottomRight = New-Object System.Drawing.Drawing2D.GraphicsPath
            $bottomRight.AddLine(504, 724, 724, 724)
            $bottomRight.AddLine(724, 724, 724, 504)
            $g.DrawPath($pen, $bottomRight)
            $bottomRight.Dispose()
        } finally {
            $pen.Dispose()
        }
    } finally {
        $g.Dispose()
    }
    return $bmp
}

function Save-Png([int]$Size, [string]$Path) {
    $bmp = New-IconBitmap $Size
    try { $bmp.Save($Path, [System.Drawing.Imaging.ImageFormat]::Png) }
    finally { $bmp.Dispose() }
}

# Writes a PNG-compressed .ico. Windows accepts embedded PNG frames for Vista and later; a 256px
# frame records its dimension as 0 because the ICONDIRENTRY width/height fields are single bytes.
function Save-Ico([string[]]$PngPaths, [string]$IcoPath) {
    $frames = @()
    foreach ($path in $PngPaths) { $frames += , ([System.IO.File]::ReadAllBytes($path)) }

    $stream = [System.IO.File]::Create($IcoPath)
    $writer = New-Object System.IO.BinaryWriter($stream)
    try {
        $writer.Write([UInt16]0)                  # reserved
        $writer.Write([UInt16]1)                  # type: icon
        $writer.Write([UInt16]$PngPaths.Length)

        $offset = 6 + (16 * $PngPaths.Length)
        for ($i = 0; $i -lt $PngPaths.Length; $i++) {
            $image = [System.Drawing.Image]::FromFile($PngPaths[$i])
            $w = if ($image.Width -ge 256) { 0 } else { [byte]$image.Width }
            $h = if ($image.Height -ge 256) { 0 } else { [byte]$image.Height }
            $image.Dispose()

            $writer.Write([byte]$w)
            $writer.Write([byte]$h)
            $writer.Write([byte]0)                # palette entries
            $writer.Write([byte]0)                # reserved
            $writer.Write([UInt16]1)              # colour planes
            $writer.Write([UInt16]32)             # bits per pixel
            $writer.Write([UInt32]$frames[$i].Length)
            $writer.Write([UInt32]$offset)
            $offset += $frames[$i].Length
        }
        foreach ($bytes in $frames) { $writer.Write($bytes) }
    } finally {
        $writer.Dispose()
        $stream.Dispose()
    }
}

New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

$icoSizes = @(16, 32, 48, 64, 128, 256)
$icoPngs = @()
foreach ($size in $icoSizes) {
    $path = Join-Path $OutDir "nexus-shot-$size.png"
    Save-Png $size $path
    $icoPngs += $path
}

Save-Png 1024 (Join-Path $OutDir 'nexus-shot-1024.png')
Save-Ico $icoPngs (Join-Path $OutDir 'nexus-shot.ico')

Write-Output "Wrote nexus-shot.ico and $($icoSizes.Count + 1) PNGs to $OutDir"

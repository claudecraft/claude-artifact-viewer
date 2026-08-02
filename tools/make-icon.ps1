# Generates app.ico (16/32/48/64/128/256, PNG-compressed entries).
# Design: dark rounded square, ivory document page, warm 4-point spark.
# 16px and 32px are drawn dedicated (downscales go muddy); larger sizes
# downscale from the 256 master.

Add-Type -AssemblyName System.Drawing

$repo = Split-Path $PSScriptRoot -Parent
$out  = Join-Path $repo 'app.ico'

$bgColor    = [System.Drawing.Color]::FromArgb(255, 31, 31, 34)
$pageColor  = [System.Drawing.Color]::FromArgb(255, 242, 237, 227)
$lineColor  = [System.Drawing.Color]::FromArgb(255, 184, 178, 164)
$sparkColor = [System.Drawing.Color]::FromArgb(255, 224, 117, 74)

function New-RoundedPath([float]$x, [float]$y, [float]$w, [float]$h, [float]$r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $r * 2
    $p.AddArc($x, $y, $d, $d, 180, 90)
    $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $p.CloseFigure()
    return $p
}

function New-StarPoints([float]$cx, [float]$cy, [float]$outer, [float]$inner) {
    $pts = @()
    for ($i = 0; $i -lt 8; $i++) {
        $ang = ($i * 45 - 90) * [Math]::PI / 180
        $r = if ($i % 2 -eq 0) { $outer } else { $inner }
        $pts += New-Object System.Drawing.PointF(($cx + $r * [Math]::Cos($ang)), ($cy + $r * [Math]::Sin($ang)))
    }
    return $pts
}

function New-Canvas([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap($size, $size)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.PixelOffsetMode = 'HighQuality'
    return $bmp, $g
}

# --- 256 master ---
$bmp256, $g = New-Canvas 256
$g.FillPath((New-Object System.Drawing.SolidBrush $bgColor), (New-RoundedPath 8 8 240 240 52))
$g.FillPath((New-Object System.Drawing.SolidBrush $pageColor), (New-RoundedPath 92 58 110 144 14))
$lineBrush = New-Object System.Drawing.SolidBrush $lineColor
foreach ($ln in @(@(108, 100, 78), @(108, 126, 78), @(108, 152, 50))) {
    $g.FillPath($lineBrush, (New-RoundedPath $ln[0] $ln[1] $ln[2] 10 5))
}
$g.FillPolygon((New-Object System.Drawing.SolidBrush $sparkColor), (New-StarPoints 84 84 46 15))
$g.Dispose()

# --- 32 dedicated ---
$bmp32, $g = New-Canvas 32
$g.FillPath((New-Object System.Drawing.SolidBrush $bgColor), (New-RoundedPath 0 0 32 32 8))
$g.FillPath((New-Object System.Drawing.SolidBrush $pageColor), (New-RoundedPath 13 8 13 18 3))
$g.FillPolygon((New-Object System.Drawing.SolidBrush $sparkColor), (New-StarPoints 11 12 9 3))
$g.Dispose()

# --- 16 dedicated ---
$bmp16, $g = New-Canvas 16
$g.FillPath((New-Object System.Drawing.SolidBrush $bgColor), (New-RoundedPath 0 0 16 16 4))
$g.FillPolygon((New-Object System.Drawing.SolidBrush $sparkColor), (New-StarPoints 8 8 6 2))
$g.Dispose()

function Resize([System.Drawing.Bitmap]$src, [int]$size) {
    $dst = New-Object System.Drawing.Bitmap($size, $size)
    $g = [System.Drawing.Graphics]::FromImage($dst)
    $g.InterpolationMode = 'HighQualityBicubic'
    $g.SmoothingMode = 'AntiAlias'
    $g.DrawImage($src, 0, 0, $size, $size)
    $g.Dispose()
    return $dst
}

# NB: string keys on purpose — integer keys on an ordered dictionary are
# treated as positional indexes by PowerShell's [] operator
$images = [ordered]@{
    '16'  = $bmp16
    '32'  = $bmp32
    '48'  = (Resize $bmp256 48)
    '64'  = (Resize $bmp256 64)
    '128' = (Resize $bmp256 128)
    '256' = $bmp256
}

# --- encode entries: BMP for <=64 (GDI+/legacy compat), PNG for 128/256 ---
function Get-BmpEntryBytes([System.Drawing.Bitmap]$bmp) {
    $w = $bmp.Width; $h = $bmp.Height
    $rect = New-Object System.Drawing.Rectangle(0, 0, $w, $h)
    $data = $bmp.LockBits($rect, 'ReadOnly', [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $px = New-Object byte[] ($data.Stride * $h)
    [System.Runtime.InteropServices.Marshal]::Copy($data.Scan0, $px, 0, $px.Length)
    $bmp.UnlockBits($data)

    $ms = New-Object System.IO.MemoryStream
    $bw = New-Object System.IO.BinaryWriter($ms)
    # BITMAPINFOHEADER, height doubled for the (all-zero) AND mask
    $bw.Write([uint32]40); $bw.Write([int]$w); $bw.Write([int]($h * 2))
    $bw.Write([uint16]1); $bw.Write([uint16]32); $bw.Write([uint32]0)
    $bw.Write([uint32]($w * $h * 4)); $bw.Write([int]0); $bw.Write([int]0)
    $bw.Write([uint32]0); $bw.Write([uint32]0)
    for ($row = $h - 1; $row -ge 0; $row--) {   # XOR data, bottom-up BGRA
        $bw.Write($px, $row * $data.Stride, $w * 4)
    }
    $maskRow = [Math]::Ceiling($w / 32.0) * 4   # AND mask rows pad to 32 bits
    $bw.Write((New-Object byte[] ($maskRow * $h)))
    $bw.Flush()
    $bytes = $ms.ToArray()
    $bw.Dispose(); $ms.Dispose()
    return , $bytes
}

$blobs = [ordered]@{}
foreach ($size in $images.Keys) {
    if ([int]$size -le 64) {
        $blobs[$size] = Get-BmpEntryBytes $images[$size]
    }
    else {
        $ms = New-Object System.IO.MemoryStream
        $images[$size].Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
        $blobs[$size] = $ms.ToArray()
        $ms.Dispose()
    }
}

$fs = [System.IO.File]::Create($out)
$w = New-Object System.IO.BinaryWriter($fs)
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$blobs.Count)
$offset = 6 + 16 * $blobs.Count
foreach ($size in $blobs.Keys) {
    $b = $blobs[$size]
    $dim = if ([int]$size -eq 256) { 0 } else { [int]$size }
    $w.Write([byte]$dim); $w.Write([byte]$dim)
    $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([uint16]1); $w.Write([uint16]32)
    $w.Write([uint32]$b.Length); $w.Write([uint32]$offset)
    $offset += $b.Length
}
foreach ($size in $blobs.Keys) { $w.Write($blobs[$size]) }
$w.Dispose(); $fs.Dispose()

foreach ($img in $images.Values) { $img.Dispose() }
Write-Host "Wrote $out ($([math]::Round((Get-Item $out).Length / 1kb, 1)) KB, $($blobs.Count) sizes)"

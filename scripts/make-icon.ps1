<#
  Renders the Dialvert app icon (multi-size .ico with PNG entries) with System.Drawing.
  Output: src\Dialvert.App\Assets\dialvert.ico
#>
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$root = Split-Path -Parent $PSScriptRoot
$outDir = Join-Path $root 'src\Dialvert.App\Assets'
New-Item -ItemType Directory -Force $outDir | Out-Null

function Render([int]$s) {
    $bmp = New-Object System.Drawing.Bitmap $s, $s, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.Clear([System.Drawing.Color]::Transparent)

    # Graphite tile, slightly rounded
    $r = [single]($s * 0.18)
    $m = [single]([Math]::Max(0.5, $s * 0.03))
    $w = [single]($s - 2 * $m)
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddArc($m, $m, $r, $r, 180, 90)
    $path.AddArc($m + $w - $r, $m, $r, $r, 270, 90)
    $path.AddArc($m + $w - $r, $m + $w - $r, $r, $r, 0, 90)
    $path.AddArc($m, $m + $w - $r, $r, $r, 90, 90)
    $path.CloseFigure()
    $g.FillPath((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 18, 18, 16))), $path)

    # The mark (same as the title bar): a 270 degree dial arc, a square in the gap, a center dot.
    # Drawn in an 18-unit box scaled to ~68 % of the tile.
    $u = [single]($s * 0.68 / 18)
    $ox = [single](($s - 18 * $u) / 2); $oy = $ox
    $signal = [System.Drawing.Color]::FromArgb(255, 220, 242, 60)
    $pen = New-Object System.Drawing.Pen $signal, ([single]([Math]::Max(1.6, 3.2 * $u)))
    $g.DrawArc($pen, $ox + 2 * $u, $oy + 2 * $u, 14 * $u, 14 * $u, 270, 270)
    $sq = New-Object System.Drawing.SolidBrush $signal
    $g.FillRectangle($sq, $ox + 1 * $u, $oy + 1 * $u, 4 * $u, 4 * $u)
    if ($s -ge 24) {
        $dot = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 239, 236, 228))
        $g.FillEllipse($dot, $ox + 7 * $u, $oy + 7 * $u, 4 * $u, 4 * $u)
    }

    $g.Dispose()
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    return ,$ms.ToArray()
}

$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
$pngs = @($sizes | ForEach-Object { ,(Render $_) })

$out = New-Object System.IO.MemoryStream
$bw = New-Object System.IO.BinaryWriter $out
$bw.Write([UInt16]0); $bw.Write([UInt16]1); $bw.Write([UInt16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]; $len = $pngs[$i].Length
    $dim = if ($s -ge 256) { 0 } else { $s }
    $bw.Write([byte]$dim); $bw.Write([byte]$dim); $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([UInt16]1); $bw.Write([UInt16]32); $bw.Write([UInt32]$len); $bw.Write([UInt32]$offset)
    $offset += $len
}
foreach ($p in $pngs) { $bw.Write($p) }
$bw.Flush()
[System.IO.File]::WriteAllBytes((Join-Path $outDir 'dialvert.ico'), $out.ToArray())
[System.IO.File]::WriteAllBytes((Join-Path $outDir 'dialvert-256.png'), $pngs[-1])
Write-Host "Icon written to $outDir"

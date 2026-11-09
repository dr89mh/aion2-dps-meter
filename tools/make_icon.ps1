# Draws the tray/exe icon (three rising bars, like a damage meter) and writes assets\meter.ico with PNG frames 16..256.
Add-Type -AssemblyName System.Drawing
$out = Join-Path $PSScriptRoot "..\assets\meter.ico"
$sizes = 16, 24, 32, 48, 64, 256
$pngs = foreach ($s in $sizes) {
    $bmp = New-Object System.Drawing.Bitmap $s, $s, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $k = $s / 32.0
    $bg = New-Object System.Drawing.Drawing2D.GraphicsPath
    $r = 7 * $k; $w = $s - 1
    $bg.AddArc(0, 0, 2*$r, 2*$r, 180, 90); $bg.AddArc($w-2*$r, 0, 2*$r, 2*$r, 270, 90)
    $bg.AddArc($w-2*$r, $w-2*$r, 2*$r, 2*$r, 0, 90); $bg.AddArc(0, $w-2*$r, 2*$r, 2*$r, 90, 90); $bg.CloseFigure()
    $g.FillPath((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 24, 26, 34))), $bg)
    $colors = @([System.Drawing.Color]::FromArgb(255, 181, 101, 217), [System.Drawing.Color]::FromArgb(255, 91, 141, 239),
                [System.Drawing.Color]::FromArgb(255, 240, 180, 60))
    $heights = 9, 15, 22
    for ($i = 0; $i -lt 3; $i++) {
        $x = (5 + $i * 8) * $k; $h = $heights[$i] * $k
        $g.FillRectangle((New-Object System.Drawing.SolidBrush $colors[$i]), [single]$x, [single](27*$k - $h), [single](6*$k), [single]$h)
    }
    $g.Dispose()
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose()
    ,$ms.ToArray()
}
$fs = [System.IO.File]::Create($out); $bw = New-Object System.IO.BinaryWriter $fs
$bw.Write([UInt16]0); $bw.Write([UInt16]1); $bw.Write([UInt16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]; $d = $pngs[$i]
    $bw.Write([byte]($(if ($s -ge 256) { 0 } else { $s }))); $bw.Write([byte]($(if ($s -ge 256) { 0 } else { $s })))
    $bw.Write([byte]0); $bw.Write([byte]0); $bw.Write([UInt16]1); $bw.Write([UInt16]32)
    $bw.Write([UInt32]$d.Length); $bw.Write([UInt32]$offset); $offset += $d.Length
}
foreach ($d in $pngs) { $bw.Write($d) }
$bw.Close()
Write-Output "geschrieben: $out"

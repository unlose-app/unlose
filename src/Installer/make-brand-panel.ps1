# Generates the installer brand side-panel bitmap (brand-panel.bmp)
# 130x280, vertical blue gradient + logo + wordmark, used by LanguageDlg and ExitDlg.
Add-Type -AssemblyName System.Drawing
$W = 130; $H = 280
$bmp = New-Object System.Drawing.Bitmap $W, $H
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.SmoothingMode = 'AntiAlias'
$g.TextRenderingHint = 'AntiAliasGridFit'

$rect = New-Object System.Drawing.Rectangle 0, 0, $W, $H
$c1 = [System.Drawing.Color]::FromArgb(255, 11, 79, 138)
$c2 = [System.Drawing.Color]::FromArgb(255, 56, 189, 248)
$brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush $rect, $c1, $c2, ([single]90)
$g.FillRectangle($brush, $rect)

# soft halo behind the logo
$glow = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(36, 255, 255, 255))
$g.FillEllipse($glow, 25, 26, 80, 80)

$logoPath = Join-Path $PSScriptRoot '..\Unlose.UI\Resources\app-icon.png'
$logo = [System.Drawing.Image]::FromFile((Resolve-Path $logoPath))
$g.DrawImage($logo, 41, 36, 48, 48)
$logo.Dispose()

$sf = New-Object System.Drawing.StringFormat
$sf.Alignment = 'Center'
$f1 = New-Object System.Drawing.Font 'Segoe UI Semibold', 20
$f2 = New-Object System.Drawing.Font 'Segoe UI', 8.5
$white = [System.Drawing.Brushes]::White
$tint = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 207, 232, 251))
$g.DrawString('unlose', $f1, $white, (New-Object System.Drawing.RectangleF 0, 102, $W, 34), $sf)
$g.DrawString('Snapshot time machine', $f2, $tint, (New-Object System.Drawing.RectangleF 0, 134, $W, 16), $sf)

$g.Dispose()
$out = Join-Path $PSScriptRoot 'brand-panel.bmp'
$bmp.Save($out, [System.Drawing.Imaging.ImageFormat]::Bmp)
$bmp.Dispose()
Write-Output "created: $out"

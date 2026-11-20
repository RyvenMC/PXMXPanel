# Baut PXMX.exe mit dem in Windows eingebauten C#-Compiler (.NET Framework 4.x) – keine Installation nötig.
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$ico = Join-Path $root 'src\app.ico'

Add-Type -AssemblyName System.Drawing
$sizes = 16, 32, 48, 256
$pngs = foreach ($s in $sizes) {
    $bmp = New-Object System.Drawing.Bitmap $s, $s
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'; $g.TextRenderingHint = 'AntiAliasGridFit'
    $rect = New-Object System.Drawing.Rectangle 0, 0, $s, $s
    $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush $rect, ([System.Drawing.Color]::FromArgb(22, 32, 34)), ([System.Drawing.Color]::FromArgb(38, 67, 51)), 45
    $r = [int]($s * 0.22); $d = $r * 2
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddArc(0, 0, $d, $d, 180, 90); $path.AddArc($s - $d - 1, 0, $d, $d, 270, 90)
    $path.AddArc($s - $d - 1, $s - $d - 1, $d, $d, 0, 90); $path.AddArc(0, $s - $d - 1, $d, $d, 90, 90); $path.CloseFigure()
    $g.FillPath($brush, $path)
    $pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(120, 104, 174, 127)), ([float][Math]::Max(1, $s * 0.025))
    $g.DrawPath($pen, $path)
    $font = New-Object System.Drawing.Font 'Segoe UI', ([float]($s * 0.42)), ([System.Drawing.FontStyle]::Bold), ([System.Drawing.GraphicsUnit]::Pixel)
    $sf = New-Object System.Drawing.StringFormat; $sf.Alignment = 'Center'; $sf.LineAlignment = 'Center'
    $textBrush = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(224, 224, 244, 229))
    $g.DrawString('PX', $font, $textBrush, (New-Object System.Drawing.RectangleF 0, 0, $s, $s), $sf)
    $brush.Dispose(); $pen.Dispose(); $font.Dispose(); $sf.Dispose(); $textBrush.Dispose(); $path.Dispose()
    $g.Dispose()
    $ms = New-Object System.IO.MemoryStream; $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose()
    , $ms.ToArray()
}
$fs = [System.IO.File]::Create($ico); $w = New-Object System.IO.BinaryWriter $fs
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]; $dim = if ($s -ge 256) { 0 } else { $s }
    $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([uint16]1); $w.Write([uint16]32); $w.Write([uint32]$pngs[$i].Length); $w.Write([uint32]$offset)
    $offset += $pngs[$i].Length
}
foreach ($p in $pngs) { $w.Write($p) }
$w.Close()

# Keep the internal assembly name separate from the RyvenPanel app and its updates.
$assemblyOut = Join-Path $root 'PXMXServerSuite.exe'
$out = Join-Path $root 'PXMX.exe'
$manifest = Join-Path $root 'src\app.manifest'
$sources = Join-Path $root 'src\*.cs'
# A running panel locks its exe, but it can be renamed.
if (Test-Path $assemblyOut) {
    try { [IO.File]::Open($assemblyOut, 'Open', 'ReadWrite', 'None').Close() }
    catch { Move-Item $assemblyOut (Join-Path $root ("PXMXServerSuite." + [DateTime]::Now.Ticks + ".old")) }
}
& $csc /nologo /target:winexe /optimize+ /platform:anycpu "/out:$assemblyOut" `
    "/win32icon:$ico" "/win32manifest:$manifest" `
    /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll `
    /r:System.Web.Extensions.dll /r:System.Security.dll /r:System.Management.dll `
    "/resource:$(Join-Path $root 'gateway\gateway.py'),gateway.py" "/resource:$(Join-Path $root 'gateway\eco.py'),eco.py" `
    "/resource:$(Join-Path $root 'gateway\mod.py'),mod.py" "/resource:$(Join-Path $root 'gateway\finanzen.py'),finanzen.py" "$sources"
if ($LASTEXITCODE -ne 0) { throw "Build fehlgeschlagen" }
if (Test-Path $out) {
    try {
        [IO.File]::Open($out, 'Open', 'ReadWrite', 'None').Close()
        Remove-Item $out -Force
    }
    catch { Move-Item $out (Join-Path $root ("PXMX." + [DateTime]::Now.Ticks + ".old")) }
}
Move-Item $assemblyOut $out
Write-Host "OK -> $out"

# Installer: setup\Setup.cs + the version, with the freshly built app embedded.
$version = [regex]::Match((Get-Content (Join-Path $root 'src\Version.cs') -Raw), 'Version = "([^"]+)"').Groups[1].Value
$dist = Join-Path $root 'dist'
New-Item -ItemType Directory -Force $dist | Out-Null
$setup = Join-Path $dist "PXMX-ServerSuite-Setup-$version.exe"
& $csc /nologo /target:winexe /optimize+ /platform:anycpu "/out:$setup" `
    "/win32icon:$ico" "/win32manifest:$manifest" `
    /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll `
    "/resource:$out,PXMXServerSuite.exe" (Join-Path $root 'setup\Setup.cs') (Join-Path $root 'src\Version.cs')
if ($LASTEXITCODE -ne 0) { throw "Setup-Build fehlgeschlagen" }
Write-Host "OK -> $setup"

Write-Warning "Automatische Updates sind in diesem eigenständigen Projekt deaktiviert."

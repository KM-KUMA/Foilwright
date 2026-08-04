#requires -Version 5.1
<#
Foilwright — マジックカラーが印刷経路を素通りするかを実測する

D-021 で「Ghostscript は RGB を変えない」ことは確認したが、その手前の
Windows PostScript ドライバがカラーマネジメントを適用する懸念が残っていた
(DOMAIN 6.3)。本スクリプトはパレットの magic_rgb と同じ値で矩形を描いて
印刷し、生成された PostScript を後段で照合できるようにする。

  powershell -ExecutionPolicy Bypass -File tools\print-magic-test.ps1
#>

param([string]$PrinterName = 'Foilwright MD-5500')

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

# palette/default.yaml の magic_rgb と同じ値。ここを変えるときは向こうも直す。
$patches = @(
    @{ Name = 'white';            Rgb = @(230, 230, 230) },
    @{ Name = 'metallic_gold';    Rgb = @(225, 160, 0) },
    @{ Name = 'metallic_silver';  Rgb = @(189, 193, 197) },
    @{ Name = 'metallic_magenta'; Rgb = @(163, 36, 115) },
    @{ Name = 'metallic_cyan';    Rgb = @(0, 176, 201) },
    @{ Name = 'black';            Rgb = @(0, 0, 0) }
)

$doc = New-Object System.Drawing.Printing.PrintDocument
$doc.PrinterSettings.PrinterName = $PrinterName
if (-not $doc.PrinterSettings.IsValid) { throw "プリンタが見つからない: $PrinterName" }
$doc.DocumentName = 'Foilwright magic colour probe'

$handler = {
    param($sender, $e)
    $g = $e.Graphics
    # 補間や色補正を挟ませない。値をそのまま置くことが目的。
    $g.CompositingMode = [System.Drawing.Drawing2D.CompositingMode]::SourceCopy
    $g.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighSpeed
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::None
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::NearestNeighbor

    $x = 60
    foreach ($p in $script:patches) {
        $colour = [System.Drawing.Color]::FromArgb($p.Rgb[0], $p.Rgb[1], $p.Rgb[2])
        $brush = New-Object System.Drawing.SolidBrush $colour
        # 100x100 の 1/100 インチ単位 = 1 インチ角
        $g.FillRectangle($brush, $x, 100, 100, 100)
        $brush.Dispose()
        $x += 120
    }
}

$doc.add_PrintPage($handler)
$doc.Print()
Write-Host "印刷を送出した: $PrinterName"
foreach ($p in $patches) {
    Write-Host ("  {0,-18} {1}" -f $p.Name, ($p.Rgb -join ','))
}

#requires -Version 5.1
<#
Foilwright — 印字位置の確認用テストページ

Ghostscript は用紙全面を描き、プリンタが刷れるのは印字可能領域だけである。
その対応付け(切り出し)が正しいかを、刷った紙を定規で測って判定できる形にする。

黒 1 色のみを使う。位置が合うまで多色や白下地を刷るのはリボンの無駄になる。

置くもの:
  - 印字可能領域の四隅に 5mm の L 字マーク(枠がどこに来るかが分かる)
  - 中央に 5mm の四角(左右上下の対称性が分かる)
  - 左上から 10mm / 20mm の位置に目盛り(ずれ量を読み取れる)

  powershell -ExecutionPolicy Bypass -File tools\print-position-test.ps1
#>

param([string]$PrinterName = 'Foilwright MD-5500')

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$doc = New-Object System.Drawing.Printing.PrintDocument
$doc.PrinterSettings.PrinterName = $PrinterName
if (-not $doc.PrinterSettings.IsValid) { throw "プリンタが見つからない: $PrinterName" }
$doc.DocumentName = 'Foilwright position test'

# ハンドラは別スコープで実行されるため、必要な値は closure で閉じ込める。
# System.Drawing の既定単位は 1/100 インチ。1mm = 100/25.4 単位。
$mm = 100.0 / 25.4

$handler = {
    param($sender, $e)

    $g = $e.Graphics
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::None
    $black = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::Black)

    $left = [single]$e.MarginBounds.Left
    $top = [single]$e.MarginBounds.Top
    $right = [single]$e.MarginBounds.Right
    $bottom = [single]$e.MarginBounds.Bottom

    $len = [single](5 * $mm)
    $thick = [single](1 * $mm)

    # 四隅の L 字。印字可能領域の枠がどこに落ちるかを示す。
    $g.FillRectangle($black, $left, $top, $len, $thick)
    $g.FillRectangle($black, $left, $top, $thick, $len)

    $g.FillRectangle($black, [single]($right - $len), $top, $len, $thick)
    $g.FillRectangle($black, [single]($right - $thick), $top, $thick, $len)

    $g.FillRectangle($black, $left, [single]($bottom - $thick), $len, $thick)
    $g.FillRectangle($black, $left, [single]($bottom - $len), $thick, $len)

    $g.FillRectangle($black, [single]($right - $len), [single]($bottom - $thick), $len, $thick)
    $g.FillRectangle($black, [single]($right - $thick), [single]($bottom - $len), $thick, $len)

    # 中央の四角。左右上下の対称性を見る。
    $side = [single](5 * $mm)
    $cx = [single]($left + ($right - $left) / 2 - $side / 2)
    $cy = [single]($top + ($bottom - $top) / 2 - $side / 2)
    $g.FillRectangle($black, $cx, $cy, $side, $side)

    # 左上から 10mm / 20mm の目盛り。ずれ量を定規で読む。
    foreach ($d in 10, 20) {
        $off = [single]($d * $mm)
        $g.FillRectangle($black, [single]($left + $off), $top, $thick, [single](3 * $mm))
        $g.FillRectangle($black, $left, [single]($top + $off), [single](3 * $mm), $thick)
    }

    $black.Dispose()
}.GetNewClosure()

$doc.add_PrintPage($handler)
$doc.Print()
Write-Host "位置確認ページを送出した: $PrinterName"
Write-Host '刷れたら四隅の L 字と中央の四角の位置を測る。'

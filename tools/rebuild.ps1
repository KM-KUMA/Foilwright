<#
.SYNOPSIS
  トレイアプリを安全に止めてビルドし、起こし直す(DOMAIN §15.12)。

.DESCRIPTION
  ビルドは Foilwright.Tray.exe を上書きするため、トレイが動いていると失敗する。
  かといって止めたまま忘れると、受け取り手のいない印刷ジョブがキューの先頭に
  刺さり、以降のジョブがすべて止まる。利用者からは「印刷しても何も起きない」
  ように見える。

  この手順を毎回やる:
    1. 印刷キューを見る(**消さない**。件数を報告するだけ)
    2. トレイを止める
    3. ビルドする
    4. トレイを起こす
    5. 名前付きパイプができたことを確認する

  Claude Code から作業するときは .claude/hooks/tray_guard.py が同じことを
  自動でやる。このスクリプトは IDE からビルドする場合など、フックが効かない
  経路のための明示的な入口。

.PARAMETER ClearQueue
  刺さったジョブを削除する。**既定では削除しない** — スプールファイル(その
  ジョブが実際に送った PostScript)は不具合を追うときの証拠になるため。
  不具合を追っていないと分かっているときだけ付ける。

.EXAMPLE
  .\tools\rebuild.ps1
  .\tools\rebuild.ps1 -ClearQueue
#>
[CmdletBinding()]
param(
    [switch]$ClearQueue,
    [string]$Printer = 'Foilwright MD-5500'
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$trayExe = Join-Path $repo 'src\Foilwright.Tray\bin\Debug\net10.0-windows\Foilwright.Tray.exe'

Write-Host '=== 1. 印刷キュー ===' -ForegroundColor Cyan
$jobs = @(Get-PrintJob -PrinterName $Printer -ErrorAction SilentlyContinue)
if ($jobs.Count -eq 0) {
    Write-Host '  0 件'
} else {
    Write-Host "  $($jobs.Count) 件残っている:" -ForegroundColor Yellow
    $jobs | ForEach-Object { Write-Host "    Id=$($_.Id) $($_.JobStatus)" }
    if ($ClearQueue) {
        $jobs | ForEach-Object { Remove-PrintJob -PrinterName $Printer -ID $_.Id -ErrorAction Continue }
        Start-Sleep -Seconds 2
        Write-Host '  削除した' -ForegroundColor Yellow
    } else {
        Write-Host '  削除していない(-ClearQueue で消せる。スプールが証拠になることがある)'
    }
}

Write-Host '=== 2. トレイを止める ===' -ForegroundColor Cyan
Stop-Process -Name Foilwright.Tray -Force -ErrorAction SilentlyContinue
Start-Sleep -Seconds 2

Write-Host '=== 3. ビルド ===' -ForegroundColor Cyan
Push-Location (Join-Path $repo 'src')
try {
    dotnet build -v q --nologo
    if ($LASTEXITCODE -ne 0) {
        Write-Host 'ビルドに失敗した。トレイは起こさない。' -ForegroundColor Red
        exit $LASTEXITCODE
    }
} finally {
    Pop-Location
}

Write-Host '=== 4. トレイを起こす ===' -ForegroundColor Cyan
if (-not (Test-Path $trayExe)) {
    Write-Host "  実行ファイルが無い: $trayExe" -ForegroundColor Red
    exit 1
}
Start-Process -FilePath $trayExe -WorkingDirectory (Split-Path -Parent $trayExe)

Write-Host '=== 5. パイプの確認 ===' -ForegroundColor Cyan
$found = $false
foreach ($i in 1..12) {
    Start-Sleep -Seconds 1
    # 名前付きパイプの一覧は `\\.\pipe\` から読む。先頭の `\\` を落とすと
    # カレントドライブの `\pipe`(例: `E:\pipe`)を探して例外になる —
    # 2026-08-21 まで、この確認は一度も機能していなかった。
    if ([System.IO.Directory]::GetFiles('\\.\pipe\') -match 'foilwright') { $found = $true; break }
}
if ($found) {
    Write-Host '  待ち受け中。印刷してよい' -ForegroundColor Green
} else {
    Write-Host '  パイプが現れない。印刷を頼む前に確認すること' -ForegroundColor Red
    exit 1
}

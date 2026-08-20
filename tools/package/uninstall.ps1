<#
.SYNOPSIS
  Foilwright を取り除く(D-039 / D-040 / D-041)。

.DESCRIPTION
  install.ps1 が %LOCALAPPDATA%\Foilwright\install-manifest.json に残した記録
  だけを頼りに片付ける。manifest が無ければ、何がこのスクリプトの導入で
  作られたのか分からないため、何も消さずに止まる。

  順序: トレイを止める → 自動起動を解除する → プリンタを削除する →
        ポートを削除する → ファイルを削除する

  設定(palette/ profiles/ papers/ media.yaml colour/)は既定では残す。
  -Purge を付けたときだけ、それらも含めて %LOCALAPPDATA%\Foilwright を消す。

.PARAMETER Purge
  設定ファイルを含め、導入ディレクトリを丸ごと消す。既定では付けない。

.EXAMPLE
  何を消すか確認だけしたい場合:
    .\uninstall.ps1 -WhatIf

.EXAMPLE
  設定は残して取り除く:
    .\uninstall.ps1

.EXAMPLE
  設定ごと全部消す:
    .\uninstall.ps1 -Purge
#>
[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'High')]
param(
    [switch]$Purge
)

$ErrorActionPreference = 'Stop'

function Write-Step {
    param([string]$Message)
    Write-Host "=== $Message ===" -ForegroundColor Cyan
}

# 設定ファイルは既定で残す。ここに列挙したものだけを「消さない対象」として扱う。
$configPaths = @('palette', 'profiles', 'papers', 'media.yaml', 'colour')

$installDir = Join-Path $env:LOCALAPPDATA 'Foilwright'
$manifestPath = Join-Path $installDir 'install-manifest.json'

if (-not (Test-Path $manifestPath)) {
    Write-Host "manifest が見つからない: $manifestPath" -ForegroundColor Yellow
    Write-Host '何を install.ps1 が作ったのか分からないため、何も消さずに終了する。' -ForegroundColor Yellow
    exit 1
}

$manifest = Get-Content $manifestPath -Raw | ConvertFrom-Json

$printerName = 'Foilwright MD-5500'
$portName = '\\.\pipe\foilwright'
$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'

$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
$isAdmin = $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin -and ($manifest.printerCreated -or $manifest.portCreated)) {
    Write-Host 'プリンタまたはポートを削除するには管理者権限が要る。管理者として実行し直してください。' -ForegroundColor Red
    exit 1
}

# --- 1. トレイを止める -------------------------------------------------------
Write-Step '1. トレイを止める'
if ($PSCmdlet.ShouldProcess('Foilwright.Tray プロセス', '停止')) {
    Stop-Process -Name 'Foilwright.Tray' -Force -ErrorAction SilentlyContinue
    Write-Host '  停止した(動いていなければ何もしていない)'
}

# --- 2. 自動起動を解除する ---------------------------------------------------
Write-Step '2. 自動起動を解除する'
if ($manifest.autostartCreated) {
    if ($PSCmdlet.ShouldProcess("$runKey の Foilwright 値", '削除')) {
        Remove-ItemProperty -Path $runKey -Name 'Foilwright' -ErrorAction SilentlyContinue
        Write-Host '  解除した'
    }
} else {
    Write-Host '  install.ps1 が登録したものではないので触らない'
}

# --- 3. プリンタを削除する ---------------------------------------------------
Write-Step '3. プリンタを削除する'
if ($manifest.printerCreated) {
    if ($PSCmdlet.ShouldProcess($printerName, '削除')) {
        Remove-Printer -Name $printerName -ErrorAction SilentlyContinue
        Write-Host '  削除した'
    }
} else {
    Write-Host '  install.ps1 が作ったものではないので触らない'
}

# --- 4. ポートを削除する -----------------------------------------------------
Write-Step '4. ポートを削除する'
if ($manifest.portCreated) {
    if ($PSCmdlet.ShouldProcess($portName, '削除')) {
        Remove-PrinterPort -Name $portName -ErrorAction SilentlyContinue
        Write-Host '  削除した'
    }
} else {
    Write-Host '  install.ps1 が作ったものではないので触らない'
}

# --- 5. ファイルを削除する ---------------------------------------------------
Write-Step '5. ファイルを削除する'
if (-not (Test-Path $installDir)) {
    Write-Host "  導入ディレクトリが無い: $installDir"
} elseif ($Purge) {
    if ($PSCmdlet.ShouldProcess($installDir, '設定ごと丸ごと削除(-Purge)')) {
        Remove-Item -Path $installDir -Recurse -Force
        Write-Host "  設定ごと削除した: $installDir"
    }
} else {
    $items = Get-ChildItem -Path $installDir -Force
    foreach ($item in $items) {
        if ($configPaths -contains $item.Name) {
            Write-Host "  残す(設定): $($item.Name)"
            continue
        }
        if ($PSCmdlet.ShouldProcess($item.FullName, '削除')) {
            Remove-Item -Path $item.FullName -Recurse -Force
            Write-Host "  削除した: $($item.Name)"
        }
    }
    Write-Host "  設定は残した(消すには -Purge を付ける): $installDir"
}

Write-Host "`n取り除きが完了した。" -ForegroundColor Green

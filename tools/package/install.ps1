<#
.SYNOPSIS
  Foilwright を導入する(D-039 / D-040 / D-041)。

.DESCRIPTION
  このスクリプトは配布 zip の直下に入っている前提で書かれている。
  同じ場所に install-manifest.json を作り、「導入時点でまだ無かったものだけ」を
  記録する。uninstall.ps1 はこの記録だけを頼りに、入れる前から存在していた
  ものには触らずに片付ける。

  手順(失敗したらそこで止まる):
    1. 管理者権限の確認(プリンタとポートの作成に要る)
    2. Ghostscript の確認(同梱しない。AGPL のため。D-021 / §12.2)
    3. %LOCALAPPDATA%\Foilwright へ配置
    4. 名前付きポート \\.\pipe\foilwright の作成
    5. プリンタ Foilwright MD-5500 の作成(MS Publisher Color Printer)
    6. 自動起動への登録(現在のユーザーのみ)
    7. トレイを起こし、名前付きパイプの待ち受けを確認する

.EXAMPLE
  管理者として PowerShell を開き、展開した zip の中で:
    .\install.ps1
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'

function Write-Step {
    param([string]$Message)
    Write-Host "=== $Message ===" -ForegroundColor Cyan
}

function Fail {
    param([string]$Message)
    Write-Host $Message -ForegroundColor Red
    exit 1
}

$packageRoot = $PSScriptRoot
$appSource = Join-Path $packageRoot 'app'
if (-not (Test-Path $appSource)) {
    Fail "配布物が壊れている: '$appSource' が見つからない。zip を展開し直してください。"
}

$printerName = 'Foilwright MD-5500'
$portName = '\\.\pipe\foilwright'
$driverName = 'MS Publisher Color Printer'
$installDir = Join-Path $env:LOCALAPPDATA 'Foilwright'
$trayExeName = 'Foilwright.Tray.exe'

# --- 1. 管理者権限の確認 -----------------------------------------------------
Write-Step '1. 管理者権限の確認'
$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Fail "管理者権限が無い。プリンタとポートの作成に管理者権限が要る。`n" +
        "PowerShell を『管理者として実行』で開き直し、もう一度 install.ps1 を実行してください。"
}
Write-Host '  OK'

# --- 2. Ghostscript の確認 ---------------------------------------------------
Write-Step '2. Ghostscript の確認'
$gsCommand = Get-Command 'gswin64c.exe' -ErrorAction SilentlyContinue
$gsPath = $null
if ($gsCommand) {
    $gsPath = $gsCommand.Source
} else {
    $gsCandidates = @(Get-ChildItem -Path 'C:\Program Files\gs\*\bin\gswin64c.exe' -ErrorAction SilentlyContinue)
    if ($gsCandidates.Count -gt 0) {
        $gsPath = $gsCandidates[0].FullName
    }
}
if (-not $gsPath) {
    Fail "Ghostscript(gswin64c.exe)が見つからない。`n" +
        "https://www.ghostscript.com/ から入手してインストールしてから、もう一度実行してください。`n" +
        "(AGPL のため Foilwright には同梱していない)"
}
Write-Host "  見つかった: $gsPath"

# --- ここから記録の準備 ------------------------------------------------------
$manifest = [ordered]@{
    version         = (Get-Content (Join-Path $appSource 'version.txt') -Raw -ErrorAction SilentlyContinue).Trim()
    installedAt     = (Get-Date).ToString('o')
    installDir      = $installDir
    portCreated     = $false
    printerCreated  = $false
    autostartCreated = $false
}

# --- 3. %LOCALAPPDATA%\Foilwright へ配置 -------------------------------------
Write-Step '3. 配置'
if (-not (Test-Path $installDir)) {
    New-Item -ItemType Directory -Path $installDir -Force | Out-Null
}
Copy-Item -Path (Join-Path $appSource '*') -Destination $installDir -Recurse -Force
Write-Host "  $installDir へ配置した"

# --- 4. 名前付きポートの作成 -------------------------------------------------
Write-Step '4. 名前付きポートの作成'
if (Get-PrinterPort -Name $portName -ErrorAction SilentlyContinue) {
    Write-Host "  既にある: $portName"
} else {
    Add-PrinterPort -Name $portName
    $manifest.portCreated = $true
    Write-Host "  作成した: $portName"
}

# --- 5. プリンタの作成 -------------------------------------------------------
Write-Step '5. プリンタの作成'
if (-not (Get-PrinterDriver -Name $driverName -ErrorAction SilentlyContinue)) {
    Add-PrinterDriver -Name $driverName
    Write-Host "  ドライバを追加した: $driverName"
}
if (Get-Printer -Name $printerName -ErrorAction SilentlyContinue) {
    Write-Host "  既にある: $printerName"
    Set-Printer -Name $printerName -PortName $portName
} else {
    Add-Printer -Name $printerName -DriverName $driverName -PortName $portName
    $manifest.printerCreated = $true
    Write-Host "  作成した: $printerName"
}

# --- 6. 自動起動への登録 -----------------------------------------------------
Write-Step '6. 自動起動への登録'
$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$trayExe = Join-Path $installDir $trayExeName
$runValue = "`"$trayExe`""
$existing = (Get-ItemProperty -Path $runKey -Name 'Foilwright' -ErrorAction SilentlyContinue).Foilwright
if ($existing) {
    Write-Host '  既に登録済み'
    if ($existing -ne $runValue) {
        Set-ItemProperty -Path $runKey -Name 'Foilwright' -Value $runValue
        Write-Host '  登録内容を更新した(パスが変わっていた)'
    }
} else {
    New-ItemProperty -Path $runKey -Name 'Foilwright' -Value $runValue -PropertyType String -Force | Out-Null
    $manifest.autostartCreated = $true
    Write-Host '  登録した'
}

# --- 7. トレイを起こし、パイプの待ち受けを確認する ---------------------------
Write-Step '7. トレイの起動確認'
if (-not (Test-Path $trayExe)) {
    Fail "トレイの実行ファイルが無い: $trayExe"
}
Start-Process -FilePath $trayExe -WorkingDirectory $installDir
$found = $false
foreach ($i in 1..15) {
    Start-Sleep -Seconds 1
    if ([System.IO.Directory]::GetFiles('\\.\pipe\') -match 'foilwright') {
        $found = $true
        break
    }
}
if ($found) {
    Write-Host '  名前付きパイプが待ち受けている' -ForegroundColor Green
} else {
    Write-Host '  名前付きパイプが現れなかった。トレイが起動しているか確認してください' -ForegroundColor Yellow
}

# --- 記録 ---------------------------------------------------------------------
$manifestPath = Join-Path $installDir 'install-manifest.json'
$manifest | ConvertTo-Json | Set-Content -Path $manifestPath -Encoding utf8
Write-Host "`n導入を記録した: $manifestPath" -ForegroundColor Green
Write-Host '導入が完了した。' -ForegroundColor Green

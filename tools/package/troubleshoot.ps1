<#
.SYNOPSIS
  「印刷したのに何も起きない」を自分で調べて直す(D-039)。

.DESCRIPTION
  この障害の原因はほぼ 1 つに絞れる — トレイアプリが動いていないと
  名前付きパイプ \\.\pipe\foilwright が存在せず、スプーラはそれを開けるまで
  待ち続ける。印刷ダイアログは「接続中」のまま固まる。

  このスクリプトは状態を読むだけで、プリンタ・ポートの作り直しはしない
  (それは install.ps1 の仕事)。管理者権限は要らない。

  調べること:
    1. トレイアプリが動いているか(何個動いているか)
    2. 名前付きパイプ \\.\pipe\foilwright があるか  ← 無いと必ず固まる
    3. 自動起動の登録と、それが指すファイルの実在
    4. プリンタ Foilwright MD-5500 とポート \\.\pipe\foilwright
    5. Ghostscript(gswin64c.exe)
    6. 印刷キューに溜まっているジョブ

  直せるもの: トレイが動いていなければ、尋ねてから起動する。
  印刷キューに残ったジョブは勝手に消さない(消すとスプールファイル =
  不具合を追う証拠も消えるため。D-039 の判断)。

.PARAMETER Yes
  尋ねずにトレイを起動する。

.EXAMPLE
  .\troubleshoot.ps1

.EXAMPLE
  .\troubleshoot.ps1 -Yes
#>
[CmdletBinding()]
param(
    [switch]$Yes
)

$ErrorActionPreference = 'Stop'

$printerName = 'Foilwright MD-5500'
$portName = '\\.\pipe\foilwright'
$pipeName = 'foilwright'
$trayProcessName = 'Foilwright.Tray'
$trayExeName = 'Foilwright.Tray.exe'
$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$runValueName = 'Foilwright'

# 「次にやること」はここへ積み、最後に 1 行だけ出す。
$script:NextAction = $null

function Write-Section {
    param([string]$Message)
    Write-Host ''
    Write-Host "=== $Message ===" -ForegroundColor Cyan
}

function Write-Ok {
    param([string]$Message)
    Write-Host "  [OK] $Message" -ForegroundColor Green
}

function Write-Ng {
    param([string]$Message)
    Write-Host "  [NG] $Message" -ForegroundColor Red
}

function Write-Note {
    param([string]$Message)
    Write-Host "  $Message" -ForegroundColor DarkGray
}

function Set-NextAction {
    param(
        [string]$Message,
        # 小さいほど急ぐ。1 = いま印刷を止めている / 2 = 次回のログオンで困る。
        # **並び順で決めない** — 点検の順番は読みやすさで決めており、
        # 深刻さの順ではないため。実際、自動起動の未登録(次回の話)が
        # 「プレビューが開いていて今止まっている」より先に採られて食い違った。
        [int]$Priority = 1
    )
    if ($null -eq $script:NextActionPriority -or $Priority -lt $script:NextActionPriority) {
        $script:NextAction = $Message
        $script:NextActionPriority = $Priority
    }
}

function Test-FoilwrightPipe {
    # \\.\pipe\ の一覧を取って名前で探す。Test-Path では名前付きパイプを
    # 正しく見られないため、ディレクトリ列挙を使う(install.ps1 と同じ方法)。
    try {
        return [bool]([System.IO.Directory]::GetFiles('\\.\pipe\') -match $pipeName)
    } catch {
        return $false
    }
}

function Get-TrayProcess {
    return @(Get-Process -Name $trayProcessName -ErrorAction SilentlyContinue)
}

Write-Host 'Foilwright トラブルシュート' -ForegroundColor Cyan
Write-Host '(状態を読むだけです。プリンタやポートは作り直しません)'

# --- 1. トレイアプリ ---------------------------------------------------------
Write-Section '1. トレイアプリ'
$trayProcesses = Get-TrayProcess
if ($trayProcesses.Count -eq 0) {
    Write-Ng "$trayProcessName は動いていない"
    Set-NextAction 'トレイアプリを起動してください(このスクリプトを -Yes 付きで実行すると起動します)。'
} elseif ($trayProcesses.Count -eq 1) {
    Write-Ok "$trayProcessName が 1 個動いている(PID $($trayProcesses[0].Id))"
} else {
    Write-Ok "$trayProcessName が動いている($($trayProcesses.Count) 個)"
    Write-Note "PID: $(($trayProcesses | ForEach-Object { $_.Id }) -join ', ')"
    Write-Note '2 個以上動くのは想定外です。余分なものはトレイアイコンの「終了」で閉じてください。'
}

# --- 2. 名前付きパイプ -------------------------------------------------------
Write-Section '2. 名前付きパイプ'
$pipeFound = Test-FoilwrightPipe
if ($pipeFound) {
    Write-Ok "$portName がある(印刷を受け取れる状態)"
} else {
    Write-Ng "$portName が無い"
    Write-Note 'これが無いと、印刷ダイアログは「接続中」のまま固まります。'
    # **ここで「次にやること」を決めてよいのは、トレイが動いていないときだけ。**
    # Set-NextAction は先に出たものを採るので、トレイが動いている場合にここで
    # 「起動してください」と置いてしまうと、後の「トレイの状態」が出す正しい助言
    # (プレビューが開いているなら、それを閉じる)を上書きできない。
    # 実際に「動いている(OK)」と「起動してください」が同時に出て食い違った。
    if ($trayProcesses.Count -eq 0) {
        Set-NextAction 'トレイアプリを起動してください(このスクリプトを -Yes 付きで実行すると起動します)。'
    }
}

# --- 3. 自動起動の登録 -------------------------------------------------------
Write-Section '3. 自動起動の登録'
$runValue = (Get-ItemProperty -Path $runKey -Name $runValueName -ErrorAction SilentlyContinue).$runValueName
if ($runValue) {
    Write-Ok "登録されている: $runValue"
    # 登録値は "..." で囲まれている。中身のパスを取り出して実在を確かめる。
    $runTarget = $runValue.Trim('"')
    if (Test-Path -LiteralPath $runTarget) {
        Write-Ok "指しているファイルが実在する: $runTarget"
    } else {
        Write-Ng "指しているファイルが無い: $runTarget"
        Set-NextAction 'install.ps1 を管理者として実行し直してください(自動起動の登録が古いパスを指しています)。' -Priority 2
    }
} else {
    $runTarget = $null
    Write-Ng "自動起動に登録されていない($runKey の $runValueName)"
    Write-Note '次回のログオン時にトレイが上がりません。'
    Set-NextAction 'install.ps1 を管理者として実行し直してください(自動起動が登録されていません)。' -Priority 2
}

# --- 4. プリンタとポート -----------------------------------------------------
Write-Section '4. プリンタとポート'
$printer = Get-Printer -Name $printerName -ErrorAction SilentlyContinue
if ($printer) {
    Write-Ok "プリンタがある: $printerName(ポート: $($printer.PortName))"
    if ($printer.PortName -ne $portName) {
        Write-Ng "ポートの割り当てが違う。期待: $portName"
        Set-NextAction 'install.ps1 を管理者として実行し直してください(プリンタのポートが違います)。'
    }
} else {
    Write-Ng "プリンタが無い: $printerName"
    Set-NextAction 'install.ps1 を管理者として実行し直してください(プリンタがありません)。'
}
if (Get-PrinterPort -Name $portName -ErrorAction SilentlyContinue) {
    Write-Ok "ポートがある: $portName"
} else {
    Write-Ng "ポートが無い: $portName"
    Set-NextAction 'install.ps1 を管理者として実行し直してください(ポートがありません)。'
}

# --- 5. Ghostscript ----------------------------------------------------------
Write-Section '5. Ghostscript'
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
if ($gsPath) {
    Write-Ok "見つかった: $gsPath"
} else {
    Write-Ng 'gswin64c.exe が見つからない'
    Write-Note '同梱していません(AGPL のため)。https://www.ghostscript.com/ から入れてください。'
    Set-NextAction 'Ghostscript を入れてください(https://www.ghostscript.com/)。'
}

# --- 6. 印刷キュー -----------------------------------------------------------
Write-Section '6. 印刷キュー'
if ($printer) {
    $jobs = @(Get-PrintJob -PrinterName $printerName -ErrorAction SilentlyContinue)
    if ($jobs.Count -eq 0) {
        Write-Ok '溜まっているジョブは無い'
    } else {
        Write-Ng "$($jobs.Count) 件のジョブが残っている"
        foreach ($job in $jobs) {
            Write-Note "Id=$($job.Id) 状態=$($job.JobStatus) 提出=$($job.SubmittedTime) 文書=$($job.DocumentName)"
        }
        Write-Note 'このスクリプトは消しません(消すと不具合を追う証拠のスプールファイルも消えるため)。'
        Write-Note '消すなら手で消してください(設定 → プリンターとスキャナー → キューを開く)。'
    }
} else {
    Write-Note 'プリンタが無いのでキューは調べられない'
}

# --- トレイが動いていなければ起こす ------------------------------------------
if ($trayProcesses.Count -gt 0 -and -not $pipeFound) {
    # 動いているのにパイプが無い。ここで もう 1 個起こしても二重起動の判定に
    # 弾かれるだけなので、起動は薦めない。
    #
    # ただし**いちばん多い原因は「プレビュー窓が開いている」**で、これは異常では
    # ない ― 確認待ちのあいだトレイは次のジョブを受け取らない設計(1 度に 1 件)。
    # ここで「トレイを終了しろ」と案内すると、**開いているプレビューを失わせる**。
    # 窓の有無で案内を分ける。
    Write-Section 'トレイの状態'
    $windowTitle = ($trayProcesses | ForEach-Object { $_.MainWindowTitle } | Where-Object { $_ }) -join ' / '
    if ($windowTitle) {
        Write-Note "プレビュー窓が開いている: $windowTitle"
        Write-Note '確認待ちのあいだは次のジョブを受け取らない(1 度に 1 件)。異常ではない。'
        Set-NextAction '開いているプレビューで「印刷開始」か「取り消し」を押して閉じてください。そのあと印刷できます。'
    } else {
        Write-Ng 'トレイは動いているのに名前付きパイプが無い(待ち受けに失敗している)'
        Set-NextAction 'トレイアイコンの「終了」でいったん閉じてから、もう一度このスクリプトを実行してください。'
    }
} elseif ($trayProcesses.Count -eq 0) {
    Write-Section 'トレイの起動'

    # 探す順: 1. 自動起動の登録が指すもの 2. %LOCALAPPDATA% 3. このスクリプトの隣の app\
    $candidates = @()
    if ($runTarget) { $candidates += $runTarget }
    $candidates += (Join-Path (Join-Path $env:LOCALAPPDATA 'Foilwright') $trayExeName)
    $candidates += (Join-Path (Join-Path $PSScriptRoot 'app') $trayExeName)
    # 開発ツリーから走らせたとき用(tools\package\ の 2 つ上がリポジトリの根)。
    # 配布 zip にはこの場所が無いので、存在確認で自然に外れる。
    # **開発環境には自動起動を登録しない方針**(D-049 補足)なので、再起動のたび
    # 手で起こすことになる — そのときにこのスクリプトが使えるようにしておく。
    $candidates += (Join-Path $PSScriptRoot '..\..\src\Foilwright.Tray\bin\Debug\net10.0-windows\Foilwright.Tray.exe')

    $trayExe = $null
    foreach ($candidate in $candidates) {
        if ($candidate -and (Test-Path -LiteralPath $candidate)) {
            $trayExe = $candidate
            break
        }
    }

    if (-not $trayExe) {
        Write-Ng '起動できる実行ファイルが見つからない。探した場所:'
        foreach ($candidate in $candidates) { Write-Note $candidate }
        Set-NextAction 'install.ps1 を管理者として実行してください(トレイアプリが見つかりません)。'
    } else {
        Write-Host "  見つかった: $trayExe"
        $go = $Yes
        if (-not $go) {
            $answer = Read-Host '  トレイアプリを起動しますか? [Y/n]'
            $go = ($answer -eq '' -or $answer -match '^[Yy]')
        }
        if (-not $go) {
            Write-Note '起動しなかった。'
            Set-NextAction "トレイアプリを起動してください: $trayExe"
        } else {
            Start-Process -FilePath $trayExe -WorkingDirectory (Split-Path -Parent $trayExe)
            Write-Host '  起動した。名前付きパイプが現れるのを待つ(最大 15 秒)…'
            $appeared = $false
            foreach ($i in 1..15) {
                Start-Sleep -Seconds 1
                if (Test-FoilwrightPipe) {
                    $appeared = $true
                    break
                }
            }
            if ($appeared) {
                Write-Ok "$portName が現れた($i 秒)"
                # ここで直ったので、パイプ・トレイ由来の「次にやること」は取り下げる。
                if ($script:NextAction -like 'トレイアプリを起動*') { $script:NextAction = $null }
                $after = Get-TrayProcess
                Write-Ok "$trayProcessName が $($after.Count) 個動いている"
            } else {
                Write-Ng '15 秒待っても名前付きパイプが現れなかった'
                Set-NextAction 'トレイアプリを手で起動し、エラーが出ないか確認してください。'
            }
        }
    }
}

# --- 次にやること -------------------------------------------------------------
Write-Host ''
if ($script:NextAction) {
    Write-Host "次にやること: $script:NextAction" -ForegroundColor Yellow
} else {
    Write-Host '次にやること: 問題は見つかりませんでした。印刷してよい状態です。' -ForegroundColor Green
}

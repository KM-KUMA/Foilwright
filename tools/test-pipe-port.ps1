#requires -Version 5.1
<#
Foilwright — スプーラの出力を名前付きパイプで受け取れるかを試す

当初計画は第三者製ポートモニタ mfilemon に依存していたが、Windows 11 は
第三者製の印刷コンポーネントを拒否する方向にある(§3.5.1 で実測)。
Windows 標準の Local Port が名前付きパイプ宛の書き込みを受け付けるなら、
**mfilemon を使わずに in-box だけでジョブを受け取れる**。

本スクリプトはパイプ側のサーバを立てて待つだけ。別途プリンタのポートを
\\.\pipe\foilwright に向けて印刷すると、ここに PostScript が流れてくる。

  powershell -ExecutionPolicy Bypass -File tools\test-pipe-port.ps1
#>

param(
    [string]$PipeName = 'foilwright',
    [string]$OutFile = 'E:\build\Foilwright\dumps\pipe_out.ps',
    [int]$TimeoutSeconds = 90
)

$ErrorActionPreference = 'Stop'

Write-Host "パイプ \\.\pipe\$PipeName で待機する(最大 $TimeoutSeconds 秒)"

$server = New-Object System.IO.Pipes.NamedPipeServerStream(
    $PipeName, [System.IO.Pipes.PipeDirection]::In, 1,
    [System.IO.Pipes.PipeTransmissionMode]::Byte,
    [System.IO.Pipes.PipeOptions]::Asynchronous)

try {
    $wait = $server.BeginWaitForConnection($null, $null)
    if (-not $wait.AsyncWaitHandle.WaitOne($TimeoutSeconds * 1000)) {
        Write-Host '接続が来なかった(タイムアウト)'
        return
    }
    $server.EndWaitForConnection($wait)
    Write-Host '接続を受けた。読み出す。'

    $out = New-Object System.IO.FileStream($OutFile, [System.IO.FileMode]::Create)
    try {
        $buffer = New-Object byte[] 65536
        $total = 0
        while (($read = $server.Read($buffer, 0, $buffer.Length)) -gt 0) {
            $out.Write($buffer, 0, $read)
            $total += $read
        }
    }
    finally { $out.Close() }

    Write-Host ("受信 {0} バイト -> {1}" -f $total, $OutFile)
    if ($total -gt 0) {
        $head = [System.IO.File]::ReadAllBytes($OutFile)[0..([Math]::Min(40, $total - 1))]
        Write-Host ('先頭: ' + [System.Text.Encoding]::ASCII.GetString($head))
    }
}
finally { $server.Dispose() }

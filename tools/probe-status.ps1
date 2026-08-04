#requires -Version 5.1
<#
Foilwright — 状態応答をラベル付きで採取する

状態バイト(応答の 5 バイト目)の意味はまだ確定していない。既知は
0x00 待機 / 0x01 完了 / 0x09 印刷実行中 で、0x10 / 0xC0 / 0xC9 は未知(§11.4)。

物理状態を変えながら本スクリプトで採取し、値と状態を突き合わせて意味を特定する。
採取結果は dumps\status-probe.log に追記されるため、複数回の実行を後から比較できる。

  powershell -ExecutionPolicy Bypass -File tools\probe-status.ps1 -Label "給紙エラー中"
#>

param(
    [Parameter(Mandatory = $true)][string]$Label,
    [string]$VidMatch = 'VID_044E',
    [string]$LogPath = 'E:\build\Foilwright\dumps\status-probe.log'
)

$ErrorActionPreference = 'Stop'

$sig = @'
using System;
using System.Runtime.InteropServices;

public static class FwProbe
{
    const uint GENERIC_READ     = 0x80000000;
    const uint GENERIC_WRITE    = 0x40000000;
    const uint FILE_SHARE_READ  = 0x00000001;
    const uint FILE_SHARE_WRITE = 0x00000002;
    const uint OPEN_EXISTING    = 3;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr CreateFile(string name, uint access, uint share,
        IntPtr sec, uint disp, uint flags, IntPtr tmpl);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool WriteFile(IntPtr h, byte[] buf, int count,
        out int written, IntPtr overlapped);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool ReadFile(IntPtr h, byte[] buf, int count,
        out int read, IntPtr overlapped);

    // cmd を書いて応答を読む。応答を返さないコマンドには使わないこと
    // (空読みするとインターフェースがウェッジする。DOMAIN 11.1.1)。
    public static byte[] Query(string devicePath, byte[] cmd, out string err)
    {
        IntPtr h = CreateFile(devicePath, GENERIC_READ | GENERIC_WRITE,
            FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING,
            0, IntPtr.Zero);
        if (h == new IntPtr(-1))
        {
            err = "OPEN_FAILED win32=" + Marshal.GetLastWin32Error();
            return null;
        }
        try
        {
            int written;
            if (!WriteFile(h, cmd, cmd.Length, out written, IntPtr.Zero))
            {
                err = "WRITE_FAILED win32=" + Marshal.GetLastWin32Error();
                return null;
            }
            // 応答を最後まで読み切る。途中で打ち切るとデバイス側に読み残しが
            // 滞留し、次の会話の先頭にそれが出てきて以後すべてがずれる
            // (2026-08-04 に実地で踏んだ)。
            var all = new System.Collections.Generic.List<byte>();
            byte[] buf = new byte[4096];
            while (true)
            {
                int read;
                if (!ReadFile(h, buf, buf.Length, out read, IntPtr.Zero))
                {
                    err = "READ_FAILED win32=" + Marshal.GetLastWin32Error();
                    return null;
                }
                for (int i = 0; i < read; i++) { all.Add(buf[i]); }
                // バッファ未満で終わりとみなす。ちょうど埋まったときだけ続きを読む。
                if (read < buf.Length) { break; }
            }
            err = null;
            return all.ToArray();
        }
        finally { CloseHandle(h); }
    }
}
'@

Add-Type -TypeDefinition $sig -Language CSharp

$guid = '{28d78fad-5a12-11D1-ae5b-0000f803a8c2}'
$root = "HKLM:\SYSTEM\CurrentControlSet\Control\DeviceClasses\$guid"
$devPath = $null
Get-ChildItem $root -ErrorAction SilentlyContinue | ForEach-Object {
    if ($_.PSChildName -match [regex]::Escape($VidMatch) -and -not $devPath) {
        $devPath = '\\?\' + ($_.PSChildName -replace '^##\?#', '')
    }
}
if (-not $devPath) { throw "該当デバイスなし: $VidMatch" }

$lines = @()
$lines += "=== $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')  状態: $Label ==="

# 応答を返すことが確認済みの 4 つ(DOMAIN 15.2)。
foreach ($sub in 0x01, 0x02, 0x03, 0x04) {
    $err = $null
    $reply = [FwProbe]::Query($devPath, [byte[]](0x05, $sub), [ref]$err)
    if ($null -eq $reply) {
        $lines += ("05 {0:x2} -> {1}" -f $sub, $err)
        continue
    }
    $hex = ($reply | ForEach-Object { $_.ToString('x2') }) -join ' '
    $lines += ("05 {0:x2} -> {1} バイト" -f $sub, $reply.Length)
    $lines += "   $hex"
    Start-Sleep -Milliseconds 300
}

$lines | ForEach-Object { Write-Host $_ }
Add-Content -Path $LogPath -Value $lines -Encoding UTF8
Write-Host "記録: $LogPath"

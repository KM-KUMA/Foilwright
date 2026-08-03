#requires -Version 5.1
<#
Foilwright — スプーラを迂回した直接書き込み(切り分け用)

スプーラ・ポートモニタを一切通さず、usbprint.sys のデバイス
インターフェースへ WriteFile で直接書き込む。

DOMAIN §11.1.1 の切り分け: スプーラ経由の送出が Error になるとき、
この直接書き込みの成否とエラーコードで障害箇所を特定する。

  成功           → デバイスへの転送経路は生きている(スプーラ側の問題)
  失敗(コード付き) → usbprint.sys → デバイスの転送が失敗している

使い方:
  powershell -ExecutionPolicy Bypass -File tools\write-direct.ps1 `
      -Path dumps\phase1_blackraster.bin -VidMatch VID_044E
#>

param(
    [Parameter(Mandatory = $true)][string]$Path,
    [string]$VidMatch = 'VID_044E'
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path -LiteralPath $Path)) { throw "ファイルなし: $Path" }
$bytes = [System.IO.File]::ReadAllBytes((Resolve-Path -LiteralPath $Path))
Write-Host ("データ : {0} ({1} バイト)" -f $Path, $bytes.Length)

$sig = @'
using System;
using System.Runtime.InteropServices;

public static class FwDirect
{
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

    public static string Send(string devicePath, byte[] data)
    {
        IntPtr h = CreateFile(devicePath, GENERIC_WRITE,
            FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
        if (h == new IntPtr(-1))
            return "OPEN_FAILED win32=" + Marshal.GetLastWin32Error();
        try
        {
            // まとめて 1 回で書く。バルク転送の成否がそのまま返る。
            int written;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            bool ok = WriteFile(h, data, data.Length, out written, IntPtr.Zero);
            sw.Stop();
            if (!ok)
                return "WRITE_FAILED win32=" + Marshal.GetLastWin32Error()
                     + " written=" + written + " elapsed=" + sw.ElapsedMilliseconds + "ms";
            return "WRITE_OK written=" + written + "/" + data.Length
                 + " elapsed=" + sw.ElapsedMilliseconds + "ms";
        }
        finally { CloseHandle(h); }
    }
}
'@

Add-Type -TypeDefinition $sig -Language CSharp

$guid = '{28d78fad-5a12-11D1-ae5b-0000f803a8c2}'
$root = "HKLM:\SYSTEM\CurrentControlSet\Control\DeviceClasses\$guid"
$found = $false

Get-ChildItem $root -ErrorAction SilentlyContinue | ForEach-Object {
    $name = $_.PSChildName
    if ($name -notmatch [regex]::Escape($VidMatch)) { return }
    $devPath = '\\?\' + ($name -replace '^##\?#', '')
    $found = $true
    Write-Host ("対象   : {0}" -f $name)
    Write-Host ("結果   : " + [FwDirect]::Send($devPath, $bytes))
}

if (-not $found) { Write-Host ("該当デバイスなし: " + $VidMatch) }

#requires -Version 5.1
<#
Foilwright — Windows から usbprint.sys 経由で ALPS USB プロトコルを喋る

DOMAIN §15 のパケット層を、WSL / usbipd を介さず Windows のまま実装できるか
を確かめるための試作。成立すれば L0 に WinUSB への差し替えが不要になり、
利用者は USB を挿すだけで済む(D-018 の実装方式の選択に直結)。

  -Status         状態問い合わせのみ(05 01 -> 38 バイト)。紙を消費しない
  -Path <file>    RGL ジョブをフレーミングして送出

使い方:
  powershell -ExecutionPolicy Bypass -File tools\alps-send.ps1 -Status
  powershell -ExecutionPolicy Bypass -File tools\alps-send.ps1 -Path dumps\phase1_blackraster.bin
#>

param(
    [string]$Path,
    [switch]$Status,
    [string]$VidMatch = 'VID_044E'
)

$ErrorActionPreference = 'Stop'

$sig = @'
using System;
using System.Runtime.InteropServices;

public static class FwAlps
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

    public static IntPtr Open(string devicePath)
    {
        return CreateFile(devicePath, GENERIC_READ | GENERIC_WRITE,
            FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING,
            0, IntPtr.Zero);
    }

    public static void Close(IntPtr h) { CloseHandle(h); }

    public static string Write(IntPtr h, byte[] data)
    {
        int written;
        if (!WriteFile(h, data, data.Length, out written, IntPtr.Zero))
            return "WRITE_FAILED win32=" + Marshal.GetLastWin32Error();
        return "OK(" + written + ")";
    }

    // 返り値は読めたバイト列。失敗時は null を返し err に理由を入れる。
    public static byte[] Read(IntPtr h, int count, out string err)
    {
        byte[] buf = new byte[count];
        int read;
        if (!ReadFile(h, buf, count, out read, IntPtr.Zero))
        {
            err = "READ_FAILED win32=" + Marshal.GetLastWin32Error();
            return null;
        }
        err = null;
        byte[] outb = new byte[read];
        Array.Copy(buf, outb, read);
        return outb;
    }
}
'@

Add-Type -TypeDefinition $sig -Language CSharp

# usbprint.sys が公開するデバイスインターフェースのパスをレジストリから得る。
# キー名の先頭の '##?#' だけが '\\?\' に対応し、以降の '#' はそのまま残す。
$guid = '{28d78fad-5a12-11D1-ae5b-0000f803a8c2}'
$root = "HKLM:\SYSTEM\CurrentControlSet\Control\DeviceClasses\$guid"
$devPath = $null
Get-ChildItem $root -ErrorAction SilentlyContinue | ForEach-Object {
    if ($_.PSChildName -match [regex]::Escape($VidMatch) -and -not $devPath) {
        $devPath = '\\?\' + ($_.PSChildName -replace '^##\?#', '')
    }
}
if (-not $devPath) { throw "該当デバイスなし: $VidMatch" }
Write-Host "デバイス: $devPath"

$h = [FwAlps]::Open($devPath)
if ($h -eq [IntPtr]::new(-1)) {
    throw ("オープン失敗 win32=" + [Runtime.InteropServices.Marshal]::GetLastWin32Error())
}

try {
    # まず状態問い合わせで双方向が成立するかを見る。ここが通れば
    # usbprint.sys のままプロトコルを喋れることになる。
    Write-Host ("状態要求 05 01 : " + [FwAlps]::Write($h, [byte[]](0x05, 0x01)))
    $err = $null
    $reply = [FwAlps]::Read($h, 128, [ref]$err)
    if ($null -eq $reply) {
        Write-Host "状態応答      : $err"
    }
    else {
        Write-Host ("状態応答      : {0} バイト {1}" -f $reply.Length,
            ([BitConverter]::ToString($reply[0..([Math]::Min(7, $reply.Length - 1))]) -replace '-', ' '))
    }

    if ($Status -or -not $Path) { return }

    if (-not (Test-Path -LiteralPath $Path)) { throw "ファイルなし: $Path" }
    $rgl = [System.IO.File]::ReadAllBytes((Resolve-Path -LiteralPath $Path))
    Write-Host ("ジョブ        : {0} ({1} バイト)" -f $Path, $rgl.Length)

    $max = 32764
    for ($off = 0; $off -lt $rgl.Length; $off += $max) {
        $len = [Math]::Min($max, $rgl.Length - $off)
        $chunk = New-Object byte[] $len
        [Array]::Copy($rgl, $off, $chunk, 0, $len)

        Write-Host ("  送信要求 05 ff : " + [FwAlps]::Write($h, [byte[]](0x05, 0xFF)))
        $ack = [FwAlps]::Read($h, 8, [ref]$err)
        Write-Host ("  許可           : " + $(if ($null -eq $ack) { $err } else { [BitConverter]::ToString($ack) }))

        $n = $len - 1
        $pkt = New-Object byte[] ($len + 4)
        $pkt[0] = 0x02; $pkt[1] = 0x01
        $pkt[2] = [byte]($n -band 0xFF); $pkt[3] = [byte](($n -shr 8) -band 0xFF)
        [Array]::Copy($chunk, 0, $pkt, 4, $len)

        Write-Host ("  データ         : " + [FwAlps]::Write($h, $pkt))
        $ack = [FwAlps]::Read($h, 8, [ref]$err)
        Write-Host ("  受理           : " + $(if ($null -eq $ack) { $err } else { [BitConverter]::ToString($ack) }))
    }
}
finally { [FwAlps]::Close($h) }

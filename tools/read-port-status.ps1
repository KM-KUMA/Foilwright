#requires -Version 5.1
<#
Foilwright — プリンタ状態の読み出し(usbprint.sys の IOCTL 経由)

DOMAIN §11.4 の実装試行。usbprint.sys を迂回せず、ドライバが公開している
IOCTL を叩いてプリンタの状態を読む。ドライバの差し替えを伴わないため、
既存の印刷経路に影響しない。

読み出す値は USB プリンタクラス仕様の GET_PORT_STATUS(1 バイト)で、
IEEE 1284 のステータス線に対応する:

  bit 5 (0x20)  Paper Empty  1 = 用紙なし
  bit 4 (0x10)  Select       1 = オンライン
  bit 3 (0x08)  NotError     0 = エラー発生中

使い方:
  powershell -ExecutionPolicy Bypass -File tools\read-port-status.ps1
  powershell -ExecutionPolicy Bypass -File tools\read-port-status.ps1 -Match MD-5000
#>

param([string]$Match = 'ALPS')

$ErrorActionPreference = 'Continue'

$sig = @'
using System;
using System.Runtime.InteropServices;

public static class FwPort
{
    const uint GENERIC_READ    = 0x80000000;
    const uint GENERIC_WRITE   = 0x40000000;
    const uint FILE_SHARE_READ = 0x00000001;
    const uint FILE_SHARE_WRITE= 0x00000002;
    const uint OPEN_EXISTING   = 3;

    // usbprint.sys が公開する IOCTL。
    // CTL_CODE(FILE_DEVICE_UNKNOWN=0x22, func, METHOD_BUFFERED=0, FILE_ANY_ACCESS=0)
    //   = (0x22 << 16) | (func << 2)
    const uint IOCTL_USBPRINT_GET_LPT_STATUS = (0x22 << 16) | (0x13 << 2); // 0x22004C
    const uint IOCTL_USBPRINT_GET_1284_ID    = (0x22 << 16) | (0x14 << 2); // 0x220050

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr CreateFile(string name, uint access, uint share,
        IntPtr sec, uint disp, uint flags, IntPtr tmpl);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool DeviceIoControl(IntPtr h, uint code,
        IntPtr inBuf, int inSize, IntPtr outBuf, int outSize,
        out int returned, IntPtr overlapped);

    public static string Probe(string devicePath)
    {
        IntPtr h = CreateFile(devicePath, GENERIC_READ | GENERIC_WRITE,
            FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
        if (h == new IntPtr(-1))
            return "OPEN_FAILED win32=" + Marshal.GetLastWin32Error();
        try
        {
            string result = "";

            // --- ポート状態(1 バイト) ---
            IntPtr buf = Marshal.AllocCoTaskMem(64);
            try
            {
                int got;
                if (DeviceIoControl(h, IOCTL_USBPRINT_GET_LPT_STATUS,
                        IntPtr.Zero, 0, buf, 1, out got, IntPtr.Zero) && got >= 1)
                {
                    byte st = Marshal.ReadByte(buf);
                    result += "STATUS=0x" + st.ToString("x2");
                    result += " paperEmpty=" + (((st & 0x20) != 0) ? "1" : "0");
                    result += " select=" + (((st & 0x10) != 0) ? "1" : "0");
                    result += " notError=" + (((st & 0x08) != 0) ? "1" : "0");
                }
                else
                {
                    result += "STATUS_FAILED win32=" + Marshal.GetLastWin32Error();
                }
            }
            finally { Marshal.FreeCoTaskMem(buf); }

            // --- IEEE 1284 デバイス ID ---
            IntPtr idbuf = Marshal.AllocCoTaskMem(1024);
            try
            {
                int got;
                if (DeviceIoControl(h, IOCTL_USBPRINT_GET_1284_ID,
                        IntPtr.Zero, 0, idbuf, 1024, out got, IntPtr.Zero) && got > 2)
                {
                    // 先頭 2 バイトは長さ(ビッグエンディアン)
                    byte[] d = new byte[got];
                    Marshal.Copy(idbuf, d, 0, got);
                    string id = System.Text.Encoding.ASCII.GetString(d, 2, got - 2).Trim('\0');
                    result += "\n      DEVICE_ID=" + id;
                }
                else
                {
                    result += "\n      DEVICE_ID_FAILED win32=" + Marshal.GetLastWin32Error();
                }
            }
            finally { Marshal.FreeCoTaskMem(idbuf); }

            return result;
        }
        finally { CloseHandle(h); }
    }
}
'@

Add-Type -TypeDefinition $sig -Language CSharp

Write-Host "--- usbprint インターフェースを列挙 ---"

# usbprint のデバイスインターフェース GUID
$guid = '{28d78fad-5a12-11D1-ae5b-0000f803a8c2}'
$paths = @()

# SetupAPI を使わず、レジストリから公開済みインターフェースのシンボリックリンクを取得
$root = "HKLM:\SYSTEM\CurrentControlSet\Control\DeviceClasses\$guid"
if (Test-Path $root) {
    Get-ChildItem $root -ErrorAction SilentlyContinue | ForEach-Object {
        $name = $_.PSChildName
        # レジストリ側は  ##?#USB#VID_...#serial#{guid}
        # デバイスパスは  \\?\USB#VID_...#serial#{guid}
        # 先頭の '##?#' だけが '\\?\' に対応し、以降の '#' はそのまま残す。
        $p = '\\?\' + ($name -replace '^##\?#', '')
        $paths += , @($name, $p)
    }
}

if ($paths.Count -eq 0) {
    Write-Host '  (インターフェースが見つかりません)'
    return
}

foreach ($pair in $paths) {
    $name = $pair[0]
    $path = $pair[1]
    if ($Match -and $name -notmatch [regex]::Escape($Match) -and $path -notmatch [regex]::Escape($Match)) {
        # ALPS の VID(044E)や機種名で絞る。一致しなければスキップしない —
        # デバイスパスに機種名が入らない構成もあるため全件試す
    }
    Write-Host ("  {0}" -f $name)
    Write-Host ("      -> " + [FwPort]::Probe($path))
}

# Foilwright — プリンタ状態の読み出し試行(切り分け用)
# Copyright (C) 2026 JunkQuality (github.com/KM-KUMA/Foilwright)
# SPDX-License-Identifier: GPL-3.0-or-later
#
# DOMAIN §11.4(エラー状態の読み出し)が成立するかを確かめるための試行。
# 双方向でプリンタから応答を読めるかを、2 つの経路で試す。
#
#   1. ReadPrinter  — スプーラ経由。双方向プリンタなら応答が返る
#   2. GET_PORT_STATUS 相当 — usbprint.sys の IOCTL。より低レイヤ
#
# 使い方:
#   powershell -ExecutionPolicy Bypass -File tools\read-status.ps1 -PrinterName Foilwright-Test

param([Parameter(Mandatory = $true)][string]$PrinterName)

$ErrorActionPreference = 'Continue'

$sig = @'
using System;
using System.Runtime.InteropServices;

public static class FwStatus
{
    [DllImport("winspool.drv", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool OpenPrinter(string src, out IntPtr h, IntPtr pd);
    [DllImport("winspool.drv", SetLastError = true)]
    static extern bool ClosePrinter(IntPtr h);
    [DllImport("winspool.drv", SetLastError = true)]
    static extern bool ReadPrinter(IntPtr h, IntPtr buf, int cb, out int read);

    public static string ReadViaSpooler(string printer, int bytes)
    {
        IntPtr h;
        if (!OpenPrinter(printer, out h, IntPtr.Zero))
            return "OpenPrinter 失敗 (Win32 error " + Marshal.GetLastWin32Error() + ")";
        try
        {
            IntPtr buf = Marshal.AllocCoTaskMem(bytes);
            try
            {
                int read;
                bool ok = ReadPrinter(h, buf, bytes, out read);
                if (!ok)
                    return "ReadPrinter 失敗 (Win32 error " + Marshal.GetLastWin32Error() + ")";
                if (read == 0)
                    return "応答なし (0 バイト)";
                byte[] d = new byte[read];
                Marshal.Copy(buf, d, 0, read);
                return read + " バイト: " + BitConverter.ToString(d);
            }
            finally { Marshal.FreeCoTaskMem(buf); }
        }
        finally { ClosePrinter(h); }
    }
}
'@

Add-Type -TypeDefinition $sig -Language CSharp

Write-Host '--- 経路 1: ReadPrinter(スプーラ経由)---'
Write-Host ('  ' + [FwStatus]::ReadViaSpooler($PrinterName, 128))

Write-Host '--- 経路 2: ポートへの直接オープン ---'
$port = (Get-Printer -Name $PrinterName).PortName
foreach ($path in @("\\.\$port", $port)) {
    try {
        $fs = [System.IO.File]::Open($path, 'Open', 'Read')
        Write-Host ("  {0}: オープン成功" -f $path)
        $fs.Close()
    }
    catch {
        Write-Host ("  {0}: {1}" -f $path, $_.Exception.Message)
    }
}

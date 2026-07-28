# Foilwright — RAW 送出ツール(Phase 1 用の暫定実装)
# Copyright (C) 2026 JunkQuality (github.com/KM-KUMA/Foilwright)
# SPDX-License-Identifier: GPL-3.0-or-later
#
# 生成済みのバイト列を、加工せずそのままプリンタへ送る。
# DOMAIN §3.2 の経路のうち「トレイアプリ → WritePrinter (RAW)」に相当する
# 部分を、L0 実装前に手動で代替するためのもの。
#
# 使い方:
#   powershell -ExecutionPolicy Bypass -File tools\send-raw.ps1 `
#       -Path dumps\phase1_5mm_black.bin -PrinterName Foilwright-Test
#
# -WhatIf を付けると送信せず内容の確認だけ行う。

[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [Parameter(Mandatory = $true)][string]$Path,
    [Parameter(Mandatory = $true)][string]$PrinterName
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path -LiteralPath $Path)) {
    throw "ファイルが見つかりません: $Path"
}
$bytes = [System.IO.File]::ReadAllBytes((Resolve-Path -LiteralPath $Path))
if ($bytes.Length -eq 0) {
    throw "ファイルが空です: $Path"
}

$printer = Get-Printer -Name $PrinterName -ErrorAction SilentlyContinue
if ($null -eq $printer) {
    throw "プリンタが見つかりません: $PrinterName"
}

Write-Host ("送信元 : {0} ({1} バイト)" -f $Path, $bytes.Length)
Write-Host ("送信先 : {0} → ポート {1}" -f $printer.Name, $printer.PortName)
Write-Host ("先頭   : {0}" -f (($bytes[0..15] | ForEach-Object { $_.ToString('x2') }) -join ' '))

if (-not $PSCmdlet.ShouldProcess($PrinterName, "RAW $($bytes.Length) バイトを送出")) {
    Write-Host '(-WhatIf のため送信しませんでした)'
    return
}

# winspool.drv を直接呼ぶ。プリンタドライバによる加工を挟まず、
# バイト列をそのまま渡すため(Generic / Text Only でも改変を避ける)。
$signature = @'
using System;
using System.Runtime.InteropServices;

public static class FwRawPrinter
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DOCINFO
    {
        [MarshalAs(UnmanagedType.LPWStr)] public string DocName;
        [MarshalAs(UnmanagedType.LPWStr)] public string OutputFile;
        [MarshalAs(UnmanagedType.LPWStr)] public string Datatype;
    }

    [DllImport("winspool.drv", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool OpenPrinter(string src, out IntPtr hPrinter, IntPtr pd);
    [DllImport("winspool.drv", SetLastError = true)]
    private static extern bool ClosePrinter(IntPtr hPrinter);
    [DllImport("winspool.drv", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool StartDocPrinter(IntPtr hPrinter, int level, ref DOCINFO di);
    [DllImport("winspool.drv", SetLastError = true)]
    private static extern bool EndDocPrinter(IntPtr hPrinter);
    [DllImport("winspool.drv", SetLastError = true)]
    private static extern bool StartPagePrinter(IntPtr hPrinter);
    [DllImport("winspool.drv", SetLastError = true)]
    private static extern bool EndPagePrinter(IntPtr hPrinter);
    [DllImport("winspool.drv", SetLastError = true)]
    private static extern bool WritePrinter(IntPtr hPrinter, IntPtr buf, int count, out int written);

    public static int Send(string printerName, byte[] data, string docName)
    {
        IntPtr hPrinter;
        if (!OpenPrinter(printerName, out hPrinter, IntPtr.Zero))
            throw new Exception("OpenPrinter failed: " + Marshal.GetLastWin32Error());
        try
        {
            DOCINFO di = new DOCINFO();
            di.DocName = docName;
            di.Datatype = "RAW";
            if (!StartDocPrinter(hPrinter, 1, ref di))
                throw new Exception("StartDocPrinter failed: " + Marshal.GetLastWin32Error());
            try
            {
                if (!StartPagePrinter(hPrinter))
                    throw new Exception("StartPagePrinter failed: " + Marshal.GetLastWin32Error());
                IntPtr buf = Marshal.AllocCoTaskMem(data.Length);
                try
                {
                    Marshal.Copy(data, 0, buf, data.Length);
                    int written;
                    if (!WritePrinter(hPrinter, buf, data.Length, out written))
                        throw new Exception("WritePrinter failed: " + Marshal.GetLastWin32Error());
                    EndPagePrinter(hPrinter);
                    return written;
                }
                finally { Marshal.FreeCoTaskMem(buf); }
            }
            finally { EndDocPrinter(hPrinter); }
        }
        finally { ClosePrinter(hPrinter); }
    }
}
'@

Add-Type -TypeDefinition $signature -Language CSharp
$written = [FwRawPrinter]::Send($PrinterName, $bytes, "Foilwright Phase 1")
Write-Host ("送出完了: {0} / {1} バイト" -f $written, $bytes.Length)
if ($written -ne $bytes.Length) {
    Write-Warning '送出バイト数が一致しません。スプーラが途中で切った可能性があります。'
}

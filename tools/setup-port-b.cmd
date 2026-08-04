@echo off
rem Foilwright: point the virtual printer at a fixed file instead of FILE:,
rem so printing does not stop to ask for a filename. This is a development
rem stand-in for mfilemon. Run as administrator.
set LOG=E:\build\Foilwright\dumps\setup-port-b.log
set OUT=E:\build\Foilwright\dumps\spool.ps
echo === %DATE% %TIME% === > "%LOG%"

powershell -NoProfile -Command ^
  "$out = 'E:\build\Foilwright\dumps\spool.ps';" ^
  "if (-not (Get-PrinterPort -Name $out -ErrorAction SilentlyContinue)) {" ^
  "  try { Add-PrinterPort -Name $out -ErrorAction Stop; 'port: added' } catch { 'port failed: ' + $_.Exception.Message }" ^
  "} else { 'port: already present' };" ^
  "try { Set-Printer -Name 'Foilwright MD-5500' -PortName $out -ErrorAction Stop; 'printer: repointed' } catch { 'repoint failed: ' + $_.Exception.Message };" ^
  "Get-Printer -Name 'Foilwright MD-5500' | Format-List Name, DriverName, PortName" >> "%LOG%" 2>&1

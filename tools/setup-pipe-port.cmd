@echo off
rem Foilwright: point the virtual printer at a named pipe instead of a file.
rem If the in-box Local Port monitor accepts this, the third-party port
rem monitor (mfilemon) is not needed at all. Run as administrator.
set LOG=E:\build\Foilwright\dumps\setup-pipe-port.log
echo === %DATE% %TIME% === > "%LOG%"

powershell -NoProfile -Command ^
  "$pipe = '\\.\pipe\foilwright';" ^
  "if (-not (Get-PrinterPort -Name $pipe -ErrorAction SilentlyContinue)) {" ^
  "  try { Add-PrinterPort -Name $pipe -ErrorAction Stop; 'port: added' } catch { 'port failed: ' + $_.Exception.Message }" ^
  "} else { 'port: already present' };" ^
  "try { Set-Printer -Name 'Foilwright MD-5500' -PortName $pipe -ErrorAction Stop; 'printer: repointed' } catch { 'repoint failed: ' + $_.Exception.Message };" ^
  "Get-Printer -Name 'Foilwright MD-5500' | Format-List Name, PortName" >> "%LOG%" 2>&1

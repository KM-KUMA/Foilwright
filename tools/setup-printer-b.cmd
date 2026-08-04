@echo off
rem Foilwright: create the virtual printer on the in-box generic PostScript
rem driver (D-022). No signing, no driver package -- everything used here
rem ships with Windows.
rem
rem The port is FILE: for now: printing asks for a filename and writes the
rem PostScript there, which is enough to prove the chain up to the converter.
rem mfilemon replaces it once the converter exists.
rem
rem MUST run as administrator.
rem
rem UNDO: powershell -Command "Remove-Printer 'Foilwright MD-5500'"
set LOG=E:\build\Foilwright\dumps\setup-printer-b.log
echo === %DATE% %TIME% === > "%LOG%"

powershell -NoProfile -Command ^
  "try { Add-PrinterDriver -Name 'MS Publisher Color Printer' -ErrorAction Stop; 'driver: added' } catch { 'driver: ' + $_.Exception.Message };" ^
  "if (-not (Get-Printer -Name 'Foilwright MD-5500' -ErrorAction SilentlyContinue)) {" ^
  "  try { Add-Printer -Name 'Foilwright MD-5500' -DriverName 'MS Publisher Color Printer' -PortName 'FILE:' -ErrorAction Stop; 'printer: added' }" ^
  "  catch { 'printer failed: ' + $_.Exception.Message }" ^
  "} else { 'printer: already present' };" ^
  "Get-Printer -Name 'Foilwright MD-5500' -ErrorAction SilentlyContinue | Format-List Name, DriverName, PortName, PrinterStatus" >> "%LOG%" 2>&1

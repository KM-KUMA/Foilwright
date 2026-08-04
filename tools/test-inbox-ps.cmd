@echo off
rem Foilwright: does an in-box PostScript driver still install on this Windows?
rem Decides whether ERROR_PRINTER_DRIVER_BLOCKED is about third-party packages
rem specifically, or about v3 printer drivers in general (D-022).
set LOG=E:\build\Foilwright\dumps\inbox-ps.log
echo === %DATE% %TIME% === > "%LOG%"
powershell -NoProfile -Command ^
  "try { Add-PrinterDriver -Name 'MS Publisher Color Printer' -ErrorAction Stop; 'in-box PS driver: OK' }" ^
  "catch { 'in-box PS driver failed: ' + $_.Exception.Message + ' / ' + $_.FullyQualifiedErrorId };" ^
  "Get-PrinterDriver | Where-Object { $_.Name -like 'MS Publisher*' } | Format-List Name, DriverVersion" >> "%LOG%" 2>&1

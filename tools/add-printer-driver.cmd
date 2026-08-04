@echo off
rem Foilwright: register the staged driver package as a printer driver.
rem MUST run as administrator. Assumes install-printer.cmd already staged it.
set LOG=E:\build\Foilwright\dumps\add-driver.log
echo === %DATE% %TIME% === > "%LOG%"

powershell -NoProfile -Command ^
  "$inf = (Get-ChildItem C:\Windows\System32\DriverStore\FileRepository -Filter foilwright.inf -Recurse -ErrorAction SilentlyContinue | Select-Object -First 1).FullName;" ^
  "'INF: ' + $inf;" ^
  "try { Add-PrinterDriver -Name 'Foilwright MD-5500' -InfPath $inf -ErrorAction Stop; 'Add-PrinterDriver: OK' }" ^
  "catch { 'Add-PrinterDriver failed: ' + $_.Exception.Message };" ^
  "try { Add-PrinterDriver -Name 'Foilwright MD-5500' -ErrorAction Stop; 'fallback (no InfPath): OK' }" ^
  "catch { 'fallback failed: ' + $_.Exception.Message };" ^
  "Get-PrinterDriver | Where-Object { $_.Name -like 'Foilwright*' } | Format-List Name, Manufacturer, DriverVersion" >> "%LOG%" 2>&1

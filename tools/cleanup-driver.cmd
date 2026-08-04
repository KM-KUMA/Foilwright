@echo off
rem Foilwright: remove the driver package that ERROR_PRINTER_DRIVER_BLOCKED
rem left staged in the driver store (see DOMAIN 3.5.1). Run as administrator.
rem
rem The signing certificate is NOT removed here; that is a separate decision.
rem To remove it as well:
rem   powershell -Command "Get-ChildItem Cert:\LocalMachine\Root, Cert:\LocalMachine\TrustedPublisher | Where-Object { $_.Subject -like '*Foilwright*' } | Remove-Item"
set LOG=E:\build\Foilwright\dumps\cleanup-driver.log
echo === %DATE% %TIME% === > "%LOG%"

for /f "tokens=*" %%i in ('powershell -NoProfile -Command "(pnputil /enum-drivers) -join \"`n\" -split \"`n`n\" | Where-Object { $_ -match 'foilwright.inf' } | ForEach-Object { if ($_ -match '(oem\d+\.inf)') { $matches[1] } }"') do (
  echo removing %%i >> "%LOG%" 2>&1
  pnputil /delete-driver %%i /uninstall /force >> "%LOG%" 2>&1
)

echo --- remaining --- >> "%LOG%" 2>&1
powershell -NoProfile -Command "(pnputil /enum-drivers) -join \"`n\" | Select-String -Pattern 'foilwright' -SimpleMatch | Measure-Object | Select-Object -ExpandProperty Count" >> "%LOG%" 2>&1

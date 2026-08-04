@echo off
rem Foilwright: trust the signing certificate and install the PPD package.
rem MUST run as administrator.
rem
rem WHAT THIS CHANGES ON THIS MACHINE:
rem   1. adds ppd\foilwright.cer to LocalMachine\Root and TrustedPublisher,
rem      which makes Windows trust anything signed by that certificate
rem   2. imports the driver package into the driver store
rem   3. registers a printer driver named "Foilwright MD-5500"
rem
rem UNDO:
rem   powershell -Command "Remove-PrinterDriver 'Foilwright MD-5500'"
rem   powershell -Command "Get-ChildItem Cert:\LocalMachine\Root, Cert:\LocalMachine\TrustedPublisher | Where-Object { $_.Subject -like '*Foilwright*' } | Remove-Item"
rem
cd /d E:\build\Foilwright
set LOG=E:\build\Foilwright\dumps\install-printer.log
echo === %DATE% %TIME% === > "%LOG%"

echo --- trusting the signing certificate --- >> "%LOG%" 2>&1
certutil -addstore -f Root "E:\build\Foilwright\ppd\foilwright.cer" >> "%LOG%" 2>&1
certutil -addstore -f TrustedPublisher "E:\build\Foilwright\ppd\foilwright.cer" >> "%LOG%" 2>&1

echo --- verifying the catalog signature --- >> "%LOG%" 2>&1
powershell -NoProfile -Command "Get-AuthenticodeSignature 'E:\build\Foilwright\ppd\foilwright.cat' | Format-List Status, StatusMessage, SignerCertificate" >> "%LOG%" 2>&1

echo --- staging the driver package --- >> "%LOG%" 2>&1
pnputil /add-driver "E:\build\Foilwright\ppd\foilwright.inf" >> "%LOG%" 2>&1
echo pnputil exit: %ERRORLEVEL% >> "%LOG%" 2>&1

echo --- installing the printer driver --- >> "%LOG%" 2>&1
rundll32 printui.dll,PrintUIEntry /ia /m "Foilwright MD-5500" /f "E:\build\Foilwright\ppd\foilwright.inf" >> "%LOG%" 2>&1
echo printui exit: %ERRORLEVEL% >> "%LOG%" 2>&1

echo --- result --- >> "%LOG%" 2>&1
powershell -NoProfile -Command "Get-PrinterDriver | Where-Object { $_.Name -like 'Foilwright*' } | Format-List Name, Manufacturer, DriverVersion" >> "%LOG%" 2>&1

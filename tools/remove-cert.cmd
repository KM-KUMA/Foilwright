@echo off
rem Foilwright: remove the self-signed certificate that was trusted while
rem testing the PPD driver package (DOMAIN 3.5.1 / D-022).
rem That approach was abandoned, so the trust must not be left behind.
rem MUST run as administrator.
set LOG=E:\build\Foilwright\dumps\remove-cert.log
echo === %DATE% %TIME% === > "%LOG%"
powershell -NoProfile -Command ^
  "$c = Get-ChildItem Cert:\LocalMachine\Root, Cert:\LocalMachine\TrustedPublisher, Cert:\CurrentUser\My | Where-Object { $_.Subject -like '*Foilwright*' };" ^
  "if (-not $c) { 'nothing to remove' } else { $c | ForEach-Object { 'removing: ' + $_.PSParentPath + ' / ' + $_.Thumbprint; Remove-Item $_.PSPath -Force } };" ^
  "'--- remaining ---';" ^
  "(Get-ChildItem Cert:\LocalMachine\Root, Cert:\LocalMachine\TrustedPublisher, Cert:\CurrentUser\My | Where-Object { $_.Subject -like '*Foilwright*' } | Measure-Object).Count" >> "%LOG%" 2>&1

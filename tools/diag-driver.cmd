@echo off
rem Foilwright: capture the exact failure code from Add-PrinterDriver.
set LOG=E:\build\Foilwright\dumps\diag-driver.log
echo === %DATE% %TIME% === > "%LOG%"
powershell -NoProfile -Command ^
  "$ErrorActionPreference='Stop';" ^
  "$inf = (Get-ChildItem C:\Windows\System32\DriverStore\FileRepository -Filter foilwright.inf -Recurse | Select-Object -First 1).FullName;" ^
  "try { Add-PrinterDriver -Name 'Foilwright MD-5500' -InfPath $inf }" ^
  "catch { $e = $_; 'Message : ' + $e.Exception.Message; 'HResult : 0x{0:X8}' -f $e.Exception.HResult; 'CategoryInfo: ' + $e.CategoryInfo; 'FullyQualifiedErrorId: ' + $e.FullyQualifiedErrorId }" >> "%LOG%" 2>&1

echo --- printui with UI (shows a real dialog if it fails) --- >> "%LOG%" 2>&1
echo --- last 30 setupapi lines --- >> "%LOG%" 2>&1
powershell -NoProfile -Command "Get-Content C:\Windows\INF\setupapi.dev.log -Tail 30" >> "%LOG%" 2>&1

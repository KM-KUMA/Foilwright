@echo off
rem Foilwright: build the distributable zip. Double-click this file, or run it
rem from a shell. It wraps tools\make-package.ps1 so no PowerShell knowledge is
rem needed (D-041).
rem
rem WHAT IT DOES:
rem   1. dotnet publish (self-contained, win-x64) for the tray app and the CLI
rem   2. collects the config files next to the executables
rem   3. adds install.ps1 / uninstall.ps1 / README.txt
rem   4. zips everything into dist\
rem
rem WHAT IT DOES NOT DO:
rem   - it does NOT install anything on this machine
rem   - it does NOT touch the printer, the port, or the tray app
rem   To install, run install.ps1 from inside the zip (as administrator).
rem
rem REQUIRES: the .NET SDK (dotnet build must already work here).
rem
rem NOTE: this file is ASCII on purpose. Japanese in a .cmd breaks under cp932,
rem so all the Japanese output comes from make-package.ps1 instead.

rem UTF-8 so the PowerShell script's Japanese output is readable.
chcp 65001 >nul

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0make-package.ps1" %*
set RC=%ERRORLEVEL%

echo.
if %RC% NEQ 0 (
  echo [FAILED] exit code %RC%
  echo Nothing was installed. Check the messages above.
) else (
  echo [OK] the zip is in the dist folder.
)
echo.
pause
exit /b %RC%

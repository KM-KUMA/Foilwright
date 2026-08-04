@echo off
"C:\Program Files\usbipd-win\usbipd.exe" unbind --busid 1-2 > E:\build\Foilwright\dumps\unbind.log 2>&1
echo exit=%ERRORLEVEL% >> E:\build\Foilwright\dumps\unbind.log

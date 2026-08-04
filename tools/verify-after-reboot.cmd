@echo off
rem Foilwright: check the printer path after removing USBPcap and rebooting.
rem
rem Order matters. Each step must pass before the next one means anything,
rem and nothing else may talk to the printer while this runs (DOMAIN 15.2.1).
rem
rem   1. USBPcap really gone?
rem   2. usbipd not holding the device?
rem   3. status query answers?
rem   4. a small black square actually prints?
rem
rem Step 4 consumes one sheet and a little black ribbon.
set LOG=E:\build\Foilwright\dumps\verify-after-reboot.log
cd /d E:\build\Foilwright
echo === %DATE% %TIME% === > "%LOG%"

echo --- 1. USBPcap driver --- >> "%LOG%" 2>&1
sc query USBPcap >> "%LOG%" 2>&1

echo --- 2. usbipd sharing state --- >> "%LOG%" 2>&1
"C:\Program Files\usbipd-win\usbipd.exe" list >> "%LOG%" 2>&1

echo --- 3. status query --- >> "%LOG%" 2>&1
powershell -NoProfile -ExecutionPolicy Bypass -File "E:\build\Foilwright\tools\probe-status.ps1" -Label "USBPcap 削除 + 再起動後" >> "%LOG%" 2>&1

echo --- 4. small black square --- >> "%LOG%" 2>&1
powershell -NoProfile -ExecutionPolicy Bypass -File "E:\build\Foilwright\tools\alps-send.ps1" -Path "E:\build\Foilwright\dumps\phase1_blackraster.bin" >> "%LOG%" 2>&1

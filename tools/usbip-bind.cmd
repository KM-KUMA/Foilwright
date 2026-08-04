@echo off
rem Foilwright: share MD-5500 (busid 1-2) with usbipd. Run as admin.
rem --force is required while the USBPcap filter driver is installed.
"C:\Program Files\usbipd-win\usbipd.exe" bind --busid 1-2 --force
"C:\Program Files\usbipd-win\usbipd.exe" list
timeout /t 6

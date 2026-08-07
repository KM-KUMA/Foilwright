@echo off
rem Foilwright: capture the bus while the official driver reports a mechanism
rem error, to find where it learns the *kind* of mechanism.
rem
rem Background: 05 01 does not distinguish the 8 mechanism errors listed in
rem DOMAIN 13.8.3 -- the response is byte-identical for a paper feed error and
rem a carriage error (11.4, correction of 2026-08-08). The official driver
rem names them, so it reads the kind from somewhere else.
rem
rem Run as administrator, AFTER rebooting (the filter driver needs it).
rem Stop with Ctrl+C once the driver has shown its error dialog.
rem
rem Remember to uninstall USBPcap afterwards:
rem   winget uninstall --id desowin.USBPcap   (then reboot)
cd /d E:\build\Foilwright
"C:\Program Files\USBPcap\USBPcapCMD.exe" -d \\.\USBPcap1 -o E:\build\Foilwright\dumps\mech_error.pcap -A
pause

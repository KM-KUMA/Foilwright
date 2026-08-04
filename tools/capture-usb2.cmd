@echo off
rem Foilwright USB capture, second run (run as admin). Stop with Ctrl+C.
cd /d E:\build\Foilwright
"C:\Program Files\USBPcap\USBPcapCMD.exe" -d \\.\USBPcap1 -o E:\build\Foilwright\dumps\vm_print2.pcap -A
pause

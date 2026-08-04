@echo off
rem Foilwright: reset the printer's USB interface without unplugging it.
rem
rem The ALPS bulk protocol is a single shared conversation (write a command,
rem read its reply). If two processes talk to the device at once the replies
rem get crossed and the interface wedges: reads block forever. Disabling and
rem re-enabling the device is the software equivalent of a replug.
rem
rem MUST run as administrator.
set LOG=E:\build\Foilwright\dumps\reset-printer.log
echo === %DATE% %TIME% === > "%LOG%"

powershell -NoProfile -Command ^
  "$d = Get-PnpDevice -PresentOnly | Where-Object { $_.InstanceId -match 'VID_044E' };" ^
  "if (-not $d) { 'device not found'; exit 1 };" ^
  "'device: ' + $d.FriendlyName + ' (' + $d.Status + ')';" ^
  "Disable-PnpDevice -InstanceId $d.InstanceId -Confirm:$false -ErrorAction Stop; 'disabled';" ^
  "Start-Sleep -Seconds 3;" ^
  "Enable-PnpDevice -InstanceId $d.InstanceId -Confirm:$false -ErrorAction Stop; 'enabled';" ^
  "Start-Sleep -Seconds 3;" ^
  "(Get-PnpDevice -InstanceId $d.InstanceId).Status" >> "%LOG%" 2>&1

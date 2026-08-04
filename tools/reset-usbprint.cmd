@echo off
rem Foilwright: restart the USBPRINT child node.
rem
rem The printer-class device interface ({28d78fad-...}) can end up registered
rem but not linked, in which case no user-mode open can reach the printer and
rem every read times out. Restarting the parent USB node does not always fix
rem it; this targets the USBPRINT child that owns the interface.
rem
rem MUST run as administrator.
set LOG=E:\build\Foilwright\dumps\reset-usbprint.log
echo === %DATE% %TIME% === > "%LOG%"

powershell -NoProfile -Command ^
  "$c = Get-PnpDevice -PresentOnly | Where-Object { $_.InstanceId -match 'USBPRINT' -and $_.FriendlyName -match 'MD-5500' };" ^
  "if (-not $c) { 'USBPRINT child not found'; exit 1 };" ^
  "'child: ' + $c.InstanceId + ' (' + $c.Status + ')';" ^
  "Disable-PnpDevice -InstanceId $c.InstanceId -Confirm:$false -ErrorAction Continue; 'disabled';" ^
  "Start-Sleep -Seconds 3;" ^
  "Enable-PnpDevice -InstanceId $c.InstanceId -Confirm:$false -ErrorAction Continue; 'enabled';" ^
  "Start-Sleep -Seconds 4;" ^
  "'status: ' + (Get-PnpDevice -InstanceId $c.InstanceId).Status;" ^
  "$g = '{28d78fad-5a12-11D1-ae5b-0000f803a8c2}';" ^
  "Get-ChildItem \"HKLM:\SYSTEM\CurrentControlSet\Control\DeviceClasses\$g\" -EA SilentlyContinue | ForEach-Object {" ^
  "  $ctl = Get-ItemProperty \"$($_.PSPath)\#\Control\" -EA SilentlyContinue;" ^
  "  'Linked=' + ($null -ne $ctl) + '  ' + $_.PSChildName }" >> "%LOG%" 2>&1

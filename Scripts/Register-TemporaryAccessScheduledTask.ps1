[CmdletBinding()]
param(
    [string]$TaskName = 'UserChangeQueueWeb - Temporary Access',
    [string]$ScriptPath = 'C:\Program Files\UserChangeQueueWeb\Worker\Invoke-TemporaryAccessQueue.ps1',
    [string]$AppSettingsPath = 'C:\inetpub\UserChangeQueueWeb\appsettings.json',
    [string]$TaskUser = 'SYSTEM'
)
$ErrorActionPreference='Stop'
$arguments = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -File `"$ScriptPath`" -AppSettingsPath `"$AppSettingsPath`""
$action = New-ScheduledTaskAction -Execute 'powershell.exe' -Argument $arguments
$trigger = New-ScheduledTaskTrigger -Once -At (Get-Date).AddMinutes(1) -RepetitionInterval (New-TimeSpan -Minutes 5)
$settings = New-ScheduledTaskSettingsSet -StartWhenAvailable -MultipleInstances IgnoreNew -ExecutionTimeLimit (New-TimeSpan -Minutes 30)
if ($TaskUser -eq 'SYSTEM') { $principal = New-ScheduledTaskPrincipal -UserId 'SYSTEM' -LogonType ServiceAccount -RunLevel Highest }
else { $principal = New-ScheduledTaskPrincipal -UserId $TaskUser -LogonType Password -RunLevel Highest }
Register-ScheduledTask -TaskName $TaskName -Action $action -Trigger $trigger -Settings $settings -Principal $principal -Description 'Adds and removes approved temporary AD group memberships.' -Force

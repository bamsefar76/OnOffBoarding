[CmdletBinding()]
param(
    [Parameter()]
    [string]$TaskName = 'UserChangeQueueWeb - Project Import',

    [Parameter()]
    [string]$ScriptPath = 'C:\Program Files\UserChangeQueueWeb\Scripts\Invoke-ProjectImport.ps1',

    [Parameter()]
    [string]$TaskUser,

    [Parameter()]
    [ValidateRange(5, 1440)]
    [int]$RepeatEveryMinutes = 30
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if (-not (Test-Path -LiteralPath $ScriptPath -PathType Leaf)) {
    throw "Importer script not found at '$ScriptPath'."
}

$action = New-ScheduledTaskAction `
    -Execute 'powershell.exe' `
    -Argument ('-NoProfile -NonInteractive -ExecutionPolicy Bypass -File "{0}"' -f $ScriptPath)

$trigger = New-ScheduledTaskTrigger -Once -At ((Get-Date).AddMinutes(2)) `
    -RepetitionInterval (New-TimeSpan -Minutes $RepeatEveryMinutes)

$settings = New-ScheduledTaskSettingsSet `
    -StartWhenAvailable `
    -MultipleInstances IgnoreNew `
    -ExecutionTimeLimit (New-TimeSpan -Minutes 20) `
    -RestartCount 3 `
    -RestartInterval (New-TimeSpan -Minutes 5)

if ([string]::IsNullOrWhiteSpace($TaskUser)) {
    throw 'Specify -TaskUser. Use a domain service account or gMSA that can read the CSV folder and connect to UserDatabase.'
}

if ($TaskUser.EndsWith('$')) {
    $principal = New-ScheduledTaskPrincipal -UserId $TaskUser -LogonType ServiceAccount -RunLevel Highest
    Register-ScheduledTask -TaskName $TaskName -Action $action -Trigger $trigger -Settings $settings -Principal $principal -Force | Out-Null
}
else {
    $credential = Get-Credential -UserName $TaskUser -Message 'Enter the password for the scheduled-task service account.'
    Register-ScheduledTask `
        -TaskName $TaskName `
        -Action $action `
        -Trigger $trigger `
        -Settings $settings `
        -User $credential.UserName `
        -Password $credential.GetNetworkCredential().Password `
        -RunLevel Highest `
        -Force | Out-Null
}

Write-Host "Scheduled task '$TaskName' was registered to run every $RepeatEveryMinutes minutes."

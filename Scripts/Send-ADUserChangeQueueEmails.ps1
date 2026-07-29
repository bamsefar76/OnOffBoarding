<#
.SYNOPSIS
    Sends due rows from the shared dbo.ADUserChangeQueueEmails email queue.

.DESCRIPTION
    This is intentionally separate from Invoke-ADUserChangeQueue.ps1. The AD
    worker only queues messages. This worker sends due messages after their
    EarliestSendAt timestamp has passed, with retries.

    Normal scheduled task command:

        .\Send-ADUserChangeQueueEmails.ps1 -Verbose

    Required database settings are created by:

        Database\ADUserChangeQueueEmails.Required.sql
#>

[CmdletBinding()]
param(
    [Parameter()]
	[string]$ConnectionString = $env:USERCHANGEQUEUE_CONNECTION_STRING,

    [Parameter()]
    [ValidateRange(1,500)]
    [int]$BatchSize = 25,

    [Parameter()]
    [switch]$DryRun,

    [Parameter()]
    [string]$LogPath
)

if ([string]::IsNullOrWhiteSpace($ConnectionString)) {
    throw @"
No database connection string was supplied.

Provide one using either:

  -ConnectionString "Server=SQLSERVER\INSTANCE;Database=UserDatabase;Integrated Security=True;TrustServerCertificate=True;"

or define the machine-level environment variable:

  USERCHANGEQUEUE_CONNECTION_STRING
"@
}

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# Do not use $PSScriptRoot in a parameter default expression. Windows
# PowerShell can evaluate that expression before $PSScriptRoot is populated.
$script:ScriptDirectory = if (-not [string]::IsNullOrWhiteSpace($PSScriptRoot)) {
    $PSScriptRoot
}
elseif (-not [string]::IsNullOrWhiteSpace($PSCommandPath)) {
    Split-Path -Path $PSCommandPath -Parent
}
else {
    (Get-Location).Path
}

# Store worker logs outside the application directory by default.
if ([string]::IsNullOrWhiteSpace($LogPath)) {
    $defaultLogDirectory = Join-Path $env:ProgramData 'UserChangeQueueWeb\Logs'
    $LogPath = Join-Path $defaultLogDirectory ("EmailWorker-{0:yyyyMMdd}.log" -f (Get-Date))
}

$script:TranscriptStarted = $false

function Write-Info {
    param([Parameter(Mandatory=$true)][string]$Message)
    Write-Host ("[{0:yyyy-MM-dd HH:mm:ss}] {1}" -f (Get-Date), $Message)
}

function Test-IsBlank {
    param([object]$Value)
    if ($null -eq $Value) { return $true }
    return [string]::IsNullOrWhiteSpace([string]$Value)
}

function Get-EmailDomainFromAddress {
    param([object]$Value)

    if (Test-IsBlank $Value) { return $null }
    $text = ([string]$Value).Trim()
    $at = $text.LastIndexOf('@')
    if ($at -lt 0 -or $at -ge ($text.Length - 1)) { return $null }
    return $text.Substring($at + 1).Trim().ToLowerInvariant()
}

function ConvertTo-DbNullIfNull {
    param([object]$Value)
    if ($null -eq $Value) { return [DBNull]::Value }
    return $Value
}

function Add-SqlParameter {
    param(
        [Parameter(Mandatory=$true)][System.Data.SqlClient.SqlCommand]$Command,
        [Parameter(Mandatory=$true)][string]$Name,
        [Parameter(Mandatory=$true)][System.Data.SqlDbType]$Type,
        [Parameter()][object]$Value,
        [Parameter()][int]$Size = 0
    )

    if ($Size -gt 0) {
        $parameter = $Command.Parameters.Add($Name, $Type, $Size)
    }
    else {
        $parameter = $Command.Parameters.Add($Name, $Type)
    }

    $parameter.Value = ConvertTo-DbNullIfNull $Value
    return $parameter
}

function ConvertFrom-DataRow {
    param([Parameter(Mandatory=$true)][System.Data.DataRow]$Row)

    $values = [ordered]@{}
    foreach ($column in $Row.Table.Columns) {
        $name = $column.ColumnName
        if ($Row.IsNull($column)) {
            $values[$name] = $null
        }
        else {
            $values[$name] = $Row[$column]
        }
    }

    return [pscustomobject]$values
}

function Invoke-SqlQueryRows {
    param([Parameter(Mandatory=$true)][System.Data.SqlClient.SqlCommand]$Command)

    $rows = New-Object System.Collections.Generic.List[object]
    $table = New-Object System.Data.DataTable
    $reader = $Command.ExecuteReader()
    try {
        [void]$table.Load($reader)
    }
    finally {
        $reader.Close()
    }

    foreach ($row in $table.Rows) {
        [void]$rows.Add((ConvertFrom-DataRow $row))
    }

    return $rows
}

function Get-SettingValue {
    param(
        [Parameter(Mandatory=$true)][System.Data.SqlClient.SqlConnection]$Connection,
        [Parameter(Mandatory=$true)][string]$SettingName
    )

    $cmd = $Connection.CreateCommand()
    $cmd.CommandText = @"
IF OBJECT_ID(N'dbo.UserChangeQueueSettings', N'U') IS NULL
BEGIN
    SELECT CAST(NULL AS nvarchar(max)) AS SettingValue;
END
ELSE
BEGIN
    SELECT TOP (1)
        SettingValue
    FROM dbo.UserChangeQueueSettings
    WHERE SettingName = @SettingName
      AND Active = 1;
END
"@
    [void](Add-SqlParameter $cmd '@SettingName' ([System.Data.SqlDbType]::NVarChar) $SettingName 100)

    try {
        $rows = @(Invoke-SqlQueryRows $cmd)
        if ($rows.Count -eq 0) { return $null }
        if (Test-IsBlank $rows[0].SettingValue) { return $null }
        return ([string]$rows[0].SettingValue).Trim()
    }
    finally {
        $cmd.Dispose()
    }
}

function Get-IntSettingValue {
    param(
        [Parameter(Mandatory=$true)][System.Data.SqlClient.SqlConnection]$Connection,
        [Parameter(Mandatory=$true)][string]$SettingName,
        [Parameter(Mandatory=$true)][int]$DefaultValue
    )

    $raw = Get-SettingValue -Connection $Connection -SettingName $SettingName
    if (Test-IsBlank $raw) { return $DefaultValue }

    $parsed = 0
    if ([int]::TryParse([string]$raw, [ref]$parsed)) { return $parsed }

    Write-Warning "Setting '$SettingName' has non-integer value '$raw'. Using default $DefaultValue."
    return $DefaultValue
}

function Get-BoolSettingValue {
    param(
        [Parameter(Mandatory=$true)][System.Data.SqlClient.SqlConnection]$Connection,
        [Parameter(Mandatory=$true)][string]$SettingName,
        [Parameter(Mandatory=$true)][bool]$DefaultValue
    )

    $raw = Get-SettingValue -Connection $Connection -SettingName $SettingName
    if (Test-IsBlank $raw) { return $DefaultValue }

    switch -Regex (([string]$raw).Trim().ToLowerInvariant()) {
        '^(1|true|yes|y|ja|on)$' { return $true }
        '^(0|false|no|n|nei|off)$' { return $false }
        default {
            Write-Warning "Setting '$SettingName' has non-boolean value '$raw'. Using default $DefaultValue."
            return $DefaultValue
        }
    }
}

function Get-DueEmails {
    param(
        [Parameter(Mandatory=$true)][System.Data.SqlClient.SqlConnection]$Connection,
        [Parameter(Mandatory=$true)][int]$MaxAttempts
    )

    $cmd = $Connection.CreateCommand()
    $cmd.CommandText = @"
IF OBJECT_ID(N'dbo.ADUserChangeQueueEmails', N'U') IS NULL
BEGIN
    SELECT TOP (0)
        CAST(NULL AS bigint) AS EmailQueueId,
        CAST(NULL AS bigint) AS RequestId,
        CAST(NULL AS nvarchar(50)) AS EmailType,
        CAST(NULL AS nvarchar(320)) AS ToEmail,
        CAST(NULL AS nvarchar(200)) AS ToName,
        CAST(NULL AS nvarchar(500)) AS Subject,
        CAST(NULL AS nvarchar(max)) AS BodyHtml,
        CAST(NULL AS int) AS Attempts,
        CAST(NULL AS nvarchar(320)) AS RequestMail,
        CAST(NULL AS nvarchar(320)) AS RequestUpn,
        CAST(NULL AS nvarchar(200)) AS QueueDomain,
        CAST(NULL AS nvarchar(100)) AS TemplateName;
END
ELSE
BEGIN
    SELECT TOP (@BatchSize)
        e.EmailQueueId,
        e.RequestId,
        e.EmailType,
        e.ToEmail,
        e.ToName,
        e.Subject,
        e.BodyHtml,
        e.Attempts,
        q.Mail AS RequestMail,
        q.NewUserPrincipalName AS RequestUpn,
        e.Domain AS QueueDomain,
        e.TemplateName
    FROM dbo.ADUserChangeQueueEmails AS e WITH (READPAST)
    LEFT JOIN dbo.ADUserChangeQueue AS q
        ON q.RequestId = e.RequestId
    WHERE e.Status IN (N'Pending', N'Retry')
      AND e.EarliestSendAt <= SYSDATETIME()
      AND e.Attempts < @MaxAttempts
    ORDER BY e.EarliestSendAt, e.EmailQueueId;
END
"@
    [void](Add-SqlParameter $cmd '@BatchSize' ([System.Data.SqlDbType]::Int) $BatchSize)
    [void](Add-SqlParameter $cmd '@MaxAttempts' ([System.Data.SqlDbType]::Int) $MaxAttempts)

    try { return @(Invoke-SqlQueryRows $cmd) }
    finally { $cmd.Dispose() }
}

function Claim-Email {
    param(
        [Parameter(Mandatory=$true)][System.Data.SqlClient.SqlConnection]$Connection,
        [Parameter(Mandatory=$true)][long]$EmailQueueId
    )

    if ($DryRun) {
        Write-Info "DRYRUN: would claim email queue row $EmailQueueId."
        return $true
    }

    $cmd = $Connection.CreateCommand()
    $cmd.CommandText = @"
UPDATE dbo.ADUserChangeQueueEmails
SET
    Status = N'Processing',
    Attempts = Attempts + 1,
    LastAttemptAt = SYSDATETIME(),
    UpdatedAt = SYSDATETIME(),
    UpdatedBy = SUSER_SNAME()
WHERE EmailQueueId = @EmailQueueId
  AND Status IN (N'Pending', N'Retry')
  AND EarliestSendAt <= SYSDATETIME();
"@
    [void](Add-SqlParameter $cmd '@EmailQueueId' ([System.Data.SqlDbType]::BigInt) $EmailQueueId)

    try { return ($cmd.ExecuteNonQuery() -eq 1) }
    finally { $cmd.Dispose() }
}

function Complete-Email {
    param(
        [Parameter(Mandatory=$true)][System.Data.SqlClient.SqlConnection]$Connection,
        [Parameter(Mandatory=$true)][long]$EmailQueueId
    )

    if ($DryRun) {
        Write-Info "DRYRUN: would mark email queue row $EmailQueueId as Sent."
        return
    }

    $cmd = $Connection.CreateCommand()
    $cmd.CommandText = @"
UPDATE dbo.ADUserChangeQueueEmails
SET
    Status = N'Sent',
    SentAt = SYSDATETIME(),
    ErrorMessage = NULL,
    UpdatedAt = SYSDATETIME(),
    UpdatedBy = SUSER_SNAME()
WHERE EmailQueueId = @EmailQueueId;
"@
    [void](Add-SqlParameter $cmd '@EmailQueueId' ([System.Data.SqlDbType]::BigInt) $EmailQueueId)

    try { [void]$cmd.ExecuteNonQuery() }
    finally { $cmd.Dispose() }
}

function Fail-EmailAttempt {
    param(
        [Parameter(Mandatory=$true)][System.Data.SqlClient.SqlConnection]$Connection,
        [Parameter(Mandatory=$true)][long]$EmailQueueId,
        [Parameter(Mandatory=$true)][string]$ErrorMessage,
        [Parameter(Mandatory=$true)][int]$MaxAttempts,
        [Parameter(Mandatory=$true)][int]$RetryDelayMinutes
    )

    $message = $ErrorMessage
    if ($message.Length -gt 3900) {
        $message = $message.Substring(0, 3900)
    }

    if ($DryRun) {
        Write-Info "DRYRUN: would record failed send for email queue row $EmailQueueId. Error: $message"
        return
    }

    $cmd = $Connection.CreateCommand()
    $cmd.CommandText = @"
UPDATE dbo.ADUserChangeQueueEmails
SET
    Status = CASE WHEN Attempts >= @MaxAttempts THEN N'Failed' ELSE N'Retry' END,
    EarliestSendAt = CASE WHEN Attempts >= @MaxAttempts THEN EarliestSendAt ELSE DATEADD(minute, @RetryDelayMinutes, SYSDATETIME()) END,
    ErrorMessage = @ErrorMessage,
    UpdatedAt = SYSDATETIME(),
    UpdatedBy = SUSER_SNAME()
WHERE EmailQueueId = @EmailQueueId;
"@
    [void](Add-SqlParameter $cmd '@EmailQueueId' ([System.Data.SqlDbType]::BigInt) $EmailQueueId)
    [void](Add-SqlParameter $cmd '@MaxAttempts' ([System.Data.SqlDbType]::Int) $MaxAttempts)
    [void](Add-SqlParameter $cmd '@RetryDelayMinutes' ([System.Data.SqlDbType]::Int) $RetryDelayMinutes)
    [void](Add-SqlParameter $cmd '@ErrorMessage' ([System.Data.SqlDbType]::NVarChar) $message 4000)

    try { [void]$cmd.ExecuteNonQuery() }
    finally { $cmd.Dispose() }
}

function New-SmtpClientFromSettings {
    param([Parameter(Mandatory=$true)][System.Data.SqlClient.SqlConnection]$Connection)

    $server = Get-SettingValue -Connection $Connection -SettingName 'SmtpServer'
    if (Test-IsBlank $server -or [string]::Equals($server, 'CHANGE-ME-SMTP-SERVER', [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Set dbo.UserChangeQueueSettings SettingName='SmtpServer' before running the email worker."
    }

    $port = Get-IntSettingValue -Connection $Connection -SettingName 'SmtpPort' -DefaultValue 25
    $useSsl = Get-BoolSettingValue -Connection $Connection -SettingName 'SmtpUseSsl' -DefaultValue $false
    $useDefaultCredentials = Get-BoolSettingValue -Connection $Connection -SettingName 'SmtpUseDefaultCredentials' -DefaultValue $true
    $credentialUser = Get-SettingValue -Connection $Connection -SettingName 'SmtpCredentialUser'
    $credentialPasswordPath = Get-SettingValue -Connection $Connection -SettingName 'SmtpCredentialPasswordPath'

    $smtp = [System.Net.Mail.SmtpClient]::new($server, $port)
    $smtp.EnableSsl = $useSsl
    $smtp.Timeout = 120000

    if (-not (Test-IsBlank $credentialUser)) {
        if (Test-IsBlank $credentialPasswordPath) {
            throw "SmtpCredentialUser is set, but SmtpCredentialPasswordPath is blank."
        }

        if (-not (Test-Path -LiteralPath $credentialPasswordPath)) {
            throw "SmtpCredentialPasswordPath '$credentialPasswordPath' does not exist or is not readable."
        }

        $securePassword = Import-Clixml -LiteralPath $credentialPasswordPath
        $credential = [System.Management.Automation.PSCredential]::new($credentialUser, $securePassword)
        $smtp.UseDefaultCredentials = $false
        $smtp.Credentials = $credential.GetNetworkCredential()
    }
    else {
        $smtp.UseDefaultCredentials = $useDefaultCredentials
    }

    return $smtp
}


function Get-EmailTemplateImages {
    param(
        [Parameter(Mandatory=$true)][System.Data.SqlClient.SqlConnection]$Connection,
        [Parameter(Mandatory=$true)][string]$TemplateName,
        [Parameter()][string]$Domain
    )

    $cmd = $Connection.CreateCommand()
    $cmd.CommandText = @"
IF OBJECT_ID(N'dbo.EmailTemplateImages', N'U') IS NULL
BEGIN
    SELECT TOP (0)
        CAST(NULL AS nvarchar(100)) AS ContentId,
        CAST(NULL AS nvarchar(1000)) AS ImagePath,
        CAST(NULL AS nvarchar(100)) AS MimeType,
        CAST(NULL AS nvarchar(200)) AS Domain;
END
ELSE
BEGIN
    SELECT
        ContentId,
        ImagePath,
        MimeType,
        Domain
    FROM dbo.EmailTemplateImages
    WHERE TemplateName = @TemplateName
      AND Active = 1
      AND
      (
          Domain = N'*'
          OR LOWER(Domain) = LOWER(@Domain)
      )
    ORDER BY
        CASE WHEN LOWER(Domain) = LOWER(@Domain) THEN 0 ELSE 1 END,
        Id;
END
"@
    [void](Add-SqlParameter $cmd '@TemplateName' ([System.Data.SqlDbType]::NVarChar) $TemplateName 100)
    [void](Add-SqlParameter $cmd '@Domain' ([System.Data.SqlDbType]::NVarChar) $Domain 200)

    try {
        $rows = @(Invoke-SqlQueryRows $cmd)
        $seen = @{}
        $result = New-Object System.Collections.Generic.List[object]
        foreach ($row in $rows) {
            if (Test-IsBlank $row.ContentId) { continue }
            $cid = ([string]$row.ContentId).Trim()
            if ($seen.ContainsKey($cid)) { continue }
            $seen[$cid] = $true
            [void]$result.Add($row)
        }
        return $result
    }
    finally {
        $cmd.Dispose()
    }
}

function Send-QueuedEmail {
    param(
        [Parameter(Mandatory=$true)][System.Data.SqlClient.SqlConnection]$Connection,
        [Parameter(Mandatory=$true)]$SmtpClient,
        [Parameter(Mandatory=$true)]$Email
    )

    $fromEmail = Get-SettingValue -Connection $Connection -SettingName 'SmtpFromEmail'
    if (Test-IsBlank $fromEmail) { throw "Set dbo.UserChangeQueueSettings SettingName='SmtpFromEmail'." }

    $fromName = Get-SettingValue -Connection $Connection -SettingName 'SmtpFromName'
    if (Test-IsBlank $fromName) { $fromName = $fromEmail }

    $message = [System.Net.Mail.MailMessage]::new()
    try {
        $message.From = [System.Net.Mail.MailAddress]::new($fromEmail, $fromName)
        if (Test-IsBlank $Email.ToName) {
            [void]$message.To.Add([System.Net.Mail.MailAddress]::new([string]$Email.ToEmail))
        }
        else {
            [void]$message.To.Add([System.Net.Mail.MailAddress]::new([string]$Email.ToEmail, [string]$Email.ToName))
        }

        $message.Subject = [string]$Email.Subject

        $domain = if (-not (Test-IsBlank $Email.QueueDomain)) { ([string]$Email.QueueDomain).Trim().ToLowerInvariant() } else {
            $domainSource = if (-not (Test-IsBlank $Email.RequestMail)) { $Email.RequestMail } elseif (-not (Test-IsBlank $Email.RequestUpn)) { $Email.RequestUpn } else { $Email.ToEmail }
            Get-EmailDomainFromAddress -Value $domainSource
        }
        if (Test-IsBlank $domain) { $domain = '*' }

        $templateName = if (-not (Test-IsBlank $Email.TemplateName)) { [string]$Email.TemplateName } else { [string]$Email.EmailType }
        $images = @(Get-EmailTemplateImages -Connection $Connection -TemplateName $templateName -Domain $domain)

        if ($DryRun) {
            Write-Info "DRYRUN: would send $($Email.EmailType) email queue row $($Email.EmailQueueId) to $($Email.ToEmail) subject '$($Email.Subject)' with $($images.Count) embedded image(s) for domain '$domain'."
            return
        }

        if ($images.Count -eq 0) {
            $message.Body = [string]$Email.BodyHtml
            $message.IsBodyHtml = $true
        }
        else {
            $view = [System.Net.Mail.AlternateView]::CreateAlternateViewFromString([string]$Email.BodyHtml, $null, 'text/html')
            foreach ($image in $images) {
                if (Test-IsBlank $image.ImagePath -or -not (Test-Path -LiteralPath ([string]$image.ImagePath))) {
                    Write-Warning "Embedded image '$($image.ContentId)' for template '$templateName' domain '$domain' points to missing path '$($image.ImagePath)'."
                    continue
                }

                $resource = [System.Net.Mail.LinkedResource]::new([string]$image.ImagePath, [string]$image.MimeType)
                $resource.ContentId = ([string]$image.ContentId).Trim()
                $resource.TransferEncoding = [System.Net.Mime.TransferEncoding]::Base64
                $resource.ContentLink = [Uri]::new("cid:$($resource.ContentId)")
                [void]$view.LinkedResources.Add($resource)
                Write-Info "Embedded image cid:$($resource.ContentId) from '$($image.ImagePath)' for $($Email.EmailType) email queue row $($Email.EmailQueueId)."
            }

            $message.AlternateViews.Add($view)
        }

        $SmtpClient.Send($message)
    }
    finally {
        $message.Dispose()
    }
}

try {
    $logDirectory = Split-Path -Path $LogPath -Parent
    if (-not (Test-IsBlank $logDirectory) -and -not (Test-Path -LiteralPath $logDirectory)) {
        New-Item -ItemType Directory -Path $logDirectory -Force | Out-Null
    }

    try {
        Start-Transcript -Path $LogPath -Append | Out-Null
        $script:TranscriptStarted = $true
    }
    catch {
        Write-Warning "Could not start transcript at '$LogPath': $($_.Exception.Message)"
    }

    Write-Info "Starting shared email queue worker. DryRun=$DryRun BatchSize=$BatchSize"

    $connection = [System.Data.SqlClient.SqlConnection]::new($ConnectionString)
    $connection.Open()

    try {
        $maxAttempts = Get-IntSettingValue -Connection $connection -SettingName 'EmailMaxAttempts' -DefaultValue 20
        $retryDelayMinutes = Get-IntSettingValue -Connection $connection -SettingName 'EmailRetryDelayMinutes' -DefaultValue 30

        $smtp = New-SmtpClientFromSettings -Connection $connection
        try {
            $emails = @(Get-DueEmails -Connection $connection -MaxAttempts $maxAttempts)
            Write-Info "Found $($emails.Count) due email row(s)."

            foreach ($email in $emails) {
                $emailQueueId = [long]$email.EmailQueueId
                if (-not (Claim-Email -Connection $connection -EmailQueueId $emailQueueId)) {
                    Write-Warning "Email queue row $emailQueueId was not claimed. It may have been processed by another worker."
                    continue
                }

                try {
                    Send-QueuedEmail -Connection $connection -SmtpClient $smtp -Email $email
                    Complete-Email -Connection $connection -EmailQueueId $emailQueueId
                    Write-Info "Sent $($email.EmailType) email queue row $emailQueueId to $($email.ToEmail)."
                }
                catch {
                    $message = $_.Exception.Message
                    Write-Warning "Email queue row $emailQueueId failed: $message"
                    Fail-EmailAttempt -Connection $connection -EmailQueueId $emailQueueId -ErrorMessage $message -MaxAttempts $maxAttempts -RetryDelayMinutes $retryDelayMinutes
                }
            }
        }
        finally {
            if ($null -ne $smtp) { $smtp.Dispose() }
        }
    }
    finally {
        $connection.Close()
        $connection.Dispose()
    }
}
finally {
    if ($script:TranscriptStarted) {
        try { Stop-Transcript | Out-Null } catch { }
    }
}

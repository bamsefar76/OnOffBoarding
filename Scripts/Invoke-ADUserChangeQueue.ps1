<#
.SYNOPSIS
    Processes approved AD user change requests from dbo.ADUserChangeQueue.

.DESCRIPTION
    This script is intended to run on a domain-joined Windows server with the
    ActiveDirectory PowerShell module installed. It reads due queue rows from
    UserDatabase, applies CREATE/UPDATE changes to Active Directory, applies
    queued group changes from dbo.ADUserChangeQueueGroups, and updates queue
    status to the success/failure values allowed by the database constraint.

    Default behavior is intentionally conservative:
      - only Status = Approved is processed
      - CREATE requests become eligible one calendar day before ExecuteAfter by default
      - UPDATE requests must have ExecuteAfter due
      - Pending rows are not touched
      - AD changes can be previewed with -DryRun

.NOTES
    For CREATE requests where Enabled = 1, the worker can either use a shared
    SecureString password file or generate a unique random initial password per
    request.

    Shared password file option:

        Read-Host "Initial password" -AsSecureString | Export-Clixml C:\Secure\UserQueueInitialPassword.xml
        .\Invoke-ADUserChangeQueue.ps1 -InitialPasswordPath C:\Secure\UserQueueInitialPassword.xml

    Default option when -InitialPasswordPath is omitted:

        A 16-character random password is generated per created user and written
        to the generated-password handoff CSV. Protect that CSV carefully and
        delete it after the passwords have been handed over.
#>

[CmdletBinding()]
param(
    [Parameter()]
    [string]$ConnectionString = $env:USERCHANGEQUEUE_CONNECTION_STRING,

    [Parameter()]
    [int]$BatchSize = 10,

    [Parameter()]
    [long[]]$RequestId,

    [Parameter()]
    [ValidateSet('Approved','Pending','Processing')]
    [string]$StatusToProcess = 'Approved',

    [Parameter()]
    [switch]$IgnoreExecuteAfter,

    [Parameter()]
    [switch]$ForceExecuteAfterOverride,

    [Parameter()]
    [ValidateRange(0,30)]
    [int]$CreateLeadDays = 1,

    [Parameter()]
    [switch]$DryRun,

    [Parameter()]
    [switch]$ForcePasswordChangeAtNextLogon,

    [Parameter()]
    [string]$InitialPasswordPath,

    [Parameter()]
    [bool]$GenerateRandomInitialPassword = $true,

    [Parameter()]
    [ValidateRange(16,128)]
    [int]$GeneratedPasswordLength = 16,

    [Parameter()]
    [string]$GeneratedPasswordSpecialCharacters = '!#$%&*+-=?@_',

    [Parameter()]
    [string]$GeneratedPasswordOutputPath = (Join-Path $env:ProgramData ("UserChangeQueueWeb\GeneratedInitialPasswords\GeneratedInitialPasswords-{0:yyyyMMdd}.csv" -f (Get-Date))),

    [Parameter()]
    [switch]$ApplyOfficeLicenseGroup,

    [Parameter()]
    [switch]$SkipOfficeLicenseGroup,

    [Parameter()]
    [switch]$StrictOfficeLicenseGroup,

    [Parameter()]
    [bool]$EnableRemoteMailbox = $true,

    [Parameter()]
    [switch]$RequireRemoteMailbox,

    [Parameter()]
    [switch]$RemoteMailboxForRequestsWithoutLicense,

    [Parameter()]
    [string]$RemoteRoutingDomain,

    [Parameter()]
    [string]$RemoteRoutingAddressTemplate = '{alias}@{remoteRoutingDomain}',

    [Parameter()]
    [switch]$AllowRemoteMailboxAdAttributeFallback,

    [Parameter()]
    [string]$ExchangeSnapInName = 'Microsoft.Exchange.Management.PowerShell.SnapIn',

    [Parameter()]
    [string]$ADServer,

    [Parameter()]
    [switch]$AllowExistingCreateRecovery,

    [Parameter()]
    [switch]$RenameCNToDisplayName,

    [Parameter()]
    [switch]$MoveUserOnUpdate,

    [Parameter()]
    [string[]]$AllowedAttributeJsonAttributes = @(
        'extensionAttribute1',
        'extensionAttribute2',
        'extensionAttribute3',
        'extensionAttribute4',
        'extensionAttribute5',
        'extensionAttribute6',
        'extensionAttribute7',
        'extensionAttribute8',
        'extensionAttribute9',
        'extensionAttribute10',
        'extensionAttribute11',
        'extensionAttribute12',
        'extensionAttribute13',
        'extensionAttribute14',
        'extensionAttribute15',
        'physicalDeliveryOfficeName',
        'officePhone'
    ),

    [Parameter()]
    [string[]]$ExtensionAttributeMapping = @(),

    [Parameter()]
    [switch]$ClearMappedExtensionAttributesWhenBlank,

    [Parameter()]
    [switch]$RequireADAttributeBusinessRules,

    [Parameter()]
    [switch]$TraceADAttributeRules,

    [Parameter()]
    [string]$CompletedStatus = 'Auto',

    [Parameter()]
    [string]$FailedStatus = 'Auto',

    [Parameter()]
    [string]$LogPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($ConnectionString)) {
    throw "Supply -ConnectionString or define USERCHANGEQUEUE_CONNECTION_STRING."
}

# Resolve a stable script directory without relying on $PSScriptRoot in a
# parameter default expression. Some Windows PowerShell hosts evaluate those
# defaults before $PSScriptRoot is populated.
$script:ScriptDirectory = if (-not [string]::IsNullOrWhiteSpace($PSScriptRoot)) {
    $PSScriptRoot
}
elseif (-not [string]::IsNullOrWhiteSpace($PSCommandPath)) {
    Split-Path -Path $PSCommandPath -Parent
}
else {
    (Get-Location).Path
}

# Keep logs outside the web root and outside the worker script folder by default.
if ([string]::IsNullOrWhiteSpace($LogPath)) {
    $defaultLogDirectory = Join-Path $env:ProgramData 'UserChangeQueueWeb\Logs'
    $LogPath = Join-Path $defaultLogDirectory ("QueueWorker-{0:yyyyMMdd}.log" -f (Get-Date))
}

$script:InitialPassword = $null
$script:TranscriptStarted = $false
$script:RandomNumberGenerator = [System.Security.Cryptography.RandomNumberGenerator]::Create()
$script:ADServerName = $null
$script:CompletedStatusName = $null
$script:FailedStatusName = $null
$script:ExchangeShellLoaded = $false
$script:ResolvedRemoteRoutingDomain = $null
$script:ADAttributeRules = @()
$script:ADAttributeRuleConditionsByRuleSetId = @{}

function Write-Info {
    param([Parameter(Mandatory=$true)][string]$Message)
    Write-Host ("[{0:yyyy-MM-dd HH:mm:ss}] {1}" -f (Get-Date), $Message)
}

function Test-IsBlank {
    param([object]$Value)
    if ($null -eq $Value) { return $true }
    return [string]::IsNullOrWhiteSpace([string]$Value)
}

function Add-ADServerIfConfigured {
    param([Parameter(Mandatory=$true)][hashtable]$Hashtable)

    if (-not (Test-IsBlank $script:ADServerName)) {
        $Hashtable['Server'] = $script:ADServerName
    }
}


function ConvertTo-LdapPathDistinguishedName {
    param([Parameter(Mandatory=$true)][string]$DistinguishedName)

    # ADSI treats '/' as a path separator, so escape it if it appears in a DN value.
    return $DistinguishedName.Replace('/', '\/')
}

function Get-ADSingleValuedStringAttribute {
    param(
        [Parameter(Mandatory=$true)][string]$Identity,
        [Parameter(Mandatory=$true)][string]$AttributeName
    )

    $params = @{
        Identity = $Identity
        Properties = $AttributeName
        ErrorAction = 'Stop'
    }
    Add-ADServerIfConfigured $params

    $user = Get-ADUser @params
    $property = $user.PSObject.Properties[$AttributeName]
    if ($null -eq $property -or $null -eq $property.Value) {
        return $null
    }

    return [string]$property.Value
}

function Test-ADSingleValuedStringAttributeMatches {
    param(
        [Parameter(Mandatory=$true)][string]$Identity,
        [Parameter(Mandatory=$true)][string]$AttributeName,
        [AllowNull()][string]$ExpectedValue
    )

    try {
        $currentValue = Get-ADSingleValuedStringAttribute -Identity $Identity -AttributeName $AttributeName
    }
    catch {
        Write-Warning "Could not verify $AttributeName on $Identity after AD write error: $($_.Exception.Message)"
        return $false
    }

    if (Test-IsBlank $ExpectedValue) {
        return (Test-IsBlank $currentValue)
    }

    return [string]::Equals([string]$currentValue, [string]$ExpectedValue, [System.StringComparison]::Ordinal)
}

function Set-ADSingleValuedStringAttribute {
    param(
        [Parameter(Mandatory=$true)][string]$Identity,
        [Parameter(Mandatory=$true)][string]$AttributeName,
        [AllowNull()][string]$Value
    )

    # In some Exchange Management Shell sessions Set-ADUser/ADSI can write the
    # value successfully but still throw the vague exception "Argument types do
    # not match". Treat the operation as successful if a post-write readback
    # proves that AD contains the requested value.
    if (Test-IsBlank $Value) {
        $clearParams = @{
            Identity = $Identity
            Clear = $AttributeName
            ErrorAction = 'Stop'
        }
        Add-ADServerIfConfigured $clearParams

        try {
            Set-ADUser @clearParams | Out-Null
        }
        catch {
            if (Test-ADSingleValuedStringAttributeMatches -Identity $Identity -AttributeName $AttributeName -ExpectedValue $null) {
                Write-Warning "Set-ADUser reported an error while clearing $AttributeName for $Identity, but readback confirms the attribute is clear. Continuing. Error: $($_.Exception.Message)"
                return
            }
            throw
        }
        return
    }

    $replaceHash = @{}
    $replaceHash[[string]$AttributeName] = [string]$Value

    $replaceParams = @{
        Identity = $Identity
        Replace = $replaceHash
        ErrorAction = 'Stop'
    }
    Add-ADServerIfConfigured $replaceParams

    try {
        Set-ADUser @replaceParams | Out-Null
    }
    catch {
        if (Test-ADSingleValuedStringAttributeMatches -Identity $Identity -AttributeName $AttributeName -ExpectedValue ([string]$Value)) {
            Write-Warning "Set-ADUser reported an error while setting $AttributeName for $Identity, but readback confirms the value is '$Value'. Continuing. Error: $($_.Exception.Message)"
            return
        }
        throw
    }
}

function Get-CryptoRandomInt {
    param([Parameter(Mandatory=$true)][int]$MaxExclusive)

    if ($MaxExclusive -le 0) {
        throw 'MaxExclusive must be greater than zero.'
    }

    $bytes = New-Object byte[] 4
    $limit = [uint32]::MaxValue - ([uint32]::MaxValue % [uint32]$MaxExclusive)

    do {
        $script:RandomNumberGenerator.GetBytes($bytes)
        $value = [BitConverter]::ToUInt32($bytes, 0)
    } while ($value -ge $limit)

    return [int]($value % [uint32]$MaxExclusive)
}

function New-RandomInitialPasswordPlainText {
    param(
        [Parameter(Mandatory=$true)][ValidateRange(16,128)][int]$Length,
        [Parameter(Mandatory=$true)][string]$SpecialCharacters
    )

    if ($Length -lt 16) {
        throw 'Generated password length must be at least 16 characters.'
    }

    if ([string]::IsNullOrEmpty($SpecialCharacters)) {
        throw 'GeneratedPasswordSpecialCharacters cannot be empty.'
    }

    $upper = 'ABCDEFGHIJKLMNOPQRSTUVWXYZ'.ToCharArray()
    $lower = 'abcdefghijklmnopqrstuvwxyz'.ToCharArray()
    $digits = '0123456789'.ToCharArray()
    $special = $SpecialCharacters.ToCharArray()
    $all = @($upper + $lower + $digits + $special)

    $characters = New-Object System.Collections.Generic.List[char]
    [void]$characters.Add($upper[(Get-CryptoRandomInt $upper.Length)])
    [void]$characters.Add($lower[(Get-CryptoRandomInt $lower.Length)])
    [void]$characters.Add($digits[(Get-CryptoRandomInt $digits.Length)])
    [void]$characters.Add($special[(Get-CryptoRandomInt $special.Length)])

    while ($characters.Count -lt $Length) {
        [void]$characters.Add($all[(Get-CryptoRandomInt $all.Length)])
    }

    for ($i = $characters.Count - 1; $i -gt 0; $i--) {
        $j = Get-CryptoRandomInt ($i + 1)
        $tmp = $characters[$i]
        $characters[$i] = $characters[$j]
        $characters[$j] = $tmp
    }

    return (-join $characters.ToArray())
}

function Protect-GeneratedPasswordOutputFile {
    param([Parameter(Mandatory=$true)][string]$Path)

    try {
        $acl = Get-Acl -LiteralPath $Path
        $acl.SetAccessRuleProtection($true, $false)

        $rights = [System.Security.AccessControl.FileSystemRights]::FullControl
        $inheritanceFlags = [System.Security.AccessControl.InheritanceFlags]::None
        $propagationFlags = [System.Security.AccessControl.PropagationFlags]::None
        $accessType = [System.Security.AccessControl.AccessControlType]::Allow

        $administratorsSid = New-Object System.Security.Principal.SecurityIdentifier -ArgumentList 'S-1-5-32-544'
        $systemSid = New-Object System.Security.Principal.SecurityIdentifier -ArgumentList 'S-1-5-18'
        $identityRefs = @(
            [System.Security.Principal.WindowsIdentity]::GetCurrent().User,
            $administratorsSid,
            $systemSid
        )

        foreach ($identityRef in $identityRefs) {
            $rule = New-Object System.Security.AccessControl.FileSystemAccessRule -ArgumentList $identityRef, $rights, $inheritanceFlags, $propagationFlags, $accessType
            [void]$acl.AddAccessRule($rule)
        }

        Set-Acl -LiteralPath $Path -AclObject $acl
    }
    catch {
        Write-Warning "Generated password file '$Path' was written, but its ACL could not be hardened automatically. Restrict access to this file manually. $($_.Exception.Message)"
    }
}

function Write-GeneratedPasswordRecord {
    param(
        [Parameter(Mandatory=$true)]$Request,
        [Parameter(Mandatory=$true)][string]$PlainTextPassword
    )

    if (Test-IsBlank $GeneratedPasswordOutputPath) {
        Write-Warning "Generated password for request $($Request.RequestId) was not written because GeneratedPasswordOutputPath is empty."
        return
    }

    $directory = Split-Path -Path $GeneratedPasswordOutputPath -Parent
    if (-not (Test-IsBlank $directory) -and -not (Test-Path -LiteralPath $directory)) {
        New-Item -ItemType Directory -Path $directory -Force | Out-Null
    }

    [pscustomobject]@{
        GeneratedAt = (Get-Date).ToString('s')
        RequestId = [long]$Request.RequestId
        SamAccountName = [string]$Request.NewSamAccountName
        UserPrincipalName = [string]$Request.NewUserPrincipalName
        DisplayName = [string]$Request.NewDisplayName
        InitialPassword = $PlainTextPassword
    } | Export-Csv -LiteralPath $GeneratedPasswordOutputPath -Append -NoTypeInformation -Encoding UTF8

    Protect-GeneratedPasswordOutputFile -Path $GeneratedPasswordOutputPath

    Write-Info "Generated initial password for $($Request.NewSamAccountName) was written to $GeneratedPasswordOutputPath."
}

function Get-InitialPasswordForCreateRequest {
    param([Parameter(Mandatory=$true)]$Request)

    $enabled = Get-RequestEnabledValue -Request $Request -DefaultValue $true
    if (-not $enabled) {
        return [pscustomobject]@{
            SecureString = $null
            PlainText = $null
            Generated = $false
        }
    }

    if ($null -ne $script:InitialPassword) {
        return [pscustomobject]@{
            SecureString = $script:InitialPassword
            PlainText = $null
            Generated = $false
        }
    }

    if ($DryRun) {
        Write-Info "DRYRUN: would generate a $GeneratedPasswordLength-character random initial password for $($Request.NewSamAccountName)."
        return [pscustomobject]@{
            SecureString = $null
            PlainText = $null
            Generated = $true
        }
    }

    if ($GenerateRandomInitialPassword) {
        $plainTextPassword = New-RandomInitialPasswordPlainText -Length $GeneratedPasswordLength -SpecialCharacters $GeneratedPasswordSpecialCharacters
        return [pscustomobject]@{
            SecureString = (ConvertTo-SecureString -String $plainTextPassword -AsPlainText -Force)
            PlainText = $plainTextPassword
            Generated = $true
        }
    }

    throw "Request $($Request.RequestId) creates an enabled account, but neither -InitialPasswordPath nor -GenerateRandomInitialPassword was supplied."
}

function Get-RequestEnabledValue {
    param(
        [Parameter(Mandatory=$true)]$Request,
        [Parameter(Mandatory=$true)][bool]$DefaultValue
    )

    if ($null -eq $Request.Enabled) { return $DefaultValue }
    return [bool]$Request.Enabled
}

function ConvertTo-DbNullIfNull {
    param([object]$Value)
    if ($null -eq $Value) { return [DBNull]::Value }
    return $Value
}

function ConvertTo-LdapEscapedValue {
    param([Parameter(Mandatory=$true)][string]$Value)

    return $Value.Replace('\', '\5c').Replace('*', '\2a').Replace('(', '\28').Replace(')', '\29').Replace(([string][char]0), '\00')
}

function ConvertTo-ADCountryCode {
    param([string]$Country)

    if (Test-IsBlank $Country) { return $null }

    $value = $Country.Trim()
    switch ($value.ToLowerInvariant()) {
        'norway' { return 'NO' }
        'norge' { return 'NO' }
        default {
            if ($value.Length -eq 2) { return $value.ToUpperInvariant() }
            return $value
        }
    }
}

function New-SqlConnection {
    $connection = New-Object System.Data.SqlClient.SqlConnection($ConnectionString)
    $connection.Open()
    return $connection
}

function Get-DomainCountryMetadata {
    param(
        [Parameter(Mandatory=$true)]$Request
    )

    $domainName = $null
    foreach ($address in @($Request.NewUserPrincipalName, $Request.Mail)) {
        if (-not (Test-IsBlank $address)) {
            $text = [string]$address
            $atIndex = $text.LastIndexOf('@')
            if ($atIndex -ge 0 -and $atIndex -lt ($text.Length - 1)) {
                $domainName = $text.Substring($atIndex + 1).Trim()
                break
            }
        }
    }

    if (Test-IsBlank $domainName) {
        return [pscustomobject]@{
            Domain = $null
            CountryISO2 = (ConvertTo-ADCountryCode $Request.Country)
            CountryName = $null
            CountryCode = $null
        }
    }

    $connection = New-SqlConnection
    try {
        $cmd = $connection.CreateCommand()
        $cmd.CommandText = @"
SELECT TOP (1)
    NULLIF(LTRIM(RTRIM(CountryISO2)), ''),
    NULLIF(LTRIM(RTRIM(CountryName)), ''),
    CountryCode
FROM dbo.domains
WHERE LOWER(LTRIM(RTRIM([domain]))) = LOWER(@Domain);
"@
        [void](Add-SqlParameter $cmd '@Domain' ([System.Data.SqlDbType]::NVarChar) $domainName 255)

        $reader = $cmd.ExecuteReader()
        try {
            if ($reader.Read()) {
                return [pscustomobject]@{
                    Domain = $domainName
                    CountryISO2 = if ($reader.IsDBNull(0)) { $null } else { [string]$reader.GetString(0) }
                    CountryName = if ($reader.IsDBNull(1)) { $null } else { [string]$reader.GetString(1) }
                    CountryCode = if ($reader.IsDBNull(2)) { $null } else { [int]$reader.GetInt32(2) }
                }
            }
        }
        finally {
            $reader.Dispose()
        }
    }
    finally {
        $connection.Dispose()
    }

    Write-Warning "No dbo.domains row was found for UPN/mail domain '$domainName'. Falling back to the queue Country value; co and countryCode will not be populated."
    return [pscustomobject]@{
        Domain = $domainName
        CountryISO2 = (ConvertTo-ADCountryCode $Request.Country)
        CountryName = $null
        CountryCode = $null
    }
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


function Get-AllowedQueueStatuses {
    param([Parameter(Mandatory=$true)][System.Data.SqlClient.SqlConnection]$Connection)

    $cmd = $Connection.CreateCommand()
    $cmd.CommandText = @"
SELECT cc.definition
FROM sys.check_constraints AS cc
WHERE cc.parent_object_id = OBJECT_ID(N'dbo.ADUserChangeQueue')
  AND
  (
      cc.name = N'CK_ADUserChangeQueue_Status'
      OR cc.definition LIKE N'%Status%'
  );
"@

    $definitions = New-Object System.Collections.Generic.List[string]
    try {
        $reader = $cmd.ExecuteReader()
        try {
            while ($reader.Read()) {
                if (-not $reader.IsDBNull(0)) {
                    [void]$definitions.Add([string]$reader.GetValue(0))
                }
            }
        }
        finally {
            $reader.Dispose()
        }
    }
    finally {
        $cmd.Dispose()
    }

    $values = New-Object System.Collections.Generic.List[string]
    foreach ($definition in $definitions) {
        foreach ($match in [regex]::Matches($definition, "'((?:''|[^'])*)'")) {
            $value = $match.Groups[1].Value.Replace("''", "'").Trim()
            if (-not (Test-IsBlank $value) -and -not $values.Contains($value)) {
                [void]$values.Add($value)
            }
        }
    }

    return @($values)
}

function Resolve-QueueStatusName {
    param(
        [Parameter(Mandatory=$true)][string]$ConfiguredValue,
        [Parameter(Mandatory=$true)][string[]]$Candidates,
        [Parameter(Mandatory=$true)][string]$Purpose,
        [Parameter()][string[]]$AllowedStatuses
    )

    $hasAllowedList = ($null -ne $AllowedStatuses -and $AllowedStatuses.Count -gt 0)

    if (-not (Test-IsBlank $ConfiguredValue) -and $ConfiguredValue.Trim() -ne 'Auto') {
        $configured = $ConfiguredValue.Trim()
        if ($hasAllowedList -and -not ($AllowedStatuses -contains $configured)) {
            throw "$Purpose status '$configured' is not allowed by dbo.ADUserChangeQueue status constraint. Allowed values: $($AllowedStatuses -join ', ')"
        }
        return $configured
    }

    if ($hasAllowedList) {
        foreach ($candidate in $Candidates) {
            if ($AllowedStatuses -contains $candidate) {
                return $candidate
            }
        }

        throw "Could not auto-select a $Purpose status from candidates [$($Candidates -join ', ')] because the database allows only: $($AllowedStatuses -join ', ')"
    }

    Write-Warning "Could not read allowed queue statuses from CK_ADUserChangeQueue_Status. Falling back to first configured candidate '$($Candidates[0])'."
    return $Candidates[0]
}

function Initialize-QueueStatusNames {
    param([Parameter(Mandatory=$true)][System.Data.SqlClient.SqlConnection]$Connection)

    $allowedStatuses = @(Get-AllowedQueueStatuses -Connection $Connection)
    if ($allowedStatuses.Count -gt 0) {
        Write-Info "Allowed queue statuses from database: $($allowedStatuses -join ', ')"
    }
    else {
        Write-Warning "No allowed queue statuses could be read from the database constraint."
    }

    $script:CompletedStatusName = Resolve-QueueStatusName `
        -ConfiguredValue $CompletedStatus `
        -Candidates @('Implemented','Completed','Done') `
        -Purpose 'success' `
        -AllowedStatuses $allowedStatuses

    $script:FailedStatusName = Resolve-QueueStatusName `
        -ConfiguredValue $FailedStatus `
        -Candidates @('Failed','Error','Rejected','Cancelled') `
        -Purpose 'failure' `
        -AllowedStatuses $allowedStatuses

    Write-Info "Queue worker will mark successful requests as '$script:CompletedStatusName' and failed requests as '$script:FailedStatusName'."
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


function Get-QueueWorkerSettingValue {
    param(
        [Parameter(Mandatory=$true)][System.Data.SqlClient.SqlConnection]$Connection,
        [Parameter(Mandatory=$true)][string]$SettingName
    )

    $cmd = $Connection.CreateCommand()
    $cmd.CommandText = @"
IF OBJECT_ID(N'dbo.UserChangeQueueSettings', N'U') IS NULL
BEGIN
    SELECT CAST(NULL AS nvarchar(1000)) AS SettingValue;
END
ELSE
BEGIN
    SELECT TOP (1)
        SettingValue
    FROM dbo.UserChangeQueueSettings
    WHERE SettingName = @SettingName
      AND Active = 1
    ORDER BY UpdatedAt DESC, CreatedAt DESC;
END
"@
    [void](Add-SqlParameter $cmd '@SettingName' ([System.Data.SqlDbType]::NVarChar) $SettingName 100)

    $rows = @(Invoke-SqlQueryRows $cmd)
    if ($rows.Count -eq 0) { return $null }

    $value = $rows[0].SettingValue
    if (Test-IsBlank $value) { return $null }
    return ([string]$value).Trim()
}

function Initialize-QueueWorkerDatabaseSettings {
    param([Parameter(Mandatory=$true)][System.Data.SqlClient.SqlConnection]$Connection)

    if (-not (Test-IsBlank $RemoteRoutingDomain)) {
        $script:ResolvedRemoteRoutingDomain = ([string]$RemoteRoutingDomain).Trim().TrimStart('@')
        Write-Info "Remote routing domain was supplied on the command line; using '$script:ResolvedRemoteRoutingDomain'."
    }
    else {
        $dbRemoteRoutingDomain = Get-QueueWorkerSettingValue -Connection $Connection -SettingName 'RemoteRoutingDomain'
        if (-not (Test-IsBlank $dbRemoteRoutingDomain)) {
            $script:ResolvedRemoteRoutingDomain = ([string]$dbRemoteRoutingDomain).Trim().TrimStart('@')
            Write-Info "Remote routing domain loaded from dbo.UserChangeQueueSettings: '$script:ResolvedRemoteRoutingDomain'."
        }
        else {
            $script:ResolvedRemoteRoutingDomain = $null
            Write-Warning "Remote routing domain was not supplied and dbo.UserChangeQueueSettings does not contain an active RemoteRoutingDomain value. Remote mailbox processing will warn or fail depending on -RequireRemoteMailbox."
        }
    }
}

function ConvertTo-HtmlEncodedText {
    param([object]$Value)

    if ($null -eq $Value) { return '' }
    return [System.Net.WebUtility]::HtmlEncode([string]$Value)
}

function ConvertTo-EmailTemplateText {
    param(
        [Parameter()][string]$Template,
        [Parameter(Mandatory=$true)]$Request,
        [Parameter()]$AdUser,
        [Parameter()][string]$InitialPassword = ''
    )

    if (Test-IsBlank $Template) { return $null }

    $displayName = if (-not (Test-IsBlank $Request.NewDisplayName)) { [string]$Request.NewDisplayName } elseif ($null -ne $AdUser -and -not (Test-IsBlank $AdUser.Name)) { [string]$AdUser.Name } else { [string]$Request.NewSamAccountName }
    $givenName = if (-not (Test-IsBlank $Request.NewGivenName)) { [string]$Request.NewGivenName } else { $displayName }
    $sam = if (-not (Test-IsBlank $Request.NewSamAccountName)) { [string]$Request.NewSamAccountName } elseif ($null -ne $AdUser -and -not (Test-IsBlank $AdUser.SamAccountName)) { [string]$AdUser.SamAccountName } else { '' }
    $upn = if (-not (Test-IsBlank $Request.NewUserPrincipalName)) { [string]$Request.NewUserPrincipalName } else { '' }
    $mail = if (-not (Test-IsBlank $Request.Mail)) { [string]$Request.Mail } elseif (-not (Test-IsBlank $Request.NewUserPrincipalName)) { [string]$Request.NewUserPrincipalName } else { '' }
    $executeAfter = if ($null -ne $Request.ExecuteAfter) { '{0:dd.MM.yyyy HH:mm}' -f ([datetime]$Request.ExecuteAfter) } else { '' }
	$managerDisplayName = ''

if (-not (Test-IsBlank $Request.ManagerSamAccountName)) {
    try {
        $managerUser = Resolve-ADUserBySamAccountName `
            -SamAccountName ([string]$Request.ManagerSamAccountName)

        if ($null -ne $managerUser) {
            if (-not (Test-IsBlank $managerUser.DisplayName)) {
                $managerDisplayName = [string]$managerUser.DisplayName
            }
            elseif (-not (Test-IsBlank $managerUser.Name)) {
                $managerDisplayName = [string]$managerUser.Name
            }
        }
    }
    catch {
        Write-Warning "Could not resolve manager display name for '$($Request.ManagerSamAccountName)': $($_.Exception.Message)"
    }
}
	$accountExpirationDate = if ($null -ne $Request.AccountExpirationDate) {
    '{0:dd.MM.yyyy}' -f ([datetime]$Request.AccountExpirationDate)} else {''}
 
    $tokens = @{
        DisplayName = $displayName
        GivenName = $givenName
        SamAccountName = $sam
        UserPrincipalName = $upn
        Mail = $mail
        PrivateEmail = if (Test-IsBlank $Request.PrivateEmail) { '' } else { [string]$Request.PrivateEmail }
        Company = if (Test-IsBlank $Request.Company) { '' } else { [string]$Request.Company }
        Department = if (Test-IsBlank $Request.Department) { '' } else { [string]$Request.Department }
        Title = if (Test-IsBlank $Request.Title) { '' } else { [string]$Request.Title }
        Office = if (Test-IsBlank $Request.Office) { '' } else { [string]$Request.Office }
        ManagerSamAccountName = if (Test-IsBlank $Request.ManagerSamAccountName) { '' } else { [string]$Request.ManagerSamAccountName }
        RequestedBy = if (Test-IsBlank $Request.RequestedBy) { '' } else { [string]$Request.RequestedBy }
        ExecuteAfter = $executeAfter
		ManagerDisplayName = $managerDisplayName
		AccountExpirationDate = $accountExpirationDate
		InitialPassword = $InitialPassword
        AccessCard = if (Test-IsBlank $Request.AccessCard) { '' } else { [string]$Request.AccessCard }
    }

    $result = [string]$Template
    foreach ($key in $tokens.Keys) {
        $value = [string]$tokens[$key]
        $singleBrace = [regex]::Escape('{' + $key + '}')
        $doubleBrace = [regex]::Escape('{{' + $key + '}}')
        $result = [regex]::Replace($result, $singleBrace, [System.Text.RegularExpressions.MatchEvaluator]{ param($m) $value })
        $result = [regex]::Replace($result, $doubleBrace, [System.Text.RegularExpressions.MatchEvaluator]{ param($m) $value })
    }

    return $result
}

function Test-EmailQueueTableExists {
    param([Parameter(Mandatory=$true)][System.Data.SqlClient.SqlConnection]$Connection)

    $cmd = $Connection.CreateCommand()
    $cmd.CommandText = "SELECT CASE WHEN OBJECT_ID(N'dbo.ADUserChangeQueueEmails', N'U') IS NULL THEN 0 ELSE 1 END;"
    try { return ([int]$cmd.ExecuteScalar() -eq 1) }
    finally { $cmd.Dispose() }
}

function Get-QueueWorkerIntSettingValue {
    param(
        [Parameter(Mandatory=$true)][System.Data.SqlClient.SqlConnection]$Connection,
        [Parameter(Mandatory=$true)][string]$SettingName,
        [Parameter(Mandatory=$true)][int]$DefaultValue
    )

    $raw = Get-QueueWorkerSettingValue -Connection $Connection -SettingName $SettingName
    if (Test-IsBlank $raw) { return $DefaultValue }

    $parsed = 0
    if ([int]::TryParse([string]$raw, [ref]$parsed)) { return $parsed }

    Write-Warning "Setting '$SettingName' has non-integer value '$raw'. Using default $DefaultValue."
    return $DefaultValue
}


function Get-QueueWorkerEmailTemplate {
    param(
        [Parameter(Mandatory=$true)][System.Data.SqlClient.SqlConnection]$Connection,
        [Parameter(Mandatory=$true)][string]$TemplateName,
        [Parameter()][string]$Domain = '*',
        [Parameter()][string]$LanguageCode = 'en'
    )

    if (Test-IsBlank $Domain) { $Domain = '*' }
    $Domain = ([string]$Domain).Trim().TrimStart('@').ToLowerInvariant()

    $cmd = $Connection.CreateCommand()
    $cmd.CommandText = @"
IF OBJECT_ID(N'dbo.EmailTemplates', N'U') IS NULL
BEGIN
    SELECT TOP (0)
        CAST(NULL AS int) AS Id,
        CAST(NULL AS nvarchar(100)) AS TemplateName,
        CAST(NULL AS nvarchar(200)) AS Domain,
        CAST(NULL AS nvarchar(10)) AS LanguageCode,
        CAST(NULL AS nvarchar(500)) AS Subject,
        CAST(NULL AS nvarchar(max)) AS HtmlBody,
        CAST(NULL AS nvarchar(max)) AS PlainTextBody;
END
ELSE
BEGIN
    SELECT TOP (1)
        Id,
        TemplateName,
        Domain,
        LanguageCode,
        Subject,
        HtmlBody,
        PlainTextBody
    FROM dbo.EmailTemplates
    WHERE LOWER(LTRIM(RTRIM(TemplateName))) = LOWER(LTRIM(RTRIM(@TemplateName)))
      AND Active = 1
      AND LOWER(LTRIM(RTRIM(Domain))) IN (LOWER(@Domain), N'*')
      AND LOWER(LTRIM(RTRIM(LanguageCode))) IN (LOWER(@LanguageCode), N'en')
    ORDER BY
        CASE WHEN LOWER(LTRIM(RTRIM(Domain))) = LOWER(@Domain) THEN 0 ELSE 1 END,
        CASE WHEN LOWER(LTRIM(RTRIM(LanguageCode))) = LOWER(@LanguageCode) THEN 0 ELSE 1 END,
        COALESCE(UpdatedAt, CreatedAt) DESC,
        Id DESC;
END
"@
    [void](Add-SqlParameter $cmd '@TemplateName' ([System.Data.SqlDbType]::NVarChar) $TemplateName 100)
    [void](Add-SqlParameter $cmd '@Domain' ([System.Data.SqlDbType]::NVarChar) $Domain 200)
    [void](Add-SqlParameter $cmd '@LanguageCode' ([System.Data.SqlDbType]::NVarChar) $LanguageCode 10)

    try {
        $rows = @(Invoke-SqlQueryRows $cmd)
        if ($rows.Count -eq 0) {
            Write-Warning "No active email template matched name '$TemplateName', domain '$Domain', language '$LanguageCode'."
            return $null
        }

        $selected = $rows[0]
        Write-Info "Selected email template Id=$($selected.Id), name='$($selected.TemplateName)', domain='$($selected.Domain)', language='$($selected.LanguageCode)' for requested domain '$Domain'."
        return $selected
    }
    finally {
        $cmd.Dispose()
    }
}

function Get-PreferredCreateEmailAddress {
    param([Parameter(Mandatory=$true)]$Request)

    if (-not (Test-IsBlank $Request.PrivateEmail)) { return ([string]$Request.PrivateEmail).Trim() }
    if (-not (Test-IsBlank $Request.Mail)) { return ([string]$Request.Mail).Trim() }
    if (-not (Test-IsBlank $Request.NewUserPrincipalName)) { return ([string]$Request.NewUserPrincipalName).Trim() }
    return $null
}

function Get-EmailDomainFromAddress {
    param([object]$Value)

    if (Test-IsBlank $Value) { return $null }
    $text = ([string]$Value).Trim()
    $at = $text.LastIndexOf('@')
    if ($at -lt 0 -or $at -ge ($text.Length - 1)) { return $null }
    return $text.Substring($at + 1).Trim().ToLowerInvariant()
}

function Test-AccessCardRequested {
    param([object]$Value)

    if (Test-IsBlank $Value) { return $false }
    $text = ([string]$Value).Trim().ToLowerInvariant()
    if ($text -in @('0','false','no','nei','nej','none','n/a','na','not requested','notrequired','not required')) { return $false }
    return $true
}

function Add-QueueEmailIfMissing {
    param(
        [Parameter(Mandatory=$true)][System.Data.SqlClient.SqlConnection]$Connection,
        [Parameter(Mandatory=$true)][long]$RequestIdValue,
        [Parameter(Mandatory=$true)][string]$EmailType,
        [Parameter(Mandatory=$true)][string]$ToEmail,
        [Parameter()][string]$ToName,
        [Parameter(Mandatory=$true)][string]$Subject,
        [Parameter(Mandatory=$true)][string]$BodyHtml,
        [Parameter(Mandatory=$true)][datetime]$EarliestSendAt
    )

    if (Test-IsBlank $ToEmail) { return $false }

    $cmd = $Connection.CreateCommand()
    $cmd.CommandText = @"
IF NOT EXISTS
(
    SELECT 1
    FROM dbo.ADUserChangeQueueEmails
    WHERE RequestId = @RequestId
      AND EmailType = @EmailType
      AND ToEmail = @ToEmail
      AND Status <> N'Cancelled'
)
BEGIN
    INSERT INTO dbo.ADUserChangeQueueEmails
    (
        RequestId,
        EmailType,
        ToEmail,
        ToName,
        Subject,
        BodyHtml,
        EarliestSendAt,
        Status,
        CreatedAt
    )
    VALUES
    (
        @RequestId,
        @EmailType,
        @ToEmail,
        NULLIF(@ToName, N''),
        @Subject,
        @BodyHtml,
        @EarliestSendAt,
        N'Pending',
        SYSDATETIME()
    );

    SELECT CAST(1 AS int) AS Inserted;
END
ELSE
BEGIN
    SELECT CAST(0 AS int) AS Inserted;
END
"@
    [void](Add-SqlParameter $cmd '@RequestId' ([System.Data.SqlDbType]::BigInt) $RequestIdValue)
    [void](Add-SqlParameter $cmd '@EmailType' ([System.Data.SqlDbType]::NVarChar) $EmailType 50)
    [void](Add-SqlParameter $cmd '@ToEmail' ([System.Data.SqlDbType]::NVarChar) $ToEmail 320)
    [void](Add-SqlParameter $cmd '@ToName' ([System.Data.SqlDbType]::NVarChar) $ToName 200)
    [void](Add-SqlParameter $cmd '@Subject' ([System.Data.SqlDbType]::NVarChar) $Subject 500)
    [void](Add-SqlParameter $cmd '@BodyHtml' ([System.Data.SqlDbType]::NVarChar) $BodyHtml)
    [void](Add-SqlParameter $cmd '@EarliestSendAt' ([System.Data.SqlDbType]::DateTime2) $EarliestSendAt)

    try { return ([int]$cmd.ExecuteScalar() -eq 1) }
    finally { $cmd.Dispose() }
}

function Get-AccessCardEmailRecipientsForDomain {
    param(
        [Parameter(Mandatory=$true)][System.Data.SqlClient.SqlConnection]$Connection,
        [Parameter()][string]$Domain
    )

    $cmd = $Connection.CreateCommand()
    $cmd.CommandText = @"
IF OBJECT_ID(N'dbo.AccessCardEmailRecipients', N'U') IS NULL
BEGIN
    SELECT TOP (0)
        CAST(NULL AS int) AS Id,
        CAST(NULL AS nvarchar(255)) AS Domain,
        CAST(NULL AS nvarchar(320)) AS RecipientEmail,
        CAST(NULL AS nvarchar(200)) AS RecipientName;
END
ELSE
BEGIN
    SELECT
        Id,
        Domain,
        RecipientEmail,
        RecipientName
    FROM dbo.AccessCardEmailRecipients
    WHERE IsActive = 1
      AND NULLIF(LTRIM(RTRIM(RecipientEmail)), N'') IS NOT NULL
      AND
      (
          @Domain IS NULL
          OR LOWER(LTRIM(RTRIM(Domain))) = LOWER(@Domain)
          OR LTRIM(RTRIM(Domain)) = N'*'
      )
    ORDER BY
        CASE WHEN LOWER(LTRIM(RTRIM(Domain))) = LOWER(@Domain) THEN 0 ELSE 1 END,
        RecipientEmail;
END
"@
    [void](Add-SqlParameter $cmd '@Domain' ([System.Data.SqlDbType]::NVarChar) $Domain 255)

    try { return @(Invoke-SqlQueryRows $cmd) }
    finally { $cmd.Dispose() }
}

function New-DefaultWelcomeEmailBodyHtml {
    param([Parameter(Mandatory=$true)]$Request)

    $givenName = ConvertTo-HtmlEncodedText $(if (-not (Test-IsBlank $Request.NewGivenName)) { $Request.NewGivenName } else { $Request.NewDisplayName })
    $displayName = ConvertTo-HtmlEncodedText $Request.NewDisplayName
    $upn = ConvertTo-HtmlEncodedText $Request.NewUserPrincipalName
    $mail = ConvertTo-HtmlEncodedText $(Get-PreferredCreateEmailAddress -Request $Request)

    return @"
<p>Hello $givenName,</p>
<p>Your account has been created.</p>
<table>
<tr><td><strong>Name</strong></td><td>$displayName</td></tr>
<tr><td><strong>Username</strong></td><td>$upn</td></tr>
<tr><td><strong>Email</strong></td><td>$mail</td></tr>
</table>
<p>If you cannot sign in yet, please wait a little while and try again. Some services may need time to finish syncing.</p>
<p>Welcome.</p>
"@
}

function New-DefaultAccessCardEmailBodyHtml {
    param([Parameter(Mandatory=$true)]$Request)

    $displayName = ConvertTo-HtmlEncodedText $Request.NewDisplayName
    $sam = ConvertTo-HtmlEncodedText $Request.NewSamAccountName
    $upn = ConvertTo-HtmlEncodedText $Request.NewUserPrincipalName
    $company = ConvertTo-HtmlEncodedText $Request.Company
    $department = ConvertTo-HtmlEncodedText $Request.Department
    $title = ConvertTo-HtmlEncodedText $Request.Title
    $office = ConvertTo-HtmlEncodedText $Request.Office
    $manager = ConvertTo-HtmlEncodedText $Request.ManagerSamAccountName
    $requestedBy = ConvertTo-HtmlEncodedText $Request.RequestedBy
    $executeAfter = if ($null -ne $Request.ExecuteAfter) { ConvertTo-HtmlEncodedText ('{0:dd.MM.yyyy HH:mm}' -f ([datetime]$Request.ExecuteAfter)) } else { '' }
    $accessCard = ConvertTo-HtmlEncodedText $Request.AccessCard

    return @"
<p>An access card was requested for a newly created user.</p>
<table>
<tr><td><strong>Name</strong></td><td>$displayName</td></tr>
<tr><td><strong>sAMAccountName</strong></td><td>$sam</td></tr>
<tr><td><strong>UPN</strong></td><td>$upn</td></tr>
<tr><td><strong>Company</strong></td><td>$company</td></tr>
<tr><td><strong>Department</strong></td><td>$department</td></tr>
<tr><td><strong>Title</strong></td><td>$title</td></tr>
<tr><td><strong>Office</strong></td><td>$office</td></tr>
<tr><td><strong>Manager</strong></td><td>$manager</td></tr>
<tr><td><strong>Requested by</strong></td><td>$requestedBy</td></tr>
<tr><td><strong>Execute after</strong></td><td>$executeAfter</td></tr>
<tr><td><strong>Access card value</strong></td><td>$accessCard</td></tr>
</table>
"@
}

function Add-CreateRequestEmails {
    param(
        [Parameter(Mandatory=$true)][System.Data.SqlClient.SqlConnection]$Connection,
        [Parameter(Mandatory=$true)]$Request,
        [Parameter()]$AdUser,
        [Parameter()][string]$InitialPassword = ''
    )

    $requestIdValue = [long]$Request.RequestId

    if ($DryRun) {
        Write-Info "DRYRUN: would queue CREATE emails for request $requestIdValue."
        return
    }

    if (-not (Test-EmailQueueTableExists -Connection $Connection)) {
        Write-Warning "dbo.ADUserChangeQueueEmails does not exist, so no welcome/access-card emails were queued for request $requestIdValue. Run Database\\ADUserChangeQueueEmails.Required.sql."
        return
    }

    $delayHours = Get-QueueWorkerIntSettingValue -Connection $Connection -SettingName 'WelcomeEmailDelayHours' -DefaultValue 2
    if ($delayHours -lt 2) {
        Write-Warning "WelcomeEmailDelayHours is $delayHours. Enforcing minimum 2 hours."
        $delayHours = 2
    }

    $welcomeEarliestSendAt = (Get-Date).AddHours($delayHours)
    $nowEarliestSendAt = Get-Date
    $templateLanguage = Get-QueueWorkerSettingValue -Connection $Connection -SettingName 'EmailTemplateLanguage'
    if (Test-IsBlank $templateLanguage) { $templateLanguage = 'en' }

    # Template branding is based on the new company account, never the private recipient.
    $companyAddress = $null
    if (-not (Test-IsBlank $Request.Mail)) {
        $companyAddress = ([string]$Request.Mail).Trim()
    }
    elseif (-not (Test-IsBlank $Request.NewUserPrincipalName)) {
        $companyAddress = ([string]$Request.NewUserPrincipalName).Trim()
    }

    $templateDomain = Get-EmailDomainFromAddress -Value $companyAddress
    if (Test-IsBlank $templateDomain) { $templateDomain = '*' }

    $welcomeRecipients = New-Object System.Collections.Generic.List[string]
    if (-not (Test-IsBlank $Request.PrivateEmail)) {
        $welcomeRecipients.Add(([string]$Request.PrivateEmail).Trim())
    }
    if (-not (Test-IsBlank $companyAddress)) {
        $alreadyAdded = $false
        foreach ($recipientAddress in $welcomeRecipients) {
            if ([string]::Equals($recipientAddress, $companyAddress, [System.StringComparison]::OrdinalIgnoreCase)) {
                $alreadyAdded = $true
                break
            }
        }
        if (-not $alreadyAdded) { $welcomeRecipients.Add($companyAddress) }
    }

    if ($welcomeRecipients.Count -gt 0) {
        $welcomeTemplate = Get-QueueWorkerEmailTemplate `
            -Connection $Connection `
            -TemplateName 'Welcome' `
            -Domain $templateDomain `
            -LanguageCode $templateLanguage

        if ($null -ne $welcomeTemplate -and -not (Test-IsBlank $welcomeTemplate.Subject)) {
            $welcomeSubjectTemplate = [string]$welcomeTemplate.Subject
        }
        else {
            $welcomeSubjectTemplate = Get-QueueWorkerSettingValue -Connection $Connection -SettingName 'WelcomeEmailSubject'
            if (Test-IsBlank $welcomeSubjectTemplate) { $welcomeSubjectTemplate = 'Welcome, {GivenName}' }
        }

        $welcomeSubject = ConvertTo-EmailTemplateText -Template $welcomeSubjectTemplate -Request $Request -AdUser $AdUser

        if ($null -ne $welcomeTemplate -and -not (Test-IsBlank $welcomeTemplate.HtmlBody)) {
            $welcomeBodyTemplate = [string]$welcomeTemplate.HtmlBody
        }
        else {
            $welcomeBodyTemplate = Get-QueueWorkerSettingValue -Connection $Connection -SettingName 'WelcomeEmailBodyHtml'
        }

        foreach ($welcomeTo in $welcomeRecipients) {
            $passwordForRecipient = ''
            if (
                -not (Test-IsBlank $Request.PrivateEmail) -and
                [string]::Equals($welcomeTo, ([string]$Request.PrivateEmail).Trim(), [System.StringComparison]::OrdinalIgnoreCase)
            ) {
                $passwordForRecipient = $InitialPassword
            }

            $welcomeBody = ConvertTo-EmailTemplateText `
                -Template $welcomeBodyTemplate `
                -Request $Request `
                -AdUser $AdUser `
                -InitialPassword $passwordForRecipient

            if (Test-IsBlank $welcomeBody) {
                $welcomeBody = New-DefaultWelcomeEmailBodyHtml -Request $Request
            }

            $insertedWelcome = Add-QueueEmailIfMissing `
                -Connection $Connection `
                -RequestIdValue $requestIdValue `
                -EmailType 'Welcome' `
                -ToEmail $welcomeTo `
                -ToName ([string]$Request.NewDisplayName) `
                -Subject $welcomeSubject `
                -BodyHtml $welcomeBody `
                -EarliestSendAt $welcomeEarliestSendAt

            if ($insertedWelcome) {
                Write-Info "Queued welcome email for request $requestIdValue to $welcomeTo using template domain '$templateDomain'."
            }
            else {
                Write-Info "Welcome email for request $requestIdValue to $welcomeTo was already queued; skipping duplicate."
            }
        }
    }
    else {
        Write-Warning "Request $requestIdValue has neither PrivateEmail, Mail nor NewUserPrincipalName; welcome email was not queued."
    }

    if (Test-AccessCardRequested -Value $Request.AccessCard) {
        $recipients = @(Get-AccessCardEmailRecipientsForDomain -Connection $Connection -Domain $templateDomain)
        if ($recipients.Count -eq 0) {
            Write-Warning "Request $requestIdValue requested access card, but no active AccessCardEmailRecipients matched domain '$templateDomain'."
            return
        }

        $accessTemplate = Get-QueueWorkerEmailTemplate `
            -Connection $Connection `
            -TemplateName 'AccessCard' `
            -Domain $templateDomain `
            -LanguageCode $templateLanguage

        if ($null -ne $accessTemplate -and -not (Test-IsBlank $accessTemplate.Subject)) {
            $accessSubjectTemplate = [string]$accessTemplate.Subject
        }
        else {
            $accessSubjectTemplate = Get-QueueWorkerSettingValue -Connection $Connection -SettingName 'AccessCardEmailSubject'
            if (Test-IsBlank $accessSubjectTemplate) { $accessSubjectTemplate = 'Access card request for {DisplayName}' }
        }
        $accessSubject = ConvertTo-EmailTemplateText -Template $accessSubjectTemplate -Request $Request -AdUser $AdUser

        if ($null -ne $accessTemplate -and -not (Test-IsBlank $accessTemplate.HtmlBody)) {
            $accessBodyTemplate = [string]$accessTemplate.HtmlBody
        }
        else {
            $accessBodyTemplate = Get-QueueWorkerSettingValue -Connection $Connection -SettingName 'AccessCardEmailBodyHtml'
        }
        $accessBody = ConvertTo-EmailTemplateText -Template $accessBodyTemplate -Request $Request -AdUser $AdUser
        if (Test-IsBlank $accessBody) { $accessBody = New-DefaultAccessCardEmailBodyHtml -Request $Request }

        foreach ($recipient in $recipients) {
            $insertedAccess = Add-QueueEmailIfMissing `
                -Connection $Connection `
                -RequestIdValue $requestIdValue `
                -EmailType 'AccessCard' `
                -ToEmail ([string]$recipient.RecipientEmail) `
                -ToName ([string]$recipient.RecipientName) `
                -Subject $accessSubject `
                -BodyHtml $accessBody `
                -EarliestSendAt $nowEarliestSendAt

            if ($insertedAccess) {
                Write-Info "Queued access-card email for request $requestIdValue to $($recipient.RecipientEmail) using template domain '$templateDomain'."
            }
            else {
                Write-Info "Access-card email for request $requestIdValue to $($recipient.RecipientEmail) was already queued; skipping duplicate."
            }
        }
    }
}

function Get-QueueRequests {
    param([Parameter(Mandatory=$true)][System.Data.SqlClient.SqlConnection]$Connection)

    $idFilter = ''
    if ($RequestId -and $RequestId.Count -gt 0) {
        $tokens = New-Object System.Collections.Generic.List[string]
        for ($i = 0; $i -lt $RequestId.Count; $i++) {
            $tokens.Add("@RequestId$i")
        }
        $idFilter = " AND RequestId IN ($($tokens -join ', '))"
    }

    $executeFilter = ''
    if (-not ($IgnoreExecuteAfter -and $ForceExecuteAfterOverride)) {
        $executeFilter = @"
 AND
 (
     ExecuteAfter IS NULL
     OR
     (
         UPPER(LTRIM(RTRIM(RequestType))) = 'CREATE'
         AND CONVERT(date, ExecuteAfter) <= DATEADD(day, @CreateLeadDays, CONVERT(date, SYSDATETIME()))
     )
     OR
     (
         UPPER(LTRIM(RTRIM(RequestType))) <> 'CREATE'
         AND ExecuteAfter <= SYSDATETIME()
     )
 )
"@
    }

    $cmd = $Connection.CreateCommand()
    $cmd.CommandText = @"
SELECT TOP (@BatchSize)
    RequestId,
    RequestType,
    Status,
    ExecuteAfter,
    TargetObjectGUID,
    TargetSamAccountName,
    NewSamAccountName,
    NewUserPrincipalName,
    NewDisplayName,
    NewGivenName,
    NewSurname,
    NewOU,
    ManagerSamAccountName,
    Department,
    ProjectNumber,
    Title,
    Mail,
    PrivateEmail,
    Enabled,
    AttributeJson,
    RequestedBy,
    EmployeeType,
    Company,
    StreetAddress,
    PostalCode,
    City,
    Country,
    Office,
    AccountExpirationDate,
    MobilePhone,
    ComputerType,
    AccessCard,
    TargetDisplayName,
    OfficeLicense
FROM dbo.ADUserChangeQueue WITH (READPAST)
WHERE Status = @StatusToProcess
$executeFilter
$idFilter
ORDER BY ExecuteAfter, RequestId;
"@

    [void](Add-SqlParameter $cmd '@BatchSize' ([System.Data.SqlDbType]::Int) $BatchSize)
    [void](Add-SqlParameter $cmd '@StatusToProcess' ([System.Data.SqlDbType]::NVarChar) $StatusToProcess 50)
    [void](Add-SqlParameter $cmd '@CreateLeadDays' ([System.Data.SqlDbType]::Int) $CreateLeadDays)

    if ($RequestId -and $RequestId.Count -gt 0) {
        for ($i = 0; $i -lt $RequestId.Count; $i++) {
            [void](Add-SqlParameter $cmd "@RequestId$i" ([System.Data.SqlDbType]::BigInt) $RequestId[$i])
        }
    }

    try {
        return (Invoke-SqlQueryRows $cmd)
    }
    finally {
        $cmd.Dispose()
    }
}

function Get-QueuedGroups {
    param(
        [Parameter(Mandatory=$true)][System.Data.SqlClient.SqlConnection]$Connection,
        [Parameter(Mandatory=$true)][long]$RequestIdValue
    )

    $existsCmd = $Connection.CreateCommand()
    $existsCmd.CommandText = "SELECT CASE WHEN OBJECT_ID(N'dbo.ADUserChangeQueueGroups', N'U') IS NULL THEN 0 ELSE 1 END;"
    $exists = [int]$existsCmd.ExecuteScalar()
    $existsCmd.Dispose()

    if ($exists -ne 1) {
        return @()
    }

    $cmd = $Connection.CreateCommand()
    $cmd.CommandText = @"
SELECT
    qg.Id,
    qg.RequestId,
    qg.GroupObjectGUID,
    qg.Action,
    qg.Source,
    qg.RuleSetId,
    qg.Selected,
    qg.Required,
    qg.ApprovalRequired,
    qg.Reason,
    COALESCE(NULLIF(qg.SnapshotGroupSamAccountName, ''), g.SamAccountName) AS GroupSamAccountName,
    COALESCE(NULLIF(qg.SnapshotGroupName, ''), g.Name) AS GroupName,
    COALESCE(NULLIF(qg.SnapshotGroupDistinguishedName, ''), g.DistinguishedName) AS GroupDistinguishedName
FROM dbo.ADUserChangeQueueGroups AS qg
LEFT JOIN dbo.ADGroups AS g
    ON g.ObjectGUID = qg.GroupObjectGUID
WHERE qg.RequestId = @RequestId
  AND qg.Selected = 1
ORDER BY qg.Id;
"@
    [void](Add-SqlParameter $cmd '@RequestId' ([System.Data.SqlDbType]::BigInt) $RequestIdValue)

    try {
        return (Invoke-SqlQueryRows $cmd)
    }
    finally {
        $cmd.Dispose()
    }
}

function Claim-QueueRequest {
    param(
        [Parameter(Mandatory=$true)][System.Data.SqlClient.SqlConnection]$Connection,
        [Parameter(Mandatory=$true)][long]$RequestIdValue
    )

    if ($DryRun) {
        Write-Info "DRYRUN: would mark request $RequestIdValue as Processing."
        return $true
    }

    $cmd = $Connection.CreateCommand()
    $cmd.CommandText = @"
UPDATE dbo.ADUserChangeQueue
SET
    Status = 'Processing',
    StartedAt = SYSDATETIME(),
    FinishedAt = NULL,
    ErrorMessage = NULL
WHERE RequestId = @RequestId
  AND Status = @StatusToProcess;
"@
    [void](Add-SqlParameter $cmd '@RequestId' ([System.Data.SqlDbType]::BigInt) $RequestIdValue)
    [void](Add-SqlParameter $cmd '@StatusToProcess' ([System.Data.SqlDbType]::NVarChar) $StatusToProcess 50)

    try {
        $affected = $cmd.ExecuteNonQuery()
        return ($affected -eq 1)
    }
    finally {
        $cmd.Dispose()
    }
}

function Complete-QueueRequest {
    param(
        [Parameter(Mandatory=$true)][System.Data.SqlClient.SqlConnection]$Connection,
        [Parameter(Mandatory=$true)][long]$RequestIdValue,
        [Parameter()][Guid]$TargetObjectGuid,
        [Parameter()][string]$TargetSamAccountName
    )

    if ($DryRun) {
        Write-Info "DRYRUN: would mark request $RequestIdValue as $script:CompletedStatusName."
        return
    }

    $cmd = $Connection.CreateCommand()
    $cmd.CommandText = @"
UPDATE dbo.ADUserChangeQueue
SET
    Status = @CompletedStatus,
    FinishedAt = SYSDATETIME(),
    ErrorMessage = NULL,
    TargetObjectGUID = COALESCE(@TargetObjectGUID, TargetObjectGUID),
    TargetSamAccountName = COALESCE(NULLIF(@TargetSamAccountName, ''), TargetSamAccountName)
WHERE RequestId = @RequestId;
"@
    [void](Add-SqlParameter $cmd '@RequestId' ([System.Data.SqlDbType]::BigInt) $RequestIdValue)
    [void](Add-SqlParameter $cmd '@CompletedStatus' ([System.Data.SqlDbType]::NVarChar) $script:CompletedStatusName 50)

    if ($TargetObjectGuid -eq [Guid]::Empty) {
        [void](Add-SqlParameter $cmd '@TargetObjectGUID' ([System.Data.SqlDbType]::UniqueIdentifier) $null)
    }
    else {
        [void](Add-SqlParameter $cmd '@TargetObjectGUID' ([System.Data.SqlDbType]::UniqueIdentifier) $TargetObjectGuid)
    }

    [void](Add-SqlParameter $cmd '@TargetSamAccountName' ([System.Data.SqlDbType]::NVarChar) $TargetSamAccountName 300)

    try {
        [void]$cmd.ExecuteNonQuery()
    }
    finally {
        $cmd.Dispose()
    }
}

function Fail-QueueRequest {
    param(
        [Parameter(Mandatory=$true)][System.Data.SqlClient.SqlConnection]$Connection,
        [Parameter(Mandatory=$true)][long]$RequestIdValue,
        [Parameter(Mandatory=$true)][string]$ErrorMessage
    )

    $message = $ErrorMessage
    if ($message.Length -gt 3900) {
        $message = $message.Substring(0, 3900)
    }

    if ($DryRun) {
        Write-Info "DRYRUN: would mark request $RequestIdValue as $script:FailedStatusName. Error: $message"
        return
    }

    $cmd = $Connection.CreateCommand()
    $cmd.CommandText = @"
UPDATE dbo.ADUserChangeQueue
SET
    Status = @FailedStatus,
    FinishedAt = SYSDATETIME(),
    ErrorMessage = @ErrorMessage
WHERE RequestId = @RequestId;
"@
    [void](Add-SqlParameter $cmd '@RequestId' ([System.Data.SqlDbType]::BigInt) $RequestIdValue)
    [void](Add-SqlParameter $cmd '@FailedStatus' ([System.Data.SqlDbType]::NVarChar) $script:FailedStatusName 50)
    [void](Add-SqlParameter $cmd '@ErrorMessage' ([System.Data.SqlDbType]::NVarChar) $message 4000)

    try {
        [void]$cmd.ExecuteNonQuery()
    }
    finally {
        $cmd.Dispose()
    }
}

function Invoke-ADOperation {
    param(
        [Parameter(Mandatory=$true)][string]$Description,
        [Parameter(Mandatory=$true)][scriptblock]$ScriptBlock
    )

    if ($DryRun) {
        Write-Info "DRYRUN: $Description"
        return $null
    }

    Write-Info $Description
    return & $ScriptBlock
}

function Resolve-ADUserBySamAccountName {
    param([string]$SamAccountName)

    if (Test-IsBlank $SamAccountName) { return $null }

    $escaped = ConvertTo-LdapEscapedValue $SamAccountName.Trim()
    $params = @{
        LDAPFilter = "(sAMAccountName=$escaped)"
        Properties = 'DistinguishedName','ObjectGUID','SamAccountName','UserPrincipalName','DisplayName'
        ErrorAction = 'Stop'
    }
    Add-ADServerIfConfigured $params
    $matches = @(Get-ADUser @params)

    if ($matches.Count -gt 1) {
        throw "More than one AD user matched sAMAccountName '$SamAccountName'."
    }

    if ($matches.Count -eq 0) { return $null }
    return $matches[0]
}

function Resolve-ADGroupByName {
    param([string]$GroupName)

    if (Test-IsBlank $GroupName) { return $null }

    $escaped = ConvertTo-LdapEscapedValue $GroupName.Trim()
    $filter = "(|(sAMAccountName=$escaped)(name=$escaped)(cn=$escaped))"
    $params = @{
        LDAPFilter = $filter
        Properties = 'DistinguishedName','ObjectGUID','SamAccountName','Name'
        ErrorAction = 'Stop'
    }
    Add-ADServerIfConfigured $params
    $matches = @(Get-ADGroup @params)

    if ($matches.Count -gt 1) {
        throw "More than one AD group matched '$GroupName'."
    }

    if ($matches.Count -eq 0) { return $null }
    return $matches[0]
}

function Resolve-ADGroupFromQueueGroup {
    param([Parameter(Mandatory=$true)]$QueueGroup)

    if (-not (Test-IsBlank $QueueGroup.GroupDistinguishedName)) {
        $params = @{
            Identity = $QueueGroup.GroupDistinguishedName
            Properties = 'DistinguishedName','ObjectGUID','SamAccountName','Name'
            ErrorAction = 'Stop'
        }
        Add-ADServerIfConfigured $params
        return Get-ADGroup @params
    }

    if ($null -ne $QueueGroup.GroupObjectGUID) {
        $params = @{
            Identity = [Guid]$QueueGroup.GroupObjectGUID
            Properties = 'DistinguishedName','ObjectGUID','SamAccountName','Name'
            ErrorAction = 'Stop'
        }
        Add-ADServerIfConfigured $params
        return Get-ADGroup @params
    }

    if (-not (Test-IsBlank $QueueGroup.GroupSamAccountName)) {
        return Resolve-ADGroupByName $QueueGroup.GroupSamAccountName
    }

    if (-not (Test-IsBlank $QueueGroup.GroupName)) {
        return Resolve-ADGroupByName $QueueGroup.GroupName
    }

    throw "Cannot resolve queued group row $($QueueGroup.Id); no DN, GUID, SamAccountName, or Name was available."
}

function Add-HashtableValueIfPresent {
    param(
        [Parameter(Mandatory=$true)][hashtable]$Hashtable,
        [Parameter(Mandatory=$true)][string]$Key,
        [Parameter()][object]$Value
    )

    if (-not (Test-IsBlank $Value)) {
        $Hashtable[$Key] = [string]$Value
    }
}


function Test-IsExtensionAttributeName {
    param([Parameter(Mandatory=$true)][string]$AttributeName)

    return $AttributeName -match '^extensionAttribute([1-9]|1[0-5])$'
}

function Get-RequestPropertyValue {
    param(
        [Parameter(Mandatory=$true)]$Request,
        [Parameter(Mandatory=$true)][string]$PropertyName
    )

    $property = $Request.PSObject.Properties[$PropertyName]
    if ($null -eq $property) {
        throw "Extension attribute mapping source '$PropertyName' was not found on the queue request object."
    }

    return $property.Value
}

function Get-RequestUpnDomain {
    param([Parameter(Mandatory=$true)]$Request)

    $value = $null
    if (-not (Test-IsBlank $Request.NewUserPrincipalName)) {
        $value = [string]$Request.NewUserPrincipalName
    }
    elseif (-not (Test-IsBlank $Request.Mail)) {
        $value = [string]$Request.Mail
    }

    if (Test-IsBlank $value) { return $null }

    $atIndex = $value.IndexOf('@')
    if ($atIndex -lt 0 -or $atIndex -eq ($value.Length - 1)) { return $null }

    return $value.Substring($atIndex + 1).Trim().ToLowerInvariant()
}

function Get-MappedExtensionAttributeValue {
    param(
        [Parameter(Mandatory=$true)]$Request,
        [Parameter(Mandatory=$true)][string]$Source
    )

    $sourceValue = $Source.Trim()

    if ($sourceValue.StartsWith('Literal:', [System.StringComparison]::OrdinalIgnoreCase)) {
        return $sourceValue.Substring('Literal:'.Length)
    }

    if ($sourceValue.StartsWith('Constant:', [System.StringComparison]::OrdinalIgnoreCase)) {
        return $sourceValue.Substring('Constant:'.Length)
    }

    switch -Regex ($sourceValue) {
        '^(UpnDomain|UserPrincipalNameDomain|Domain)$' { return (Get-RequestUpnDomain -Request $Request) }
        '^MailDomain$' {
            if (Test-IsBlank $Request.Mail) { return $null }
            $mail = [string]$Request.Mail
            $atIndex = $mail.IndexOf('@')
            if ($atIndex -lt 0 -or $atIndex -eq ($mail.Length - 1)) { return $null }
            return $mail.Substring($atIndex + 1).Trim().ToLowerInvariant()
        }
        default { return (Get-RequestPropertyValue -Request $Request -PropertyName $sourceValue) }
    }
}

function Set-MappedExtensionAttributes {
    param(
        [Parameter(Mandatory=$true)]$Identity,
        [Parameter(Mandatory=$true)]$Request
    )

    if ($null -eq $ExtensionAttributeMapping -or $ExtensionAttributeMapping.Count -eq 0) { return }

    $replace = @{}
    $clear = New-Object System.Collections.Generic.List[string]

    foreach ($mapping in $ExtensionAttributeMapping) {
        if (Test-IsBlank $mapping) { continue }

        $parts = ([string]$mapping).Split('=', 2)
        if ($parts.Count -ne 2) {
            throw "Invalid extension attribute mapping '$mapping'. Use the format extensionAttributeN=RequestField, for example extensionAttribute1=EmployeeType."
        }

        $attributeName = $parts[0].Trim()
        $sourceName = $parts[1].Trim()

        if (-not (Test-IsExtensionAttributeName $attributeName)) {
            throw "Invalid extension attribute mapping target '$attributeName'. Only extensionAttribute1 through extensionAttribute15 are supported."
        }

        $mappedValue = $null
        if (-not (Test-IsBlank $sourceName)) {
            $mappedValue = Get-MappedExtensionAttributeValue -Request $Request -Source $sourceName
        }

        if (Test-IsBlank $mappedValue) {
            if ($ClearMappedExtensionAttributesWhenBlank) {
                $clear.Add($attributeName)
            }
            continue
        }

        $replace[$attributeName] = [string]$mappedValue
    }

    if ($replace.Count -gt 0) {
        Invoke-ADOperation "Set mapped extension attributes for $Identity" {
            Set-ADUser -Identity $Identity -Replace $replace -Server $script:ADServerName -ErrorAction Stop
        }
    }

    if ($clear.Count -gt 0) {
        Invoke-ADOperation "Clear blank mapped extension attributes for $Identity" {
            Set-ADUser -Identity $Identity -Clear $clear.ToArray() -Server $script:ADServerName -ErrorAction Stop
        }
    }
}



function Get-ObjectPropertyValueSafe {
    param(
        [Parameter(Mandatory=$true)]$Object,
        [Parameter(Mandatory=$true)][string]$Name,
        [AllowNull()]$Default = $null
    )

    if ($null -eq $Object) { return $Default }

    try {
        $property = $Object.PSObject.Properties[$Name]
        if ($null -eq $property) { return $Default }
        if ($null -eq $property.Value -or $property.Value -is [System.DBNull]) { return $Default }
        return $property.Value
    }
    catch {
        return $Default
    }
}

function ConvertTo-SafeRuleString {
    param(
        [AllowNull()]$Value,
        [string]$Default = ''
    )

    if ($null -eq $Value -or $Value -is [System.DBNull]) { return $Default }

    try {
        $text = [System.Convert]::ToString($Value, [System.Globalization.CultureInfo]::InvariantCulture)
        if ($null -eq $text) { return $Default }
        return $text.Trim()
    }
    catch {
        try { return ([string]$Value).Trim() } catch { return $Default }
    }
}

function ConvertTo-SafeRuleBoolean {
    param(
        [AllowNull()]$Value,
        [bool]$Default = $false
    )

    if ($null -eq $Value -or $Value -is [System.DBNull]) { return $Default }
    if ($Value -is [bool]) { return [bool]$Value }
    if ($Value -is [byte] -or $Value -is [int] -or $Value -is [long]) { return ([int64]$Value -ne 0) }

    $text = ConvertTo-SafeRuleString -Value $Value -Default ''
    if ([string]::IsNullOrWhiteSpace($text)) { return $Default }

    switch -Regex ($text.Trim()) {
        '^(1|true|yes|y)$' { return $true }
        '^(0|false|no|n)$' { return $false }
        default { return $Default }
    }
}

function Get-ADAttributeRuleFieldText {
    param(
        [Parameter(Mandatory=$true)]$Object,
        [Parameter(Mandatory=$true)][string]$Name,
        [string]$Default = ''
    )

    return (ConvertTo-SafeRuleString -Value (Get-ObjectPropertyValueSafe -Object $Object -Name $Name -Default $null) -Default $Default)
}

function Get-ADAttributeRuleFieldBool {
    param(
        [Parameter(Mandatory=$true)]$Object,
        [Parameter(Mandatory=$true)][string]$Name,
        [bool]$Default = $false
    )

    return (ConvertTo-SafeRuleBoolean -Value (Get-ObjectPropertyValueSafe -Object $Object -Name $Name -Default $null) -Default $Default)
}

function Initialize-ADAttributeRules {
    param([Parameter(Mandatory=$true)][System.Data.SqlClient.SqlConnection]$Connection)

    $script:ADAttributeRules = @()
    $script:ADAttributeRuleConditionsByRuleSetId = @{}

    $existsCmd = $Connection.CreateCommand()
    $existsCmd.CommandText = @"
SELECT CASE
    WHEN OBJECT_ID(N'dbo.ADAttributeRuleSets', N'U') IS NULL THEN 0
    WHEN OBJECT_ID(N'dbo.ADAttributeRuleConditions', N'U') IS NULL THEN 0
    ELSE 1
END;
"@

    try {
        $exists = [int]$existsCmd.ExecuteScalar()
    }
    finally {
        $existsCmd.Dispose()
    }

    if ($exists -ne 1) {
        Write-Info "AD attribute rule tables were not found. Business-rule extension attributes will be skipped."
        return
    }

    $rulesCmd = $Connection.CreateCommand()
    $rulesCmd.CommandText = @"
SELECT
    RuleSetId,
    RuleSetName,
    Priority,
    MatchMode,
    AppliesToAllUsers,
    AttributeName,
    ValueSourceType,
    ValueSource,
    ClearWhenBlank
FROM dbo.ADAttributeRuleSets
WHERE Active = 1
  AND (EffectiveFrom IS NULL OR EffectiveFrom <= CONVERT(date, SYSDATETIME()))
  AND (EffectiveTo IS NULL OR EffectiveTo >= CONVERT(date, SYSDATETIME()))
ORDER BY Priority, RuleSetId;
"@

    try {
        $script:ADAttributeRules = @(Invoke-SqlQueryRows $rulesCmd)
    }
    finally {
        $rulesCmd.Dispose()
    }

    $conditionsCmd = $Connection.CreateCommand()
    $conditionsCmd.CommandText = @"
SELECT
    c.ConditionId,
    c.RuleSetId,
    c.FieldName,
    c.Operator,
    c.MatchValue,
    c.MatchValue2
FROM dbo.ADAttributeRuleConditions AS c
INNER JOIN dbo.ADAttributeRuleSets AS r
    ON r.RuleSetId = c.RuleSetId
WHERE r.Active = 1
  AND (r.EffectiveFrom IS NULL OR r.EffectiveFrom <= CONVERT(date, SYSDATETIME()))
  AND (r.EffectiveTo IS NULL OR r.EffectiveTo >= CONVERT(date, SYSDATETIME()))
ORDER BY c.RuleSetId, c.ConditionId;
"@

    try {
        $conditions = @(Invoke-SqlQueryRows $conditionsCmd)
    }
    finally {
        $conditionsCmd.Dispose()
    }

    foreach ($condition in @($conditions)) {
        $key = Get-ADAttributeRuleFieldText -Object $condition -Name 'RuleSetId' -Default ''
        if (Test-IsBlank $key) { continue }
        if (-not $script:ADAttributeRuleConditionsByRuleSetId.ContainsKey($key)) {
            # Store a normal PowerShell array, not a Generic.List. In Windows PowerShell
            # + Exchange shells, passing a boxed Generic.List further into the rule
            # evaluator can produce the unhelpful .NET error "Argument types do not match".
            $script:ADAttributeRuleConditionsByRuleSetId[$key] = @()
        }
        $script:ADAttributeRuleConditionsByRuleSetId[$key] = @($script:ADAttributeRuleConditionsByRuleSetId[$key]) + @($condition)
    }

    Write-Info "Loaded $($script:ADAttributeRules.Count) active AD attribute business rule(s)."

    if ($TraceADAttributeRules -and $script:ADAttributeRules.Count -gt 0) {
        Write-Info "TRACE AD attribute rule initialization details follow."
        foreach ($rule in @($script:ADAttributeRules)) {
            try {
                $ruleSetIdText = Get-ADAttributeRuleFieldText -Object $rule -Name 'RuleSetId' -Default '<unknown>'
                $ruleName = Get-ADAttributeRuleFieldText -Object $rule -Name 'RuleSetName' -Default ''
                if (Test-IsBlank $ruleName) { $ruleName = "RuleSetId $ruleSetIdText" }
                $attrName = Get-ADAttributeRuleFieldText -Object $rule -Name 'AttributeName' -Default '<unknown attribute>'
                $priorityText = Get-ADAttributeRuleFieldText -Object $rule -Name 'Priority' -Default '<unknown>'
                $appliesText = [string](Get-ADAttributeRuleFieldBool -Object $rule -Name 'AppliesToAllUsers' -Default $false)
                $valueSourceType = Get-ADAttributeRuleFieldText -Object $rule -Name 'ValueSourceType' -Default '<unknown>'
                $valueSource = Get-ADAttributeRuleFieldText -Object $rule -Name 'ValueSource' -Default ''
                $conditionCount = 0
                if ($script:ADAttributeRuleConditionsByRuleSetId.ContainsKey($ruleSetIdText)) {
                    $conditionCount = @($script:ADAttributeRuleConditionsByRuleSetId[$ruleSetIdText]).Count
                }
                Write-Info "TRACE Loaded AD attribute rule RuleSetId=$ruleSetIdText Name='$ruleName' Attribute=$attrName Priority=$priorityText AppliesToAllUsers=$appliesText Conditions=$conditionCount ValueSourceType=$valueSourceType ValueSource='$valueSource'"
            }
            catch {
                Write-Warning "TRACE could not print one AD attribute rule during initialization: $($_.Exception.Message)"
            }
        }
    }
}

function Test-ADAttributeRuleCondition {
    param(
        [Parameter(Mandatory=$true)]$Request,
        [Parameter(Mandatory=$true)]$Condition
    )

    $fieldName = Get-ADAttributeRuleFieldText -Object $Condition -Name 'FieldName' -Default ''
    $operator = Get-ADAttributeRuleFieldText -Object $Condition -Name 'Operator' -Default ''
    $matchText = Get-ADAttributeRuleFieldText -Object $Condition -Name 'MatchValue' -Default ''

    $actualValue = $null
    if (-not (Test-IsBlank $fieldName)) {
        $actualValue = Get-MappedExtensionAttributeValue -Request $Request -Source $fieldName
    }

    $actualText = ConvertTo-SafeRuleString -Value $actualValue -Default ''

    $result = $false

    switch ($operator) {
        'Equals' { $result = [string]::Equals($actualText, $matchText, [System.StringComparison]::OrdinalIgnoreCase) }
        'NotEquals' { $result = -not [string]::Equals($actualText, $matchText, [System.StringComparison]::OrdinalIgnoreCase) }
        'Contains' { $result = ($actualText.IndexOf($matchText, [System.StringComparison]::OrdinalIgnoreCase) -ge 0) }
        'StartsWith' { $result = $actualText.StartsWith($matchText, [System.StringComparison]::OrdinalIgnoreCase) }
        'EndsWith' { $result = $actualText.EndsWith($matchText, [System.StringComparison]::OrdinalIgnoreCase) }
        'In' {
            if (Test-IsBlank $matchText) {
                $result = $false
            }
            else {
                $values = $matchText.Split(@(';', ','), [System.StringSplitOptions]::RemoveEmptyEntries)
                foreach ($value in $values) {
                    if ([string]::Equals($actualText, $value.Trim(), [System.StringComparison]::OrdinalIgnoreCase)) {
                        $result = $true
                        break
                    }
                }
            }
        }
        'IsEmpty' { $result = (Test-IsBlank $actualText) }
        'IsNotEmpty' { $result = -not (Test-IsBlank $actualText) }
        default { throw "Unsupported AD attribute rule condition operator '$operator'." }
    }

    if ($TraceADAttributeRules) {
        $conditionIdText = Get-ADAttributeRuleFieldText -Object $Condition -Name 'ConditionId' -Default '<unknown>'
        Write-Info "TRACE AD attribute condition ${conditionIdText}: Field='$fieldName' Actual='$actualText' Operator='$operator' Match='$matchText' Result=$result"
    }

    return $result
}

function Test-ADAttributeRuleMatches {
    param(
        [Parameter(Mandatory=$true)]$Request,
        [Parameter(Mandatory=$true)]$Rule
    )

    $ruleSetIdTextForTrace = Get-ADAttributeRuleFieldText -Object $Rule -Name 'RuleSetId' -Default '<unknown>'
    $ruleNameForTrace = Get-ADAttributeRuleFieldText -Object $Rule -Name 'RuleSetName' -Default ''
    if (Test-IsBlank $ruleNameForTrace) { $ruleNameForTrace = "RuleSetId $ruleSetIdTextForTrace" }
    $attributeForTrace = Get-ADAttributeRuleFieldText -Object $Rule -Name 'AttributeName' -Default '<unknown attribute>'

    if (Get-ADAttributeRuleFieldBool -Object $Rule -Name 'AppliesToAllUsers' -Default $false) {
        if ($TraceADAttributeRules) {
            Write-Info "TRACE AD attribute rule '$ruleNameForTrace' for $attributeForTrace matches because AppliesToAllUsers = True."
        }
        return $true
    }

    $key = Get-ADAttributeRuleFieldText -Object $Rule -Name 'RuleSetId' -Default ''
    $conditions = @()
    if ($script:ADAttributeRuleConditionsByRuleSetId.ContainsKey($key)) {
        # The hashtable value is stored as a plain PowerShell array of condition rows.
        # Force enumeration here so foreach receives each condition object, not the
        # collection wrapper itself.
        $conditions = @($script:ADAttributeRuleConditionsByRuleSetId[$key] | ForEach-Object { $_ })
    }

    if ($conditions.Count -eq 0) {
        # A rule with no conditions must explicitly set AppliesToAllUsers = 1.
        if ($TraceADAttributeRules) {
            Write-Info "TRACE AD attribute rule '$ruleNameForTrace' for $attributeForTrace does not match because it has no conditions and AppliesToAllUsers is not True."
        }
        return $false
    }

    $matchMode = (Get-ADAttributeRuleFieldText -Object $Rule -Name 'MatchMode' -Default 'ALL').ToUpperInvariant()
    if ($TraceADAttributeRules) {
        Write-Info "TRACE AD attribute rule '$ruleNameForTrace' for $attributeForTrace evaluating $($conditions.Count) condition(s) with MatchMode=$matchMode."
    }

    if ($matchMode -eq 'ANY') {
        foreach ($condition in $conditions) {
            if (Test-ADAttributeRuleCondition -Request $Request -Condition $condition) {
                if ($TraceADAttributeRules) {
                    Write-Info "TRACE AD attribute rule '$ruleNameForTrace' for $attributeForTrace matched by ANY condition."
                }
                return $true
            }
        }
        if ($TraceADAttributeRules) {
            Write-Info "TRACE AD attribute rule '$ruleNameForTrace' for $attributeForTrace did not match any ANY condition."
        }
        return $false
    }

    foreach ($condition in $conditions) {
        if (-not (Test-ADAttributeRuleCondition -Request $Request -Condition $condition)) {
            if ($TraceADAttributeRules) {
                Write-Info "TRACE AD attribute rule '$ruleNameForTrace' for $attributeForTrace failed an ALL condition."
            }
            return $false
        }
    }

    if ($TraceADAttributeRules) {
        Write-Info "TRACE AD attribute rule '$ruleNameForTrace' for $attributeForTrace matched all ALL conditions."
    }
    return $true
}

function Get-ADAttributeRuleValue {
    param(
        [Parameter(Mandatory=$true)]$Request,
        [Parameter(Mandatory=$true)]$Rule
    )

    $sourceType = Get-ADAttributeRuleFieldText -Object $Rule -Name 'ValueSourceType' -Default ''
    $sourceValue = Get-ADAttributeRuleFieldText -Object $Rule -Name 'ValueSource' -Default ''

    switch ($sourceType) {
        'Field' {
            if (Test-IsBlank $sourceValue) { return $null }
            return (Get-MappedExtensionAttributeValue -Request $Request -Source $sourceValue)
        }
        'Literal' { return $sourceValue }
        'Clear' { return $null }
        default { throw "Unsupported AD attribute rule value source type '$sourceType'." }
    }
}

function ConvertTo-ADSingleValueString {
    param(
        [AllowNull()]$Value
    )

    if ($null -eq $Value -or $Value -is [System.DBNull]) {
        return $null
    }

    if ($Value -is [datetime]) {
        return $Value.ToString('yyyy-MM-dd HH:mm:ss', [System.Globalization.CultureInfo]::InvariantCulture)
    }

    if ($Value -is [bool]) {
        if ($Value) { return 'True' }
        return 'False'
    }

    $text = [System.Convert]::ToString($Value, [System.Globalization.CultureInfo]::InvariantCulture)
    if ($null -eq $text) {
        return $null
    }

    $text = $text.Trim()
    if ($text.Length -eq 0) {
        return $null
    }

    return $text
}


function Set-ADSingleValuedStringAttributeBestEffort {
    param(
        [Parameter(Mandatory=$true)][string]$Identity,
        [Parameter(Mandatory=$true)][string]$AttributeName,
        [AllowNull()][string]$Value,
        [string]$RuleName = '<unknown rule>',
        [string]$RequestIdText = '<unknown>'
    )

    if ($DryRun) {
        if (Test-IsBlank $Value) {
            Write-Info "DRYRUN: Clear business-rule extension attribute ${AttributeName} for ${Identity}"
        }
        else {
            Write-Info "DRYRUN: Set business-rule extension attribute ${AttributeName} for ${Identity} to '$Value'"
        }
        return
    }

    $operationError = $null

    try {
        if (Test-IsBlank $Value) {
            $clearParams = @{
                Identity = [string]$Identity
                Clear = [string]$AttributeName
                ErrorAction = 'Stop'
            }
            Add-ADServerIfConfigured $clearParams
            Set-ADUser @clearParams | Out-Null
        }
        else {
            $replaceHash = @{}
            $replaceHash[[string]$AttributeName] = [string]$Value

            $replaceParams = @{
                Identity = [string]$Identity
                Replace = $replaceHash
                ErrorAction = 'Stop'
            }
            Add-ADServerIfConfigured $replaceParams
            Set-ADUser @replaceParams | Out-Null
        }
    }
    catch {
        $operationError = $_.Exception.Message
    }

    # In this environment Set-ADUser can successfully write extensionAttributeX
    # and still throw "Argument types do not match" in the Exchange/AD shell.
    # Do not let that block provisioning. Always read back and warn if the
    # desired value cannot be verified.
    try {
        $matches = Test-ADSingleValuedStringAttributeMatches -Identity ([string]$Identity) -AttributeName ([string]$AttributeName) -ExpectedValue ([string]$Value)
        if ($matches) {
            if (-not (Test-IsBlank $operationError)) {
                Write-Warning "Business-rule AD attribute write for $AttributeName on $Identity reported an error, but readback confirms the desired value. Continuing. Request=$RequestIdText Rule='$RuleName'. Error: $operationError"
            }
            return
        }

        $currentValue = Get-ADSingleValuedStringAttribute -Identity ([string]$Identity) -AttributeName ([string]$AttributeName)
        if (Test-IsBlank $operationError) {
            Write-Warning "Business-rule AD attribute write for $AttributeName on $Identity completed without a terminating error, but readback value is '$currentValue' instead of '$Value'. Continuing. Request=$RequestIdText Rule='$RuleName'."
        }
        else {
            Write-Warning "Business-rule AD attribute write for $AttributeName on $Identity reported an error and readback value is '$currentValue' instead of '$Value'. Continuing. Request=$RequestIdText Rule='$RuleName'. Error: $operationError"
        }
    }
    catch {
        if (Test-IsBlank $operationError) {
            Write-Warning "Business-rule AD attribute write for $AttributeName on $Identity could not be verified. Continuing. Request=$RequestIdText Rule='$RuleName'. Verification error: $($_.Exception.Message)"
        }
        else {
            Write-Warning "Business-rule AD attribute write for $AttributeName on $Identity reported an error and could not be verified. Continuing. Request=$RequestIdText Rule='$RuleName'. Write error: $operationError. Verification error: $($_.Exception.Message)"
        }
    }
}

function Set-BusinessRuleExtensionAttributes {
    param(
        [Parameter(Mandatory=$true)]$Identity,
        [Parameter(Mandatory=$true)]$Request
    )

    if ($null -eq $script:ADAttributeRules -or $script:ADAttributeRules.Count -eq 0) { return }

    # Important: each rule is isolated. A bad write for extensionAttribute13 must
    # not prevent later matching rules such as extensionAttribute4 or
    # extensionAttribute7 from being evaluated and attempted.
    $decidedAttributes = @{}

    foreach ($rule in @($script:ADAttributeRules)) {
        $ruleName = '<unknown rule>'
        $attributeName = '<unknown attribute>'
        $requestIdText = '<unknown>'

        try {
            $attributeName = Get-ADAttributeRuleFieldText -Object $rule -Name 'AttributeName' -Default ''
            $ruleSetIdText = Get-ADAttributeRuleFieldText -Object $rule -Name 'RuleSetId' -Default '<unknown>'
            $ruleName = Get-ADAttributeRuleFieldText -Object $rule -Name 'RuleSetName' -Default ''
            if (Test-IsBlank $ruleName) { $ruleName = "RuleSetId $ruleSetIdText" }
            $requestIdText = Get-ADAttributeRuleFieldText -Object $Request -Name 'RequestId' -Default '<unknown>'

            if (-not (Test-IsExtensionAttributeName $attributeName)) {
                Write-Warning "Invalid AD attribute business rule target '$attributeName' in '$ruleName'. Only extensionAttribute1 through extensionAttribute15 are supported. Skipping this rule."
                continue
            }

            $attributeKey = $attributeName.ToLowerInvariant()
            if ($decidedAttributes.ContainsKey($attributeKey)) {
                Write-Verbose "Skipping AD attribute rule '$ruleName' for $attributeName on request $requestIdText because an earlier matching rule already decided this attribute."
                continue
            }

            if (-not (Test-ADAttributeRuleMatches -Request $Request -Rule $rule)) {
                Write-Verbose "AD attribute rule '$ruleName' did not match $attributeName for request $requestIdText."
                continue
            }

            # First matching rule wins for each attribute. Mark the attribute as
            # decided only after the rule matches; even if the write throws, later
            # rules for the same attribute should not fight it.
            $decidedAttributes[$attributeKey] = $true

            $mappedValue = Get-ADAttributeRuleValue -Request $Request -Rule $rule
            $stringValue = ConvertTo-ADSingleValueString -Value $mappedValue

            if (Test-IsBlank $stringValue) {
                if (Get-ADAttributeRuleFieldBool -Object $rule -Name 'ClearWhenBlank' -Default $false) {
                    Write-Info "AD attribute rule '$ruleName' clears $attributeName for request $requestIdText."
                    try {
                        Set-ADSingleValuedStringAttributeBestEffort -Identity ([string]$Identity) -AttributeName ([string]$attributeName) -Value $null -RuleName ([string]$ruleName) -RequestIdText ([string]$requestIdText)
                    }
                    catch {
                        Write-Warning "AD attribute rule '$ruleName' failed while clearing $attributeName for request $requestIdText. Continuing to next rule. Error: $($_.Exception.Message)"
                    }
                }
                else {
                    Write-Info "AD attribute rule '$ruleName' matched $attributeName for request $requestIdText, but produced a blank value."
                }
                continue
            }

            $valueText = [string]$stringValue
            Write-Info "AD attribute rule '$ruleName' sets $attributeName for request $requestIdText to '$valueText'."

            try {
                Set-ADSingleValuedStringAttributeBestEffort -Identity ([string]$Identity) -AttributeName ([string]$attributeName) -Value ([string]$valueText) -RuleName ([string]$ruleName) -RequestIdText ([string]$requestIdText)
            }
            catch {
                $readbackMessage = ''
                try {
                    $readbackValue = Get-ADSingleValuedStringAttribute -Identity ([string]$Identity) -AttributeName ([string]$attributeName)
                    if ([string]::Equals(([string]$readbackValue), ([string]$valueText), [System.StringComparison]::OrdinalIgnoreCase)) {
                        $readbackMessage = " Readback confirms '$readbackValue'."
                    }
                    else {
                        $readbackMessage = " Readback is '$readbackValue', expected '$valueText'."
                    }
                }
                catch {
                    $readbackMessage = " Readback failed: $($_.Exception.Message)"
                }

                Write-Warning "AD attribute rule '$ruleName' for $attributeName on request $requestIdText failed while writing. Continuing to next rule.$readbackMessage Error: $($_.Exception.Message)"
            }
        }
        catch {
            Write-Warning "AD attribute rule '$ruleName' for $attributeName on request $requestIdText failed before/while evaluating. Continuing to next rule. Error: $($_.Exception.Message)"
            continue
        }
    }
}


function Invoke-BusinessRuleExtensionAttributesNonBlocking {
    param(
        [Parameter(Mandatory=$true)][string]$Identity,
        [Parameter(Mandatory=$true)]$Request
    )

    if ($null -eq $script:ADAttributeRules -or $script:ADAttributeRules.Count -eq 0) { return }

    try {
        $null = Set-BusinessRuleExtensionAttributes -Identity ([string]$Identity) -Request $Request
    }
    catch {
        $requestIdText = '<unknown>'
        try {
            if ($null -ne $Request.RequestId -and $Request.RequestId -isnot [System.DBNull]) {
                $requestIdText = [string]$Request.RequestId
            }
        }
        catch { }

        $message = "Business-rule extension attribute processing failed for request $requestIdText and identity $Identity. Error: $($_.Exception.Message)"
        if ($RequireADAttributeBusinessRules) {
            throw $message
        }

        Write-Warning "$message Continuing because -RequireADAttributeBusinessRules was not supplied."
    }
}

function ConvertTo-LocalDateTime {
    param(
        [Parameter(Mandatory=$true)][datetime]$Value
    )

    if ($Value.Kind -eq [System.DateTimeKind]::Local) {
        return $Value
    }

    if ($Value.Kind -eq [System.DateTimeKind]::Utc) {
        return $Value.ToLocalTime()
    }

    return [System.DateTime]::SpecifyKind($Value, [System.DateTimeKind]::Local)
}

function Get-ADAccountExpiresFileTime {
    param(
        [Parameter(Mandatory=$true)][datetime]$Value
    )

    $localValue = ConvertTo-LocalDateTime -Value $Value

    # AD stores accountExpires as the instant when the account stops being valid.
    # For a date-only value from SQL, 2026-11-30 means the account should be valid
    # for the entire local calendar day of 2026-11-30. The exclusive AD expiry
    # instant is therefore local midnight at the start of 2026-12-01.
    # This avoids ADUC / PowerShell displaying the previous date.
    if ($localValue.TimeOfDay -eq [TimeSpan]::Zero) {
        $localValue = [System.DateTime]::SpecifyKind($localValue.Date.AddDays(1), [System.DateTimeKind]::Local)
    }

    return [Int64]$localValue.ToFileTimeUtc()
}

function Set-ADAccountExpirationInclusive {
    param(
        [Parameter(Mandatory=$true)]$Identity,
        [Parameter(Mandatory=$true)][datetime]$Value
    )

    $accountExpiresFileTime = Get-ADAccountExpiresFileTime -Value $Value
    $effectiveLocalExpiry = [System.DateTime]::FromFileTimeUtc($accountExpiresFileTime).ToLocalTime()

    $parameters = @{
        Identity = $Identity
        Replace = @{ accountExpires = $accountExpiresFileTime }
        ErrorAction = 'Stop'
    }
    Add-ADServerIfConfigured $parameters

    Invoke-ADOperation "Set account expiration for $Identity to exclusive local instant $($effectiveLocalExpiry.ToString('yyyy-MM-dd HH:mm:ss'))" {
        Set-ADUser @parameters
    }
}

function Set-StandardADUserAttributes {
    param(
        [Parameter(Mandatory=$true)]$Identity,
        [Parameter(Mandatory=$true)]$Request
    )

    $parameters = @{
        Identity = $Identity
        ErrorAction = 'Stop'
    }
    Add-ADServerIfConfigured $parameters

    Add-HashtableValueIfPresent $parameters 'UserPrincipalName' $Request.NewUserPrincipalName
    Add-HashtableValueIfPresent $parameters 'DisplayName' $Request.NewDisplayName
    Add-HashtableValueIfPresent $parameters 'GivenName' $Request.NewGivenName
    Add-HashtableValueIfPresent $parameters 'Surname' $Request.NewSurname
    Add-HashtableValueIfPresent $parameters 'EmailAddress' $Request.Mail
    Add-HashtableValueIfPresent $parameters 'Department' $Request.Department
    Add-HashtableValueIfPresent $parameters 'Title' $Request.Title
    Add-HashtableValueIfPresent $parameters 'Company' $Request.Company
    Add-HashtableValueIfPresent $parameters 'StreetAddress' $Request.StreetAddress
    Add-HashtableValueIfPresent $parameters 'PostalCode' $Request.PostalCode
    Add-HashtableValueIfPresent $parameters 'City' $Request.City
    Add-HashtableValueIfPresent $parameters 'Office' $Request.Office
    Add-HashtableValueIfPresent $parameters 'MobilePhone' $Request.MobilePhone
    Add-HashtableValueIfPresent $parameters 'officePhone' $Request.MobilePhone

    $countryMetadata = Get-DomainCountryMetadata -Request $Request
    Add-HashtableValueIfPresent $parameters 'Country' $countryMetadata.CountryISO2

    $countryReplace = @{}
    if (-not (Test-IsBlank $countryMetadata.CountryName)) {
        $countryReplace['co'] = [string]$countryMetadata.CountryName
    }
    if ($null -ne $countryMetadata.CountryCode) {
        $countryReplace['countryCode'] = [int]$countryMetadata.CountryCode
    }
    if ($countryReplace.Count -gt 0) {
        $parameters['Replace'] = $countryReplace
    }

    if (-not (Test-IsBlank $Request.ManagerSamAccountName)) {
        $manager = Resolve-ADUserBySamAccountName $Request.ManagerSamAccountName
        if ($null -eq $manager) {
            throw "Manager '$($Request.ManagerSamAccountName)' was not found in AD."
        }
        $parameters['Manager'] = $manager.DistinguishedName
    }

    if ($parameters.Count -gt 2) {
        Invoke-ADOperation "Set standard AD attributes for $Identity" {
            Set-ADUser @parameters
        }
    }

    if (-not (Test-IsBlank $Request.NewSamAccountName)) {
        Invoke-ADOperation "Set sAMAccountName for $Identity to $($Request.NewSamAccountName)" {
            Set-ADUser -Identity $Identity -SamAccountName ([string]$Request.NewSamAccountName) -Server $script:ADServerName -ErrorAction Stop
        }
    }

    if (-not (Test-IsBlank $Request.EmployeeType)) {
        Invoke-ADOperation "Set employeeType for $Identity to $($Request.EmployeeType)" {
            Set-ADUser -Identity $Identity -Replace @{ employeeType = [string]$Request.EmployeeType } -Server $script:ADServerName -ErrorAction Stop
        }
    }

    if ($null -ne $Request.AccountExpirationDate) {
        Set-ADAccountExpirationInclusive -Identity $Identity -Value ([datetime]$Request.AccountExpirationDate)
    }
}

function Set-AttributeJsonAttributes {
    param(
        [Parameter(Mandatory=$true)]$Identity,
        [Parameter()]$AttributeJson
    )

    if (Test-IsBlank $AttributeJson) { return }

    $jsonText = [string]$AttributeJson
    $jsonObject = $jsonText | ConvertFrom-Json -ErrorAction Stop

    $replace = @{}
    $clear = New-Object System.Collections.Generic.List[string]
    $allowedLookup = @{}
    foreach ($name in $AllowedAttributeJsonAttributes) {
        $allowedLookup[$name.ToLowerInvariant()] = $true
    }

    foreach ($property in $jsonObject.PSObject.Properties) {
        $attributeName = $property.Name
        if (-not $allowedLookup.ContainsKey($attributeName.ToLowerInvariant())) {
            throw "AttributeJson contains unsupported AD attribute '$attributeName'. Add it to -AllowedAttributeJsonAttributes if this is intentional."
        }

        if ($null -eq $property.Value) {
            $clear.Add($attributeName)
        }
        else {
            $replace[$attributeName] = $property.Value
        }
    }

    if ($replace.Count -gt 0) {
        Invoke-ADOperation "Set AttributeJson AD attributes for $Identity" {
            Set-ADUser -Identity $Identity -Replace $replace -Server $script:ADServerName -ErrorAction Stop
        }
    }

    if ($clear.Count -gt 0) {
        Invoke-ADOperation "Clear AttributeJson AD attributes for $Identity" {
            Set-ADUser -Identity $Identity -Clear $clear.ToArray() -Server $script:ADServerName -ErrorAction Stop
        }
    }
}

function Set-EnabledState {
    param(
        [Parameter(Mandatory=$true)]$Identity,
        [Parameter(Mandatory=$true)][bool]$Enabled
    )

    if ($Enabled) {
        Invoke-ADOperation "Enable AD account $Identity" {
            Enable-ADAccount -Identity $Identity -Server $script:ADServerName -ErrorAction Stop
        }
    }
    else {
        Invoke-ADOperation "Disable AD account $Identity" {
            Disable-ADAccount -Identity $Identity -Server $script:ADServerName -ErrorAction Stop
        }
    }
}


function Test-ADUserDirectMemberOfGroup {
    param(
        [Parameter(Mandatory=$true)]$AdUser,
        [Parameter(Mandatory=$true)]$AdGroup
    )

    if ($DryRun) { return $false }
    if (Test-IsBlank $AdUser.DistinguishedName) { return $false }
    if (Test-IsBlank $AdGroup.DistinguishedName) { return $false }

    try {
        $groupDn = ConvertTo-LdapEscapedValue ([string]$AdGroup.DistinguishedName)
        $userDn = ConvertTo-LdapEscapedValue ([string]$AdUser.DistinguishedName)
        $params = @{
            LDAPFilter = "(&(objectClass=group)(distinguishedName=$groupDn)(member=$userDn))"
            ErrorAction = 'Stop'
        }
        Add-ADServerIfConfigured $params
        $matches = @(Get-ADObject @params)
        return ($matches.Count -gt 0)
    }
    catch {
        Write-Warning "Could not verify whether $($AdUser.SamAccountName) is a direct member of $($AdGroup.SamAccountName). $($_.Exception.Message)"
        return $false
    }
}

function Add-ADUserToGroupWithVerification {
    param(
        [Parameter(Mandatory=$true)]$AdUser,
        [Parameter(Mandatory=$true)]$AdGroup,
        [Parameter()][string]$DescriptionPrefix = 'Add'
    )

    if (-not $DryRun -and (Test-ADUserDirectMemberOfGroup -AdUser $AdUser -AdGroup $AdGroup)) {
        Write-Info "User $($AdUser.SamAccountName) is already a direct member of $($AdGroup.SamAccountName)."
        return
    }

    if ($DryRun) {
        Invoke-ADOperation "$DescriptionPrefix $($AdUser.SamAccountName) to group $($AdGroup.SamAccountName)" {
            Add-ADGroupMember -Identity $AdGroup.DistinguishedName -Members $AdUser.DistinguishedName -Server $script:ADServerName -ErrorAction Stop
        }
        return
    }

    $errors = New-Object System.Collections.Generic.List[string]

    for ($attempt = 1; $attempt -le 2; $attempt++) {
        try {
            Invoke-ADOperation "$DescriptionPrefix $($AdUser.SamAccountName) to group $($AdGroup.SamAccountName)" {
                Add-ADGroupMember -Identity $AdGroup.DistinguishedName -Members $AdUser.DistinguishedName -Server $script:ADServerName -ErrorAction Stop
            }

            Start-Sleep -Seconds 1
            if (Test-ADUserDirectMemberOfGroup -AdUser $AdUser -AdGroup $AdGroup) {
                return
            }

            Write-Warning "Add-ADGroupMember did not throw for $($AdGroup.SamAccountName), but membership could not be verified yet. Continuing."
            return
        }
        catch {
            $errors.Add($_.Exception.Message)
            Start-Sleep -Seconds (2 * $attempt)

            if (Test-ADUserDirectMemberOfGroup -AdUser $AdUser -AdGroup $AdGroup) {
                Write-Warning "Add-ADGroupMember reported an error for $($AdGroup.SamAccountName), but $($AdUser.SamAccountName) is now a direct member. Treating as success. Error was: $($_.Exception.Message)"
                return
            }

            if ($attempt -lt 2) {
                Write-Warning "Add-ADGroupMember failed for $($AdGroup.SamAccountName); retrying once. Error was: $($_.Exception.Message)"
            }
        }
    }

    throw "Failed to add $($AdUser.SamAccountName) to group $($AdGroup.SamAccountName). Errors: $($errors -join ' | ')"
}

function Remove-ADUserFromGroupWithVerification {
    param(
        [Parameter(Mandatory=$true)]$AdUser,
        [Parameter(Mandatory=$true)]$AdGroup
    )

    if (-not $DryRun -and -not (Test-ADUserDirectMemberOfGroup -AdUser $AdUser -AdGroup $AdGroup)) {
        Write-Info "User $($AdUser.SamAccountName) is not a direct member of $($AdGroup.SamAccountName); nothing to remove."
        return
    }

    Invoke-ADOperation "Remove $($AdUser.SamAccountName) from group $($AdGroup.SamAccountName)" {
        Remove-ADGroupMember -Identity $AdGroup.DistinguishedName -Members $AdUser.DistinguishedName -Server $script:ADServerName -Confirm:$false -ErrorAction Stop
    }
}

function Apply-QueuedGroups {
    param(
        [Parameter(Mandatory=$true)]$AdUser,
        [Parameter(Mandatory=$true)]$QueueGroups
    )

    if ($null -eq $QueueGroups -or $QueueGroups.Count -eq 0) {
        return
    }

    foreach ($queueGroup in $QueueGroups) {
        $action = ([string]$queueGroup.Action).Trim().ToUpperInvariant()
        $adGroup = Resolve-ADGroupFromQueueGroup $queueGroup
        if ($null -eq $adGroup) {
            throw "Could not resolve queued group row $($queueGroup.Id)."
        }

        if ($action -eq 'ADD') {
            $null = Add-ADUserToGroupWithVerification -AdUser $AdUser -AdGroup $adGroup -DescriptionPrefix 'Add'
            continue
        }

        if ($action -eq 'REMOVE') {
            $null = Remove-ADUserFromGroupWithVerification -AdUser $AdUser -AdGroup $adGroup
            continue
        }

        throw "Unsupported queued group action '$($queueGroup.Action)' in row $($queueGroup.Id)."
    }
}


function Test-IsNoOfficeLicenseValue {
    param([object]$Value)

    if (Test-IsBlank $Value) { return $true }

    $licenseName = ([string]$Value).Trim()
    return ($licenseName -match '^(No office license|None|No license)$')
}

function Get-MailAliasForRequest {
    param([Parameter(Mandatory=$true)]$Request)

    foreach ($candidate in @($Request.Mail, $Request.NewUserPrincipalName, $Request.NewSamAccountName, $Request.TargetSamAccountName)) {
        if (Test-IsBlank $candidate) { continue }
        $text = ([string]$candidate).Trim()
        if ($text.Contains('@')) {
            $localPart = $text.Split('@')[0]
            if (-not (Test-IsBlank $localPart)) { return $localPart }
        }
        else {
            return $text
        }
    }

    throw "Request $($Request.RequestId) does not contain enough information to derive a mail alias."
}

function Get-PrimarySmtpAddressForRequest {
    param([Parameter(Mandatory=$true)]$Request)

    foreach ($candidate in @($Request.Mail, $Request.NewUserPrincipalName)) {
        if (Test-IsBlank $candidate) { continue }
        $text = ([string]$candidate).Trim()
        if ($text.Contains('@')) { return $text }
    }

    throw "Request $($Request.RequestId) does not contain Mail or NewUserPrincipalName with a valid SMTP address."
}

function Get-RemoteRoutingAddressForRequest {
    param(
        [Parameter(Mandatory=$true)]$Request,
        [Parameter(Mandatory=$true)][string]$Alias
    )

    if (Test-IsBlank $script:ResolvedRemoteRoutingDomain) {
        $message = "Remote mailbox provisioning needs a RemoteRoutingDomain value. Set dbo.UserChangeQueueSettings.SettingName = 'RemoteRoutingDomain', or pass -RemoteRoutingDomain tenant.mail.onmicrosoft.com as an override."
        if ($RequireRemoteMailbox) { throw $message }
        Write-Warning "$message Skipping remote mailbox marking for request $($Request.RequestId)."
        return $null
    }

    $routingDomain = ([string]$script:ResolvedRemoteRoutingDomain).Trim().TrimStart('@')
    if (Test-IsBlank $routingDomain) {
        $message = 'RemoteRoutingDomain resolved to an empty value.'
        if ($RequireRemoteMailbox) { throw $message }
        Write-Warning "$message Skipping remote mailbox marking for request $($Request.RequestId)."
        return $null
    }

    return $RemoteRoutingAddressTemplate.Replace('{alias}', $Alias).Replace('{remoteRoutingDomain}', $routingDomain)
}

function Test-RequestShouldHaveRemoteMailbox {
    param([Parameter(Mandatory=$true)]$Request)

    if (-not $EnableRemoteMailbox) { return $false }

    $enabled = Get-RequestEnabledValue -Request $Request -DefaultValue $true
    if (-not $enabled) { return $false }

    if ($RemoteMailboxForRequestsWithoutLicense) { return $true }

    return (-not (Test-IsNoOfficeLicenseValue $Request.OfficeLicense))
}

function Import-ExchangeShellIfAvailable {
    if ($script:ExchangeShellLoaded) { return $true }

    if (Get-Command Enable-RemoteMailbox -ErrorAction SilentlyContinue) {
        $script:ExchangeShellLoaded = $true
        return $true
    }

    if (-not (Test-IsBlank $ExchangeSnapInName)) {
        try {
            if (Get-PSSnapin -Registered -Name $ExchangeSnapInName -ErrorAction SilentlyContinue) {
                Add-PSSnapin $ExchangeSnapInName -ErrorAction Stop
                if (Get-Command Enable-RemoteMailbox -ErrorAction SilentlyContinue) {
                    $script:ExchangeShellLoaded = $true
                    return $true
                }
            }
        }
        catch {
            Write-Warning "Could not load Exchange PowerShell snap-in '$ExchangeSnapInName'. $($_.Exception.Message)"
        }
    }

    return $false
}

function Test-ADUserAlreadyRemoteMailbox {
    param([Parameter(Mandatory=$true)]$AdUser)

    $params = @{
        Identity = $AdUser.DistinguishedName
        Properties = 'targetAddress','mailNickname','proxyAddresses','msExchRemoteRecipientType','msExchRecipientTypeDetails','msExchRecipientDisplayType'
        ErrorAction = 'Stop'
    }
    Add-ADServerIfConfigured $params
    $fresh = Get-ADUser @params

    return (-not (Test-IsBlank $fresh.targetAddress) -or -not (Test-IsBlank $fresh.msExchRemoteRecipientType))
}

function Add-ADUserProxyAddressIfMissing {
    param(
        [Parameter(Mandatory=$true)]$Identity,
        [Parameter(Mandatory=$true)][string[]]$ProxyAddresses
    )

    $params = @{
        Identity = $Identity
        Properties = 'proxyAddresses'
        ErrorAction = 'Stop'
    }
    Add-ADServerIfConfigured $params
    $user = Get-ADUser @params

    $existing = @()
    if ($null -ne $user.proxyAddresses) {
        $existing = @($user.proxyAddresses | ForEach-Object { [string]$_ })
    }

    $toAdd = New-Object System.Collections.Generic.List[string]
    foreach ($address in $ProxyAddresses) {
        if (Test-IsBlank $address) { continue }
        $exists = $false
        foreach ($current in $existing) {
            if ([string]::Equals($current, $address, [System.StringComparison]::OrdinalIgnoreCase)) {
                $exists = $true
                break
            }
        }
        if (-not $exists) { [void]$toAdd.Add($address) }
    }

    if ($toAdd.Count -gt 0) {
        Invoke-ADOperation "Add proxyAddresses to ${Identity}: $($toAdd -join ', ')" {
            Set-ADUser -Identity $Identity -Add @{ proxyAddresses = $toAdd.ToArray() } -Server $script:ADServerName -ErrorAction Stop
        }
    }
}

function Enable-RemoteMailboxForUser {
    param(
        [Parameter(Mandatory=$true)]$AdUser,
        [Parameter(Mandatory=$true)]$Request
    )

    if (-not (Test-RequestShouldHaveRemoteMailbox -Request $Request)) { return }

    $alias = Get-MailAliasForRequest -Request $Request
    $primarySmtpAddress = Get-PrimarySmtpAddressForRequest -Request $Request
    $remoteRoutingAddress = Get-RemoteRoutingAddressForRequest -Request $Request -Alias $alias
    if (Test-IsBlank $remoteRoutingAddress) { return }

    if (-not $DryRun -and (Test-ADUserAlreadyRemoteMailbox -AdUser $AdUser)) {
        Write-Info "AD user $($AdUser.SamAccountName) already appears to be marked as a remote mailbox."
        return
    }

    if (Import-ExchangeShellIfAvailable) {
        $null = Invoke-ADOperation "Enable remote mailbox for $($AdUser.SamAccountName) with routing address $remoteRoutingAddress" {
            Enable-RemoteMailbox -Identity $AdUser.DistinguishedName -Alias $alias -PrimarySmtpAddress $primarySmtpAddress -RemoteRoutingAddress $remoteRoutingAddress -ErrorAction Stop
        }
        return
    }

    $message = "Exchange Enable-RemoteMailbox is not available in this PowerShell session."
    if (-not $AllowRemoteMailboxAdAttributeFallback) {
        if ($RequireRemoteMailbox) { throw "$message Install/load Exchange Management Shell or run with -AllowRemoteMailboxAdAttributeFallback." }
        Write-Warning "$message Skipping remote mailbox marking for $($AdUser.SamAccountName)."
        return
    }

    # Prefer Enable-RemoteMailbox from Exchange Management Shell. This fallback exists for
    # environments that intentionally manage the same hybrid remote mailbox attributes directly.
    # It requires the Exchange schema attributes to exist in on-prem AD.
    $replace = @{
        mailNickname = $alias
        targetAddress = "SMTP:$remoteRoutingAddress"
        msExchRemoteRecipientType = 1
        msExchRecipientDisplayType = -2147483642
        msExchRecipientTypeDetails = 2147483648
    }

    $null = Invoke-ADOperation "Set remote mailbox AD attributes for $($AdUser.SamAccountName) with routing address $remoteRoutingAddress" {
        Set-ADUser -Identity $AdUser.DistinguishedName -Replace $replace -Server $script:ADServerName -ErrorAction Stop
    }

    $null = Add-ADUserProxyAddressIfMissing -Identity $AdUser.DistinguishedName -ProxyAddresses @(
        "SMTP:$primarySmtpAddress",
        "smtp:$remoteRoutingAddress"
    )
}

function Apply-OfficeLicenseGroup {
    param(
        [Parameter(Mandatory=$true)]$AdUser,
        [Parameter()]$OfficeLicense
    )

    if ($SkipOfficeLicenseGroup) { return }
    if (Test-IsBlank $OfficeLicense) { return }

    $licenseName = ([string]$OfficeLicense).Trim()
    if (Test-IsNoOfficeLicenseValue $licenseName) { return }

    $licenseGroup = Resolve-ADGroupByName $licenseName
    if ($null -eq $licenseGroup) {
        $message = "OfficeLicense '$licenseName' did not match an AD group."
        if ($StrictOfficeLicenseGroup -or $ApplyOfficeLicenseGroup) {
            throw $message
        }

        Write-Warning "$message Skipping OfficeLicense group assignment for $($AdUser.SamAccountName)."
        return
    }

    $null = Add-ADUserToGroupWithVerification -AdUser $AdUser -AdGroup $licenseGroup -DescriptionPrefix 'Add Office license group'
}

function Invoke-CreateRequest {
    param(
        [Parameter(Mandatory=$true)]$Request,
        [Parameter(Mandatory=$true)]$QueueGroups,
		[Parameter()][ref]$InitialPasswordPlainText
    )

    foreach ($required in @('NewSamAccountName','NewUserPrincipalName','NewDisplayName','NewGivenName','NewSurname','NewOU')) {
        if (Test-IsBlank $Request.$required) {
            throw "CREATE request $($Request.RequestId) is missing required field $required."
        }
    }

    if (Test-IsBlank $Request.ProjectNumber) {
        throw "CREATE request $($Request.RequestId) has no ProjectNumber. A project number is required so extensionAttribute4 can be set."
    }

    $existingUser = Resolve-ADUserBySamAccountName $Request.NewSamAccountName
    if ($null -ne $existingUser -and -not $AllowExistingCreateRecovery) {
        throw "CREATE request $($Request.RequestId) cannot continue because AD user '$($Request.NewSamAccountName)' already exists. Use -AllowExistingCreateRecovery only when recovering from a partially processed CREATE request."
    }

    if ($null -ne $existingUser -and $AllowExistingCreateRecovery) {
        if (-not (Test-IsBlank $Request.NewUserPrincipalName) -and -not (Test-IsBlank $existingUser.UserPrincipalName) -and
            -not ([string]::Equals([string]$existingUser.UserPrincipalName, [string]$Request.NewUserPrincipalName, [System.StringComparison]::OrdinalIgnoreCase))) {
            throw "Existing AD user '$($Request.NewSamAccountName)' has UPN '$($existingUser.UserPrincipalName)', but request $($Request.RequestId) expects '$($Request.NewUserPrincipalName)'. Refusing recovery."
        }

        Write-Warning "CREATE request $($Request.RequestId) found existing AD user '$($Request.NewSamAccountName)'. Continuing in recovery mode and applying remaining attributes, password, enabled state, and groups."
        $createdUser = $existingUser
        $initialPassword = Get-InitialPasswordForCreateRequest -Request $Request
		if ($null -ne $InitialPasswordPlainText) {
    $InitialPasswordPlainText.Value = if (
        $null -ne $initialPassword -and
        -not (Test-IsBlank $initialPassword.PlainText)
    ) {
        [string]$initialPassword.PlainText
    }
    else {
        ''
    }
}
        $enabled = Get-RequestEnabledValue -Request $Request -DefaultValue $true

        $null = Set-StandardADUserAttributes -Identity $createdUser.DistinguishedName -Request $Request
        $null = Invoke-BusinessRuleExtensionAttributesNonBlocking -Identity $createdUser.DistinguishedName -Request $Request
        $null = Set-AttributeJsonAttributes -Identity $createdUser.DistinguishedName -AttributeJson $Request.AttributeJson

        Invoke-ADOperation "Set extensionAttribute4 project number for $($createdUser.SamAccountName) to $($Request.ProjectNumber)" {
            Set-ADSingleValuedStringAttribute -Identity $createdUser.DistinguishedName -AttributeName 'extensionAttribute4' -Value ([string]$Request.ProjectNumber)
        }

        if ($enabled) {
            Invoke-ADOperation "Set initial password for $($createdUser.SamAccountName)" {
                Set-ADAccountPassword -Identity $createdUser.DistinguishedName -Reset -NewPassword $initialPassword.SecureString -Server $script:ADServerName -ErrorAction Stop
            }

            if ($ForcePasswordChangeAtNextLogon) {
                Invoke-ADOperation "Require password change at next logon for $($createdUser.SamAccountName)" {
                    Set-ADUser -Identity $createdUser.DistinguishedName -ChangePasswordAtLogon $true -Server $script:ADServerName -ErrorAction Stop
                }
            }
            else {
                Write-Info "Password change at next logon is not required for $($createdUser.SamAccountName)."
            }

            if ($initialPassword.Generated -and -not (Test-IsBlank $initialPassword.PlainText)) {
                Write-GeneratedPasswordRecord -Request $Request -PlainTextPassword $initialPassword.PlainText
            }
        }

        $null = Set-EnabledState -Identity $createdUser.DistinguishedName -Enabled $enabled
        $refreshedCreatedUser = Get-ADUser -Identity $createdUser.DistinguishedName -Properties DistinguishedName,ObjectGUID,SamAccountName,UserPrincipalName -Server $script:ADServerName -ErrorAction Stop
        $null = Enable-RemoteMailboxForUser -AdUser $refreshedCreatedUser -Request $Request
        $null = Apply-OfficeLicenseGroup -AdUser $refreshedCreatedUser -OfficeLicense $Request.OfficeLicense
        $null = Apply-QueuedGroups -AdUser $refreshedCreatedUser -QueueGroups $QueueGroups
        return (Get-ADUser -Identity $refreshedCreatedUser.DistinguishedName -Properties DistinguishedName,ObjectGUID,SamAccountName,UserPrincipalName -Server $script:ADServerName -ErrorAction Stop)
    }

    $initialPassword = Get-InitialPasswordForCreateRequest -Request $Request
	if ($null -ne $InitialPasswordPlainText) {
    $InitialPasswordPlainText.Value = if (
        $null -ne $initialPassword -and
        -not (Test-IsBlank $initialPassword.PlainText)
    ) {
        [string]$initialPassword.PlainText
    }
    else {
        ''
    }
}
    $enabled = Get-RequestEnabledValue -Request $Request -DefaultValue $true

    $newUserParams = @{
        Name = [string]$Request.NewDisplayName
        SamAccountName = [string]$Request.NewSamAccountName
        UserPrincipalName = [string]$Request.NewUserPrincipalName
        DisplayName = [string]$Request.NewDisplayName
        GivenName = [string]$Request.NewGivenName
        Surname = [string]$Request.NewSurname
        Path = [string]$Request.NewOU
        Enabled = $false
        ErrorAction = 'Stop'
    }
    Add-ADServerIfConfigured $newUserParams

    if (-not (Test-IsBlank $Request.Mail)) {
        $newUserParams['EmailAddress'] = [string]$Request.Mail
    }

    Invoke-ADOperation "Create AD user $($Request.NewSamAccountName) in $($Request.NewOU)" {
        New-ADUser @newUserParams
    }

    if ($DryRun) {
        return [pscustomobject]@{
            ObjectGUID = [Guid]::Empty
            SamAccountName = [string]$Request.NewSamAccountName
        }
    }

    $createdUser = Get-ADUser -Identity ([string]$Request.NewSamAccountName) -Properties DistinguishedName,ObjectGUID,SamAccountName,UserPrincipalName -Server $script:ADServerName -ErrorAction Stop

    $null = Set-StandardADUserAttributes -Identity $createdUser.DistinguishedName -Request $Request
    $null = Invoke-BusinessRuleExtensionAttributesNonBlocking -Identity $createdUser.DistinguishedName -Request $Request
    $null = Set-AttributeJsonAttributes -Identity $createdUser.DistinguishedName -AttributeJson $Request.AttributeJson

    Invoke-ADOperation "Set extensionAttribute4 project number for $($createdUser.SamAccountName) to $($Request.ProjectNumber)" {
        Set-ADSingleValuedStringAttribute -Identity $createdUser.DistinguishedName -AttributeName 'extensionAttribute4' -Value ([string]$Request.ProjectNumber)
    }

    if ($enabled) {
        Invoke-ADOperation "Set initial password for $($createdUser.SamAccountName)" {
            Set-ADAccountPassword -Identity $createdUser.DistinguishedName -Reset -NewPassword $initialPassword.SecureString -Server $script:ADServerName -ErrorAction Stop
        }

        if ($ForcePasswordChangeAtNextLogon) {
            Invoke-ADOperation "Require password change at next logon for $($createdUser.SamAccountName)" {
                Set-ADUser -Identity $createdUser.DistinguishedName -ChangePasswordAtLogon $true -Server $script:ADServerName -ErrorAction Stop
            }
        }
        else {
            Write-Info "Password change at next logon is not required for $($createdUser.SamAccountName)."
        }

        if ($initialPassword.Generated -and -not (Test-IsBlank $initialPassword.PlainText)) {
            Write-GeneratedPasswordRecord -Request $Request -PlainTextPassword $initialPassword.PlainText
        }
    }

    $null = Set-EnabledState -Identity $createdUser.DistinguishedName -Enabled $enabled
    $createdUser = Get-ADUser -Identity $createdUser.DistinguishedName -Properties DistinguishedName,ObjectGUID,SamAccountName,UserPrincipalName -Server $script:ADServerName -ErrorAction Stop
    $null = Enable-RemoteMailboxForUser -AdUser $createdUser -Request $Request
    $null = Apply-OfficeLicenseGroup -AdUser $createdUser -OfficeLicense $Request.OfficeLicense
    $null = Apply-QueuedGroups -AdUser $createdUser -QueueGroups $QueueGroups

    return (Get-ADUser -Identity $createdUser.DistinguishedName -Properties DistinguishedName,ObjectGUID,SamAccountName,UserPrincipalName -Server $script:ADServerName -ErrorAction Stop)
}

function Invoke-UpdateRequest {
    param(
        [Parameter(Mandatory=$true)]$Request,
        [Parameter(Mandatory=$true)]$QueueGroups
    )

    $identity = $null
    if ($null -ne $Request.TargetObjectGUID) {
        $identity = [Guid]$Request.TargetObjectGUID
    }
    elseif (-not (Test-IsBlank $Request.TargetSamAccountName)) {
        $identity = [string]$Request.TargetSamAccountName
    }
    else {
        throw "UPDATE request $($Request.RequestId) has neither TargetObjectGUID nor TargetSamAccountName."
    }

    if (Test-IsBlank $Request.ProjectNumber) {
        throw "UPDATE request $($Request.RequestId) has no ProjectNumber. A project number is required so extensionAttribute4 can be updated."
    }

    $adUser = Get-ADUser -Identity $identity -Properties DistinguishedName,ObjectGUID,SamAccountName,UserPrincipalName,DisplayName -Server $script:ADServerName -ErrorAction Stop

    $null = Set-StandardADUserAttributes -Identity $adUser.DistinguishedName -Request $Request
    $null = Invoke-BusinessRuleExtensionAttributesNonBlocking -Identity $adUser.DistinguishedName -Request $Request
    $null = Set-AttributeJsonAttributes -Identity $adUser.DistinguishedName -AttributeJson $Request.AttributeJson

    Invoke-ADOperation "Set extensionAttribute4 project number for $($adUser.SamAccountName) to $($Request.ProjectNumber)" {
        Set-ADSingleValuedStringAttribute -Identity $adUser.DistinguishedName -AttributeName 'extensionAttribute4' -Value ([string]$Request.ProjectNumber)
    }

    if ($RenameCNToDisplayName -and -not (Test-IsBlank $Request.NewDisplayName)) {
        $refreshedUser = Get-ADUser -Identity $adUser.ObjectGUID -Properties DistinguishedName,Name -Server $script:ADServerName -ErrorAction Stop
        if ($refreshedUser.Name -ne [string]$Request.NewDisplayName) {
            Invoke-ADOperation "Rename CN for $($adUser.SamAccountName) to $($Request.NewDisplayName)" {
                Rename-ADObject -Identity $refreshedUser.DistinguishedName -NewName ([string]$Request.NewDisplayName) -Server $script:ADServerName -ErrorAction Stop
            }
        }
    }

    if ($MoveUserOnUpdate -and -not (Test-IsBlank $Request.NewOU)) {
        $destination = [string]$Request.NewOU
        if ($destination.StartsWith('OU=', [System.StringComparison]::OrdinalIgnoreCase)) {
            $refreshedUser = Get-ADUser -Identity $adUser.ObjectGUID -Properties DistinguishedName -Server $script:ADServerName -ErrorAction Stop
            Invoke-ADOperation "Move $($adUser.SamAccountName) to $destination" {
                Move-ADObject -Identity $refreshedUser.DistinguishedName -TargetPath $destination -Server $script:ADServerName -ErrorAction Stop
            }
        }
        else {
            Write-Warning "Request $($Request.RequestId) NewOU is not an OU path, so MoveUserOnUpdate skipped it: $destination"
        }
    }

    if ($null -ne $Request.Enabled) {
        $null = Set-EnabledState -Identity $adUser.ObjectGUID -Enabled ([bool]$Request.Enabled)
    }
    else {
        Write-Warning "Request $($Request.RequestId) has no Enabled value; leaving AD enabled/disabled state unchanged."
    }

    $refreshedAdUser = Get-ADUser -Identity $adUser.ObjectGUID -Properties DistinguishedName,ObjectGUID,SamAccountName,UserPrincipalName -Server $script:ADServerName -ErrorAction Stop
    $null = Enable-RemoteMailboxForUser -AdUser $refreshedAdUser -Request $Request
    $null = Apply-OfficeLicenseGroup -AdUser $refreshedAdUser -OfficeLicense $Request.OfficeLicense
    $null = Apply-QueuedGroups -AdUser $refreshedAdUser -QueueGroups $QueueGroups

    return (Get-ADUser -Identity $refreshedAdUser.ObjectGUID -Properties DistinguishedName,ObjectGUID,SamAccountName,UserPrincipalName -Server $script:ADServerName -ErrorAction Stop)
}


function Add-ServiceDeskPlusQueueItem {
    param(
        [Parameter(Mandatory=$true)][System.Data.SqlClient.SqlConnection]$Connection,
        [Parameter(Mandatory=$true)]$Request,
        [Parameter()]$AdUser
    )

    $requestIdValue = [long]$Request.RequestId

    if ($DryRun) {
        Write-Info "DRYRUN: would queue ServiceDesk Plus requester creation for request $requestIdValue."
        return
    }

    $enabledValue = Get-QueueWorkerSettingValue -Connection $Connection -SettingName 'ServiceDeskPlusEnabled'
    if ((Test-IsBlank $enabledValue) -or ([string]$enabledValue).Trim() -notin @('1','true','yes','on')) {
        Write-Info "ServiceDesk Plus integration is disabled; request $requestIdValue was not queued."
        return
    }

    $existsCmd = $Connection.CreateCommand()
    try {
        $existsCmd.CommandText = "SELECT CASE WHEN OBJECT_ID(N'dbo.ADUserChangeQueueServiceDeskPlus', N'U') IS NULL THEN 0 ELSE 1 END;"
        if ([int]$existsCmd.ExecuteScalar() -ne 1) {
            Write-Warning "dbo.ADUserChangeQueueServiceDeskPlus does not exist; request $requestIdValue was not queued for ServiceDesk Plus. Run Database\\ServiceDeskPlus-Integration.Required.sql."
            return
        }
    }
    finally {
        $existsCmd.Dispose()
    }

    $emailAddress = if (-not (Test-IsBlank $Request.Mail)) {
        ([string]$Request.Mail).Trim()
    }
    elseif (-not (Test-IsBlank $Request.NewUserPrincipalName)) {
        ([string]$Request.NewUserPrincipalName).Trim()
    }
    elseif ($null -ne $AdUser -and $AdUser.PSObject.Properties.Name -contains 'UserPrincipalName' -and -not (Test-IsBlank $AdUser.UserPrincipalName)) {
        ([string]$AdUser.UserPrincipalName).Trim()
    }
    else {
        $null
    }

    if (Test-IsBlank $emailAddress) {
        Write-Warning "Request $requestIdValue has no company email address or UPN; ServiceDesk Plus requester creation was not queued."
        return
    }

    $requester = [ordered]@{}
    $requester['name'] = [string]$Request.NewDisplayName
    $requester['first_name'] = [string]$Request.NewGivenName
    $requester['last_name'] = [string]$Request.NewSurname
    $requester['email_id'] = $emailAddress
    $requester['job_title'] = [string]$Request.Title
    $requester['mobile'] = [string]$Request.MobilePhone

    $loginUserValue = Get-QueueWorkerSettingValue -Connection $Connection -SettingName 'ServiceDeskPlusLoginUser'
    $requester['login_user'] = ((-not (Test-IsBlank $loginUserValue)) -and ([string]$loginUserValue).Trim() -in @('1','true','yes','on'))

    $departmentName = if (-not (Test-IsBlank $Request.Department)) {
        ([string]$Request.Department).Trim()
    }
    else {
        Get-QueueWorkerSettingValue -Connection $Connection -SettingName 'ServiceDeskPlusDefaultDepartment'
    }

    $siteName = if (-not (Test-IsBlank $Request.Office)) {
        ([string]$Request.Office).Trim()
    }
    else {
        Get-QueueWorkerSettingValue -Connection $Connection -SettingName 'ServiceDeskPlusDefaultSite'
    }

    if (-not (Test-IsBlank $departmentName)) {
        $requester['department'] = [ordered]@{ name = [string]$departmentName }
    }
    if (-not (Test-IsBlank $siteName)) {
        $requester['site'] = [ordered]@{ name = [string]$siteName }
    }

    foreach ($key in @($requester.Keys)) {
        $value = $requester[$key]
        if ($null -eq $value -or ($value -is [string] -and [string]::IsNullOrWhiteSpace($value))) {
            $requester.Remove($key)
        }
    }

    $payloadJson = ([ordered]@{ requester = $requester } | ConvertTo-Json -Depth 10 -Compress)

    $cmd = $Connection.CreateCommand()
    try {
        $cmd.CommandText = @"
IF NOT EXISTS
(
    SELECT 1
    FROM dbo.ADUserChangeQueueServiceDeskPlus WITH (UPDLOCK, HOLDLOCK)
    WHERE RequestId = @RequestId
      AND Operation = N'CreateRequester'
)
BEGIN
    INSERT INTO dbo.ADUserChangeQueueServiceDeskPlus
    (
        RequestId,
        Operation,
        Status,
        EmailAddress,
        RequesterName,
        DepartmentName,
        SiteName,
        PayloadJson,
        AttemptCount,
        NextAttemptAt,
        CreatedAt,
        UpdatedAt,
        UpdatedBy
    )
    VALUES
    (
        @RequestId,
        N'CreateRequester',
        N'Pending',
        @EmailAddress,
        @RequesterName,
        @DepartmentName,
        @SiteName,
        @PayloadJson,
        0,
        SYSDATETIME(),
        SYSDATETIME(),
        SYSDATETIME(),
        N'Invoke-ADUserChangeQueue.ps1'
    );

    SELECT CAST(1 AS bit);
END
ELSE
BEGIN
    SELECT CAST(0 AS bit);
END;
"@
        [void](Add-SqlParameter $cmd '@RequestId' ([System.Data.SqlDbType]::BigInt) $requestIdValue)
        [void](Add-SqlParameter $cmd '@EmailAddress' ([System.Data.SqlDbType]::NVarChar) $emailAddress 320)
        [void](Add-SqlParameter $cmd '@RequesterName' ([System.Data.SqlDbType]::NVarChar) ([string]$Request.NewDisplayName) 300)
        [void](Add-SqlParameter $cmd '@DepartmentName' ([System.Data.SqlDbType]::NVarChar) $departmentName 300)
        [void](Add-SqlParameter $cmd '@SiteName' ([System.Data.SqlDbType]::NVarChar) $siteName 300)
        [void](Add-SqlParameter $cmd '@PayloadJson' ([System.Data.SqlDbType]::NVarChar) $payloadJson -1)

        $inserted = [bool]$cmd.ExecuteScalar()
        if ($inserted) {
            Write-Info "Queued ServiceDesk Plus requester creation for request $requestIdValue ($emailAddress)."
        }
        else {
            Write-Info "ServiceDesk Plus requester creation for request $requestIdValue was already queued; skipping duplicate."
        }
    }
    finally {
        $cmd.Dispose()
    }
}

function Invoke-QueueRequest {
    param(
        [Parameter(Mandatory=$true)][System.Data.SqlClient.SqlConnection]$Connection,
        [Parameter(Mandatory=$true)]$Request
    )

    $requestIdValue = [long]$Request.RequestId
    $requestLabel = if (-not (Test-IsBlank $Request.NewSamAccountName)) { [string]$Request.NewSamAccountName } elseif (-not (Test-IsBlank $Request.TargetSamAccountName)) { [string]$Request.TargetSamAccountName } else { [string]$Request.TargetObjectGUID }
    Write-Info "Processing request $requestIdValue ($($Request.RequestType)) for '$requestLabel'."

    if (-not (Claim-QueueRequest -Connection $Connection -RequestIdValue $requestIdValue)) {
        Write-Warning "Request $requestIdValue was not claimed. It may have been processed by another worker or its status changed."
        return
    }

    try {
        $queueGroups = @(Get-QueuedGroups -Connection $Connection -RequestIdValue $requestIdValue)
        $type = ([string]$Request.RequestType).Trim().ToUpperInvariant()
		$initialPasswordPlainText = ''
        switch ($type) {
            'CREATE' {
    $adUser = Invoke-CreateRequest `
        -Request $Request `
        -QueueGroups $queueGroups `
        -InitialPasswordPlainText ([ref]$initialPasswordPlainText)
}
            'UPDATE' { $adUser = Invoke-UpdateRequest -Request $Request -QueueGroups $queueGroups }
            default { throw "Unsupported RequestType '$($Request.RequestType)' for request $requestIdValue." }
        }

        $targetGuid = [Guid]::Empty
        $targetSam = $null
        $finalAdUser = $null
        foreach ($candidate in @($adUser)) {
            if ($null -ne $candidate -and $candidate.PSObject.Properties.Name -contains 'ObjectGUID' -and $candidate.PSObject.Properties.Name -contains 'SamAccountName') {
                $finalAdUser = $candidate
            }
        }

        if ($null -ne $finalAdUser) {
            if ($null -ne $finalAdUser.ObjectGUID) { $targetGuid = [Guid]$finalAdUser.ObjectGUID }
            if ($null -ne $finalAdUser.SamAccountName) { $targetSam = [string]$finalAdUser.SamAccountName }
        }

        if ($type -eq 'CREATE') {
            try {
                Add-CreateRequestEmails -Connection $Connection -Request $Request -AdUser $finalAdUser -InitialPassword $initialPasswordPlainText
            }
            catch {
                Write-Warning "Request $requestIdValue completed AD processing, but failed while queueing welcome/access-card email(s): $($_.Exception.Message)"
            }

            try {
                Add-ServiceDeskPlusQueueItem -Connection $Connection -Request $Request -AdUser $finalAdUser
            }
            catch {
                Write-Warning "Request $requestIdValue completed AD processing, but failed while queueing ServiceDesk Plus requester creation: $($_.Exception.Message)"
            }
        }

        Complete-QueueRequest -Connection $Connection -RequestIdValue $requestIdValue -TargetObjectGuid $targetGuid -TargetSamAccountName $targetSam
        Write-Info "Request $requestIdValue completed."
    }
    catch {
        $message = $_.Exception.Message
        Write-Warning "Request $requestIdValue failed: $message"
        Fail-QueueRequest -Connection $Connection -RequestIdValue $requestIdValue -ErrorMessage $message
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

    if ($IgnoreExecuteAfter -and -not $ForceExecuteAfterOverride) {
        throw "-IgnoreExecuteAfter was supplied, but -ForceExecuteAfterOverride was not. The worker will not bypass ExecuteAfter unless both switches are supplied. Remove -IgnoreExecuteAfter for normal scheduled runs."
    }

    Write-Info "Starting AD user change queue worker. DryRun=$DryRun StatusToProcess=$StatusToProcess BatchSize=$BatchSize IgnoreExecuteAfter=$IgnoreExecuteAfter ForceExecuteAfterOverride=$ForceExecuteAfterOverride CreateLeadDays=$CreateLeadDays GenerateRandomInitialPassword=$GenerateRandomInitialPassword ForcePasswordChangeAtNextLogon=$ForcePasswordChangeAtNextLogon CompletedStatus=$CompletedStatus FailedStatus=$FailedStatus ADServer=$ADServer SkipOfficeLicenseGroup=$SkipOfficeLicenseGroup EnableRemoteMailbox=$EnableRemoteMailbox RemoteRoutingDomainParameter=$RemoteRoutingDomain AllowRemoteMailboxAdAttributeFallback=$AllowRemoteMailboxAdAttributeFallback"

    if (-not ($IgnoreExecuteAfter -and $ForceExecuteAfterOverride)) {
        Write-Info "ExecuteAfter filter is active. CREATE requests are eligible $CreateLeadDays calendar day(s) before ExecuteAfter; UPDATE requests wait until ExecuteAfter is due."
    }
    else {
        Write-Warning "ExecuteAfter filter is OVERRIDDEN because both -IgnoreExecuteAfter and -ForceExecuteAfterOverride were supplied."
    }

    Import-Module ActiveDirectory -ErrorAction Stop

    if (-not (Test-IsBlank $ADServer)) {
        $script:ADServerName = $ADServer.Trim()
    }
    else {
        try {
            $discoveredDc = Get-ADDomainController -Discover -Writable -ErrorAction Stop
            $script:ADServerName = [string]$discoveredDc.HostName
        }
        catch {
            throw "Could not auto-discover a writable domain controller. Pass -ADServer explicitly. $($_.Exception.Message)"
        }
    }

    if (-not (Test-IsBlank $script:ADServerName)) {
        Write-Info "Using AD server/domain controller $script:ADServerName for all AD operations in this run."
    }

    if (-not (Test-IsBlank $InitialPasswordPath)) {
        if (-not (Test-Path -LiteralPath $InitialPasswordPath)) {
            throw "InitialPasswordPath '$InitialPasswordPath' does not exist."
        }
        $script:InitialPassword = Import-Clixml -LiteralPath $InitialPasswordPath
        if ($script:InitialPassword -isnot [System.Security.SecureString]) {
            throw "InitialPasswordPath '$InitialPasswordPath' did not contain a SecureString exported by Export-Clixml."
        }
        Write-Info "Using initial password from $InitialPasswordPath."
    }
    elseif ($GenerateRandomInitialPassword) {
        Write-Info "Initial passwords for enabled CREATE requests will be generated per user and written to $GeneratedPasswordOutputPath."
    }

    $connection = New-SqlConnection
    try {
        Initialize-QueueWorkerDatabaseSettings -Connection $connection
        Initialize-QueueStatusNames -Connection $connection
        Initialize-ADAttributeRules -Connection $connection
        $requests = @(Get-QueueRequests -Connection $connection)
        Write-Info "Found $($requests.Count) queue request(s) to process."

        foreach ($request in $requests) {
            Invoke-QueueRequest -Connection $connection -Request $request
        }
    }
    finally {
        $connection.Close()
        $connection.Dispose()
    }

    Write-Info "Queue worker finished."
}
finally {
    if ($script:TranscriptStarted) {
        Stop-Transcript | Out-Null
    }

    if ($null -ne $script:RandomNumberGenerator) {
        $script:RandomNumberGenerator.Dispose()
    }
}

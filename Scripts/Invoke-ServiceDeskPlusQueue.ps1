<#
.SYNOPSIS
    Processes queued ServiceDesk Plus requester operations.

.DESCRIPTION
    Reads due rows from dbo.ADUserChangeQueueServiceDeskPlus, posts requester
    payloads to the ServiceDesk Plus API, and records success, failure, retries,
    response bodies, and requester IDs independently of AD processing.

.NOTES
    Create the OAuth token file while logged on as the same Windows account that
    runs this worker:

        Read-Host 'ServiceDesk Plus OAuth token' -AsSecureString |
            Export-Clixml 'C:\ProgramData\UserChangeQueueWeb\Secrets\ServiceDeskPlusOAuthToken.clixml'

    The file may contain either the raw token or the complete Authorization value.
#>

[CmdletBinding()]
param(
    [Parameter()]
    [string]$ConnectionString = 'Server=NOR-WUSRMGM01\FMTUSERDB;Database=UserDatabase;Integrated Security=True;TrustServerCertificate=True;',

    [Parameter()]
    [ValidateRange(1,100)]
    [int]$BatchSize = 10,

    [Parameter()]
    [long[]]$IntegrationId,

    [Parameter()]
    [switch]$DryRun,

    [Parameter()]
    [string]$LogPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$script:TranscriptStarted = $false

if ([string]::IsNullOrWhiteSpace($LogPath)) {
    $logDirectory = Join-Path $env:ProgramData 'UserChangeQueueWeb\Logs'
    $LogPath = Join-Path $logDirectory ("ServiceDeskPlusWorker-{0:yyyyMMdd}.log" -f (Get-Date))
}

function Write-Info {
    param([Parameter(Mandatory=$true)][string]$Message)
    Write-Host ("[{0:yyyy-MM-dd HH:mm:ss}] {1}" -f (Get-Date), $Message)
}

function Test-IsBlank {
    param([object]$Value)
    if ($null -eq $Value) { return $true }
    return [string]::IsNullOrWhiteSpace([string]$Value)
}

function Add-SqlParameter {
    param(
        [Parameter(Mandatory=$true)][System.Data.SqlClient.SqlCommand]$Command,
        [Parameter(Mandatory=$true)][string]$Name,
        [Parameter(Mandatory=$true)][System.Data.SqlDbType]$Type,
        [Parameter()]$Value,
        [Parameter()][int]$Size = 0
    )

    $parameter = if ($Size -ne 0) {
        $Command.Parameters.Add($Name, $Type, $Size)
    }
    else {
        $Command.Parameters.Add($Name, $Type)
    }

    $parameter.Value = if ($null -eq $Value) { [DBNull]::Value } else { $Value }
    return $parameter
}

function Get-SettingValue {
    param(
        [Parameter(Mandatory=$true)][System.Data.SqlClient.SqlConnection]$Connection,
        [Parameter(Mandatory=$true)][string]$Name
    )

    $cmd = $Connection.CreateCommand()
    try {
        $cmd.CommandText = @"
SELECT TOP (1) SettingValue
FROM dbo.UserChangeQueueSettings
WHERE SettingName = @Name
  AND Active = 1;
"@
        [void](Add-SqlParameter $cmd '@Name' ([System.Data.SqlDbType]::NVarChar) $Name 100)
        $value = $cmd.ExecuteScalar()
        return $(if ($null -eq $value -or $value -is [DBNull]) { $null } else { [string]$value })
    }
    finally {
        $cmd.Dispose()
    }
}

function Get-IntSettingValue {
    param(
        [Parameter(Mandatory=$true)][System.Data.SqlClient.SqlConnection]$Connection,
        [Parameter(Mandatory=$true)][string]$Name,
        [Parameter(Mandatory=$true)][int]$DefaultValue
    )

    $value = Get-SettingValue -Connection $Connection -Name $Name
    $parsed = 0
    if (-not (Test-IsBlank $value) -and [int]::TryParse([string]$value, [ref]$parsed)) {
        return $parsed
    }
    return $DefaultValue
}

function Join-ApiUrl {
    param(
        [Parameter(Mandatory=$true)][string]$BaseUrl,
        [Parameter(Mandatory=$true)][string]$Endpoint
    )

    return ('{0}/{1}' -f $BaseUrl.TrimEnd('/'), $Endpoint.TrimStart('/'))
}

function ConvertFrom-SecureStringToPlainText {
    param([Parameter(Mandatory=$true)][Security.SecureString]$SecureString)

    $bstr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($SecureString)
    try {
        return [Runtime.InteropServices.Marshal]::PtrToStringBSTR($bstr)
    }
    finally {
        [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr)
    }
}


function Get-StoredSecret {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    if (-not (Test-Path -LiteralPath $Path)) {
        throw "Secret file does not exist: $Path"
    }

    $stored = Import-Clixml -LiteralPath $Path

    if ($stored -is [Security.SecureString]) {
        $plain = ConvertFrom-SecureStringToPlainText -SecureString $stored
    }
    elseif ($stored -is [Management.Automation.PSCredential]) {
        $plain = $stored.GetNetworkCredential().Password
    }
    else {
        $plain = [string]$stored
    }

    if ([string]::IsNullOrWhiteSpace($plain)) {
        throw "Secret file is empty: $Path"
    }

    return $plain.Trim()
}

function Get-AuthorizationHeaderValue {
    param(
        [Parameter(Mandatory = $true)]
        [string]$TokenFile
    )

    $secretsPath = Split-Path -Parent $TokenFile

    $clientIdFile = Join-Path $secretsPath 'ServiceDeskPlusClientId.clixml'
    $clientSecretFile = Join-Path $secretsPath 'ServiceDeskPlusClientSecret.clixml'
    $refreshTokenFile = Join-Path $secretsPath 'ServiceDeskPlusRefreshToken.clixml'

    $clientId = Get-StoredSecret -Path $clientIdFile
    $clientSecret = Get-StoredSecret -Path $clientSecretFile
    $refreshToken = Get-StoredSecret -Path $refreshTokenFile

    try {
        $response = Invoke-RestMethod `
            -Method Post `
            -Uri 'https://accounts.zoho.com/oauth/v2/token' `
            -ContentType 'application/x-www-form-urlencoded' `
            -Body @{
                refresh_token = $refreshToken
                client_id     = $clientId
                client_secret = $clientSecret
                grant_type    = 'refresh_token'
            } `
            -ErrorAction Stop
    }
    catch {
        $errorBody = Read-HttpErrorBody -ErrorRecord $_
        $message = "Failed to obtain ServiceDesk Plus access token: $($_.Exception.Message)"
        if (-not (Test-IsBlank $errorBody)) {
            $message = "$message Response: $errorBody"
        }
        throw $message
    }
    if ([string]::IsNullOrWhiteSpace([string]$response.access_token)) {
        $details = $response | ConvertTo-Json -Depth 5 -Compress
        throw "Zoho token response did not contain an access token. Response: $details"
    }

    return "Zoho-oauthtoken $($response.access_token)"
}

function Read-HttpErrorBody {
    param([Parameter(Mandatory=$true)]$ErrorRecord)

    try {
        $response = $ErrorRecord.Exception.Response
        if ($null -eq $response) { return $null }
        $stream = $response.GetResponseStream()
        if ($null -eq $stream) { return $null }
        $reader = New-Object System.IO.StreamReader($stream)
        try { return $reader.ReadToEnd() }
        finally { $reader.Dispose() }
    }
    catch {
        return $null
    }
}

function Get-SdpRequesterId {
    param($Response)

    if ($null -eq $Response) { return $null }

    if ($Response.PSObject.Properties.Name -contains 'requester' -and $null -ne $Response.requester) {
        if ($Response.requester.PSObject.Properties.Name -contains 'id' -and -not (Test-IsBlank $Response.requester.id)) {
            return [string]$Response.requester.id
        }
    }

    foreach ($propertyName in @('requester_id','id')) {
        if ($Response.PSObject.Properties.Name -contains $propertyName) {
            $value = $Response.$propertyName
            if (-not (Test-IsBlank $value)) { return [string]$value }
        }
    }

    return $null
}

function Claim-ServiceDeskPlusRows {
    param(
        [Parameter(Mandatory=$true)][System.Data.SqlClient.SqlConnection]$Connection,
        [Parameter(Mandatory=$true)][int]$Take,
        [Parameter(Mandatory=$true)][int]$MaxAttempts,
        [Parameter()][long[]]$Ids
    )

    $idFilter = ''
    if ($Ids -and $Ids.Count -gt 0) {
        $names = for ($i = 0; $i -lt $Ids.Count; $i++) { "@IntegrationId$i" }
        $idFilter = "AND q.IntegrationId IN ({0})" -f ($names -join ',')
    }

    $cmd = $Connection.CreateCommand()
    try {
        $cmd.CommandText = @"
;WITH due AS
(
    SELECT TOP (@BatchSize) q.IntegrationId
    FROM dbo.ADUserChangeQueueServiceDeskPlus AS q WITH (UPDLOCK, READPAST, ROWLOCK)
    WHERE q.Operation = N'CreateRequester'
      AND q.Status IN (N'Pending', N'Failed')
      AND q.AttemptCount < @MaxAttempts
      AND (q.NextAttemptAt IS NULL OR q.NextAttemptAt <= SYSDATETIME())
      $idFilter
    ORDER BY COALESCE(q.NextAttemptAt, q.CreatedAt), q.IntegrationId
)
UPDATE q
SET
    Status = N'Processing',
    LastAttemptAt = SYSDATETIME(),
    AttemptCount = AttemptCount + 1,
    UpdatedAt = SYSDATETIME(),
    UpdatedBy = N'Invoke-ServiceDeskPlusQueue.ps1'
OUTPUT
    inserted.IntegrationId,
    inserted.RequestId,
    inserted.EmailAddress,
    inserted.RequesterName,
    inserted.DepartmentName,
    inserted.SiteName,
    inserted.PayloadJson,
    inserted.AttemptCount
FROM dbo.ADUserChangeQueueServiceDeskPlus AS q
INNER JOIN due ON due.IntegrationId = q.IntegrationId;
"@
        [void](Add-SqlParameter $cmd '@BatchSize' ([System.Data.SqlDbType]::Int) $Take)
        [void](Add-SqlParameter $cmd '@MaxAttempts' ([System.Data.SqlDbType]::Int) $MaxAttempts)
        if ($Ids -and $Ids.Count -gt 0) {
            for ($i = 0; $i -lt $Ids.Count; $i++) {
                [void](Add-SqlParameter $cmd "@IntegrationId$i" ([System.Data.SqlDbType]::BigInt) $Ids[$i])
            }
        }

        $rows = @()
        $reader = $cmd.ExecuteReader()
        try {
            while ($reader.Read()) {
                $rows += [pscustomobject]@{
                    IntegrationId = $reader.GetInt64(0)
                    RequestId = $reader.GetInt64(1)
                    EmailAddress = $(if ($reader.IsDBNull(2)) { $null } else { $reader.GetString(2) })
                    RequesterName = $(if ($reader.IsDBNull(3)) { $null } else { $reader.GetString(3) })
                    DepartmentName = $(if ($reader.IsDBNull(4)) { $null } else { $reader.GetString(4) })
                    SiteName = $(if ($reader.IsDBNull(5)) { $null } else { $reader.GetString(5) })
                    PayloadJson = $(if ($reader.IsDBNull(6)) { $null } else { $reader.GetString(6) })
                    AttemptCount = $reader.GetInt32(7)
                }
            }
        }
        finally {
            $reader.Dispose()
        }
        return $rows
    }
    finally {
        $cmd.Dispose()
    }
}

function Complete-ServiceDeskPlusRow {
    param(
        [Parameter(Mandatory=$true)][System.Data.SqlClient.SqlConnection]$Connection,
        [Parameter(Mandatory=$true)][long]$Id,
        [Parameter()]$RequesterId,
        [Parameter()]$ResponseJson
    )

    $cmd = $Connection.CreateCommand()
    try {
        $cmd.CommandText = @"
UPDATE dbo.ADUserChangeQueueServiceDeskPlus
SET
    Status = N'Succeeded',
    SdpRequesterId = @RequesterId,
    ResponseJson = @ResponseJson,
    LastError = NULL,
    NextAttemptAt = NULL,
    CompletedAt = SYSDATETIME(),
    UpdatedAt = SYSDATETIME(),
    UpdatedBy = N'Invoke-ServiceDeskPlusQueue.ps1'
WHERE IntegrationId = @IntegrationId;
"@
        [void](Add-SqlParameter $cmd '@IntegrationId' ([System.Data.SqlDbType]::BigInt) $Id)
        [void](Add-SqlParameter $cmd '@RequesterId' ([System.Data.SqlDbType]::NVarChar) $RequesterId 100)
        [void](Add-SqlParameter $cmd '@ResponseJson' ([System.Data.SqlDbType]::NVarChar) $ResponseJson -1)
        [void]$cmd.ExecuteNonQuery()
    }
    finally { $cmd.Dispose() }
}

function Fail-ServiceDeskPlusRow {
    param(
        [Parameter(Mandatory=$true)][System.Data.SqlClient.SqlConnection]$Connection,
        [Parameter(Mandatory=$true)][long]$Id,
        [Parameter(Mandatory=$true)][string]$Message,
        [Parameter()]$ResponseJson,
        [Parameter(Mandatory=$true)][int]$RetryMinutes,
        [Parameter(Mandatory=$true)][int]$AttemptCount,
        [Parameter(Mandatory=$true)][int]$MaxAttempts
    )

    $nextAttemptAt = if ($AttemptCount -lt $MaxAttempts) { (Get-Date).AddMinutes($RetryMinutes) } else { $null }
    $cmd = $Connection.CreateCommand()
    try {
        $cmd.CommandText = @"
UPDATE dbo.ADUserChangeQueueServiceDeskPlus
SET
    Status = N'Failed',
    ResponseJson = @ResponseJson,
    LastError = @LastError,
    NextAttemptAt = @NextAttemptAt,
    UpdatedAt = SYSDATETIME(),
    UpdatedBy = N'Invoke-ServiceDeskPlusQueue.ps1'
WHERE IntegrationId = @IntegrationId;
"@
        [void](Add-SqlParameter $cmd '@IntegrationId' ([System.Data.SqlDbType]::BigInt) $Id)
        [void](Add-SqlParameter $cmd '@ResponseJson' ([System.Data.SqlDbType]::NVarChar) $ResponseJson -1)
        [void](Add-SqlParameter $cmd '@LastError' ([System.Data.SqlDbType]::NVarChar) $Message -1)
        [void](Add-SqlParameter $cmd '@NextAttemptAt' ([System.Data.SqlDbType]::DateTime2) $nextAttemptAt)
        [void]$cmd.ExecuteNonQuery()
    }
    finally { $cmd.Dispose() }
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

    $connection = New-Object System.Data.SqlClient.SqlConnection($ConnectionString)
    $connection.Open()
    try {
        $enabled = Get-SettingValue -Connection $connection -Name 'ServiceDeskPlusEnabled'
        if ((Test-IsBlank $enabled) -or ([string]$enabled).Trim() -notin @('1','true','yes','on')) {
            Write-Info 'ServiceDesk Plus integration is disabled. Nothing to process.'
            return
        }

        $baseUrl = Get-SettingValue -Connection $connection -Name 'ServiceDeskPlusBaseUrl'
        $endpoint = Get-SettingValue -Connection $connection -Name 'ServiceDeskPlusRequesterEndpoint'
        $tokenFile = Get-SettingValue -Connection $connection -Name 'ServiceDeskPlusOAuthTokenFile'
        $maxAttempts = Get-IntSettingValue -Connection $connection -Name 'ServiceDeskPlusMaxAttempts' -DefaultValue 10
        $retryMinutes = Get-IntSettingValue -Connection $connection -Name 'ServiceDeskPlusRetryMinutes' -DefaultValue 15

        if (Test-IsBlank $baseUrl) { throw 'ServiceDeskPlusBaseUrl is not configured.' }
        if (Test-IsBlank $endpoint) { throw 'ServiceDeskPlusRequesterEndpoint is not configured.' }
        if (Test-IsBlank $tokenFile) { throw 'ServiceDeskPlusOAuthTokenFile is not configured.' }

        $apiUrl = Join-ApiUrl -BaseUrl $baseUrl -Endpoint $endpoint
        $authorization = Get-AuthorizationHeaderValue -TokenFile $tokenFile
        $headers = @{
            Authorization = $authorization
            Accept = 'application/vnd.manageengine.sdp.v3+json'
        }

        $rows = @(Claim-ServiceDeskPlusRows -Connection $connection -Take $BatchSize -MaxAttempts $maxAttempts -Ids $IntegrationId)
        if ($rows.Count -eq 0) {
            Write-Info 'No due ServiceDesk Plus queue rows were found.'
            return
        }

        foreach ($row in $rows) {
            Write-Info "Processing SDP integration $($row.IntegrationId) for request $($row.RequestId), email '$($row.EmailAddress)', attempt $($row.AttemptCount)/$maxAttempts."

            if ($DryRun) {
                Write-Info "DRYRUN: would POST to $apiUrl with payload $($row.PayloadJson)"
                Fail-ServiceDeskPlusRow -Connection $connection -Id $row.IntegrationId -Message 'Dry run; no API call was made.' -ResponseJson $null -RetryMinutes 0 -AttemptCount $row.AttemptCount -MaxAttempts ($row.AttemptCount + 1)
                continue
            }

            try {
                if (Test-IsBlank $row.PayloadJson) {
                    throw "Queue row $($row.IntegrationId) has no PayloadJson."
                }

                # ServiceDesk Plus Cloud currently rejects the department and site fields
                # when creating requesters. Keep them in the queued payload for future use,
                # but remove them only from the outbound API request.
                $outgoingPayload = $row.PayloadJson | ConvertFrom-Json
                if ($null -ne $outgoingPayload.requester) {
                    foreach ($propertyName in @('department', 'site')) {
                        if ($null -ne $outgoingPayload.requester.PSObject.Properties[$propertyName]) {
                            $outgoingPayload.requester.PSObject.Properties.Remove($propertyName)
                        }
                    }
                }

                $outgoingPayloadJson = $outgoingPayload | ConvertTo-Json -Depth 20 -Compress

                $body = @{ input_data = $outgoingPayloadJson }

                $response = Invoke-RestMethod `
                    -Uri $apiUrl `
                    -Method Post `
                    -Headers $headers `
                    -ContentType 'application/x-www-form-urlencoded' `
                    -Body $body `
                    -ErrorAction Stop
                $responseJson = $response | ConvertTo-Json -Depth 20 -Compress
                $requesterId = Get-SdpRequesterId -Response $response

                Complete-ServiceDeskPlusRow -Connection $connection -Id $row.IntegrationId -RequesterId $requesterId -ResponseJson $responseJson
                Write-Info "SDP integration $($row.IntegrationId) succeeded. Requester id: $(if (Test-IsBlank $requesterId) { '<not returned>' } else { $requesterId })."
            }
            catch {
                $errorBody = Read-HttpErrorBody -ErrorRecord $_
                $message = $_.Exception.Message

                if (-not (Test-IsBlank $errorBody)) {
                    $message = "$message Response: $errorBody"
                }

                Fail-ServiceDeskPlusRow `
                    -Connection $connection `
                    -Id $row.IntegrationId `
                    -Message $message `
                    -ResponseJson $errorBody `
                    -RetryMinutes $retryMinutes `
                    -AttemptCount $row.AttemptCount `
                    -MaxAttempts $maxAttempts

                Write-Warning "SDP integration $($row.IntegrationId) failed: $($_.Exception.Message)"
                if (-not (Test-IsBlank $errorBody)) {
                    Write-Warning "ServiceDesk Plus response: $errorBody"
                }
            }
        }
    }
    finally {
        $connection.Dispose()
    }
}
finally {
    if ($script:TranscriptStarted) {
        try { Stop-Transcript | Out-Null } catch { }
    }
}

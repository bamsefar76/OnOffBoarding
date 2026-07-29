[CmdletBinding()]
param(
    [string]$AppSettingsPath = 'C:\inetpub\UserChangeQueueWeb\appsettings.json',
    [string]$ConnectionString,
    [int]$MaxItems = 100,
    [string]$LogPath = 'C:\ProgramData\UserChangeQueueWeb\Logs\TemporaryAccess.log'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Write-Log {
    param([string]$Message, [ValidateSet('INFO','WARNING','ERROR')] [string]$Level = 'INFO')
    $line = '[{0:yyyy-MM-dd HH:mm:ss}] [{1}] {2}' -f (Get-Date), $Level, $Message
    Write-Host $line
    $folder = Split-Path -Parent $LogPath
    if ($folder -and -not (Test-Path -LiteralPath $folder)) { New-Item -ItemType Directory -Path $folder -Force | Out-Null }
    Add-Content -LiteralPath $LogPath -Value $line -Encoding UTF8
}

function Get-DatabaseConnectionString {
    if (-not [string]::IsNullOrWhiteSpace($ConnectionString)) { return $ConnectionString }
    if (-not (Test-Path -LiteralPath $AppSettingsPath -PathType Leaf)) { throw "appsettings.json was not found at '$AppSettingsPath'." }
    $settings = Get-Content -LiteralPath $AppSettingsPath -Raw | ConvertFrom-Json
    $value = $settings.ConnectionStrings.UserDatabase
    if ([string]::IsNullOrWhiteSpace($value)) { throw "ConnectionStrings:UserDatabase is missing from '$AppSettingsPath'." }
    return [string]$value
}

function Add-Parameters {
    param([System.Data.SqlClient.SqlCommand]$Command,[hashtable]$Parameters)
    foreach ($entry in $Parameters.GetEnumerator()) {
        $value = if ($null -eq $entry.Value) { [DBNull]::Value } else { $entry.Value }
        [void]$Command.Parameters.AddWithValue($entry.Key, $value)
    }
}

function Invoke-SqlNonQuery {
    param([System.Data.SqlClient.SqlConnection]$Connection,[string]$Sql,[hashtable]$Parameters=@{})
    $cmd=$Connection.CreateCommand()
    try { $cmd.CommandText=$Sql; Add-Parameters $cmd $Parameters; return $cmd.ExecuteNonQuery() }
    finally { $cmd.Dispose() }
}

function Invoke-SqlScalar {
    param([System.Data.SqlClient.SqlConnection]$Connection,[string]$Sql,[hashtable]$Parameters=@{})
    $cmd=$Connection.CreateCommand()
    try { $cmd.CommandText=$Sql; Add-Parameters $cmd $Parameters; return $cmd.ExecuteScalar() }
    finally { $cmd.Dispose() }
}

function Normalize-LanguageCode {
    param([object]$Value)
    if ($null -eq $Value) { return 'en' }
    $v=([string]$Value).Trim().ToLowerInvariant().Replace('_','-')
    if ($v.StartsWith('nb') -or $v.StartsWith('nn') -or $v.StartsWith('no')) { return 'nb' }
    if ($v.StartsWith('sv') -or $v.StartsWith('se')) { return 'sv' }
    if ($v.StartsWith('da') -or $v.StartsWith('dk')) { return 'da' }
    if ($v.StartsWith('fi')) { return 'fi' }
    if ($v.StartsWith('nl')) { return 'nl' }
    if ($v.StartsWith('fr')) { return 'fr' }
    return 'en'
}

function Get-Setting {
    param([System.Data.SqlClient.SqlConnection]$Connection,[string]$Name)
    $v=Invoke-SqlScalar $Connection 'SELECT TOP(1) SettingValue FROM dbo.UserChangeQueueSettings WHERE SettingName=@Name AND Active=1;' @{ '@Name'=$Name }
    if ($null -eq $v -or $v -is [DBNull]) { return $null }
    return [string]$v
}

function Get-Template {
    param([System.Data.SqlClient.SqlConnection]$Connection,[string]$TemplateName,[string]$LanguageCode)
    $cmd=$Connection.CreateCommand()
    try {
        $cmd.CommandText=@'
SELECT TOP(1) Subject,HtmlBody,PlainTextBody,LanguageCode
FROM dbo.EmailTemplates
WHERE TemplateName=@TemplateName AND Active=1 AND LanguageCode IN(@LanguageCode,N'en')
ORDER BY CASE WHEN LanguageCode=@LanguageCode THEN 0 ELSE 1 END;
'@
        Add-Parameters $cmd @{ '@TemplateName'=$TemplateName; '@LanguageCode'=$LanguageCode }
        $r=$cmd.ExecuteReader()
        try {
            if (-not $r.Read()) { throw "No active email template '$TemplateName' exists for '$LanguageCode' or English." }
            return [pscustomobject]@{ Subject=$r.GetString(0); HtmlBody=$r.GetString(1); PlainTextBody=if($r.IsDBNull(2)){$null}else{$r.GetString(2)}; LanguageCode=$r.GetString(3) }
        } finally { $r.Dispose() }
    } finally { $cmd.Dispose() }
}

function Expand-Tokens {
    param([string]$Text,[hashtable]$Tokens)
    if ($null -eq $Text) { return $null }
    $result=$Text
    foreach($k in $Tokens.Keys){ $result=$result.Replace('{' + $k + '}', [string]$Tokens[$k]) }
    return $result
}

function Queue-Notification {
    param(
        [System.Data.SqlClient.SqlConnection]$Connection,
        [long]$MembershipId,[string]$EventType,[string]$RecipientType,
        [string]$ToEmail,[string]$ToName,[string]$LanguageCode,[string]$TemplateName,[hashtable]$Tokens
    )
    if ([string]::IsNullOrWhiteSpace($ToEmail)) { return }
    $template=Get-Template $Connection $TemplateName $LanguageCode
    $subject=Expand-Tokens $template.Subject $Tokens
    $html=Expand-Tokens $template.HtmlBody $Tokens
    $plain=Expand-Tokens $template.PlainTextBody $Tokens
    $domain = $null
    $at = $ToEmail.LastIndexOf('@')
    if ($at -ge 0 -and $at -lt ($ToEmail.Length - 1)) { $domain = $ToEmail.Substring($at + 1).ToLowerInvariant() }
    $correlationKey = 'TemporaryAccess:{0}:{1}:{2}:{3}' -f $MembershipId,$EventType,$RecipientType,$ToEmail.ToLowerInvariant()

    [void](Invoke-SqlNonQuery $Connection @'
IF NOT EXISTS
(
    SELECT 1
    FROM dbo.ADUserChangeQueueEmails
    WHERE CorrelationKey=@CorrelationKey
      AND Status<>N'Cancelled'
)
BEGIN
    INSERT INTO dbo.ADUserChangeQueueEmails
    (
        RequestId,SourceType,SourceId,EmailType,RecipientType,TemplateName,LanguageCode,Domain,CorrelationKey,
        ToEmail,ToName,Subject,BodyHtml,BodyText,EarliestSendAt,Status,CreatedBy
    )
    VALUES
    (
        NULL,N'TemporaryAccess',@MembershipId,@TemplateName,@RecipientType,@TemplateName,@LanguageCode,@Domain,@CorrelationKey,
        @ToEmail,@ToName,@Subject,@BodyHtml,@BodyText,SYSDATETIME(),N'Pending',N'Invoke-TemporaryAccessQueue.ps1'
    );
END;
'@ @{ '@MembershipId'=$MembershipId;'@RecipientType'=$RecipientType;'@ToEmail'=$ToEmail;'@ToName'=$ToName;'@LanguageCode'=$template.LanguageCode;'@TemplateName'=$TemplateName;'@Domain'=$domain;'@CorrelationKey'=$correlationKey;'@Subject'=$subject;'@BodyHtml'=$html;'@BodyText'=$plain })
    Write-Log "Queued $TemplateName email for '$ToEmail' in the shared email queue."
}

function Get-UserAndManager {
    param([string]$SamAccountName)
    Import-Module ActiveDirectory -ErrorAction Stop
    $u=Get-ADUser -Identity $SamAccountName -Properties mail,displayName,preferredLanguage,manager,userPrincipalName -ErrorAction Stop
    $m=$null
    if (-not [string]::IsNullOrWhiteSpace([string]$u.Manager)) {
        try { $m=Get-ADUser -Identity $u.Manager -Properties mail,displayName,preferredLanguage,userPrincipalName -ErrorAction Stop } catch { Write-Log "Could not resolve manager for '$SamAccountName': $($_.Exception.Message)" 'WARNING' }
    }
    return [pscustomobject]@{ User=$u; Manager=$m }
}

function Claim-WorkItem {
    param([System.Data.SqlClient.SqlConnection]$Connection)
    $lockId=[guid]::NewGuid(); $cmd=$Connection.CreateCommand()
    try {
        $cmd.CommandText=@'
SET NOCOUNT ON; DECLARE @Now datetime2(0)=SYSDATETIME();
;WITH Candidate AS(
 SELECT TOP(1)m.Id FROM dbo.TemporaryGroupMemberships m WITH(UPDLOCK,READPAST,ROWLOCK)
 WHERE m.Status IN(N'PendingAdd',N'PendingRemove') OR(m.Status=N'Active' AND m.ExpiresAt<=@Now)
 OR(m.Status IN(N'ProcessingAdd',N'ProcessingRemove') AND m.WorkerLockedAt<DATEADD(MINUTE,-30,@Now))
 ORDER BY CASE WHEN m.Status=N'Active' AND m.ExpiresAt<=@Now THEN 0 ELSE 1 END,m.Id)
UPDATE m SET Status=CASE WHEN m.Status IN(N'PendingAdd',N'ProcessingAdd') THEN N'ProcessingAdd' ELSE N'ProcessingRemove' END,
 WorkerLockId=@LockId,WorkerLockedAt=@Now,LastAttemptAt=@Now,AttemptCount=AttemptCount+1,UpdatedAt=@Now
OUTPUT INSERTED.Id,INSERTED.Status,INSERTED.UserSamAccountName,INSERTED.MembershipAddedBySystem,
 INSERTED.ExpiresAt,INSERTED.CancelledAt,INSERTED.Reason,g.AdGroupName,g.DisplayName
FROM dbo.TemporaryGroupMemberships m JOIN Candidate c ON c.Id=m.Id JOIN dbo.TemporaryAccessGroups g ON g.Id=m.TemporaryAccessGroupId;
'@
        [void]$cmd.Parameters.Add('@LockId',[System.Data.SqlDbType]::UniqueIdentifier);$cmd.Parameters['@LockId'].Value=$lockId
        $r=$cmd.ExecuteReader();try{
            if(-not $r.Read()){return $null}
            return [pscustomobject]@{Id=$r.GetInt64(0);Status=$r.GetString(1);UserSamAccountName=$r.GetString(2);MembershipAddedBySystem=$r.GetBoolean(3);ExpiresAt=$r.GetDateTime(4);CancelledAt=if($r.IsDBNull(5)){$null}else{$r.GetDateTime(5)};Reason=if($r.IsDBNull(6)){''}else{$r.GetString(6)};AdGroupName=$r.GetString(7);DisplayName=$r.GetString(8);LockId=$lockId}
        }finally{$r.Dispose()}
    }finally{$cmd.Dispose()}
}

function Add-EventEmails {
    param([System.Data.SqlClient.SqlConnection]$Connection,$Item,[string]$EventType,$Identity)
    $user=$Identity.User;$manager=$Identity.Manager
    $date=if($EventType -eq 'Granted'){$Item.ExpiresAt}else{Get-Date}
    $tokens=@{ UserDisplayName=[string]$user.DisplayName; UserSamAccountName=$Item.UserSamAccountName; GroupDisplayName=$Item.DisplayName; AdGroupName=$Item.AdGroupName; EventDate=$date.ToString('g'); Reason=if([string]::IsNullOrWhiteSpace($Item.Reason)){'-'}else{$Item.Reason} }
    Queue-Notification $Connection $Item.Id $EventType 'User' ([string]$user.Mail) ([string]$user.DisplayName) (Normalize-LanguageCode $user.PreferredLanguage) ("TemporaryAccess${EventType}User") $tokens
    if($null -ne $manager){ Queue-Notification $Connection $Item.Id $EventType 'Manager' ([string]$manager.Mail) ([string]$manager.DisplayName) (Normalize-LanguageCode $manager.PreferredLanguage) ("TemporaryAccess${EventType}Manager") $tokens }
}

function Complete-Add {
    param([System.Data.SqlClient.SqlConnection]$Connection,$Item)
    $identity=Get-UserAndManager $Item.UserSamAccountName; $user=$identity.User
    $group=Get-ADGroup -Identity $Item.AdGroupName -ErrorAction Stop
    $alreadyMember=[bool](Get-ADGroupMember -Identity $group -Recursive:$false | Where-Object{$_.DistinguishedName -eq $user.DistinguishedName}|Select-Object -First 1)
    $added=$false;if(-not $alreadyMember){Add-ADGroupMember -Identity $group -Members $user -ErrorAction Stop;$added=$true}
    [void](Invoke-SqlNonQuery $Connection @'
UPDATE dbo.TemporaryGroupMemberships SET Status=N'Active',StartsAt=COALESCE(StartsAt,SYSDATETIME()),
 AddedAt=CASE WHEN @Added=1 THEN SYSDATETIME() ELSE AddedAt END,WasMemberBefore=@Before,MembershipAddedBySystem=@Added,
 LastError=NULL,WorkerLockId=NULL,WorkerLockedAt=NULL,UpdatedAt=SYSDATETIME() WHERE Id=@Id AND WorkerLockId=@LockId;
'@ @{ '@Id'=$Item.Id;'@LockId'=$Item.LockId;'@Before'=$alreadyMember;'@Added'=$added })
    Add-EventEmails $Connection $Item 'Granted' $identity
    if($alreadyMember){Write-Log "'$($Item.UserSamAccountName)' was already a member of '$($Item.AdGroupName)'; existing membership will not be removed."}else{Write-Log "Added '$($Item.UserSamAccountName)' to '$($Item.AdGroupName)'."}
}

function Complete-Remove {
    param([System.Data.SqlClient.SqlConnection]$Connection,$Item)
    $identity=Get-UserAndManager $Item.UserSamAccountName
    if($Item.MembershipAddedBySystem){$group=Get-ADGroup -Identity $Item.AdGroupName -ErrorAction Stop;Remove-ADGroupMember -Identity $group -Members $identity.User -Confirm:$false -ErrorAction Stop;Write-Log "Removed '$($Item.UserSamAccountName)' from '$($Item.AdGroupName)'."}
    else{Write-Log "Did not remove '$($Item.UserSamAccountName)' from '$($Item.AdGroupName)' because this system did not add it."}
    $event=if($null -eq $Item.CancelledAt){'Expired'}else{'Removed'}
    [void](Invoke-SqlNonQuery $Connection @'
UPDATE dbo.TemporaryGroupMemberships SET Status=CASE WHEN CancelledAt IS NULL THEN N'Expired' ELSE N'Cancelled' END,
 RemovedAt=CASE WHEN @Added=1 THEN SYSDATETIME() ELSE RemovedAt END,LastError=NULL,WorkerLockId=NULL,WorkerLockedAt=NULL,UpdatedAt=SYSDATETIME()
WHERE Id=@Id AND WorkerLockId=@LockId;
'@ @{ '@Id'=$Item.Id;'@LockId'=$Item.LockId;'@Added'=$Item.MembershipAddedBySystem })
    Add-EventEmails $Connection $Item $event $identity
}

function Fail-Item { param([System.Data.SqlClient.SqlConnection]$Connection,$Item,[string]$Message)
 [void](Invoke-SqlNonQuery $Connection "UPDATE dbo.TemporaryGroupMemberships SET Status=N'Failed',LastError=@Error,WorkerLockId=NULL,WorkerLockedAt=NULL,UpdatedAt=SYSDATETIME() WHERE Id=@Id AND WorkerLockId=@LockId;" @{ '@Id'=$Item.Id;'@LockId'=$Item.LockId;'@Error'=$Message }) }

try {
    $connection=[System.Data.SqlClient.SqlConnection]::new((Get-DatabaseConnectionString));$connection.Open()
    try {
        $processed=0
        while($processed -lt $MaxItems){$item=Claim-WorkItem $connection;if($null -eq $item){break};try{if($item.Status -eq 'ProcessingAdd'){Complete-Add $connection $item}else{Complete-Remove $connection $item}}catch{$m=$_.Exception.Message;Fail-Item $connection $item $m;Write-Log "Failed membership $($item.Id): $m" 'ERROR'};$processed++}
        Write-Log "Temporary access worker completed. Processed $processed membership item(s). Email delivery is handled by the shared email worker."
    } finally {$connection.Dispose()}
} catch {Write-Log $_.Exception.Message 'ERROR';exit 1}

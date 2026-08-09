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


function Get-LicenseTemplate {
    param(
        [System.Data.SqlClient.SqlConnection]$Connection,
        [string]$TemplateName,
        [string]$LanguageCode,
        [string]$Domain
    )
    $cmd=$Connection.CreateCommand()
    try {
        $cmd.CommandText=@'
SELECT TOP(1) Subject,HtmlBody,PlainTextBody,LanguageCode
FROM dbo.EmailTemplates
WHERE TemplateName=@TemplateName
  AND Active=1
  AND LOWER(LTRIM(RTRIM(Domain))) IN (LOWER(@Domain),N'*')
  AND LOWER(LTRIM(RTRIM(LanguageCode))) IN (LOWER(@LanguageCode),N'en')
ORDER BY
  CASE WHEN LOWER(LTRIM(RTRIM(Domain)))=LOWER(@Domain) THEN 0 ELSE 1 END,
  CASE WHEN LOWER(LTRIM(RTRIM(LanguageCode)))=LOWER(@LanguageCode) THEN 0 ELSE 1 END,
  COALESCE(UpdatedAt,CreatedAt) DESC,
  Id DESC;
'@
        Add-Parameters $cmd @{ '@TemplateName'=$TemplateName; '@LanguageCode'=$LanguageCode; '@Domain'=$Domain }
        $r=$cmd.ExecuteReader()
        try {
            if(-not $r.Read()){ throw "No active email template '$TemplateName' matched domain '$Domain' and language '$LanguageCode'." }
            return [pscustomobject]@{
                Subject=$r.GetString(0)
                HtmlBody=$r.GetString(1)
                PlainTextBody=if($r.IsDBNull(2)){$null}else{$r.GetString(2)}
                LanguageCode=$r.GetString(3)
            }
        } finally { $r.Dispose() }
    } finally { $cmd.Dispose() }
}

function Queue-LicenseNotification {
    param(
        [System.Data.SqlClient.SqlConnection]$Connection,
        [long]$ApplicationId,
        [string]$ToEmail,
        [string]$ToName,
        [string]$TemplateName,
        [hashtable]$Tokens,
        [hashtable]$HtmlTokenOverrides=@{},
        [string]$RecipientType='User'
    )
    if([string]::IsNullOrWhiteSpace($ToEmail)){ return }
    $domain='*'
    $at=$ToEmail.LastIndexOf('@')
    if($at -ge 0 -and $at -lt ($ToEmail.Length-1)){ $domain=$ToEmail.Substring($at+1).ToLowerInvariant() }
    $language=Normalize-LanguageCode (Get-Setting $Connection 'EmailTemplateLanguage')
    $template=Get-LicenseTemplate $Connection $TemplateName $language $domain
    $raw=@{}
    $html=@{}
    foreach($k in $Tokens.Keys){
        $raw[$k]=[string]$Tokens[$k]
        $html[$k]=[System.Net.WebUtility]::HtmlEncode([string]$Tokens[$k])
    }
    foreach($k in $HtmlTokenOverrides.Keys){ $html[$k]=[string]$HtmlTokenOverrides[$k] }
    $subject=Expand-Tokens $template.Subject $raw
    $htmlBody=Expand-Tokens $template.HtmlBody $html
    $plain=Expand-Tokens $template.PlainTextBody $raw
    $correlationKey='LicenseRequest:{0}:{1}:{2}' -f $ApplicationId,$TemplateName,$ToEmail.ToLowerInvariant()

    [void](Invoke-SqlNonQuery $Connection @'
IF NOT EXISTS
(
    SELECT 1 FROM dbo.ADUserChangeQueueEmails
    WHERE CorrelationKey=@CorrelationKey AND Status<>N'Cancelled'
)
BEGIN
    INSERT INTO dbo.ADUserChangeQueueEmails
    (
        RequestId,SourceType,SourceId,EmailType,RecipientType,TemplateName,LanguageCode,Domain,CorrelationKey,
        ToEmail,ToName,Subject,BodyHtml,BodyText,EarliestSendAt,Status,CreatedBy
    )
    VALUES
    (
        NULL,N'LicenseRequest',@ApplicationId,@TemplateName,@RecipientType,@TemplateName,@LanguageCode,@Domain,@CorrelationKey,
        @ToEmail,@ToName,@Subject,@BodyHtml,@BodyText,SYSDATETIME(),N'Pending',N'Invoke-TemporaryAccessQueue.ps1'
    );
END;
'@ @{
        '@ApplicationId'=$ApplicationId; '@TemplateName'=$TemplateName; '@RecipientType'=$RecipientType; '@LanguageCode'=$template.LanguageCode;
        '@Domain'=$domain; '@CorrelationKey'=$correlationKey; '@ToEmail'=$ToEmail; '@ToName'=$ToName;
        '@Subject'=$subject; '@BodyHtml'=$htmlBody; '@BodyText'=$plain
    })
    Write-Log "Queued $TemplateName email for license application $ApplicationId to '$ToEmail'."
}


function Activate-AssignmentLicenseRequest {
    param([System.Data.SqlClient.SqlConnection]$Connection)

    $cmd=$Connection.CreateCommand()
    try {
        $cmd.CommandText=@'
SET NOCOUNT ON;
SET XACT_ABORT ON;

DECLARE @Now datetime2(0)=SYSDATETIME();
DECLARE @RequestId bigint;
DECLARE @ApplicationId bigint;
DECLARE @UserSam nvarchar(256);
DECLARE @UserName nvarchar(300);
DECLARE @UserEmail nvarchar(320);
DECLARE @ManagerSam nvarchar(256);
DECLARE @ManagerName nvarchar(300);
DECLARE @ManagerEmail nvarchar(320);
DECLARE @BusinessReason nvarchar(2000);

BEGIN TRANSACTION;

SELECT TOP(1) @RequestId=selection.RequestId
FROM dbo.AssignmentLicenseSelections selection WITH(UPDLOCK,READPAST,ROWLOCK)
INNER JOIN dbo.ADUserChangeQueue queueItem
    ON queueItem.RequestId=selection.RequestId
WHERE selection.LicenseApplicationId IS NULL
  AND (selection.LastAttemptAt IS NULL OR selection.LastAttemptAt<DATEADD(MINUTE,-5,@Now))
  AND UPPER(LTRIM(RTRIM(ISNULL(queueItem.Status,N'')))) IN (N'AUTO',N'IMPLEMENTED',N'COMPLETED',N'DONE')
ORDER BY selection.RequestId;

IF @RequestId IS NULL
BEGIN
    COMMIT TRANSACTION;
    SELECT CAST(NULL AS bigint) AS ApplicationId,
           CAST(NULL AS bigint) AS RequestId,
           CAST(NULL AS nvarchar(2000)) AS ErrorMessage
    WHERE 1=0;
    RETURN;
END;

UPDATE dbo.AssignmentLicenseSelections
SET LastAttemptAt=@Now,
    LastError=NULL
WHERE RequestId=@RequestId
  AND LicenseApplicationId IS NULL;

SELECT
    @UserSam=COALESCE(NULLIF(LTRIM(RTRIM(queueItem.TargetSamAccountName)),N''),NULLIF(LTRIM(RTRIM(queueItem.NewSamAccountName)),N'')),
    @UserName=COALESCE(NULLIF(LTRIM(RTRIM(queueItem.NewDisplayName)),N''),NULLIF(LTRIM(RTRIM(queueItem.TargetSamAccountName)),N''),NULLIF(LTRIM(RTRIM(queueItem.NewSamAccountName)),N''),N''),
    @UserEmail=COALESCE(NULLIF(LTRIM(RTRIM(queueItem.Mail)),N''),NULLIF(LTRIM(RTRIM(queueItem.NewUserPrincipalName)),N''),N''),
    @ManagerSam=NULLIF(LTRIM(RTRIM(queueItem.ManagerSamAccountName)),N''),
    @BusinessReason=MAX(selection.BusinessReason)
FROM dbo.ADUserChangeQueue queueItem
INNER JOIN dbo.AssignmentLicenseSelections selection
    ON selection.RequestId=queueItem.RequestId
WHERE queueItem.RequestId=@RequestId
GROUP BY queueItem.TargetSamAccountName,queueItem.NewSamAccountName,queueItem.NewDisplayName,
         queueItem.Mail,queueItem.NewUserPrincipalName,queueItem.ManagerSamAccountName;

SELECT TOP(1)
    @ManagerName=COALESCE(NULLIF(LTRIM(RTRIM(manager.DisplayName)),N''),@ManagerSam),
    @ManagerEmail=NULLIF(LTRIM(RTRIM(manager.Mail)),N'')
FROM dbo.ADObjects manager
WHERE manager.SamAccountName=@ManagerSam
  AND ISNULL(manager.IsDeleted,0)=0;

IF NULLIF(@UserSam,N'') IS NULL OR NULLIF(@ManagerSam,N'') IS NULL OR NULLIF(@ManagerEmail,N'') IS NULL
BEGIN
    DECLARE @Error nvarchar(2000)=CONCAT(
        N'Assignment license activation is waiting for required identity data. UserSam=',COALESCE(@UserSam,N'<missing>'),
        N'; ManagerSam=',COALESCE(@ManagerSam,N'<missing>'),
        N'; ManagerEmail=',COALESCE(@ManagerEmail,N'<missing>'));

    UPDATE dbo.AssignmentLicenseSelections
    SET LastError=@Error
    WHERE RequestId=@RequestId
      AND LicenseApplicationId IS NULL;

    COMMIT TRANSACTION;
    SELECT CAST(NULL AS bigint) AS ApplicationId,@RequestId AS RequestId,@Error AS ErrorMessage;
    RETURN;
END;

SELECT @ApplicationId=LicenseApplicationId
FROM dbo.LicenseApplications WITH(UPDLOCK,HOLDLOCK)
WHERE SourceQueueRequestId=@RequestId;

IF @ApplicationId IS NULL
BEGIN
    INSERT INTO dbo.LicenseApplications
    (
        RequestedForSamAccountName,
        RequestedForDisplayName,
        RequestedForEmail,
        ManagerSamAccountName,
        ManagerDisplayName,
        ManagerEmail,
        BusinessReason,
        Status,
        SubmittedAt,
        SourceQueueRequestId
    )
    VALUES
    (
        @UserSam,@UserName,@UserEmail,@ManagerSam,@ManagerName,@ManagerEmail,
        @BusinessReason,N'AwaitingManager',@Now,@RequestId
    );
    SET @ApplicationId=SCOPE_IDENTITY();
END;

INSERT INTO dbo.LicenseApplicationItems
(
    LicenseApplicationId,
    LicenseProductId,
    Status,
    FulfillmentType,
    AdGroupName,
    ProvisioningStatus
)
SELECT
    @ApplicationId,
    product.LicenseProductId,
    N'Pending',
    ISNULL(NULLIF(selection.FulfillmentType,N''),N'Manual'),
    NULLIF(selection.AdGroupName,N''),
    NULL
FROM dbo.AssignmentLicenseSelections selection
INNER JOIN dbo.LicenseProducts product
    ON product.LicenseProductId=selection.LicenseProductId
WHERE selection.RequestId=@RequestId
  AND NOT EXISTS
  (
      SELECT 1
      FROM dbo.LicenseApplicationItems existing
      WHERE existing.LicenseApplicationId=@ApplicationId
        AND existing.LicenseProductId=product.LicenseProductId
  );

UPDATE dbo.AssignmentLicenseSelections
SET LicenseApplicationId=@ApplicationId,
    ActivatedAt=COALESCE(ActivatedAt,@Now),
    LastError=NULL
WHERE RequestId=@RequestId;

COMMIT TRANSACTION;
SELECT @ApplicationId AS ApplicationId,@RequestId AS RequestId,CAST(NULL AS nvarchar(2000)) AS ErrorMessage;
'@
        $r=$cmd.ExecuteReader()
        try {
            if(-not $r.Read()){ return $null }
            return [pscustomobject]@{
                ApplicationId=if($r.IsDBNull(0)){$null}else{$r.GetInt64(0)}
                RequestId=$r.GetInt64(1)
                ErrorMessage=if($r.IsDBNull(2)){$null}else{$r.GetString(2)}
            }
        } finally { $r.Dispose() }
    } finally { $cmd.Dispose() }
}

function Queue-AssignmentLicenseManagerNotifications {
    param([System.Data.SqlClient.SqlConnection]$Connection,[int]$MaxNotifications=25)

    $baseUrlValue=Invoke-SqlScalar $Connection @'
SELECT TOP(1) SettingValue
FROM dbo.ApplicationSettings
WHERE SettingKey=N'PublicBaseUrl' AND Active=1;
'@
    if($null -eq $baseUrlValue -or $baseUrlValue -is [DBNull] -or [string]::IsNullOrWhiteSpace([string]$baseUrlValue)){
        Write-Log "PublicBaseUrl is not configured; assignment license manager notifications cannot be queued." 'WARNING'
        return 0
    }
    $baseUrl=([string]$baseUrlValue).Trim().TrimEnd('/')

    $cmd=$Connection.CreateCommand()
    try {
        $cmd.CommandText=@'
SELECT TOP(@MaxNotifications)
    application.LicenseApplicationId,
    application.RequestedForDisplayName,
    application.RequestedForEmail,
    application.ManagerDisplayName,
    application.ManagerEmail,
    application.BusinessReason
FROM dbo.LicenseApplications application
WHERE application.SourceQueueRequestId IS NOT NULL
  AND application.Status=N'AwaitingManager'
ORDER BY application.LicenseApplicationId;
'@
        [void]$cmd.Parameters.Add('@MaxNotifications',[System.Data.SqlDbType]::Int)
        $cmd.Parameters['@MaxNotifications'].Value=$MaxNotifications
        $rows=@()
        $r=$cmd.ExecuteReader()
        try {
            while($r.Read()){
                $rows += [pscustomobject]@{
                    ApplicationId=$r.GetInt64(0)
                    RequesterName=if($r.IsDBNull(1)){''}else{$r.GetString(1)}
                    RequesterEmail=if($r.IsDBNull(2)){''}else{$r.GetString(2)}
                    ManagerName=if($r.IsDBNull(3)){''}else{$r.GetString(3)}
                    ManagerEmail=if($r.IsDBNull(4)){''}else{$r.GetString(4)}
                    BusinessReason=if($r.IsDBNull(5)){''}else{$r.GetString(5)}
                }
            }
        } finally { $r.Dispose() }
    } finally { $cmd.Dispose() }

    $queued=0
    foreach($row in $rows){
        if([string]::IsNullOrWhiteSpace($row.ManagerEmail)){ continue }
        $names=Get-LicenseList $Connection $row.ApplicationId
        $licenseText=($names | ForEach-Object { '- ' + $_ }) -join [Environment]::NewLine
        $licenseHtml=($names | ForEach-Object { '&#8226; ' + [System.Net.WebUtility]::HtmlEncode($_) }) -join '<br />'
        $reviewUrl='{0}/LicenseRequests/ManagerReview?id={1}' -f $baseUrl,$row.ApplicationId
        try {
            Queue-LicenseNotification $Connection $row.ApplicationId $row.ManagerEmail $row.ManagerName 'LicenseRequestManagerReview' @{
                ApplicationId=[string]$row.ApplicationId
                ManagerName=$row.ManagerName
                RequesterName=$row.RequesterName
                RequesterEmail=$row.RequesterEmail
                BusinessReason=$row.BusinessReason
                LicenseList=$licenseText
                ReviewUrl=$reviewUrl
            } @{ LicenseList=$licenseHtml } 'Manager'
            $queued++
        } catch {
            Write-Log "Could not queue manager review email for assignment license application $($row.ApplicationId): $($_.Exception.Message)" 'ERROR'
        }
    }
    return $queued
}

function Claim-LicenseWorkItem {
    param([System.Data.SqlClient.SqlConnection]$Connection)
    $lockId=[guid]::NewGuid()
    $cmd=$Connection.CreateCommand()
    try {
        $cmd.CommandText=@'
SET NOCOUNT ON;
DECLARE @Now datetime2(0)=SYSDATETIME();
;WITH Candidate AS
(
    SELECT TOP(1) item.LicenseApplicationItemId
    FROM dbo.LicenseApplicationItems item WITH(UPDLOCK,READPAST,ROWLOCK)
    INNER JOIN dbo.LicenseApplications application
        ON application.LicenseApplicationId=item.LicenseApplicationId
    WHERE item.FulfillmentType=N'AdGroup'
      AND NULLIF(LTRIM(RTRIM(item.AdGroupName)),N'') IS NOT NULL
      AND application.ManagerDecision=N'Approved'
      AND
      (
          item.ProvisioningStatus=N'Pending'
          OR
          (
              item.ProvisioningStatus=N'Processing'
              AND item.ProvisioningLockedAt<DATEADD(MINUTE,-30,@Now)
          )
      )
    ORDER BY item.LicenseApplicationItemId
)
UPDATE item
SET ProvisioningStatus=N'Processing',
    ProvisioningLockId=@LockId,
    ProvisioningLockedAt=@Now,
    ProvisioningLastAttemptAt=@Now,
    ProvisioningAttemptCount=ProvisioningAttemptCount+1,
    ProvisioningLastError=NULL
OUTPUT
    INSERTED.LicenseApplicationItemId,
    INSERTED.LicenseApplicationId,
    application.RequestedForSamAccountName,
    application.RequestedForDisplayName,
    application.RequestedForEmail,
    INSERTED.AdGroupName,
    product.Name,
    application.BusinessReason
FROM dbo.LicenseApplicationItems item
INNER JOIN Candidate candidate
    ON candidate.LicenseApplicationItemId=item.LicenseApplicationItemId
INNER JOIN dbo.LicenseApplications application
    ON application.LicenseApplicationId=item.LicenseApplicationId
INNER JOIN dbo.LicenseProducts product
    ON product.LicenseProductId=item.LicenseProductId;
'@
        [void]$cmd.Parameters.Add('@LockId',[System.Data.SqlDbType]::UniqueIdentifier)
        $cmd.Parameters['@LockId'].Value=$lockId
        $r=$cmd.ExecuteReader()
        try {
            if(-not $r.Read()){ return $null }
            return [pscustomobject]@{
                Id=$r.GetInt64(0)
                ApplicationId=$r.GetInt64(1)
                UserSamAccountName=$r.GetString(2)
                UserDisplayName=if($r.IsDBNull(3)){''}else{$r.GetString(3)}
                UserEmail=if($r.IsDBNull(4)){''}else{$r.GetString(4)}
                AdGroupName=$r.GetString(5)
                DisplayName=$r.GetString(6)
                Reason=if($r.IsDBNull(7)){''}else{$r.GetString(7)}
                LockId=$lockId
            }
        } finally { $r.Dispose() }
    } finally { $cmd.Dispose() }
}

function Update-LicenseApplicationStatus {
    param([System.Data.SqlClient.SqlConnection]$Connection,[long]$ApplicationId)
    $cmd=$Connection.CreateCommand()
    try {
        $cmd.CommandText=@'
UPDATE dbo.LicenseApplications
SET Status = CASE
    WHEN EXISTS
    (
        SELECT 1 FROM dbo.LicenseApplicationItems
        WHERE LicenseApplicationId=@ApplicationId
          AND FulfillmentType=N'Manual' AND Status=N'Pending'
    ) THEN N'AwaitingIT'
    WHEN EXISTS
    (
        SELECT 1 FROM dbo.LicenseApplicationItems
        WHERE LicenseApplicationId=@ApplicationId
          AND FulfillmentType=N'AdGroup'
          AND ProvisioningStatus IN (N'Pending',N'Processing')
    ) THEN N'Provisioning'
    WHEN EXISTS
    (
        SELECT 1 FROM dbo.LicenseApplicationItems
        WHERE LicenseApplicationId=@ApplicationId
          AND FulfillmentType=N'AdGroup'
          AND ProvisioningStatus=N'Failed'
    ) THEN CASE
        WHEN EXISTS
        (
            SELECT 1 FROM dbo.LicenseApplicationItems
            WHERE LicenseApplicationId=@ApplicationId
              AND FulfillmentType=N'Manual' AND Status=N'Pending'
        ) THEN N'AwaitingIT'
        ELSE N'ProvisioningFailed'
    END
    WHEN NOT EXISTS
    (
        SELECT 1 FROM dbo.LicenseApplicationItems
        WHERE LicenseApplicationId=@ApplicationId AND FulfillmentType=N'Manual'
    )
     AND NOT EXISTS
    (
        SELECT 1 FROM dbo.LicenseApplicationItems
        WHERE LicenseApplicationId=@ApplicationId AND Status<>N'Completed'
    ) THEN N'Completed'
    WHEN EXISTS
    (
        SELECT 1 FROM dbo.LicenseApplicationItems
        WHERE LicenseApplicationId=@ApplicationId AND Status=N'Approved'
    )
     AND EXISTS
    (
        SELECT 1 FROM dbo.LicenseApplicationItems
        WHERE LicenseApplicationId=@ApplicationId AND Status=N'Rejected'
    ) THEN N'PartiallyApproved'
    WHEN EXISTS
    (
        SELECT 1 FROM dbo.LicenseApplicationItems
        WHERE LicenseApplicationId=@ApplicationId AND Status=N'Approved'
    ) THEN N'Approved'
    WHEN EXISTS
    (
        SELECT 1 FROM dbo.LicenseApplicationItems
        WHERE LicenseApplicationId=@ApplicationId AND Status=N'Completed'
    )
     AND EXISTS
    (
        SELECT 1 FROM dbo.LicenseApplicationItems
        WHERE LicenseApplicationId=@ApplicationId AND Status=N'Rejected'
    ) THEN N'PartiallyApproved'
    ELSE N'ITRejected'
END,
CompletedAt = CASE
    WHEN NOT EXISTS
    (
        SELECT 1 FROM dbo.LicenseApplicationItems
        WHERE LicenseApplicationId=@ApplicationId AND FulfillmentType=N'Manual'
    )
     AND NOT EXISTS
    (
        SELECT 1 FROM dbo.LicenseApplicationItems
        WHERE LicenseApplicationId=@ApplicationId AND Status<>N'Completed'
    ) THEN COALESCE(CompletedAt,SYSDATETIME())
    ELSE CompletedAt
END
WHERE LicenseApplicationId=@ApplicationId;

SELECT Status
FROM dbo.LicenseApplications
WHERE LicenseApplicationId=@ApplicationId;
'@
        Add-Parameters $cmd @{ '@ApplicationId'=$ApplicationId }
        $value=$cmd.ExecuteScalar()
        if($null -eq $value -or $value -is [DBNull]){ return $null }
        return [string]$value
    } finally { $cmd.Dispose() }
}

function Get-LicenseList {
    param([System.Data.SqlClient.SqlConnection]$Connection,[long]$ApplicationId)
    $cmd=$Connection.CreateCommand()
    try {
        $cmd.CommandText=@'
SELECT product.Name
FROM dbo.LicenseApplicationItems item
INNER JOIN dbo.LicenseProducts product ON product.LicenseProductId=item.LicenseProductId
WHERE item.LicenseApplicationId=@ApplicationId
ORDER BY product.Name;
'@
        Add-Parameters $cmd @{ '@ApplicationId'=$ApplicationId }
        $names=@()
        $r=$cmd.ExecuteReader()
        try { while($r.Read()){ $names += $r.GetString(0) } }
        finally { $r.Dispose() }
        return $names
    } finally { $cmd.Dispose() }
}

function Complete-LicenseAdd {
    param([System.Data.SqlClient.SqlConnection]$Connection,$Item)
    Import-Module ActiveDirectory -ErrorAction Stop
    $user=Get-ADUser -Identity $Item.UserSamAccountName -Properties mail,displayName -ErrorAction Stop
    $group=Get-ADGroup -Identity $Item.AdGroupName -ErrorAction Stop
    $alreadyMember=[bool](Get-ADGroupMember -Identity $group -Recursive:$false | Where-Object{$_.DistinguishedName -eq $user.DistinguishedName}|Select-Object -First 1)
    $added=$false
    if(-not $alreadyMember){ Add-ADGroupMember -Identity $group -Members $user -ErrorAction Stop; $added=$true }

    [void](Invoke-SqlNonQuery $Connection @'
UPDATE dbo.LicenseApplicationItems
SET ProvisioningStatus=N'Completed',
    Status=N'Completed',
    ProvisionedAt=SYSDATETIME(),
    WasAdGroupMemberBefore=@Before,
    MembershipAddedBySystem=@Added,
    ProvisioningLastError=NULL,
    ProvisioningLockId=NULL,
    ProvisioningLockedAt=NULL
WHERE LicenseApplicationItemId=@Id AND ProvisioningLockId=@LockId;
'@ @{ '@Id'=$Item.Id; '@LockId'=$Item.LockId; '@Before'=$alreadyMember; '@Added'=$added })

    $applicationStatus=Update-LicenseApplicationStatus $Connection $Item.ApplicationId
    if($applicationStatus -eq 'Completed'){
        $names=Get-LicenseList $Connection $Item.ApplicationId
        $licenseText=($names | ForEach-Object { '- ' + $_ }) -join [Environment]::NewLine
        $licenseHtml=($names | ForEach-Object { '&#8226; ' + [System.Net.WebUtility]::HtmlEncode($_) }) -join '<br />'
        try {
            Queue-LicenseNotification $Connection $Item.ApplicationId $Item.UserEmail $Item.UserDisplayName 'LicenseRequestAutoCompleted' @{
                ApplicationId=[string]$Item.ApplicationId
                RequesterName=$Item.UserDisplayName
                LicenseList=$licenseText
            } @{ LicenseList=$licenseHtml }
        } catch {
            Write-Log "License application $($Item.ApplicationId) completed, but the completion email could not be queued: $($_.Exception.Message)" 'ERROR'
        }
    }

    if($alreadyMember){
        Write-Log "License '$($Item.DisplayName)': '$($Item.UserSamAccountName)' was already a member of '$($Item.AdGroupName)'."
    } else {
        Write-Log "License '$($Item.DisplayName)': added '$($Item.UserSamAccountName)' to '$($Item.AdGroupName)'."
    }
}

function Fail-LicenseItem {
    param([System.Data.SqlClient.SqlConnection]$Connection,$Item,[string]$Message)
    [void](Invoke-SqlNonQuery $Connection @'
UPDATE dbo.LicenseApplicationItems
SET ProvisioningStatus=N'Failed',
    ProvisioningLastError=@Error,
    ProvisioningLockId=NULL,
    ProvisioningLockedAt=NULL
WHERE LicenseApplicationItemId=@Id AND ProvisioningLockId=@LockId;
'@ @{ '@Id'=$Item.Id; '@LockId'=$Item.LockId; '@Error'=$Message })
    [void](Update-LicenseApplicationStatus $Connection $Item.ApplicationId)
}

try {
    $connection=[System.Data.SqlClient.SqlConnection]::new((Get-DatabaseConnectionString));$connection.Open()
    try {
        $processed=0
        $temporaryProcessed=0
        $licenseProcessed=0
        $licenseFulfillmentEnabled=[bool](Invoke-SqlScalar $connection @'
SELECT CASE
    WHEN OBJECT_ID(N'dbo.LicenseApplicationItems',N'U') IS NOT NULL
     AND COL_LENGTH(N'dbo.LicenseApplicationItems',N'FulfillmentType') IS NOT NULL
     AND COL_LENGTH(N'dbo.LicenseApplicationItems',N'ProvisioningStatus') IS NOT NULL
     AND COL_LENGTH(N'dbo.LicenseApplicationItems',N'ProvisioningLockId') IS NOT NULL
    THEN 1 ELSE 0 END;
'@)
        if(-not $licenseFulfillmentEnabled){
            Write-Log "License AD-group fulfillment schema is not installed; temporary access processing will continue without license items." 'WARNING'
        }

        $assignmentLicenseEnabled=[bool](Invoke-SqlScalar $connection @'
SELECT CASE
    WHEN OBJECT_ID(N'dbo.AssignmentLicenseSelections',N'U') IS NOT NULL
     AND COL_LENGTH(N'dbo.LicenseApplications',N'SourceQueueRequestId') IS NOT NULL
    THEN 1 ELSE 0 END;
'@)
        $assignmentLicenseActivated=0
        $assignmentLicenseEmails=0
        if($assignmentLicenseEnabled){
            while($assignmentLicenseActivated -lt $MaxItems){
                $activation=Activate-AssignmentLicenseRequest $connection
                if($null -eq $activation){ break }
                if($null -ne $activation.ApplicationId){
                    $assignmentLicenseActivated++
                    Write-Log "Activated assignment license request $($activation.RequestId) as license application $($activation.ApplicationId)."
                } else {
                    Write-Log "Assignment license request $($activation.RequestId) was not activated: $($activation.ErrorMessage)" 'WARNING'
                    break
                }
            }
            $assignmentLicenseEmails=Queue-AssignmentLicenseManagerNotifications $connection
        }

        while($processed -lt $MaxItems){
            $didWork=$false

            $item=Claim-WorkItem $connection
            if($null -ne $item){
                try{
                    if($item.Status -eq 'ProcessingAdd'){ Complete-Add $connection $item }
                    else { Complete-Remove $connection $item }
                }catch{
                    $m=$_.Exception.Message
                    Fail-Item $connection $item $m
                    Write-Log "Failed temporary membership $($item.Id): $m" 'ERROR'
                }
                $processed++
                $temporaryProcessed++
                $didWork=$true
            }

            if($processed -ge $MaxItems){ break }

            $licenseItem=$null
            if($licenseFulfillmentEnabled){ $licenseItem=Claim-LicenseWorkItem $connection }
            if($null -ne $licenseItem){
                try{
                    Complete-LicenseAdd $connection $licenseItem
                }catch{
                    $m=$_.Exception.Message
                    Fail-LicenseItem $connection $licenseItem $m
                    Write-Log "Failed license provisioning item $($licenseItem.Id) for application $($licenseItem.ApplicationId): $m" 'ERROR'
                }
                $processed++
                $licenseProcessed++
                $didWork=$true
            }

            if(-not $didWork){ break }
        }
        Write-Log "Group membership worker completed. Processed $temporaryProcessed temporary item(s), $licenseProcessed license item(s), activated $assignmentLicenseActivated assignment license application(s), and checked $assignmentLicenseEmails assignment license manager notification(s). Email delivery is handled by the shared email worker."
    } finally {$connection.Dispose()}
} catch {Write-Log $_.Exception.Message 'ERROR';exit 1}

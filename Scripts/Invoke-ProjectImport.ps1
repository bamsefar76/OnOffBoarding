[CmdletBinding()]
param(
    [Parameter()]
    [string]$ImportDirectory = 'C:\ProgramData\UserChangeQueueWeb\ProjectImport\Inbox',

    [Parameter()]
    [string]$FilePattern = '*.csv',

    [Parameter()]
    [string]$InputPath,

    [Parameter()]
    [string]$ArchiveDirectory = 'C:\ProgramData\UserChangeQueueWeb\ProjectImport\Archive',

    [Parameter()]
    [string]$FailedDirectory = 'C:\ProgramData\UserChangeQueueWeb\ProjectImport\Failed',

    [Parameter()]
    [string]$LogPath = 'C:\ProgramData\UserChangeQueueWeb\Logs\ProjectImport.log',

    [Parameter()]
    [string]$AppSettingsPath = 'C:\inetpub\UserChangeQueueWeb\appsettings.json',

    [Parameter()]
    [string]$ConnectionString,

    [Parameter()]
    [int]$MaximumRows = 100000,

    [Parameter()]
    [int]$CommandTimeoutSeconds = 180
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Write-Log {
    param(
        [Parameter(Mandatory)]
        [string]$Message,

        [ValidateSet('INFO', 'WARNING', 'ERROR')]
        [string]$Level = 'INFO'
    )

    $line = '[{0:yyyy-MM-dd HH:mm:ss}] [{1}] {2}' -f (Get-Date), $Level, $Message
    Write-Host $line

    $directory = Split-Path -Path $LogPath -Parent
    if (-not [string]::IsNullOrWhiteSpace($directory)) {
        New-Item -ItemType Directory -Path $directory -Force | Out-Null
    }

    Add-Content -LiteralPath $LogPath -Value $line -Encoding UTF8
}

function Get-ConnectionString {
    if (-not [string]::IsNullOrWhiteSpace($ConnectionString)) {
        return $ConnectionString
    }

    if (-not (Test-Path -LiteralPath $AppSettingsPath -PathType Leaf)) {
        throw "ConnectionString was not supplied and appsettings.json was not found at '$AppSettingsPath'."
    }

    $settings = Get-Content -LiteralPath $AppSettingsPath -Raw -Encoding UTF8 | ConvertFrom-Json
    $value = $settings.ConnectionStrings.UserDatabase
    if ([string]::IsNullOrWhiteSpace([string]$value)) {
        throw "ConnectionStrings:UserDatabase is missing from '$AppSettingsPath'."
    }

    return [string]$value
}

function ConvertTo-NullableDateTime {
    param(
        [AllowNull()]
        [string]$Value,

        [switch]$OpenEndedStopDate,

        [int]$LineNumber,

        [string]$ColumnName
    )

    if ([string]::IsNullOrWhiteSpace($Value)) {
        return $null
    }

    # The accounting export uses dots as time separators, for example
    # 2026-01-01T00.00.00. Normalize only the time portion before parsing.
    $normalizedValue = $Value.Trim()
    if ($normalizedValue -match '^(?<date>\d{4}-\d{2}-\d{2})(?<separator>T| )(?<hour>\d{2})\.(?<minute>\d{2})\.(?<second>\d{2})$') {
        $normalizedValue = '{0}{1}{2}:{3}:{4}' -f `
            $Matches.date,
            $Matches.separator,
            $Matches.hour,
            $Matches.minute,
            $Matches.second
    }

    [string[]]$formats = @(
        "yyyy-MM-dd'T'HH:mm:ss",
        'yyyy-MM-dd HH:mm:ss',
        'yyyy-MM-dd'
    )

    $parsed = [datetime]::MinValue
    $culture = [Globalization.CultureInfo]::InvariantCulture
    $styles = [Globalization.DateTimeStyles]::AllowWhiteSpaces

    $valid = [datetime]::TryParseExact(
        $normalizedValue,
        $formats,
        $culture,
        $styles,
        [ref]$parsed
    )
    if (-not $valid) {
        $valid = [datetime]::TryParse(
            $normalizedValue,
            $culture,
            $styles,
            [ref]$parsed
        )
    }

    if (-not $valid) {
        throw "Line $LineNumber has an invalid $ColumnName value '$Value'."
    }

    if ($OpenEndedStopDate -and $parsed.Year -ge 2069) {
        return $null
    }

    return [datetime]::SpecifyKind($parsed, [DateTimeKind]::Unspecified)
}

function Get-SourceHash {
    param(
        [Parameter(Mandatory)][string]$Company,
        [Parameter(Mandatory)][string]$ProjectNumber,
        [Parameter(Mandatory)][string]$ProjectName,
        [AllowNull()][Nullable[datetime]]$StartTime,
        [AllowNull()][Nullable[datetime]]$StopTime
    )

    $start = if ($null -eq $StartTime) { '' } else { ([datetime]$StartTime).ToString('O', [Globalization.CultureInfo]::InvariantCulture) }
    $stop = if ($null -eq $StopTime) { '' } else { ([datetime]$StopTime).ToString('O', [Globalization.CultureInfo]::InvariantCulture) }
    $canonical = [string]::Join([char]0x1f, @($Company, $ProjectNumber, $ProjectName, $start, $stop))

    $sha = [Security.Cryptography.SHA256]::Create()
    try {
        $bytes = $sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($canonical))
        Write-Output -NoEnumerate $bytes
    }
    finally {
        $sha.Dispose()
    }
}

function Read-ProjectCsv {
    param([Parameter(Mandatory)][string]$Path)

    $firstLine = Get-Content -LiteralPath $Path -TotalCount 1 -Encoding UTF8
    if ([string]::IsNullOrWhiteSpace($firstLine)) {
        throw 'The CSV file is empty.'
    }

    $semicolonCount = @($firstLine.ToCharArray() | Where-Object { $_ -eq ';' }).Count
    $commaCount = @($firstLine.ToCharArray() | Where-Object { $_ -eq ',' }).Count
    $delimiter = if ($semicolonCount -gt $commaCount) { ';' } else { ',' }
    $rows = @(Import-Csv -LiteralPath $Path -Delimiter $delimiter -Encoding UTF8)

    if ($rows.Count -eq 0) {
        throw 'The CSV file contains no project rows.'
    }

    if ($rows.Count -gt $MaximumRows) {
        throw "The CSV file contains more than $MaximumRows project rows."
    }

    $requiredHeaders = @('Company', 'ProjectNumber', 'ProjectName', 'StartTime', 'StopTime')
    $actualHeaders = @($rows[0].PSObject.Properties.Name | ForEach-Object { $_.Trim().TrimStart([char]0xFEFF) })
    if ($actualHeaders.Count -ne $requiredHeaders.Count -or (Compare-Object -ReferenceObject $requiredHeaders -DifferenceObject $actualHeaders -SyncWindow 0)) {
        throw 'The CSV header must be: Company, ProjectNumber, ProjectName, StartTime, StopTime.'
    }

    $table = New-Object System.Data.DataTable
    [void]$table.Columns.Add('Company', [string])
    [void]$table.Columns.Add('ProjectNumber', [string])
    [void]$table.Columns.Add('ProjectName', [string])
    [void]$table.Columns.Add('StartTime', [datetime])
    [void]$table.Columns.Add('StopTime', [datetime])
    [void]$table.Columns.Add('SourceHash', [byte[]])

    $keys = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
    $lineNumber = 1

    foreach ($item in $rows) {
        $lineNumber++
        $company = ([string]$item.Company).Trim()
        $projectNumber = ([string]$item.ProjectNumber).Trim()
        $projectName = ([string]$item.ProjectName).Trim()

        if ([string]::IsNullOrWhiteSpace($company) -or $company.Length -gt 256) {
            throw "Line ${lineNumber}: Company is required and may contain at most 256 characters."
        }
        if ([string]::IsNullOrWhiteSpace($projectNumber) -or $projectNumber.Length -gt 100) {
            throw "Line ${lineNumber}: ProjectNumber is required and may contain at most 100 characters."
        }
        if ([string]::IsNullOrWhiteSpace($projectName) -or $projectName.Length -gt 256) {
            throw "Line ${lineNumber}: ProjectName is required and may contain at most 256 characters."
        }

        $key = $company + [char]0x1f + $projectNumber
        if (-not $keys.Add($key)) {
            throw "Line ${lineNumber}: Duplicate Company/ProjectNumber in the file: $company / $projectNumber."
        }

        $startTime = ConvertTo-NullableDateTime -Value ([string]$item.StartTime) -LineNumber $lineNumber -ColumnName 'StartTime'
        $stopTime = ConvertTo-NullableDateTime -Value ([string]$item.StopTime) -OpenEndedStopDate -LineNumber $lineNumber -ColumnName 'StopTime'
        $hash = Get-SourceHash -Company $company -ProjectNumber $projectNumber -ProjectName $projectName -StartTime $startTime -StopTime $stopTime

        $row = $table.NewRow()
        $row['Company'] = $company
        $row['ProjectNumber'] = $projectNumber
        $row['ProjectName'] = $projectName
        $row['StartTime'] = if ($null -eq $startTime) { [DBNull]::Value } else { $startTime }
        $row['StopTime'] = if ($null -eq $stopTime) { [DBNull]::Value } else { $stopTime }
        $row['SourceHash'] = [byte[]]$hash
        [void]$table.Rows.Add($row)
    }

    # A DataTable implements IEnumerable. A normal return causes PowerShell to
    # enumerate it and return DataRow objects instead of the DataTable itself.
    Write-Output -NoEnumerate $table
}

function Add-SqlParameter {
    param(
        [Parameter(Mandatory)][System.Data.SqlClient.SqlCommand]$Command,
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][System.Data.SqlDbType]$Type,
        [Parameter()][int]$Size = 0,
        [AllowNull()]$Value
    )

    $parameter = if ($Size -gt 0) { $Command.Parameters.Add($Name, $Type, $Size) } else { $Command.Parameters.Add($Name, $Type) }
    $parameter.Value = if ($null -eq $Value) { [DBNull]::Value } else { $Value }
    return $parameter
}

$mutexName = 'Global\UserChangeQueueWeb.ProjectImport'
$mutex = New-Object Threading.Mutex($false, $mutexName)
$hasMutex = $false
$processingPath = $null
$batchId = $null
$sqlConnection = $null

try {
    $hasMutex = $mutex.WaitOne(0)
    if (-not $hasMutex) {
        Write-Log 'Another project import is already running. Exiting.' 'WARNING'
        exit 0
    }

    $selectedFile = $null

    if (-not [string]::IsNullOrWhiteSpace($InputPath)) {
        if (-not (Test-Path -LiteralPath $InputPath -PathType Leaf)) {
            Write-Log "No project import file found at '$InputPath'."
            exit 0
        }

        $selectedFile = Get-Item -LiteralPath $InputPath
    }
    else {
        if (-not (Test-Path -LiteralPath $ImportDirectory -PathType Container)) {
            Write-Log "Project import directory '$ImportDirectory' does not exist."
            exit 0
        }

        $selectedFile = Get-ChildItem -LiteralPath $ImportDirectory -File -Filter $FilePattern |
            Where-Object { $_.Name -notlike '*.processing.csv' } |
            Sort-Object LastWriteTimeUtc, Name -Descending |
            Select-Object -First 1

        if ($null -eq $selectedFile) {
            Write-Log "No project import files matching '$FilePattern' were found in '$ImportDirectory'."
            exit 0
        }
    }

    $sourcePath = $selectedFile.FullName
    $sourceFileName = $selectedFile.Name
    $inputDirectory = $selectedFile.DirectoryName
    $processingName = '{0}.{1}.processing{2}' -f $selectedFile.BaseName, ([guid]::NewGuid().ToString('N')), $selectedFile.Extension
    $processingPath = Join-Path -Path $inputDirectory -ChildPath $processingName
    Move-Item -LiteralPath $sourcePath -Destination $processingPath

    Write-Log "Selected latest project import file '$sourceFileName' (last modified $($selectedFile.LastWriteTime.ToString('yyyy-MM-dd HH:mm:ss')))."
    Write-Log "Starting automated project import from '$processingPath'."
    $table = Read-ProjectCsv -Path $processingPath
    if ($table -isnot [System.Data.DataTable]) {
        throw "CSV parser returned '$($table.GetType().FullName)' instead of System.Data.DataTable."
    }
    $rowCount = $table.Rows.Count
    $connectionStringValue = Get-ConnectionString
    $fileName = $sourceFileName
    $importedBy = 'Scheduled task: ' + [Security.Principal.WindowsIdentity]::GetCurrent().Name

    $sqlConnection = New-Object System.Data.SqlClient.SqlConnection $connectionStringValue
    $sqlConnection.Open()

    $batchCommand = $sqlConnection.CreateCommand()
    try {
        $batchCommand.CommandTimeout = $CommandTimeoutSeconds
        $batchCommand.CommandText = @'
INSERT INTO dbo.ProjectImportBatches
    (FileName, ImportedBy, [RowCount], ValidRowCount, Status)
OUTPUT INSERTED.Id
VALUES
    (@FileName, @ImportedBy, @RowCount, @RowCount, N'Processing');
'@
        [void](Add-SqlParameter -Command $batchCommand -Name '@FileName' -Type NVarChar -Size 260 -Value $fileName)
        [void](Add-SqlParameter -Command $batchCommand -Name '@ImportedBy' -Type NVarChar -Size 256 -Value $importedBy)
        [void](Add-SqlParameter -Command $batchCommand -Name '@RowCount' -Type Int -Value $rowCount)
        $batchId = [long]$batchCommand.ExecuteScalar()
    }
    finally {
        $batchCommand.Dispose()
    }

    $transaction = $sqlConnection.BeginTransaction()
    try {
        $createTemp = $sqlConnection.CreateCommand()
        try {
            $createTemp.Transaction = $transaction
            $createTemp.CommandTimeout = $CommandTimeoutSeconds
            $createTemp.CommandText = @'
CREATE TABLE #IncomingProjects
(
    Company nvarchar(256) NOT NULL,
    ProjectNumber nvarchar(100) NOT NULL,
    ProjectName nvarchar(256) NOT NULL,
    StartTime datetime2(0) NULL,
    StopTime datetime2(0) NULL,
    SourceHash varbinary(32) NOT NULL
);
'@
            [void]$createTemp.ExecuteNonQuery()
        }
        finally {
            $createTemp.Dispose()
        }

        $bulk = New-Object System.Data.SqlClient.SqlBulkCopy($sqlConnection, [System.Data.SqlClient.SqlBulkCopyOptions]::CheckConstraints, $transaction)
        try {
            $bulk.DestinationTableName = '#IncomingProjects'
            $bulk.BatchSize = 2000
            $bulk.BulkCopyTimeout = $CommandTimeoutSeconds
            foreach ($column in $table.Columns) {
                [void]$bulk.ColumnMappings.Add($column.ColumnName, $column.ColumnName)
            }
            $bulk.WriteToServer($table)
        }
        finally {
            $bulk.Dispose()
        }

        $merge = $sqlConnection.CreateCommand()
        try {
            $merge.Transaction = $transaction
            $merge.CommandTimeout = $CommandTimeoutSeconds
            $merge.CommandText = @'
DECLARE @Now datetime2(0) = SYSUTCDATETIME();
DECLARE @Updated int = 0;
DECLARE @Unchanged int = 0;
DECLARE @Inserted int = 0;
DECLARE @Skipped int = 0;

SELECT @Skipped = COUNT(*)
FROM #IncomingProjects AS source
JOIN dbo.ImportedProjects AS target
  ON target.Company = source.Company
 AND target.ProjectNumber = source.ProjectNumber
WHERE target.Status <> N'Pending';

SELECT @Unchanged = COUNT(*)
FROM #IncomingProjects AS source
JOIN dbo.ImportedProjects AS target
  ON target.Company = source.Company
 AND target.ProjectNumber = source.ProjectNumber
WHERE target.Status = N'Pending'
  AND target.SourceHash = source.SourceHash;

UPDATE target
SET ProjectName = source.ProjectName,
    StartTime = source.StartTime,
    StopTime = source.StopTime,
    SourceHash = source.SourceHash,
    ImportBatchId = @BatchId,
    SourceFileName = @FileName,
    ImportedAt = @Now,
    LastSeenAt = @Now
FROM dbo.ImportedProjects AS target
JOIN #IncomingProjects AS source
  ON target.Company = source.Company
 AND target.ProjectNumber = source.ProjectNumber
WHERE target.Status = N'Pending'
  AND target.SourceHash <> source.SourceHash;
SET @Updated = @@ROWCOUNT;

UPDATE target
SET LastSeenAt = @Now,
    ImportBatchId = @BatchId,
    SourceFileName = @FileName
FROM dbo.ImportedProjects AS target
JOIN #IncomingProjects AS source
  ON target.Company = source.Company
 AND target.ProjectNumber = source.ProjectNumber
WHERE target.Status = N'Pending'
  AND target.SourceHash = source.SourceHash;

INSERT INTO dbo.ImportedProjects
(
    Company, ProjectNumber, ProjectName, StartTime, StopTime, Status,
    ImportBatchId, SourceFileName, SourceHash, ImportedAt, LastSeenAt
)
SELECT
    source.Company, source.ProjectNumber, source.ProjectName,
    source.StartTime, source.StopTime, N'Pending',
    @BatchId, @FileName, source.SourceHash, @Now, @Now
FROM #IncomingProjects AS source
WHERE NOT EXISTS
(
    SELECT 1
    FROM dbo.ImportedProjects AS target
    WHERE target.Company = source.Company
      AND target.ProjectNumber = source.ProjectNumber
);
SET @Inserted = @@ROWCOUNT;

UPDATE dbo.ProjectImportBatches
SET InsertedCount = @Inserted,
    UpdatedCount = @Updated,
    UnchangedCount = @Unchanged,
    SkippedCount = @Skipped,
    Status = N'Completed'
WHERE Id = @BatchId;

SELECT @Inserted, @Updated, @Unchanged, @Skipped;
'@
            [void](Add-SqlParameter -Command $merge -Name '@BatchId' -Type BigInt -Value $batchId)
            [void](Add-SqlParameter -Command $merge -Name '@FileName' -Type NVarChar -Size 260 -Value $fileName)

            $reader = $merge.ExecuteReader()
            try {
                [void]$reader.Read()
                $inserted = $reader.GetInt32(0)
                $updated = $reader.GetInt32(1)
                $unchanged = $reader.GetInt32(2)
                $skipped = $reader.GetInt32(3)
            }
            finally {
                $reader.Dispose()
            }
        }
        finally {
            $merge.Dispose()
        }

        $transaction.Commit()
    }
    catch {
        try { $transaction.Rollback() } catch { }
        throw
    }
    finally {
        $transaction.Dispose()
    }

    New-Item -ItemType Directory -Path $ArchiveDirectory -Force | Out-Null
    $archiveName = '{0}-{1:yyyyMMdd-HHmmss}-batch-{2}{3}' -f ([IO.Path]::GetFileNameWithoutExtension($sourceFileName)), (Get-Date), $batchId, ([IO.Path]::GetExtension($sourceFileName))
    $archivePath = Join-Path -Path $ArchiveDirectory -ChildPath $archiveName
    Move-Item -LiteralPath $processingPath -Destination $archivePath -Force
    $processingPath = $null

    Write-Log "Import completed. Rows: $rowCount; inserted: $inserted; updated: $updated; unchanged: $unchanged; protected skipped: $skipped. Archived as '$archivePath'."
    exit 0
}
catch {
    $message = $_.Exception.Message
    Write-Log "Automated project import failed: $message" 'ERROR'

    if ($null -ne $sqlConnection -and $sqlConnection.State -eq [Data.ConnectionState]::Open -and $null -ne $batchId) {
        try {
            $failureCommand = $sqlConnection.CreateCommand()
            try {
                $failureCommand.CommandTimeout = $CommandTimeoutSeconds
                $failureCommand.CommandText = @'
UPDATE dbo.ProjectImportBatches
SET Status = N'Failed',
    ErrorMessage = LEFT(@ErrorMessage, 2000)
WHERE Id = @BatchId;
'@
                [void](Add-SqlParameter -Command $failureCommand -Name '@ErrorMessage' -Type NVarChar -Size 2000 -Value $message)
                [void](Add-SqlParameter -Command $failureCommand -Name '@BatchId' -Type BigInt -Value $batchId)
                [void]$failureCommand.ExecuteNonQuery()
            }
            finally {
                $failureCommand.Dispose()
            }
        }
        catch {
            Write-Log "Could not record the failed import batch: $($_.Exception.Message)" 'WARNING'
        }
    }

    if ($null -ne $processingPath -and (Test-Path -LiteralPath $processingPath -PathType Leaf)) {
        try {
            New-Item -ItemType Directory -Path $FailedDirectory -Force | Out-Null
            $failedName = 'Projects-{0:yyyyMMdd-HHmmss}-failed.csv' -f (Get-Date)
            $failedPath = Join-Path -Path $FailedDirectory -ChildPath $failedName
            Move-Item -LiteralPath $processingPath -Destination $failedPath -Force
            Write-Log "Moved the failed file to '$failedPath'." 'WARNING'
        }
        catch {
            Write-Log "Could not move the failed file: $($_.Exception.Message)" 'WARNING'
        }
    }

    exit 1
}
finally {
    if ($null -ne $sqlConnection) {
        $sqlConnection.Dispose()
    }
    if ($hasMutex) {
        $mutex.ReleaseMutex()
    }
    $mutex.Dispose()
}

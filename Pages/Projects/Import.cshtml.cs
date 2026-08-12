using System.Data;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Data.SqlClient;
using Microsoft.VisualBasic.FileIO;
using UserChangeQueueWeb.Services;

namespace UserChangeQueueWeb.Pages;

[Authorize]
[RequestSizeLimit(10 * 1024 * 1024)]
public class ProjectImportModel : PageModel
{
    private const int MaximumRows = 100_000;
    private readonly SqlConnectionFactory _connectionFactory;

    public ProjectImportModel(SqlConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    [BindProperty]
    public IFormFile? Upload { get; set; }

    [BindProperty(SupportsGet = true)]
    public string StatusFilter { get; set; } = "Pending";

    [BindProperty]
    public long ReviewId { get; set; }

    [BindProperty]
    public string FriendlyName { get; set; } = "";

    [BindProperty]
    public string ProductionManager { get; set; } = "";

    [BindProperty]
    public string ReviewComment { get; set; } = "";

    public string? Message { get; private set; }
    public bool MessageIsError { get; private set; }
    public List<ImportError> Errors { get; } = new();
    public List<ImportBatchRow> RecentImports { get; } = new();
    public List<ImportedProjectRow> ImportedProjects { get; } = new();

    public async Task OnGetAsync()
    {
        await LoadPageDataAsync();
    }

    public async Task<IActionResult> OnPostImportAsync()
    {
        if (Upload is null || Upload.Length == 0)
        {
            SetError("Choose a non-empty CSV file.");
            await LoadPageDataAsync();
            return Page();
        }

        if (!string.Equals(Path.GetExtension(Upload.FileName), ".csv", StringComparison.OrdinalIgnoreCase))
        {
            SetError("Only CSV files are accepted.");
            await LoadPageDataAsync();
            return Page();
        }

        List<ParsedProject> rows;
        try
        {
            rows = await ParseCsvAsync(Upload, Errors);
        }
        catch (Exception ex) when (ex is InvalidDataException or DecoderFallbackException)
        {
            SetError(ex.Message);
            await LoadPageDataAsync();
            return Page();
        }

        if (Errors.Count > 0)
        {
            SetError($"The file was not imported because {Errors.Count} row(s) contain errors.");
            await LoadPageDataAsync();
            return Page();
        }

        if (rows.Count == 0)
        {
            SetError("The CSV file contains no project rows.");
            await LoadPageDataAsync();
            return Page();
        }

        try
        {
            var result = await ImportAsync(rows, Path.GetFileName(Upload.FileName));
            TempData["ProjectImportMessage"] =
                $"Imported {result.Total:N0} rows: {result.Inserted:N0} new, {result.Updated:N0} updated, " +
                $"{result.Unchanged:N0} unchanged and {result.Skipped:N0} protected approved/rejected rows skipped.";
            return RedirectToPage(new { statusFilter = "Pending" });
        }
        catch (Exception ex)
        {
            SetError("Import failed: " + ex.Message);
            await LoadPageDataAsync();
            return Page();
        }
    }

    public async Task<IActionResult> OnPostApproveAsync()
    {
        if (ReviewId <= 0)
        {
            TempData["ProjectImportError"] = "No imported project was selected.";
            return RedirectToPage(new { statusFilter = StatusFilter });
        }

        await using var cn = await _connectionFactory.OpenAsync();
        await using var tx = (SqlTransaction)await cn.BeginTransactionAsync();

        try
        {
            string company;
            string projectNumber;
            string accountingName;

            await using (var read = cn.CreateCommand())
            {
                read.Transaction = tx;
                read.CommandText = @"
SELECT Company, ProjectNumber, ProjectName
FROM dbo.ImportedProjects WITH (UPDLOCK, HOLDLOCK)
WHERE Id = @Id AND Status = N'Pending';";
                read.Parameters.AddBigInt("@Id", ReviewId);

                await using var reader = await read.ExecuteReaderAsync();
                if (!await reader.ReadAsync())
                {
                    TempData["ProjectImportError"] = "The imported project is no longer pending.";
                    await tx.RollbackAsync();
                    return RedirectToPage(new { statusFilter = StatusFilter });
                }

                company = reader.GetString(0);
                projectNumber = reader.GetString(1);
                accountingName = reader.GetString(2);
            }

            var friendlyName = string.IsNullOrWhiteSpace(FriendlyName)
                ? accountingName
                : FriendlyName.Trim();

            if (friendlyName.Length > 256)
                throw new InvalidOperationException("Friendly name may contain at most 256 characters.");
            if ((ProductionManager ?? string.Empty).Trim().Length > 256)
                throw new InvalidOperationException("Production manager may contain at most 256 characters.");
            if ((ReviewComment ?? string.Empty).Trim().Length > 1000)
                throw new InvalidOperationException("Review comment may contain at most 1000 characters.");

            int projectId;
            await using (var save = cn.CreateCommand())
            {
                save.Transaction = tx;
                save.CommandText = @"
DECLARE @ProjectId int;

SELECT TOP (1) @ProjectId = Id
FROM dbo.Projects WITH (UPDLOCK, HOLDLOCK)
WHERE Company = @Company AND ProjectNumber = @ProjectNumber
ORDER BY Id;

IF @ProjectId IS NULL
BEGIN
    INSERT INTO dbo.Projects
        (ProjectName, ProjectNumber, Company, ProductionManager, Producer, Executive, Active, LastUpdated)
    VALUES
        (@ProjectName, @ProjectNumber, @Company, @ProductionManager, NULL, NULL, 1, SYSUTCDATETIME());

    SET @ProjectId = CONVERT(int, SCOPE_IDENTITY());
END
ELSE
BEGIN
    UPDATE dbo.Projects
    SET ProjectName = @ProjectName,
        ProductionManager = NULLIF(@ProductionManager, N''),
        Active = 1,
        LastUpdated = SYSUTCDATETIME()
    WHERE Id = @ProjectId;
END;

SELECT @ProjectId;";
                save.Parameters.AddNVarChar("@Company", company, 256);
                save.Parameters.AddNVarChar("@ProjectNumber", projectNumber, 100);
                save.Parameters.AddNVarChar("@ProjectName", friendlyName, 256);
                save.Parameters.AddNVarChar("@ProductionManager", (ProductionManager ?? string.Empty).Trim(), 256);
                projectId = Convert.ToInt32(await save.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
            }

            var importedManager = AccessScopeService.ExtractSamAccountName((ProductionManager ?? string.Empty).Trim());
            if (!string.IsNullOrWhiteSpace(importedManager))
            {
                await using var manager = cn.CreateCommand();
                manager.Transaction = tx;
                manager.CommandText = @"
IF NOT EXISTS (SELECT 1 FROM dbo.ProjectManagers WHERE ProjectId=@ProjectId AND SamAccountName=@Sam)
    INSERT INTO dbo.ProjectManagers(ProjectId, SamAccountName, SortOrder) VALUES(@ProjectId, @Sam, 100);";
                manager.Parameters.AddInt("@ProjectId", projectId);
                manager.Parameters.AddNVarChar("@Sam", importedManager, 256);
                await manager.ExecuteNonQueryAsync();
            }

            await using (var review = cn.CreateCommand())
            {
                review.Transaction = tx;
                review.CommandText = @"
UPDATE dbo.ImportedProjects
SET Status = N'Approved',
    ApprovedProjectId = @ProjectId,
    ReviewedBy = @ReviewedBy,
    ReviewedAt = SYSUTCDATETIME(),
    ReviewAction = N'Approved',
    ReviewComment = NULLIF(@ReviewComment, N'')
WHERE Id = @Id AND Status = N'Pending';";
                review.Parameters.AddBigInt("@Id", ReviewId);
                review.Parameters.AddInt("@ProjectId", projectId);
                review.Parameters.AddNVarChar("@ReviewedBy", User.Identity?.Name, 256);
                review.Parameters.AddNVarChar("@ReviewComment", (ReviewComment ?? string.Empty).Trim(), 1000);
                if (await review.ExecuteNonQueryAsync() != 1)
                    throw new DBConcurrencyException("The imported project changed while it was being approved.");
            }

            await tx.CommitAsync();
            TempData["ProjectImportMessage"] = $"Project {company} / {projectNumber} was approved.";
        }
        catch (Exception ex)
        {
            await tx.RollbackAsync();
            TempData["ProjectImportError"] = "Approval failed: " + ex.Message;
        }

        return RedirectToPage(new { statusFilter = StatusFilter });
    }

    public async Task<IActionResult> OnPostRejectAsync()
    {
        if (ReviewId <= 0)
        {
            TempData["ProjectImportError"] = "No imported project was selected.";
            return RedirectToPage(new { statusFilter = StatusFilter });
        }

        await using var cn = await _connectionFactory.OpenAsync();
        await using var cmd = cn.CreateCommand();
        cmd.CommandText = @"
UPDATE dbo.ImportedProjects
SET Status = N'Rejected',
    ApprovedProjectId = NULL,
    ReviewedBy = @ReviewedBy,
    ReviewedAt = SYSUTCDATETIME(),
    ReviewAction = N'Rejected',
    ReviewComment = NULLIF(@ReviewComment, N'')
WHERE Id = @Id AND Status = N'Pending';";
        cmd.Parameters.AddBigInt("@Id", ReviewId);
        cmd.Parameters.AddNVarChar("@ReviewedBy", User.Identity?.Name, 256);
        cmd.Parameters.AddNVarChar("@ReviewComment", (ReviewComment ?? string.Empty).Trim(), 1000);

        var changed = await cmd.ExecuteNonQueryAsync();
        TempData[changed == 1 ? "ProjectImportMessage" : "ProjectImportError"] = changed == 1
            ? "The imported project was rejected."
            : "The imported project is no longer pending.";
        return RedirectToPage(new { statusFilter = StatusFilter });
    }

    public async Task<IActionResult> OnPostReopenAsync()
    {
        await using var cn = await _connectionFactory.OpenAsync();
        await using var cmd = cn.CreateCommand();
        cmd.CommandText = @"
UPDATE dbo.ImportedProjects
SET Status = N'Pending',
    ApprovedProjectId = NULL,
    ReviewedBy = NULL,
    ReviewedAt = NULL,
    ReviewAction = N'Reopened',
    ReviewComment = NULLIF(@ReviewComment, N'')
WHERE Id = @Id AND Status = N'Rejected';";
        cmd.Parameters.AddBigInt("@Id", ReviewId);
        cmd.Parameters.AddNVarChar("@ReviewComment", (ReviewComment ?? string.Empty).Trim(), 1000);
        var changed = await cmd.ExecuteNonQueryAsync();
        TempData[changed == 1 ? "ProjectImportMessage" : "ProjectImportError"] = changed == 1
            ? "The imported project was returned to the review queue."
            : "Only rejected projects can be reopened.";
        return RedirectToPage(new { statusFilter = StatusFilter });
    }

    private async Task LoadPageDataAsync()
    {
        if (TempData.TryGetValue("ProjectImportMessage", out var message))
        {
            Message = Convert.ToString(message);
        }
        if (TempData.TryGetValue("ProjectImportError", out var error))
        {
            Message = Convert.ToString(error);
            MessageIsError = true;
        }

        await using var cn = await _connectionFactory.OpenAsync();

        await using (var cmd = cn.CreateCommand())
        {
            cmd.CommandText = @"
SELECT TOP (20)
    Id, FileName, ImportedAt, ImportedBy, [RowCount], InsertedCount,
    UpdatedCount, UnchangedCount, SkippedCount, Status, ErrorMessage
FROM dbo.ProjectImportBatches
ORDER BY Id DESC;";

            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                RecentImports.Add(new ImportBatchRow
                {
                    Id = reader.GetInt64(0),
                    FileName = reader.GetString(1),
                    ImportedAt = reader.GetDateTime(2),
                    ImportedBy = reader.IsDBNull(3) ? "" : reader.GetString(3),
                    RowCount = reader.GetInt32(4),
                    InsertedCount = reader.GetInt32(5),
                    UpdatedCount = reader.GetInt32(6),
                    UnchangedCount = reader.GetInt32(7),
                    SkippedCount = reader.GetInt32(8),
                    Status = reader.GetString(9),
                    ErrorMessage = reader.IsDBNull(10) ? "" : reader.GetString(10)
                });
            }
        }

        var normalizedStatus = NormalizeStatusFilter(StatusFilter);
        StatusFilter = normalizedStatus;

        await using (var cmd = cn.CreateCommand())
        {
            cmd.CommandText = @"
SELECT TOP (1000)
    Id, Company, ProjectNumber, ProjectName, StartTime, StopTime,
    Status, SourceFileName, ImportedAt, LastSeenAt, ApprovedProjectId,
    ReviewedBy, ReviewedAt, ReviewAction, ReviewComment
FROM dbo.ImportedProjects
WHERE @Status = N'All' OR Status = @Status
ORDER BY
    CASE Status WHEN N'Pending' THEN 0 WHEN N'Approved' THEN 1 ELSE 2 END,
    Company, ProjectName, ProjectNumber;";
            cmd.Parameters.AddNVarChar("@Status", normalizedStatus, 20);

            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                ImportedProjects.Add(new ImportedProjectRow
                {
                    Id = reader.GetInt64(0),
                    Company = reader.GetString(1),
                    ProjectNumber = reader.GetString(2),
                    ProjectName = reader.GetString(3),
                    StartTime = reader.IsDBNull(4) ? null : reader.GetDateTime(4),
                    StopTime = reader.IsDBNull(5) ? null : reader.GetDateTime(5),
                    Status = reader.GetString(6),
                    SourceFileName = reader.GetString(7),
                    ImportedAt = reader.GetDateTime(8),
                    LastSeenAt = reader.GetDateTime(9),
                    ApprovedProjectId = reader.IsDBNull(10) ? null : reader.GetInt32(10),
                    ReviewedBy = reader.IsDBNull(11) ? "" : reader.GetString(11),
                    ReviewedAt = reader.IsDBNull(12) ? null : reader.GetDateTime(12),
                    ReviewAction = reader.IsDBNull(13) ? "" : reader.GetString(13),
                    ReviewComment = reader.IsDBNull(14) ? "" : reader.GetString(14)
                });
            }
        }
    }

    private async Task<ImportResult> ImportAsync(IReadOnlyCollection<ParsedProject> rows, string fileName)
    {
        await using var cn = await _connectionFactory.OpenAsync();
        await using var tx = (SqlTransaction)await cn.BeginTransactionAsync();
        long batchId;

        try
        {
            await using (var batchCommand = cn.CreateCommand())
            {
                batchCommand.Transaction = tx;
                batchCommand.CommandText = @"
INSERT INTO dbo.ProjectImportBatches
    (FileName, ImportedBy, [RowCount], ValidRowCount, Status)
OUTPUT INSERTED.Id
VALUES
    (@FileName, @ImportedBy, @RowCount, @RowCount, N'Processing');";
                batchCommand.Parameters.AddNVarChar("@FileName", fileName, 260);
                batchCommand.Parameters.AddNVarChar("@ImportedBy", User.Identity?.Name, 256);
                batchCommand.Parameters.AddInt("@RowCount", rows.Count);
                batchId = Convert.ToInt64(await batchCommand.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
            }

            await using (var createTemp = cn.CreateCommand())
            {
                createTemp.Transaction = tx;
                createTemp.CommandText = @"
CREATE TABLE #IncomingProjects
(
    Company nvarchar(256) NOT NULL,
    ProjectNumber nvarchar(100) NOT NULL,
    ProjectName nvarchar(256) NOT NULL,
    StartTime datetime2(0) NULL,
    StopTime datetime2(0) NULL,
    SourceHash varbinary(32) NOT NULL
);";
                await createTemp.ExecuteNonQueryAsync();
            }

            var table = BuildDataTable(rows);
            using (var bulk = new SqlBulkCopy(cn, SqlBulkCopyOptions.CheckConstraints, tx))
            {
                bulk.DestinationTableName = "#IncomingProjects";
                bulk.BatchSize = 2000;
                bulk.BulkCopyTimeout = 120;
                foreach (DataColumn column in table.Columns)
                {
                    bulk.ColumnMappings.Add(column.ColumnName, column.ColumnName);
                }
                await bulk.WriteToServerAsync(table);
            }

            ImportResult result;
            await using (var command = cn.CreateCommand())
            {
                command.Transaction = tx;
                command.CommandText = @"
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

SELECT @Inserted, @Updated, @Unchanged, @Skipped;";
                command.Parameters.AddBigInt("@BatchId", batchId);
                command.Parameters.AddNVarChar("@FileName", fileName, 260);

                await using var reader = await command.ExecuteReaderAsync();
                await reader.ReadAsync();
                result = new ImportResult(
                    rows.Count,
                    reader.GetInt32(0),
                    reader.GetInt32(1),
                    reader.GetInt32(2),
                    reader.GetInt32(3));
            }

            await tx.CommitAsync();
            return result;
        }
        catch
        {
            await tx.RollbackAsync();
            throw;
        }
    }

    private static DataTable BuildDataTable(IEnumerable<ParsedProject> rows)
    {
        var table = new DataTable();
        table.Columns.Add("Company", typeof(string));
        table.Columns.Add("ProjectNumber", typeof(string));
        table.Columns.Add("ProjectName", typeof(string));
        table.Columns.Add("StartTime", typeof(DateTime));
        table.Columns.Add("StopTime", typeof(DateTime));
        table.Columns.Add("SourceHash", typeof(byte[]));

        foreach (var project in rows)
        {
            table.Rows.Add(
                project.Company,
                project.ProjectNumber,
                project.ProjectName,
                project.StartTime.HasValue ? project.StartTime.Value : DBNull.Value,
                project.StopTime.HasValue ? project.StopTime.Value : DBNull.Value,
                project.SourceHash);
        }

        return table;
    }

    private static async Task<List<ParsedProject>> ParseCsvAsync(IFormFile file, ICollection<ImportError> errors)
    {
        var temporaryFile = Path.GetTempFileName();
        try
        {
            await using (var destination = System.IO.File.Create(temporaryFile))
            {
                await file.CopyToAsync(destination);
            }

            using var parser = new TextFieldParser(temporaryFile, new UTF8Encoding(true, true), true)
            {
                TextFieldType = FieldType.Delimited,
                HasFieldsEnclosedInQuotes = true,
                TrimWhiteSpace = false
            };

            var firstLine = parser.ReadLine();
            if (firstLine is null)
            {
                throw new InvalidDataException("The CSV file is empty.");
            }

            var delimiter = CountOutsideQuotes(firstLine, ';') > CountOutsideQuotes(firstLine, ',') ? ";" : ",";
            parser.SetDelimiters(delimiter);

            // Reopen because delimiter detection consumed the header.
            parser.Close();
            using var actualParser = new TextFieldParser(temporaryFile, new UTF8Encoding(true, true), true)
            {
                TextFieldType = FieldType.Delimited,
                HasFieldsEnclosedInQuotes = true,
                TrimWhiteSpace = true
            };
            actualParser.SetDelimiters(delimiter);

            var headers = actualParser.ReadFields() ?? Array.Empty<string>();
            var expected = new[] { "Company", "ProjectNumber", "ProjectName", "StartTime", "StopTime" };
            if (headers.Length != expected.Length || !headers.Select(NormalizeHeader).SequenceEqual(expected, StringComparer.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("The CSV header must be: Company, ProjectNumber, ProjectName, StartTime, StopTime.");
            }

            var result = new List<ParsedProject>();
            var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var lineNumber = 1;

            while (!actualParser.EndOfData)
            {
                lineNumber++;
                string[]? fields;
                try
                {
                    fields = actualParser.ReadFields();
                }
                catch (MalformedLineException ex)
                {
                    errors.Add(new ImportError(lineNumber, ex.Message));
                    continue;
                }

                if (fields is null || fields.All(string.IsNullOrWhiteSpace))
                    continue;

                if (result.Count >= MaximumRows)
                    throw new InvalidDataException($"The file contains more than {MaximumRows:N0} project rows.");

                if (fields.Length != 5)
                {
                    errors.Add(new ImportError(lineNumber, $"Expected 5 columns, found {fields.Length}."));
                    continue;
                }

                var company = fields[0].Trim();
                var projectNumber = fields[1].Trim();
                var projectName = fields[2].Trim();

                if (company.Length == 0 || company.Length > 256)
                    errors.Add(new ImportError(lineNumber, "Company is required and may contain at most 256 characters."));
                if (projectNumber.Length == 0 || projectNumber.Length > 100)
                    errors.Add(new ImportError(lineNumber, "ProjectNumber is required and may contain at most 100 characters."));
                if (projectName.Length == 0 || projectName.Length > 256)
                    errors.Add(new ImportError(lineNumber, "ProjectName is required and may contain at most 256 characters."));

                var startOk = TryParseAccountingDate(fields[3], false, out var startTime);
                var stopOk = TryParseAccountingDate(fields[4], true, out var stopTime);
                if (!startOk)
                    errors.Add(new ImportError(lineNumber, $"Invalid StartTime '{fields[3]}'."));
                if (!stopOk)
                    errors.Add(new ImportError(lineNumber, $"Invalid StopTime '{fields[4]}'."));

                var key = company + "\u001f" + projectNumber;
                if (!keys.Add(key))
                    errors.Add(new ImportError(lineNumber, $"Duplicate Company/ProjectNumber in the file: {company} / {projectNumber}."));

                if (errors.Any(error => error.LineNumber == lineNumber))
                    continue;

                var canonical = string.Join("\u001f", company, projectNumber, projectName,
                    startTime?.ToString("O", CultureInfo.InvariantCulture) ?? "",
                    stopTime?.ToString("O", CultureInfo.InvariantCulture) ?? "");
                var hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
                result.Add(new ParsedProject(company, projectNumber, projectName, startTime, stopTime, hash));
            }

            return result;
        }
        finally
        {
            System.IO.File.Delete(temporaryFile);
        }
    }

    private static bool TryParseAccountingDate(string? value, bool openEndedStopDate, out DateTime? result)
    {
        result = null;
        if (string.IsNullOrWhiteSpace(value))
            return true;

        var formats = new[]
        {
            "yyyy-MM-dd'T'HH.mm.ss", "yyyy-MM-dd'T'HH:mm:ss",
            "yyyy-MM-dd HH.mm.ss", "yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd"
        };

        if (!DateTime.TryParseExact(value.Trim(), formats, CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeLocal, out var parsed)
            && !DateTime.TryParse(value.Trim(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out parsed))
        {
            return false;
        }

        if (openEndedStopDate && parsed.Year >= 2069)
        {
            result = null;
            return true;
        }

        result = DateTime.SpecifyKind(parsed, DateTimeKind.Unspecified);
        return true;
    }

    private static int CountOutsideQuotes(string value, char delimiter)
    {
        var count = 0;
        var quoted = false;
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] == '"')
            {
                if (quoted && i + 1 < value.Length && value[i + 1] == '"')
                    i++;
                else
                    quoted = !quoted;
            }
            else if (!quoted && value[i] == delimiter)
            {
                count++;
            }
        }
        return count;
    }

    private static string NormalizeHeader(string value) => value.Trim().TrimStart('\uFEFF');

    private static string NormalizeStatusFilter(string? value) => value switch
    {
        "Approved" => "Approved",
        "Rejected" => "Rejected",
        "All" => "All",
        _ => "Pending"
    };

    private void SetError(string message)
    {
        Message = message;
        MessageIsError = true;
    }

    private sealed record ParsedProject(string Company, string ProjectNumber, string ProjectName,
        DateTime? StartTime, DateTime? StopTime, byte[] SourceHash);

    private sealed record ImportResult(int Total, int Inserted, int Updated, int Unchanged, int Skipped);

    public sealed record ImportError(int LineNumber, string Message);

    public sealed class ImportBatchRow
    {
        public long Id { get; init; }
        public string FileName { get; init; } = "";
        public DateTime ImportedAt { get; init; }
        public string ImportedBy { get; init; } = "";
        public int RowCount { get; init; }
        public int InsertedCount { get; init; }
        public int UpdatedCount { get; init; }
        public int UnchangedCount { get; init; }
        public int SkippedCount { get; init; }
        public string Status { get; init; } = "";
        public string ErrorMessage { get; init; } = "";
    }

    public sealed class ImportedProjectRow
    {
        public long Id { get; init; }
        public string Company { get; init; } = "";
        public string ProjectNumber { get; init; } = "";
        public string ProjectName { get; init; } = "";
        public DateTime? StartTime { get; init; }
        public DateTime? StopTime { get; init; }
        public string Status { get; init; } = "";
        public string SourceFileName { get; init; } = "";
        public DateTime ImportedAt { get; init; }
        public DateTime LastSeenAt { get; init; }
        public int? ApprovedProjectId { get; init; }
        public string ReviewedBy { get; init; } = "";
        public DateTime? ReviewedAt { get; init; }
        public string ReviewAction { get; init; } = "";
        public string ReviewComment { get; init; } = "";
    }
}

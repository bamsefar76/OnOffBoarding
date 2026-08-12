using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Data.SqlClient;
using UserChangeQueueWeb.Services;

namespace UserChangeQueueWeb.Pages.Employees;

[Authorize]
public sealed class MaintenanceModel : PageModel
{
    private const int PageSize = 30;
    private readonly SqlConnectionFactory _connectionFactory;

    public MaintenanceModel(SqlConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    [BindProperty(SupportsGet = true)] public string? Search { get; set; }
    [BindProperty(SupportsGet = true)] public string? StatusFilter { get; set; }
    [BindProperty(SupportsGet = true)] public string? StateFilter { get; set; }
    [BindProperty(SupportsGet = true)] public int ReviewDays { get; set; } = 180;
    [BindProperty(SupportsGet = true)] public int PageNumber { get; set; } = 1;
    [BindProperty(SupportsGet = true)] public long? EditId { get; set; }

    [BindProperty] public EmployeeEditInput Edit { get; set; } = new();
    [BindProperty] public string? DeleteReason { get; set; }

    [TempData] public string? MessageKey { get; set; }

    public List<EmployeeRow> Employees { get; } = new();
    public List<string> StatusOptions { get; } = new();
    public List<AuditRow> AuditRows { get; } = new();
    public EmployeeEditor? SelectedEmployee { get; private set; }
    public SummaryCounts Summary { get; private set; } = new();
    public int TotalRows { get; private set; }
    public int TotalPages => Math.Max(1, (int)Math.Ceiling(TotalRows / (double)PageSize));

    public async Task OnGetAsync()
    {
        NormalizeFilters();
        await LoadAsync();
    }

    public async Task<IActionResult> OnPostSaveAsync()
    {
        NormalizeFilters();
        NormalizeEdit();

        if (Edit.EmployeeId <= 0 || string.IsNullOrWhiteSpace(Edit.GivenName) || string.IsNullOrWhiteSpace(Edit.Surname))
        {
            MessageKey = "employeeMaintenance.message.required";
            EditId = Edit.EmployeeId > 0 ? Edit.EmployeeId : null;
            return RedirectToCurrentPage();
        }

        await using var cn = await _connectionFactory.OpenAsync(HttpContext.RequestAborted);
        await using var tx = (SqlTransaction)await cn.BeginTransactionAsync(HttpContext.RequestAborted);

        var before = await LoadSnapshotAsync(cn, Edit.EmployeeId, tx);
        if (before is null)
        {
            await tx.RollbackAsync(HttpContext.RequestAborted);
            MessageKey = "employeeMaintenance.message.notFound";
            return RedirectToCurrentPage(clearEdit: true);
        }

        if (string.Equals(before.Status, "Merged", StringComparison.OrdinalIgnoreCase))
        {
            await tx.RollbackAsync(HttpContext.RequestAborted);
            MessageKey = "employeeMaintenance.message.mergedReadOnly";
            EditId = Edit.EmployeeId;
            return RedirectToCurrentPage();
        }

        if (!await IsAllowedStatusAsync(cn, Edit.Status, tx))
        {
            await tx.RollbackAsync(HttpContext.RequestAborted);
            MessageKey = "employeeMaintenance.message.invalidStatus";
            EditId = Edit.EmployeeId;
            return RedirectToCurrentPage();
        }

        var changedBy = User.Identity?.Name ?? Environment.UserName;
        await using (var cmd = cn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = @"
UPDATE dbo.Employees
SET CanonicalGivenName = @GivenName,
    CanonicalSurname = @Surname,
    PrivateEmail = @PrivateEmail,
    NormalizedPrivateEmail = @NormalizedPrivateEmail,
    MobilePhone = @MobilePhone,
    NormalizedMobilePhone = @NormalizedMobilePhone,
    Status = @Status,
    IsTestData = @IsTestData,
    MaintenanceNote = @MaintenanceNote,
    LastReviewedAt = SYSDATETIME(),
    LastReviewedBy = @ChangedBy,
    UpdatedBy = @ChangedBy
WHERE EmployeeId = @EmployeeId;";
            cmd.Parameters.Add(new SqlParameter("@EmployeeId", System.Data.SqlDbType.BigInt) { Value = Edit.EmployeeId });
            cmd.Parameters.AddNVarChar("@GivenName", Edit.GivenName, 200);
            cmd.Parameters.AddNVarChar("@Surname", Edit.Surname, 200);
            cmd.Parameters.AddNVarChar("@PrivateEmail", Edit.PrivateEmail, 320);
            cmd.Parameters.AddNVarChar("@NormalizedPrivateEmail", PersonMatchingService.NormalizeEmail(Edit.PrivateEmail), 320);
            cmd.Parameters.AddNVarChar("@MobilePhone", Edit.MobilePhone, 100);
            cmd.Parameters.AddNVarChar("@NormalizedMobilePhone", PersonMatchingService.NormalizePhone(Edit.MobilePhone), 50);
            cmd.Parameters.AddNVarChar("@Status", Edit.Status, 50);
            cmd.Parameters.AddBit("@IsTestData", Edit.IsTestData);
            cmd.Parameters.AddNVarChar("@MaintenanceNote", Edit.MaintenanceNote, 1000);
            cmd.Parameters.AddNVarChar("@ChangedBy", changedBy, 300);
            await cmd.ExecuteNonQueryAsync(HttpContext.RequestAborted);
        }

        var after = await LoadSnapshotAsync(cn, Edit.EmployeeId, tx);
        await InsertAuditAsync(cn, tx, Edit.EmployeeId, "Update", changedBy, Edit.MaintenanceNote, before, after);
        await tx.CommitAsync(HttpContext.RequestAborted);

        MessageKey = "employeeMaintenance.message.saved";
        EditId = Edit.EmployeeId;
        return RedirectToCurrentPage();
    }

    public async Task<IActionResult> OnPostReviewAsync(long employeeId)
    {
        NormalizeFilters();
        if (employeeId <= 0)
        {
            MessageKey = "employeeMaintenance.message.notFound";
            return RedirectToCurrentPage(clearEdit: true);
        }

        await using var cn = await _connectionFactory.OpenAsync(HttpContext.RequestAborted);
        await using var tx = (SqlTransaction)await cn.BeginTransactionAsync(HttpContext.RequestAborted);
        var before = await LoadSnapshotAsync(cn, employeeId, tx);
        if (before is null)
        {
            await tx.RollbackAsync(HttpContext.RequestAborted);
            MessageKey = "employeeMaintenance.message.notFound";
            return RedirectToCurrentPage(clearEdit: true);
        }

        if (string.Equals(before.Status, "Merged", StringComparison.OrdinalIgnoreCase))
        {
            await tx.RollbackAsync(HttpContext.RequestAborted);
            MessageKey = "employeeMaintenance.message.mergedReadOnly";
            EditId = employeeId;
            return RedirectToCurrentPage();
        }

        var changedBy = User.Identity?.Name ?? Environment.UserName;
        await using (var cmd = cn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = @"
UPDATE dbo.Employees
SET LastReviewedAt = SYSDATETIME(),
    LastReviewedBy = @ChangedBy,
    UpdatedBy = @ChangedBy
WHERE EmployeeId = @EmployeeId;";
            cmd.Parameters.Add(new SqlParameter("@EmployeeId", System.Data.SqlDbType.BigInt) { Value = employeeId });
            cmd.Parameters.AddNVarChar("@ChangedBy", changedBy, 300);
            await cmd.ExecuteNonQueryAsync(HttpContext.RequestAborted);
        }

        var after = await LoadSnapshotAsync(cn, employeeId, tx);
        await InsertAuditAsync(cn, tx, employeeId, "Review", changedBy, null, before, after);
        await tx.CommitAsync(HttpContext.RequestAborted);

        MessageKey = "employeeMaintenance.message.reviewed";
        EditId = employeeId;
        return RedirectToCurrentPage();
    }

    public async Task<IActionResult> OnPostClearAdLinkAsync(long employeeId)
    {
        NormalizeFilters();
        if (employeeId <= 0)
        {
            MessageKey = "employeeMaintenance.message.notFound";
            return RedirectToCurrentPage(clearEdit: true);
        }

        await using var cn = await _connectionFactory.OpenAsync(HttpContext.RequestAborted);
        await using var tx = (SqlTransaction)await cn.BeginTransactionAsync(HttpContext.RequestAborted);
        var before = await LoadSnapshotAsync(cn, employeeId, tx);
        if (before is null)
        {
            await tx.RollbackAsync(HttpContext.RequestAborted);
            MessageKey = "employeeMaintenance.message.notFound";
            return RedirectToCurrentPage(clearEdit: true);
        }

        if (string.Equals(before.Status, "Merged", StringComparison.OrdinalIgnoreCase))
        {
            await tx.RollbackAsync(HttpContext.RequestAborted);
            MessageKey = "employeeMaintenance.message.mergedReadOnly";
            EditId = employeeId;
            return RedirectToCurrentPage();
        }

        if (!before.IsTestData && await HasLiveAdLinkAsync(cn, employeeId, tx))
        {
            await tx.RollbackAsync(HttpContext.RequestAborted);
            MessageKey = "employeeMaintenance.message.clearAdBlockedLive";
            EditId = employeeId;
            return RedirectToCurrentPage();
        }

        var changedBy = User.Identity?.Name ?? Environment.UserName;
        await using (var cmd = cn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = @"
UPDATE dbo.Employees
SET CurrentADObjectGuid = NULL,
    CurrentSamAccountName = NULL,
    CurrentUPN = NULL,
    Status = CASE WHEN Status = N'Active' THEN N'Inactive' ELSE Status END,
    LastReviewedAt = SYSDATETIME(),
    LastReviewedBy = @ChangedBy,
    UpdatedBy = @ChangedBy
WHERE EmployeeId = @EmployeeId;";
            cmd.Parameters.Add(new SqlParameter("@EmployeeId", System.Data.SqlDbType.BigInt) { Value = employeeId });
            cmd.Parameters.AddNVarChar("@ChangedBy", changedBy, 300);
            await cmd.ExecuteNonQueryAsync(HttpContext.RequestAborted);
        }

        var after = await LoadSnapshotAsync(cn, employeeId, tx);
        await InsertAuditAsync(cn, tx, employeeId, "ClearAdLink", changedBy, null, before, after);
        await tx.CommitAsync(HttpContext.RequestAborted);

        MessageKey = "employeeMaintenance.message.adLinkCleared";
        EditId = employeeId;
        return RedirectToCurrentPage();
    }

    public async Task<IActionResult> OnPostDeleteAsync(long employeeId)
    {
        NormalizeFilters();
        DeleteReason = NullIfWhiteSpace(DeleteReason);
        if (employeeId <= 0)
        {
            MessageKey = "employeeMaintenance.message.notFound";
            return RedirectToCurrentPage(clearEdit: true);
        }
        if (string.IsNullOrWhiteSpace(DeleteReason))
        {
            MessageKey = "employeeMaintenance.message.deleteReasonRequired";
            EditId = employeeId;
            return RedirectToCurrentPage();
        }

        await using var cn = await _connectionFactory.OpenAsync(HttpContext.RequestAborted);
        await using var tx = (SqlTransaction)await cn.BeginTransactionAsync(HttpContext.RequestAborted);
        var before = await LoadSnapshotAsync(cn, employeeId, tx);
        if (before is null)
        {
            await tx.RollbackAsync(HttpContext.RequestAborted);
            MessageKey = "employeeMaintenance.message.notFound";
            return RedirectToCurrentPage(clearEdit: true);
        }

        if (string.Equals(before.Status, "Merged", StringComparison.OrdinalIgnoreCase))
        {
            await tx.RollbackAsync(HttpContext.RequestAborted);
            MessageKey = "employeeMaintenance.message.mergedReadOnly";
            EditId = employeeId;
            return RedirectToCurrentPage();
        }

        var references = await LoadProtectedReferencesAsync(cn, employeeId, tx);
        if (references.Assignments > 0 || references.Requests > 0)
        {
            await tx.RollbackAsync(HttpContext.RequestAborted);
            MessageKey = "employeeMaintenance.message.deleteBlockedHistory";
            EditId = employeeId;
            return RedirectToCurrentPage();
        }
        if (!references.SafeMaintenanceDelete)
        {
            await tx.RollbackAsync(HttpContext.RequestAborted);
            MessageKey = "employeeMaintenance.message.deleteNotCandidate";
            EditId = employeeId;
            return RedirectToCurrentPage();
        }

        var changedBy = User.Identity?.Name ?? Environment.UserName;
        await InsertAuditAsync(cn, tx, employeeId, "Delete", changedBy, DeleteReason, before, null);

        try
        {
            await using var cmd = cn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = @"
IF OBJECT_ID(N'dbo.EmployeeDuplicateDismissals', N'U') IS NOT NULL
    DELETE FROM dbo.EmployeeDuplicateDismissals WHERE EmployeeId1 = @EmployeeId OR EmployeeId2 = @EmployeeId;

IF OBJECT_ID(N'dbo.EmployeeNames', N'U') IS NOT NULL
    DELETE FROM dbo.EmployeeNames WHERE EmployeeId = @EmployeeId;

DELETE FROM dbo.Employees WHERE EmployeeId = @EmployeeId;";
            cmd.Parameters.Add(new SqlParameter("@EmployeeId", System.Data.SqlDbType.BigInt) { Value = employeeId });
            await cmd.ExecuteNonQueryAsync(HttpContext.RequestAborted);
            await tx.CommitAsync(HttpContext.RequestAborted);
        }
        catch (SqlException ex) when (ex.Number == 547)
        {
            await tx.RollbackAsync(HttpContext.RequestAborted);
            MessageKey = "employeeMaintenance.message.deleteBlockedReference";
            EditId = employeeId;
            return RedirectToCurrentPage();
        }

        MessageKey = "employeeMaintenance.message.deleted";
        return RedirectToCurrentPage(clearEdit: true);
    }

    private async Task LoadAsync()
    {
        await using var cn = await _connectionFactory.OpenAsync(HttpContext.RequestAborted);
        await LoadStatusOptionsAsync(cn);
        await LoadSummaryAsync(cn);
        await LoadEmployeesAsync(cn);

        if (EditId.HasValue && EditId.Value > 0)
        {
            SelectedEmployee = await LoadEditorAsync(cn, EditId.Value);
            if (SelectedEmployee is not null)
            {
                Edit = new EmployeeEditInput
                {
                    EmployeeId = SelectedEmployee.EmployeeId,
                    GivenName = SelectedEmployee.GivenName,
                    Surname = SelectedEmployee.Surname,
                    PrivateEmail = SelectedEmployee.PrivateEmail,
                    MobilePhone = SelectedEmployee.MobilePhone,
                    Status = SelectedEmployee.Status,
                    IsTestData = SelectedEmployee.IsTestData,
                    MaintenanceNote = SelectedEmployee.MaintenanceNote
                };
                await LoadAuditAsync(cn, SelectedEmployee.EmployeeId);
            }
        }
    }

    private async Task LoadStatusOptionsAsync(SqlConnection cn)
    {
        await using var cmd = cn.CreateCommand();
        cmd.CommandText = @"
SELECT DISTINCT Status
FROM dbo.Employees
WHERE NULLIF(LTRIM(RTRIM(Status)), N'') IS NOT NULL
ORDER BY Status;";
        await using var reader = await cmd.ExecuteReaderAsync(HttpContext.RequestAborted);
        while (await reader.ReadAsync(HttpContext.RequestAborted))
        {
            StatusOptions.Add(reader.GetString(0));
        }
    }

    private async Task LoadSummaryAsync(SqlConnection cn)
    {
        await using var cmd = cn.CreateCommand();
        cmd.CommandText = @"
;WITH CurrentAssignments AS
(
    SELECT
        a.EmployeeId,
        COUNT_BIG(*) AS CurrentAssignmentCount
    FROM dbo.Assignments a
    WHERE a.EndDate IS NULL
       OR a.EndDate >= CAST(GETDATE() AS date)
    GROUP BY a.EmployeeId
),
QueueIdentityLinks AS
(
    SELECT DISTINCT queueIdentity.EmployeeId
    FROM dbo.ADUserChangeQueue queueIdentity
    INNER JOIN dbo.ADObjects queueAd
        ON ISNULL(queueAd.IsDeleted, 0) = 0
       AND
       (
           (queueIdentity.TargetObjectGUID IS NOT NULL AND queueAd.ObjectGUID = queueIdentity.TargetObjectGUID)
           OR
           (queueIdentity.TargetObjectGUID IS NULL
            AND queueAd.SamAccountName = COALESCE(NULLIF(queueIdentity.TargetSamAccountName, N''), NULLIF(queueIdentity.NewSamAccountName, N'')))
       )
    WHERE queueIdentity.EmployeeId IS NOT NULL
)
SELECT
    COUNT_BIG(*) AS TotalCount,
    SUM(CASE WHEN e.IsTestData = 1 THEN 1 ELSE 0 END) AS TestCount,
    SUM(CASE WHEN e.Status <> N'Merged' AND (e.LastReviewedAt IS NULL OR e.LastReviewedAt < DATEADD(day, -@ReviewDays, SYSDATETIME())) THEN 1 ELSE 0 END) AS NeedsReviewCount,
    SUM(CASE WHEN e.Status <> N'Merged'
                  AND (ad.ObjectGUID IS NULL OR ISNULL(ad.IsDeleted, 0) = 1)
                  AND ISNULL(currentAssignment.CurrentAssignmentCount, 0) = 0
                  AND queueIdentity.EmployeeId IS NULL
             THEN 1 ELSE 0 END) AS StaleCount
FROM dbo.Employees e
LEFT JOIN dbo.ADObjects ad ON ad.ObjectGUID = e.CurrentADObjectGuid
LEFT JOIN CurrentAssignments currentAssignment ON currentAssignment.EmployeeId = e.EmployeeId
LEFT JOIN QueueIdentityLinks queueIdentity ON queueIdentity.EmployeeId = e.EmployeeId;";
        cmd.Parameters.Add(new SqlParameter("@ReviewDays", System.Data.SqlDbType.Int) { Value = ReviewDays });
        await using var reader = await cmd.ExecuteReaderAsync(HttpContext.RequestAborted);
        if (await reader.ReadAsync(HttpContext.RequestAborted))
        {
            Summary = new SummaryCounts
            {
                Total = reader.IsDBNull(0) ? 0 : Convert.ToInt32(reader.GetInt64(0)),
                Test = reader.IsDBNull(1) ? 0 : Convert.ToInt32(reader.GetValue(1)),
                NeedsReview = reader.IsDBNull(2) ? 0 : Convert.ToInt32(reader.GetValue(2)),
                Stale = reader.IsDBNull(3) ? 0 : Convert.ToInt32(reader.GetValue(3))
            };
        }
    }

    private async Task LoadEmployeesAsync(SqlConnection cn)
    {
        var whereSql = BuildWhereSql();

        await using (var countCmd = cn.CreateCommand())
        {
            countCmd.CommandText = $@"
SELECT COUNT_BIG(*)
FROM dbo.Employees e
{whereSql};";
            AddWhereParameters(countCmd);
            var countValue = await countCmd.ExecuteScalarAsync(HttpContext.RequestAborted);
            TotalRows = countValue is null or DBNull ? 0 : Convert.ToInt32(countValue);
        }

        if (PageNumber > TotalPages) PageNumber = TotalPages;
        var offset = (PageNumber - 1) * PageSize;

        await using var cmd = cn.CreateCommand();
        cmd.CommandText = $@"
SELECT
    e.EmployeeId,
    e.CanonicalGivenName,
    e.CanonicalSurname,
    e.PrivateEmail,
    e.MobilePhone,
    COALESCE(NULLIF(e.CurrentSamAccountName, N''), queueAd.SamAccountName, N'') AS CurrentSamAccountName,
    COALESCE(NULLIF(e.CurrentUPN, N''), queueAd.UserPrincipalName, N'') AS CurrentUPN,
    e.Status,
    e.IsTestData,
    e.LastReviewedAt,
    e.LastReviewedBy,
    e.MaintenanceNote,
    CASE WHEN EXISTS
    (
        SELECT 1
        FROM dbo.ADObjects ad
        WHERE ad.ObjectGUID = e.CurrentADObjectGuid
          AND ISNULL(ad.IsDeleted, 0) = 0
    ) OR queueAd.ObjectGUID IS NOT NULL
      THEN CAST(1 AS bit) ELSE CAST(0 AS bit) END AS HasLiveAd,
    CONVERT(int, (SELECT COUNT_BIG(*) FROM dbo.Assignments a WHERE a.EmployeeId = e.EmployeeId)) AS AssignmentCount,
    CONVERT(int, (SELECT COUNT_BIG(*)
                  FROM dbo.Assignments a
                  WHERE a.EmployeeId = e.EmployeeId
                    AND (a.EndDate IS NULL OR a.EndDate >= CAST(GETDATE() AS date)))) AS CurrentAssignmentCount,
    (SELECT MAX(COALESCE(a.EndDate, a.StartDate))
     FROM dbo.Assignments a
     WHERE a.EmployeeId = e.EmployeeId) AS LastAssignmentDate,
    CONVERT(int, (SELECT COUNT_BIG(*) FROM dbo.ADUserChangeQueue q WHERE q.EmployeeId = e.EmployeeId)) AS RequestCount
FROM dbo.Employees e
OUTER APPLY
(
    SELECT TOP (1)
        ad.ObjectGUID,
        ad.SamAccountName,
        ad.UserPrincipalName
    FROM dbo.ADUserChangeQueue q
    INNER JOIN dbo.ADObjects ad
        ON ISNULL(ad.IsDeleted, 0) = 0
       AND
       (
           (q.TargetObjectGUID IS NOT NULL AND ad.ObjectGUID = q.TargetObjectGUID)
           OR
           (q.TargetObjectGUID IS NULL
            AND ad.SamAccountName = COALESCE(NULLIF(q.TargetSamAccountName, N''), NULLIF(q.NewSamAccountName, N'')))
       )
    WHERE q.EmployeeId = e.EmployeeId
    ORDER BY q.RequestId DESC
) queueAd
{whereSql}
ORDER BY
    CASE WHEN e.IsTestData = 1 THEN 0 ELSE 1 END,
    CASE WHEN e.LastReviewedAt IS NULL THEN 0 ELSE 1 END,
    e.LastReviewedAt,
    e.EmployeeId DESC
OFFSET @Offset ROWS FETCH NEXT @PageSize ROWS ONLY;";
        AddWhereParameters(cmd);
        cmd.Parameters.Add(new SqlParameter("@Offset", System.Data.SqlDbType.Int) { Value = offset });
        cmd.Parameters.Add(new SqlParameter("@PageSize", System.Data.SqlDbType.Int) { Value = PageSize });

        await using var reader = await cmd.ExecuteReaderAsync(HttpContext.RequestAborted);
        while (await reader.ReadAsync(HttpContext.RequestAborted))
        {
            Employees.Add(new EmployeeRow
            {
                EmployeeId = reader.GetInt64(0),
                GivenName = Get(reader, 1),
                Surname = Get(reader, 2),
                PrivateEmail = Get(reader, 3),
                MobilePhone = Get(reader, 4),
                CurrentSamAccountName = Get(reader, 5),
                CurrentUPN = Get(reader, 6),
                Status = Get(reader, 7),
                IsTestData = reader.GetBoolean(8),
                LastReviewedAt = reader.IsDBNull(9) ? null : reader.GetDateTime(9),
                LastReviewedBy = Get(reader, 10),
                MaintenanceNote = Get(reader, 11),
                HasLiveAd = reader.GetBoolean(12),
                AssignmentCount = reader.GetInt32(13),
                CurrentAssignmentCount = reader.GetInt32(14),
                LastAssignmentDate = reader.IsDBNull(15) ? null : reader.GetDateTime(15),
                RequestCount = reader.GetInt32(16)
            });
        }

        await reader.DisposeAsync();
        await LoadVisibleReferencesAsync(cn);
    }

    private async Task LoadVisibleReferencesAsync(SqlConnection cn)
    {
        if (Employees.Count == 0) return;

        var byId = Employees.ToDictionary(e => e.EmployeeId);
        var parameterNames = new List<string>(Employees.Count);

        await using (var assignmentCmd = cn.CreateCommand())
        {
            for (var i = 0; i < Employees.Count; i++)
            {
                var name = $"@EmployeeId{i}";
                parameterNames.Add(name);
                assignmentCmd.Parameters.Add(new SqlParameter(name, System.Data.SqlDbType.BigInt) { Value = Employees[i].EmployeeId });
            }

            assignmentCmd.CommandText = $@"
SELECT
    a.EmployeeId,
    a.AssignmentId,
    ISNULL(a.ProjectNumber, N''),
    ISNULL(a.ProjectName, N''),
    a.StartDate,
    a.EndDate,
    ISNULL(a.Status, N'')
FROM dbo.Assignments a
WHERE a.EmployeeId IN ({string.Join(", ", parameterNames)})
ORDER BY a.EmployeeId, a.StartDate DESC, a.AssignmentId DESC;";

            await using var assignmentReader = await assignmentCmd.ExecuteReaderAsync(HttpContext.RequestAborted);
            while (await assignmentReader.ReadAsync(HttpContext.RequestAborted))
            {
                var employeeId = assignmentReader.GetInt64(0);
                if (!byId.TryGetValue(employeeId, out var employee)) continue;
                employee.AssignmentReferences.Add(new AssignmentReference
                {
                    AssignmentId = assignmentReader.GetInt64(1),
                    ProjectNumber = Get(assignmentReader, 2),
                    ProjectName = Get(assignmentReader, 3),
                    StartDate = assignmentReader.GetDateTime(4),
                    EndDate = assignmentReader.IsDBNull(5) ? null : assignmentReader.GetDateTime(5),
                    Status = Get(assignmentReader, 6)
                });
            }
        }

        parameterNames.Clear();
        await using (var requestCmd = cn.CreateCommand())
        {
            for (var i = 0; i < Employees.Count; i++)
            {
                var name = $"@EmployeeId{i}";
                parameterNames.Add(name);
                requestCmd.Parameters.Add(new SqlParameter(name, System.Data.SqlDbType.BigInt) { Value = Employees[i].EmployeeId });
            }

            requestCmd.CommandText = $@"
SELECT
    q.EmployeeId,
    q.RequestId,
    ISNULL(q.RequestType, N''),
    ISNULL(q.Status, N''),
    q.ExecuteAfter,
    q.CreatedAt,
    ISNULL(q.ProjectNumber, N''),
    ISNULL(q.ProjectName, N'')
FROM dbo.ADUserChangeQueue q
WHERE q.EmployeeId IN ({string.Join(", ", parameterNames)})
ORDER BY q.EmployeeId, q.CreatedAt DESC, q.RequestId DESC;";

            await using var requestReader = await requestCmd.ExecuteReaderAsync(HttpContext.RequestAborted);
            while (await requestReader.ReadAsync(HttpContext.RequestAborted))
            {
                var employeeId = requestReader.GetInt64(0);
                if (!byId.TryGetValue(employeeId, out var employee)) continue;
                employee.RequestReferences.Add(new RequestReference
                {
                    RequestId = requestReader.GetInt64(1),
                    RequestType = Get(requestReader, 2),
                    Status = Get(requestReader, 3),
                    ExecuteAfter = requestReader.IsDBNull(4) ? null : requestReader.GetDateTime(4),
                    CreatedAt = requestReader.IsDBNull(5) ? null : requestReader.GetDateTime(5),
                    ProjectNumber = Get(requestReader, 6),
                    ProjectName = Get(requestReader, 7)
                });
            }
        }
    }

    private string BuildWhereSql()
    {
        var clauses = new List<string>();

        if (!string.IsNullOrWhiteSpace(Search))
        {
            clauses.Add(@"
(
    CONVERT(nvarchar(30), e.EmployeeId) = @Search
    OR CONCAT(ISNULL(e.CanonicalGivenName, N''), N' ', ISNULL(e.CanonicalSurname, N'')) LIKE N'%' + @Search + N'%'
    OR ISNULL(e.PrivateEmail, N'') LIKE N'%' + @Search + N'%'
    OR ISNULL(e.MobilePhone, N'') LIKE N'%' + @Search + N'%'
    OR ISNULL(e.CurrentSamAccountName, N'') LIKE N'%' + @Search + N'%'
    OR ISNULL(e.CurrentUPN, N'') LIKE N'%' + @Search + N'%'
    OR ISNULL(e.MaintenanceNote, N'') LIKE N'%' + @Search + N'%'
    OR EXISTS
    (
        SELECT 1
        FROM dbo.ADUserChangeQueue identityRequest
        WHERE identityRequest.EmployeeId = e.EmployeeId
          AND
          (
              ISNULL(identityRequest.TargetSamAccountName, N'') LIKE N'%' + @Search + N'%'
              OR ISNULL(identityRequest.NewSamAccountName, N'') LIKE N'%' + @Search + N'%'
              OR ISNULL(identityRequest.NewUserPrincipalName, N'') LIKE N'%' + @Search + N'%'
          )
    )
)");
        }

        if (!string.IsNullOrWhiteSpace(StatusFilter))
            clauses.Add("e.Status = @Status");

        switch (StateFilter)
        {
            case "current":
                clauses.Add(@"(
    EXISTS
    (
        SELECT 1 FROM dbo.ADObjects ad
        WHERE ad.ObjectGUID = e.CurrentADObjectGuid
          AND ISNULL(ad.IsDeleted, 0) = 0
    )
    OR EXISTS
    (
        SELECT 1 FROM dbo.Assignments a
        WHERE a.EmployeeId = e.EmployeeId
          AND (a.EndDate IS NULL OR a.EndDate >= CAST(GETDATE() AS date))
    )
    OR EXISTS
    (
        SELECT 1
        FROM dbo.ADUserChangeQueue q
        INNER JOIN dbo.ADObjects ad
            ON ISNULL(ad.IsDeleted, 0) = 0
           AND
           (
               (q.TargetObjectGUID IS NOT NULL AND ad.ObjectGUID = q.TargetObjectGUID)
               OR
               (q.TargetObjectGUID IS NULL
                AND ad.SamAccountName = COALESCE(NULLIF(q.TargetSamAccountName, N''), NULLIF(q.NewSamAccountName, N'')))
           )
        WHERE q.EmployeeId = e.EmployeeId
    )
)");
                break;
            case "stale":
                clauses.Add(@"(
    e.Status <> N'Merged'
    AND NOT EXISTS
    (
        SELECT 1 FROM dbo.ADObjects ad
        WHERE ad.ObjectGUID = e.CurrentADObjectGuid
          AND ISNULL(ad.IsDeleted, 0) = 0
    )
    AND NOT EXISTS
    (
        SELECT 1 FROM dbo.Assignments a
        WHERE a.EmployeeId = e.EmployeeId
          AND (a.EndDate IS NULL OR a.EndDate >= CAST(GETDATE() AS date))
    )
    AND NOT EXISTS
    (
        SELECT 1
        FROM dbo.ADUserChangeQueue q
        INNER JOIN dbo.ADObjects ad
            ON ISNULL(ad.IsDeleted, 0) = 0
           AND
           (
               (q.TargetObjectGUID IS NOT NULL AND ad.ObjectGUID = q.TargetObjectGUID)
               OR
               (q.TargetObjectGUID IS NULL
                AND ad.SamAccountName = COALESCE(NULLIF(q.TargetSamAccountName, N''), NULLIF(q.NewSamAccountName, N'')))
           )
        WHERE q.EmployeeId = e.EmployeeId
    )
)");
                break;
            case "needsReview":
                clauses.Add("(e.Status <> N'Merged' AND (e.LastReviewedAt IS NULL OR e.LastReviewedAt < DATEADD(day, -@ReviewDays, SYSDATETIME())))");
                break;
            case "test":
                clauses.Add("e.IsTestData = 1");
                break;
        }

        return clauses.Count == 0
            ? string.Empty
            : "WHERE " + string.Join("\n  AND ", clauses);
    }

    private void AddWhereParameters(SqlCommand cmd)
    {
        if (!string.IsNullOrWhiteSpace(Search))
            cmd.Parameters.AddNVarChar("@Search", Search, 320);
        if (!string.IsNullOrWhiteSpace(StatusFilter))
            cmd.Parameters.AddNVarChar("@Status", StatusFilter, 50);
        if (string.Equals(StateFilter, "needsReview", StringComparison.OrdinalIgnoreCase))
            cmd.Parameters.Add(new SqlParameter("@ReviewDays", System.Data.SqlDbType.Int) { Value = ReviewDays });
    }

    private async Task<EmployeeEditor?> LoadEditorAsync(SqlConnection cn, long employeeId)
    {
        await using var cmd = cn.CreateCommand();
        cmd.CommandText = @"
SELECT
    e.EmployeeId,
    e.CanonicalGivenName,
    e.CanonicalSurname,
    e.PrivateEmail,
    e.MobilePhone,
    e.CurrentADObjectGuid,
    e.CurrentSamAccountName,
    e.CurrentUPN,
    e.Status,
    e.IsTestData,
    e.LastReviewedAt,
    e.LastReviewedBy,
    e.MaintenanceNote,
    CASE WHEN ad.ObjectGUID IS NOT NULL AND ISNULL(ad.IsDeleted, 0) = 0 THEN CAST(1 AS bit) ELSE CAST(0 AS bit) END,
    ISNULL(assignmentInfo.AssignmentCount, 0),
    ISNULL(assignmentInfo.CurrentAssignmentCount, 0),
    assignmentInfo.LastAssignmentDate,
    ISNULL(requestInfo.RequestCount, 0)
FROM dbo.Employees e
LEFT JOIN dbo.ADObjects ad ON ad.ObjectGUID = e.CurrentADObjectGuid
OUTER APPLY
(
    SELECT COUNT(*) AS AssignmentCount,
           SUM(CASE WHEN a.EndDate IS NULL OR a.EndDate >= CAST(GETDATE() AS date) THEN 1 ELSE 0 END) AS CurrentAssignmentCount,
           MAX(COALESCE(a.EndDate, a.StartDate)) AS LastAssignmentDate
    FROM dbo.Assignments a
    WHERE a.EmployeeId = e.EmployeeId
) assignmentInfo
OUTER APPLY
(
    SELECT COUNT(*) AS RequestCount
    FROM dbo.ADUserChangeQueue q
    WHERE q.EmployeeId = e.EmployeeId
) requestInfo
WHERE e.EmployeeId = @EmployeeId;";
        cmd.Parameters.Add(new SqlParameter("@EmployeeId", System.Data.SqlDbType.BigInt) { Value = employeeId });
        await using var reader = await cmd.ExecuteReaderAsync(HttpContext.RequestAborted);
        if (!await reader.ReadAsync(HttpContext.RequestAborted)) return null;

        return new EmployeeEditor
        {
            EmployeeId = reader.GetInt64(0),
            GivenName = Get(reader, 1),
            Surname = Get(reader, 2),
            PrivateEmail = Get(reader, 3),
            MobilePhone = Get(reader, 4),
            CurrentADObjectGuid = reader.IsDBNull(5) ? null : reader.GetGuid(5),
            CurrentSamAccountName = Get(reader, 6),
            CurrentUPN = Get(reader, 7),
            Status = Get(reader, 8),
            IsTestData = reader.GetBoolean(9),
            LastReviewedAt = reader.IsDBNull(10) ? null : reader.GetDateTime(10),
            LastReviewedBy = Get(reader, 11),
            MaintenanceNote = Get(reader, 12),
            HasLiveAd = reader.GetBoolean(13),
            AssignmentCount = reader.GetInt32(14),
            CurrentAssignmentCount = reader.GetInt32(15),
            LastAssignmentDate = reader.IsDBNull(16) ? null : reader.GetDateTime(16),
            RequestCount = reader.GetInt32(17)
        };
    }

    private async Task LoadAuditAsync(SqlConnection cn, long employeeId)
    {
        await using var cmd = cn.CreateCommand();
        cmd.CommandText = @"
SELECT TOP (20) Action, ChangedAt, ChangedBy, Reason
FROM dbo.EmployeeMaintenanceAudit
WHERE EmployeeId = @EmployeeId
ORDER BY ChangedAt DESC, EmployeeMaintenanceAuditId DESC;";
        cmd.Parameters.Add(new SqlParameter("@EmployeeId", System.Data.SqlDbType.BigInt) { Value = employeeId });
        await using var reader = await cmd.ExecuteReaderAsync(HttpContext.RequestAborted);
        while (await reader.ReadAsync(HttpContext.RequestAborted))
        {
            AuditRows.Add(new AuditRow
            {
                Action = reader.GetString(0),
                ChangedAt = reader.GetDateTime(1),
                ChangedBy = reader.GetString(2),
                Reason = Get(reader, 3)
            });
        }
    }

    private async Task<bool> IsAllowedStatusAsync(SqlConnection cn, string? status, SqlTransaction tx)
    {
        if (string.IsNullOrWhiteSpace(status) || string.Equals(status, "Merged", StringComparison.OrdinalIgnoreCase)) return false;
        await using var cmd = cn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = @"
SELECT CASE WHEN EXISTS
(
    SELECT 1 FROM dbo.Employees
    WHERE Status = @Status AND Status <> N'Merged'
) THEN 1 ELSE 0 END;";
        cmd.Parameters.AddNVarChar("@Status", status, 50);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(HttpContext.RequestAborted)) == 1;
    }

    private static async Task<EmployeeSnapshot?> LoadSnapshotAsync(SqlConnection cn, long employeeId, SqlTransaction tx)
    {
        await using var cmd = cn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = @"
SELECT EmployeeId, CanonicalGivenName, CanonicalSurname, PrivateEmail, MobilePhone,
       CurrentADObjectGuid, CurrentSamAccountName, CurrentUPN, Status, IsTestData,
       LastReviewedAt, LastReviewedBy, MaintenanceNote
FROM dbo.Employees
WHERE EmployeeId = @EmployeeId;";
        cmd.Parameters.Add(new SqlParameter("@EmployeeId", System.Data.SqlDbType.BigInt) { Value = employeeId });
        await using var reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) return null;
        return new EmployeeSnapshot
        {
            EmployeeId = reader.GetInt64(0),
            GivenName = Get(reader, 1),
            Surname = Get(reader, 2),
            PrivateEmail = Get(reader, 3),
            MobilePhone = Get(reader, 4),
            CurrentADObjectGuid = reader.IsDBNull(5) ? null : reader.GetGuid(5),
            CurrentSamAccountName = Get(reader, 6),
            CurrentUPN = Get(reader, 7),
            Status = Get(reader, 8),
            IsTestData = reader.GetBoolean(9),
            LastReviewedAt = reader.IsDBNull(10) ? null : reader.GetDateTime(10),
            LastReviewedBy = Get(reader, 11),
            MaintenanceNote = Get(reader, 12)
        };
    }

    private static async Task<ReferenceCounts> LoadProtectedReferencesAsync(SqlConnection cn, long employeeId, SqlTransaction tx)
    {
        await using var cmd = cn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = @"
SELECT
    (SELECT COUNT(*) FROM dbo.Assignments WHERE EmployeeId = @EmployeeId),
    (SELECT COUNT(*) FROM dbo.ADUserChangeQueue WHERE EmployeeId = @EmployeeId),
    CASE WHEN e.IsTestData = 1
              OR
              (
                  (ad.ObjectGUID IS NULL OR ISNULL(ad.IsDeleted, 0) = 1)
                  AND NOT EXISTS
                  (
                      SELECT 1 FROM dbo.Assignments currentAssignment
                      WHERE currentAssignment.EmployeeId = e.EmployeeId
                        AND (currentAssignment.EndDate IS NULL OR currentAssignment.EndDate >= CAST(GETDATE() AS date))
                  )
                  AND NOT EXISTS
                  (
                      SELECT 1
                      FROM dbo.ADUserChangeQueue queueIdentity
                      INNER JOIN dbo.ADObjects queueAd
                          ON ISNULL(queueAd.IsDeleted, 0) = 0
                         AND
                         (
                             (queueIdentity.TargetObjectGUID IS NOT NULL AND queueAd.ObjectGUID = queueIdentity.TargetObjectGUID)
                             OR
                             (queueIdentity.TargetObjectGUID IS NULL
                              AND queueAd.SamAccountName = COALESCE(NULLIF(queueIdentity.TargetSamAccountName, N''), NULLIF(queueIdentity.NewSamAccountName, N'')))
                         )
                      WHERE queueIdentity.EmployeeId = e.EmployeeId
                  )
              )
         THEN CAST(1 AS bit) ELSE CAST(0 AS bit) END
FROM dbo.Employees e
LEFT JOIN dbo.ADObjects ad ON ad.ObjectGUID = e.CurrentADObjectGuid
WHERE e.EmployeeId = @EmployeeId;";
        cmd.Parameters.Add(new SqlParameter("@EmployeeId", System.Data.SqlDbType.BigInt) { Value = employeeId });
        await using var reader = await cmd.ExecuteReaderAsync();
        await reader.ReadAsync();
        return new ReferenceCounts(reader.GetInt32(0), reader.GetInt32(1), reader.GetBoolean(2));
    }

    private static async Task<bool> HasLiveAdLinkAsync(SqlConnection cn, long employeeId, SqlTransaction tx)
    {
        await using var cmd = cn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = @"
SELECT CASE WHEN EXISTS
(
    SELECT 1
    FROM dbo.Employees e
    INNER JOIN dbo.ADObjects ad ON ad.ObjectGUID = e.CurrentADObjectGuid
    WHERE e.EmployeeId = @EmployeeId
      AND ISNULL(ad.IsDeleted, 0) = 0
) THEN 1 ELSE 0 END;";
        cmd.Parameters.Add(new SqlParameter("@EmployeeId", System.Data.SqlDbType.BigInt) { Value = employeeId });
        return Convert.ToInt32(await cmd.ExecuteScalarAsync()) == 1;
    }

    private static async Task InsertAuditAsync(
        SqlConnection cn,
        SqlTransaction tx,
        long employeeId,
        string action,
        string changedBy,
        string? reason,
        EmployeeSnapshot? before,
        EmployeeSnapshot? after)
    {
        await using var cmd = cn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = @"
INSERT INTO dbo.EmployeeMaintenanceAudit
(EmployeeId, Action, ChangedBy, Reason, BeforeJson, AfterJson)
VALUES
(@EmployeeId, @Action, @ChangedBy, @Reason, @BeforeJson, @AfterJson);";
        cmd.Parameters.Add(new SqlParameter("@EmployeeId", System.Data.SqlDbType.BigInt) { Value = employeeId });
        cmd.Parameters.AddNVarChar("@Action", action, 40);
        cmd.Parameters.AddNVarChar("@ChangedBy", changedBy, 300);
        cmd.Parameters.AddNVarChar("@Reason", reason, 1000);
        cmd.Parameters.AddNVarCharMax("@BeforeJson", before is null ? null : JsonSerializer.Serialize(before));
        cmd.Parameters.AddNVarCharMax("@AfterJson", after is null ? null : JsonSerializer.Serialize(after));
        await cmd.ExecuteNonQueryAsync();
    }

    private IActionResult RedirectToCurrentPage(bool clearEdit = false)
    {
        return RedirectToPage(new
        {
            search = Search,
            statusFilter = StatusFilter,
            stateFilter = StateFilter,
            reviewDays = ReviewDays,
            pageNumber = PageNumber,
            editId = clearEdit ? null : EditId
        });
    }

    private void NormalizeFilters()
    {
        Search = NullIfWhiteSpace(Search);
        StatusFilter = NullIfWhiteSpace(StatusFilter);
        StateFilter = NullIfWhiteSpace(StateFilter);
        ReviewDays = Math.Clamp(ReviewDays <= 0 ? 180 : ReviewDays, 1, 3650);
        PageNumber = Math.Max(1, PageNumber);
    }

    private void NormalizeEdit()
    {
        Edit.GivenName = Edit.GivenName.Trim();
        Edit.Surname = Edit.Surname.Trim();
        Edit.PrivateEmail = NullIfWhiteSpace(Edit.PrivateEmail);
        Edit.MobilePhone = NullIfWhiteSpace(Edit.MobilePhone);
        Edit.Status = Edit.Status.Trim();
        Edit.MaintenanceNote = NullIfWhiteSpace(Edit.MaintenanceNote);
    }

    private static string? NullIfWhiteSpace(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static string Get(SqlDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? string.Empty : reader.GetString(ordinal);

    public sealed class EmployeeEditInput
    {
        public long EmployeeId { get; set; }
        public string GivenName { get; set; } = string.Empty;
        public string Surname { get; set; } = string.Empty;
        public string? PrivateEmail { get; set; }
        public string? MobilePhone { get; set; }
        public string Status { get; set; } = string.Empty;
        public bool IsTestData { get; set; }
        public string? MaintenanceNote { get; set; }
    }

    public class EmployeeRow
    {
        public long EmployeeId { get; init; }
        public string GivenName { get; init; } = string.Empty;
        public string Surname { get; init; } = string.Empty;
        public string DisplayName => $"{GivenName} {Surname}".Trim();
        public string PrivateEmail { get; init; } = string.Empty;
        public string MobilePhone { get; init; } = string.Empty;
        public string CurrentSamAccountName { get; init; } = string.Empty;
        public string CurrentUPN { get; init; } = string.Empty;
        public string Status { get; init; } = string.Empty;
        public bool IsTestData { get; init; }
        public DateTime? LastReviewedAt { get; init; }
        public string LastReviewedBy { get; init; } = string.Empty;
        public string MaintenanceNote { get; init; } = string.Empty;
        public bool HasLiveAd { get; init; }
        public int AssignmentCount { get; init; }
        public int CurrentAssignmentCount { get; init; }
        public DateTime? LastAssignmentDate { get; init; }
        public int RequestCount { get; init; }
        public List<AssignmentReference> AssignmentReferences { get; } = new();
        public List<RequestReference> RequestReferences { get; } = new();
        public bool IsStale => !string.Equals(Status, "Merged", StringComparison.OrdinalIgnoreCase)
            && !HasLiveAd
            && CurrentAssignmentCount == 0;
        public bool HasProtectedHistory => AssignmentCount > 0 || RequestCount > 0;
        public bool CanDelete => !string.Equals(Status, "Merged", StringComparison.OrdinalIgnoreCase)
            && !HasProtectedHistory
            && (IsTestData || IsStale);
    }

    public sealed class EmployeeEditor : EmployeeRow
    {
        public Guid? CurrentADObjectGuid { get; init; }
        public bool CanClearAdLink => CurrentADObjectGuid.HasValue
            && (IsTestData || !HasLiveAd);
    }


    public sealed class AssignmentReference
    {
        public long AssignmentId { get; init; }
        public string ProjectNumber { get; init; } = string.Empty;
        public string ProjectName { get; init; } = string.Empty;
        public DateTime StartDate { get; init; }
        public DateTime? EndDate { get; init; }
        public string Status { get; init; } = string.Empty;
    }

    public sealed class RequestReference
    {
        public long RequestId { get; init; }
        public string RequestType { get; init; } = string.Empty;
        public string Status { get; init; } = string.Empty;
        public DateTime? ExecuteAfter { get; init; }
        public DateTime? CreatedAt { get; init; }
        public string ProjectNumber { get; init; } = string.Empty;
        public string ProjectName { get; init; } = string.Empty;
    }

    public sealed class AuditRow
    {
        public string Action { get; init; } = string.Empty;
        public DateTime ChangedAt { get; init; }
        public string ChangedBy { get; init; } = string.Empty;
        public string Reason { get; init; } = string.Empty;
    }

    public sealed class SummaryCounts
    {
        public int Total { get; init; }
        public int Test { get; init; }
        public int NeedsReview { get; init; }
        public int Stale { get; init; }
    }

    private sealed class EmployeeSnapshot
    {
        public long EmployeeId { get; init; }
        public string GivenName { get; init; } = string.Empty;
        public string Surname { get; init; } = string.Empty;
        public string PrivateEmail { get; init; } = string.Empty;
        public string MobilePhone { get; init; } = string.Empty;
        public Guid? CurrentADObjectGuid { get; init; }
        public string CurrentSamAccountName { get; init; } = string.Empty;
        public string CurrentUPN { get; init; } = string.Empty;
        public string Status { get; init; } = string.Empty;
        public bool IsTestData { get; init; }
        public DateTime? LastReviewedAt { get; init; }
        public string LastReviewedBy { get; init; } = string.Empty;
        public string MaintenanceNote { get; init; } = string.Empty;
    }

    private sealed record ReferenceCounts(int Assignments, int Requests, bool SafeMaintenanceDelete);
}

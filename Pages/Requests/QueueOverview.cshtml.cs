using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Data.SqlClient;
using UserChangeQueueWeb.Services;

namespace UserChangeQueueWeb.Pages;

[Authorize]
public sealed class QueueOverviewModel : PageModel
{
    private readonly SqlConnectionFactory _connectionFactory;
    private readonly QueueAuditService _auditService;

    public QueueOverviewModel(
        SqlConnectionFactory connectionFactory,
        QueueAuditService auditService)
    {
        _connectionFactory = connectionFactory;
        _auditService = auditService;
    }

    [BindProperty(SupportsGet = true, Name = "view")]
    public string ViewMode { get; set; } = "failed";

    [BindProperty(SupportsGet = true)]
    public string? Search { get; set; }

    [TempData]
    public string? StatusMessage { get; set; }

    public string PageTitle => NormalizeViewMode(ViewMode) switch
    {
        "ready" => "Ready to process",
        "today" => "Starting today",
        "completed" => "Completed last 7 days",
        _ => "Failed requests"
    };

    public string PageDescription => NormalizeViewMode(ViewMode) switch
    {
        "ready" => "Approved requests that are eligible for worker processing now.",
        "today" => "User creation requests whose start date is today.",
        "completed" => "Requests completed during the last seven days.",
        _ => "Review failures, open the request, correct its data, and retry it."
    };

    public List<QueueItem> Items { get; } = new();

    public async Task OnGetAsync()
    {
        ViewMode = NormalizeViewMode(ViewMode);
        await LoadItemsAsync();
    }

    public async Task<IActionResult> OnPostRetryAsync(long requestId)
    {
        ViewMode = NormalizeViewMode(ViewMode);

        if (requestId <= 0)
        {
            StatusMessage = "Invalid request id.";
            return RedirectToPage(new { view = ViewMode, Search });
        }

        var changedBy = User.Identity?.Name ?? Environment.UserName;
        await using var connection = await _connectionFactory.OpenAsync(HttpContext.RequestAborted);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(HttpContext.RequestAborted);

        try
        {
            var oldJson = await _auditService.ReadQueueRowJsonAsync(connection, requestId, transaction);
            if (oldJson is null)
            {
                await transaction.RollbackAsync(HttpContext.RequestAborted);
                StatusMessage = $"Request {requestId} was not found.";
                return RedirectToPage(new { view = ViewMode, Search });
            }

            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = @"
UPDATE dbo.ADUserChangeQueue
SET
    Status = N'Approved',
    ErrorMessage = NULL,
    FinishedAt = NULL,
    ApprovedBy = @ChangedBy,
    ApprovedAt = SYSDATETIME()
WHERE RequestId = @RequestId
  AND Status = N'Failed';";
            command.Parameters.Add("@RequestId", System.Data.SqlDbType.BigInt).Value = requestId;
            command.Parameters.Add("@ChangedBy", System.Data.SqlDbType.NVarChar, 300).Value = changedBy;

            var changedRows = await command.ExecuteNonQueryAsync(HttpContext.RequestAborted);
            if (changedRows != 1)
            {
                await transaction.RollbackAsync(HttpContext.RequestAborted);
                StatusMessage = $"Request {requestId} is no longer in Failed status.";
                return RedirectToPage(new { view = ViewMode, Search });
            }

            await _auditService.MarkRequestUpdatedAsync(connection, requestId, changedBy, transaction);
            var newJson = await _auditService.ReadQueueRowJsonAsync(connection, requestId, transaction);
            await _auditService.WriteHistoryAsync(
                connection,
                requestId,
                "RETRY_REQUESTED",
                changedBy,
                oldJson,
                newJson,
                transaction);

            await transaction.CommitAsync(HttpContext.RequestAborted);
            StatusMessage = $"Request {requestId} was returned to Approved and will be retried by the queue worker.";
        }
        catch
        {
            await transaction.RollbackAsync(HttpContext.RequestAborted);
            throw;
        }

        return RedirectToPage(new { view = ViewMode, Search });
    }


    public async Task<IActionResult> OnPostDeleteAsync(long requestId)
    {
        ViewMode = NormalizeViewMode(ViewMode);

        if (requestId <= 0)
        {
            StatusMessage = "Invalid request id.";
            return RedirectToPage(new { view = ViewMode, Search });
        }

        await using var connection = await _connectionFactory.OpenAsync(HttpContext.RequestAborted);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(HttpContext.RequestAborted);

        try
        {
            string? displayName;
            string? status;

            await using (var readCommand = connection.CreateCommand())
            {
                readCommand.Transaction = transaction;
                readCommand.CommandText = @"
SELECT
    COALESCE(NULLIF(NewDisplayName, N''), NULLIF(TargetDisplayName, N''), NULLIF(TargetSamAccountName, N''), NULLIF(NewSamAccountName, N''), N'') AS DisplayName,
    Status
FROM dbo.ADUserChangeQueue WITH (UPDLOCK, HOLDLOCK)
WHERE RequestId = @RequestId;";
                readCommand.Parameters.Add("@RequestId", System.Data.SqlDbType.BigInt).Value = requestId;

                await using var reader = await readCommand.ExecuteReaderAsync(HttpContext.RequestAborted);
                if (!await reader.ReadAsync(HttpContext.RequestAborted))
                {
                    await transaction.RollbackAsync(HttpContext.RequestAborted);
                    StatusMessage = $"Request {requestId} was not found.";
                    return RedirectToPage(new { view = ViewMode, Search });
                }

                displayName = reader.IsDBNull(0) ? null : reader.GetString(0);
                status = reader.IsDBNull(1) ? null : reader.GetString(1);
            }

            if (string.Equals(status, "Processing", StringComparison.OrdinalIgnoreCase))
            {
                await transaction.RollbackAsync(HttpContext.RequestAborted);
                StatusMessage = $"Request {requestId} is currently being processed and cannot be deleted.";
                return RedirectToPage(new { view = ViewMode, Search });
            }

            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = @"
IF OBJECT_ID(N'dbo.ADUserChangeQueueServiceDeskPlus', N'U') IS NOT NULL
    DELETE FROM dbo.ADUserChangeQueueServiceDeskPlus WHERE RequestId = @RequestId;

IF OBJECT_ID(N'dbo.ADUserChangeQueueEmails', N'U') IS NOT NULL
    DELETE FROM dbo.ADUserChangeQueueEmails WHERE RequestId = @RequestId;

IF OBJECT_ID(N'dbo.ADUserChangeQueueGroups', N'U') IS NOT NULL
    DELETE FROM dbo.ADUserChangeQueueGroups WHERE RequestId = @RequestId;

IF OBJECT_ID(N'dbo.ADUserChangeQueueHistory', N'U') IS NOT NULL
    DELETE FROM dbo.ADUserChangeQueueHistory WHERE RequestId = @RequestId;

-- Assignment license selections are staging/reservation rows owned by the queue request.
-- Remove them before the parent request so deployments with the original NO ACTION FK
-- can still delete requests safely.
IF OBJECT_ID(N'dbo.AssignmentLicenseSelections', N'U') IS NOT NULL
    DELETE FROM dbo.AssignmentLicenseSelections WHERE RequestId = @RequestId;

-- A materialized license application is audit data and must survive deletion of the
-- originating queue request. Detach only the optional provenance link.
IF OBJECT_ID(N'dbo.LicenseApplications', N'U') IS NOT NULL
   AND COL_LENGTH(N'dbo.LicenseApplications', N'SourceQueueRequestId') IS NOT NULL
    UPDATE dbo.LicenseApplications
       SET SourceQueueRequestId = NULL
     WHERE SourceQueueRequestId = @RequestId;

DELETE FROM dbo.ADUserChangeQueue
WHERE RequestId = @RequestId
  AND ISNULL(Status, N'') <> N'Processing';";
            command.Parameters.Add("@RequestId", System.Data.SqlDbType.BigInt).Value = requestId;

            var affected = await command.ExecuteNonQueryAsync(HttpContext.RequestAborted);
            if (affected < 1)
            {
                await transaction.RollbackAsync(HttpContext.RequestAborted);
                StatusMessage = $"Request {requestId} could not be deleted.";
                return RedirectToPage(new { view = ViewMode, Search });
            }

            await transaction.CommitAsync(HttpContext.RequestAborted);
            StatusMessage = string.IsNullOrWhiteSpace(displayName)
                ? $"Request {requestId} was permanently deleted."
                : $"Request {requestId} for {displayName} was permanently deleted.";
        }
        catch
        {
            await transaction.RollbackAsync(HttpContext.RequestAborted);
            throw;
        }

        return RedirectToPage(new { view = ViewMode, Search });
    }

    private async Task LoadItemsAsync()
    {
        Items.Clear();
        var mode = NormalizeViewMode(ViewMode);

        await using var connection = await _connectionFactory.OpenAsync(HttpContext.RequestAborted);
        await using var command = connection.CreateCommand();
        command.CommandText = $@"
SELECT TOP (500)
    RequestId,
    RequestType,
    Status,
    ExecuteAfter,
    CreatedAt,
    FinishedAt,
    COALESCE(NULLIF(NewDisplayName, N''), NULLIF(TargetDisplayName, N''), NULLIF(TargetSamAccountName, N''), NULLIF(NewSamAccountName, N''), N'') AS DisplayName,
    COALESCE(NULLIF(NewUserPrincipalName, N''), NULLIF(Mail, N''), NULLIF(NewSamAccountName, N''), NULLIF(TargetSamAccountName, N''), N'') AS AccountName,
    COALESCE(Office, N'') AS Office,
    COALESCE(RequestedBy, N'') AS RequestedBy,
    COALESCE(ErrorMessage, N'') AS ErrorMessage
FROM dbo.ADUserChangeQueue
WHERE {GetModePredicate(mode)}
  AND
  (
        @Search IS NULL
     OR CONVERT(nvarchar(30), RequestId) LIKE @SearchLike
     OR NewDisplayName LIKE @SearchLike
     OR TargetDisplayName LIKE @SearchLike
     OR NewSamAccountName LIKE @SearchLike
     OR TargetSamAccountName LIKE @SearchLike
     OR NewUserPrincipalName LIKE @SearchLike
     OR Mail LIKE @SearchLike
     OR ErrorMessage LIKE @SearchLike
  )
ORDER BY
    CASE WHEN Status = N'Failed' THEN 0 ELSE 1 END,
    COALESCE(FinishedAt, CreatedAt) DESC,
    RequestId DESC;";

        var search = string.IsNullOrWhiteSpace(Search) ? null : Search.Trim();
        command.Parameters.Add("@Search", System.Data.SqlDbType.NVarChar, 300).Value = search is null ? DBNull.Value : search;
        command.Parameters.Add("@SearchLike", System.Data.SqlDbType.NVarChar, 302).Value = search is null ? DBNull.Value : $"%{search}%";

        await using var reader = await command.ExecuteReaderAsync(HttpContext.RequestAborted);
        while (await reader.ReadAsync(HttpContext.RequestAborted))
        {
            Items.Add(new QueueItem
            {
                RequestId = reader.GetInt64(reader.GetOrdinal("RequestId")),
                RequestType = GetString(reader, "RequestType"),
                Status = GetString(reader, "Status"),
                ExecuteAfter = GetNullableDateTime(reader, "ExecuteAfter"),
                CreatedAt = GetNullableDateTime(reader, "CreatedAt"),
                FinishedAt = GetNullableDateTime(reader, "FinishedAt"),
                DisplayName = GetString(reader, "DisplayName"),
                AccountName = GetString(reader, "AccountName"),
                Office = GetString(reader, "Office"),
                RequestedBy = GetString(reader, "RequestedBy"),
                ErrorMessage = GetString(reader, "ErrorMessage")
            });
        }
    }

    private static string GetModePredicate(string mode) => mode switch
    {
        "ready" => "Status = N'Approved' AND (ExecuteAfter IS NULL OR ExecuteAfter <= SYSDATETIME() OR (RequestType = N'CREATE' AND CONVERT(date, DATEADD(day, -1, ExecuteAfter)) <= CONVERT(date, SYSDATETIME())))",
        "today" => "RequestType = N'CREATE' AND ExecuteAfter IS NOT NULL AND CONVERT(date, ExecuteAfter) = CONVERT(date, SYSDATETIME())",
        "completed" => "Status IN (N'Done', N'Completed', N'Implemented') AND FinishedAt >= DATEADD(day, -7, SYSDATETIME())",
        _ => "Status = N'Failed'"
    };

    private static string NormalizeViewMode(string? value)
    {
        return value?.Trim().ToLowerInvariant() switch
        {
            "ready" => "ready",
            "today" => "today",
            "completed" => "completed",
            _ => "failed"
        };
    }

    private static string GetString(SqlDataReader reader, string column)
    {
        var ordinal = reader.GetOrdinal(column);
        return reader.IsDBNull(ordinal) ? string.Empty : reader.GetString(ordinal);
    }

    private static DateTime? GetNullableDateTime(SqlDataReader reader, string column)
    {
        var ordinal = reader.GetOrdinal(column);
        return reader.IsDBNull(ordinal) ? null : reader.GetDateTime(ordinal);
    }

    public sealed class QueueItem
    {
        public long RequestId { get; set; }
        public string RequestType { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public DateTime? ExecuteAfter { get; set; }
        public DateTime? CreatedAt { get; set; }
        public DateTime? FinishedAt { get; set; }
        public string DisplayName { get; set; } = string.Empty;
        public string AccountName { get; set; } = string.Empty;
        public string Office { get; set; } = string.Empty;
        public string RequestedBy { get; set; } = string.Empty;
        public string ErrorMessage { get; set; } = string.Empty;
        public bool CanRetry => string.Equals(Status, "Failed", StringComparison.OrdinalIgnoreCase);
        public string EditPage => string.Equals(RequestType, "UPDATE", StringComparison.OrdinalIgnoreCase)
            ? "/Requests/UpdateUser"
            : "/Requests/NewUser";
    }
}

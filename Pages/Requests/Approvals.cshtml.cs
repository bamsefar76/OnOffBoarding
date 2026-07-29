using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Data.SqlClient;
using UserChangeQueueWeb.Services;

namespace UserChangeQueueWeb.Pages;

[Authorize]
public class ApprovalsModel : PageModel
{
    private readonly SqlConnectionFactory _connectionFactory;
    private readonly QueueAuditService _auditService;

    public ApprovalsModel(SqlConnectionFactory connectionFactory, QueueAuditService auditService)
    {
        _connectionFactory = connectionFactory;
        _auditService = auditService;
    }

    [BindProperty(SupportsGet = true)]
    public string? RequestType { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? RequestedBy { get; set; }

    [BindProperty]
    public List<long> SelectedRequestIds { get; set; } = new();

    [TempData]
    public string? StatusMessage { get; set; }

    public List<PendingApprovalRow> PendingRequests { get; set; } = new();

    public async Task OnGetAsync()
    {
        await LoadPendingRequestsAsync();
    }

    public async Task<IActionResult> OnPostApproveAsync(long requestId)
    {
        var approved = await ApproveRequestAsync(requestId);
        StatusMessage = approved
            ? $"Request {requestId} was approved."
            : $"Request {requestId} could not be approved. It may already have been changed.";

        return RedirectToPage(new
        {
            RequestType,
            RequestedBy
        });
    }

    public async Task<IActionResult> OnPostApproveSelectedAsync()
    {
        var requestIds = SelectedRequestIds
            .Where(id => id > 0)
            .Distinct()
            .ToList();

        if (requestIds.Count == 0)
        {
            StatusMessage = "No requests were selected.";
            return RedirectToPage(new
            {
                RequestType,
                RequestedBy
            });
        }

        var approvedCount = 0;

        foreach (var requestId in requestIds)
        {
            if (await ApproveRequestAsync(requestId))
            {
                approvedCount++;
            }
        }

        StatusMessage = $"Approved {approvedCount} of {requestIds.Count} selected request(s).";

        return RedirectToPage(new
        {
            RequestType,
            RequestedBy
        });
    }

    public async Task<IActionResult> OnPostRejectAsync(long requestId)
    {
        var rejectStatus = await GetRejectStatusAsync();
        var rejected = await RejectRequestAsync(requestId, rejectStatus);
        StatusMessage = rejected
            ? $"Request {requestId} was rejected and set to {rejectStatus}."
            : $"Request {requestId} could not be rejected. It may already have been changed.";

        return RedirectToPage(new
        {
            RequestType,
            RequestedBy
        });
    }

    public async Task<IActionResult> OnPostRejectSelectedAsync()
    {
        var requestIds = SelectedRequestIds
            .Where(id => id > 0)
            .Distinct()
            .ToList();

        if (requestIds.Count == 0)
        {
            StatusMessage = "No requests were selected.";
            return RedirectToPage(new
            {
                RequestType,
                RequestedBy
            });
        }

        var rejectStatus = await GetRejectStatusAsync();
        var rejectedCount = 0;

        foreach (var requestId in requestIds)
        {
            if (await RejectRequestAsync(requestId, rejectStatus))
            {
                rejectedCount++;
            }
        }

        StatusMessage = $"Rejected {rejectedCount} of {requestIds.Count} selected request(s) as {rejectStatus}.";

        return RedirectToPage(new
        {
            RequestType,
            RequestedBy
        });
    }

    private Task<bool> ApproveRequestAsync(long requestId)
    {
        var approvedBy = User.Identity?.Name ?? Environment.UserName;
        return ChangePendingRequestStatusAsync(
            requestId,
            newStatus: "Approved",
            changedBy: approvedBy,
            historyChangeType: "APPROVED",
            setApprovalFields: true);
    }

    private Task<bool> RejectRequestAsync(long requestId, string rejectStatus)
    {
        var rejectedBy = User.Identity?.Name ?? Environment.UserName;
        return ChangePendingRequestStatusAsync(
            requestId,
            newStatus: rejectStatus,
            changedBy: rejectedBy,
            historyChangeType: "REJECTED",
            setApprovalFields: false);
    }

    private async Task<bool> ChangePendingRequestStatusAsync(
        long requestId,
        string newStatus,
        string changedBy,
        string historyChangeType,
        bool setApprovalFields)
    {
        await using var cn = await _connectionFactory.OpenAsync();

        using var tx = (SqlTransaction)await cn.BeginTransactionAsync();

        try
        {
            var oldJson = await _auditService.ReadQueueRowJsonAsync(cn, requestId, tx);

            if (oldJson == null)
            {
                tx.Rollback();
                return false;
            }

            await using var cmd = cn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = setApprovalFields
                ? @"
UPDATE dbo.ADUserChangeQueue
SET
    Status = @NewStatus,
    ApprovedBy = @ChangedBy,
    ApprovedAt = SYSDATETIME(),
    ErrorMessage = NULL
WHERE RequestId = @RequestId
  AND UPPER(LTRIM(RTRIM(ISNULL(Status, N'')))) = N'PENDING';
"
                : @"
UPDATE dbo.ADUserChangeQueue
SET
    Status = @NewStatus,
    ErrorMessage = NULL
WHERE RequestId = @RequestId
  AND UPPER(LTRIM(RTRIM(ISNULL(Status, N'')))) = N'PENDING';
";
            cmd.Parameters.AddBigInt("@RequestId", requestId);
            cmd.Parameters.AddRequiredNVarChar("@NewStatus", newStatus, 30);
            cmd.Parameters.AddRequiredNVarChar("@ChangedBy", changedBy, 300);

            var changedRows = await cmd.ExecuteNonQueryAsync();

            if (changedRows != 1)
            {
                tx.Rollback();
                return false;
            }

            await _auditService.MarkRequestUpdatedAsync(cn, requestId, changedBy, tx);
            var newJson = await _auditService.ReadQueueRowJsonAsync(cn, requestId, tx);

            await _auditService.WriteHistoryAsync(
                cn,
                requestId,
                historyChangeType,
                changedBy,
                oldJson,
                newJson,
                tx);

            tx.Commit();
            return true;
        }
        catch
        {
            try
            {
                tx.Rollback();
            }
            catch
            {
                // Ignore rollback errors and rethrow the original exception.
            }

            throw;
        }
    }

    private async Task<string> GetRejectStatusAsync()
    {
        var allowedStatuses = await GetAllowedQueueStatusesAsync();

        foreach (var candidate in new[] { "Rejected", "Denied", "Cancelled" })
        {
            if (allowedStatuses.Contains(candidate, StringComparer.OrdinalIgnoreCase))
            {
                return candidate;
            }
        }

        throw new InvalidOperationException("No supported reject status was found in CK_ADUserChangeQueue_Status. Add Rejected, Denied, or Cancelled to the allowed statuses.");
    }

    private async Task<HashSet<string>> GetAllowedQueueStatusesAsync()
    {
        var statuses = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using var cn = await _connectionFactory.OpenAsync();

        await using var cmd = cn.CreateCommand();
        cmd.CommandText = @"
SELECT cc.definition
FROM sys.check_constraints AS cc
WHERE cc.name = N'CK_ADUserChangeQueue_Status'
  AND cc.parent_object_id = OBJECT_ID(N'dbo.ADUserChangeQueue');
";

        var definition = (await cmd.ExecuteScalarAsync())?.ToString();

        if (string.IsNullOrWhiteSpace(definition))
        {
            statuses.Add("Cancelled");
            return statuses;
        }

        foreach (System.Text.RegularExpressions.Match match in System.Text.RegularExpressions.Regex.Matches(definition, @"'([^']+)'", System.Text.RegularExpressions.RegexOptions.CultureInvariant))
        {
            if (match.Groups.Count > 1 && !string.IsNullOrWhiteSpace(match.Groups[1].Value))
            {
                statuses.Add(match.Groups[1].Value.Trim());
            }
        }

        return statuses;
    }

    private async Task LoadPendingRequestsAsync()
    {
        PendingRequests.Clear();

        var normalizedRequestType = NormalizeRequestType(RequestType);
        var requestedByFilter = string.IsNullOrWhiteSpace(RequestedBy)
            ? null
            : RequestedBy.Trim();

        var whereParts = new List<string>
        {
            "UPPER(LTRIM(RTRIM(ISNULL(q.Status, N'')))) = N'PENDING'"
        };

        await using var cn = await _connectionFactory.OpenAsync();

        await using var cmd = cn.CreateCommand();

        if (!string.IsNullOrWhiteSpace(normalizedRequestType))
        {
            whereParts.Add("UPPER(LTRIM(RTRIM(ISNULL(q.RequestType, N'')))) = @RequestType");
            cmd.Parameters.AddRequiredNVarChar("@RequestType", normalizedRequestType, 20);
        }

        if (!string.IsNullOrWhiteSpace(requestedByFilter))
        {
            whereParts.Add("q.RequestedBy LIKE @RequestedBy");
            cmd.Parameters.AddNVarChar("@RequestedBy", "%" + requestedByFilter + "%", 300);
        }

        cmd.CommandText = $@"
SELECT TOP (200)
    q.RequestId,
    ISNULL(q.RequestType, N'') AS RequestType,
    ISNULL(q.RequestCategory, N'') AS RequestCategory,
    ISNULL(q.Status, N'') AS Status,
    q.ExecuteAfter,
    ISNULL(NULLIF(q.NewDisplayName, N''), ISNULL(NULLIF(q.TargetDisplayName, N''), ISNULL(NULLIF(q.TargetSamAccountName, N''), ISNULL(NULLIF(q.NewSamAccountName, N''), N'')))) AS DisplayName,
    ISNULL(q.NewSamAccountName, N'') AS NewSamAccountName,
    ISNULL(q.TargetSamAccountName, N'') AS TargetSamAccountName,
    ISNULL(q.NewUserPrincipalName, N'') AS NewUserPrincipalName,
    ISNULL(q.Mail, N'') AS Mail,
    ISNULL(q.Department, N'') AS Department,
    ISNULL(q.Title, N'') AS Title,
    ISNULL(q.EmployeeType, N'') AS EmployeeType,
    ISNULL(q.Company, N'') AS Company,
    ISNULL(q.Office, N'') AS Office,
    ISNULL(q.OfficeLicense, N'') AS OfficeLicense,
    ISNULL(q.ComputerType, N'') AS ComputerType,
    ISNULL(q.AccessCard, 0) AS AccessCard,
    ISNULL(q.RequestedBy, N'') AS RequestedBy,
    q.CreatedAt
FROM dbo.ADUserChangeQueue AS q
WHERE {string.Join(" AND ", whereParts)}
ORDER BY
    q.ExecuteAfter ASC,
    q.CreatedAt ASC,
    q.RequestId ASC;
";

        await using var reader = await cmd.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {
            PendingRequests.Add(new PendingApprovalRow
            {
                RequestId = Convert.ToInt64(reader.GetValue(0)),
                RequestType = reader.GetString(1).Trim(),
                RequestCategory = reader.GetString(2).Trim(),
                Status = reader.GetString(3).Trim(),
                ExecuteAfter = reader.IsDBNull(4) ? null : reader.GetDateTime(4),
                DisplayName = reader.GetString(5),
                NewSamAccountName = reader.GetString(6),
                TargetSamAccountName = reader.GetString(7),
                NewUserPrincipalName = reader.GetString(8),
                Mail = reader.GetString(9),
                Department = reader.GetString(10),
                Title = reader.GetString(11),
                EmployeeType = reader.GetString(12),
                Company = reader.GetString(13),
                Office = reader.GetString(14),
                OfficeLicense = reader.GetString(15),
                ComputerType = reader.GetString(16),
                AccessCard = Convert.ToBoolean(reader.GetValue(17)),
                RequestedBy = reader.GetString(18),
                // ADUserChangeQueue.CreatedAt is stored as UTC (column default is sysutcdatetime()).
                // SqlClient returns DateTime.Kind = Unspecified regardless, so it must be explicitly
                // tagged as UTC before converting to the server's local time zone for display.
                CreatedAt = reader.IsDBNull(19)
                    ? null
                    : DateTime.SpecifyKind(reader.GetDateTime(19), DateTimeKind.Utc).ToLocalTime()
            });
        }
    }

    private static string? NormalizeRequestType(string? requestType)
    {
        if (string.IsNullOrWhiteSpace(requestType))
        {
            return null;
        }

        var normalized = requestType.Trim().ToUpperInvariant();

        return normalized is "CREATE" or "UPDATE"
            ? normalized
            : null;
    }

    public class PendingApprovalRow
    {
        public long RequestId { get; set; }
        public string RequestType { get; set; } = "";
        public string RequestCategory { get; set; } = "";
        public string Status { get; set; } = "";
        public DateTime? ExecuteAfter { get; set; }
        public string DisplayName { get; set; } = "";
        public string NewSamAccountName { get; set; } = "";
        public string TargetSamAccountName { get; set; } = "";
        public string NewUserPrincipalName { get; set; } = "";
        public string Mail { get; set; } = "";
        public string Department { get; set; } = "";
        public string Title { get; set; } = "";
        public string EmployeeType { get; set; } = "";
        public string Company { get; set; } = "";
        public string Office { get; set; } = "";
        public string OfficeLicense { get; set; } = "";
        public string ComputerType { get; set; } = "";
        public bool AccessCard { get; set; }
        public string RequestedBy { get; set; } = "";
        public DateTime? CreatedAt { get; set; }

        public string DisplayRequestType => string.IsNullOrWhiteSpace(RequestCategory) ? RequestType : RequestCategory;

        public string NormalizedRequestType => (RequestType ?? "").Trim().ToUpperInvariant();

        public string EditUrl => NormalizedRequestType == "CREATE"
            ? $"/Requests/NewUser?requestId={RequestId}"
            : NormalizedRequestType == "UPDATE"
                ? $"/Requests/UpdateUser?requestId={RequestId}"
                : "#";

        public string DisplayText => !string.IsNullOrWhiteSpace(DisplayName)
            ? DisplayName
            : !string.IsNullOrWhiteSpace(NewSamAccountName)
                ? NewSamAccountName
                : !string.IsNullOrWhiteSpace(TargetSamAccountName)
                    ? TargetSamAccountName
                    : $"Request {RequestId}";

        public bool IsDue => !ExecuteAfter.HasValue || ExecuteAfter.Value <= DateTime.Now;
    }
}

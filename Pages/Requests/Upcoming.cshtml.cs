using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Data.SqlClient;
using UserChangeQueueWeb.Services;
using System.Globalization;

namespace UserChangeQueueWeb.Pages;

[Authorize]
public class UpcomingChangesModel : PageModel
{
    private static readonly string[] OpenStatuses =
    {
        "Pending",
        "Approved",
        "Processing"
    };

    private readonly SqlConnectionFactory _connectionFactory;
    private readonly AccessScopeService _accessScopeService;
    private UserAccessScope _scope = UserAccessScope.Empty;

    public UpcomingChangesModel(
        SqlConnectionFactory connectionFactory,
        AccessScopeService accessScopeService)
    {
        _connectionFactory = connectionFactory;
        _accessScopeService = accessScopeService;
    }

    [BindProperty(SupportsGet = true)] public string? Office { get; set; }
    [BindProperty(SupportsGet = true)] public string? Status { get; set; }
    [BindProperty(SupportsGet = true)] public string? RequestType { get; set; }
    [BindProperty(SupportsGet = true)] public string? Search { get; set; }
    [BindProperty(SupportsGet = true)] public bool ShowPast { get; set; }

    public List<string> OfficeOptions { get; set; } = new();
    public List<string> StatusOptions { get; set; } = new();
    public List<UpcomingChangeItem> Items { get; set; } = new();
    public bool CanOpenRequestDetails { get; private set; }

    public async Task OnGetAsync()
    {
        _scope = await _accessScopeService.GetCurrentAsync(User, HttpContext.RequestAborted);
        CanOpenRequestDetails = _scope.IsIT || _scope.IsHR;
        await using var connection = await _connectionFactory.OpenAsync(HttpContext.RequestAborted);

        // HR is permanently scoped to the office stored on their own AD object.
        if (_scope.IsHR && !_scope.IsIT)
        {
            Office = _scope.Office;
        }

        OfficeOptions = await LoadOfficeOptionsAsync(connection);
        StatusOptions = await LoadStatusOptionsAsync(connection);
        Items = await LoadItemsAsync(connection);
    }

    private static bool IsOpenStatus(string? status)
    {
        return OpenStatuses.Any(openStatus => string.Equals(openStatus, status, StringComparison.OrdinalIgnoreCase));
    }

    private async Task<List<string>> LoadOfficeOptionsAsync(SqlConnection connection)
    {
        const string sql = @"
SELECT DISTINCT NULLIF(LTRIM(RTRIM(q.Office)), N'') AS Office
FROM dbo.ADUserChangeQueue AS q
WHERE q.Status IN (N'Pending', N'Approved', N'Processing')
  AND NULLIF(LTRIM(RTRIM(q.Office)), N'') IS NOT NULL
  AND
  (
       @IsIT = 1
    OR (@IsHR = 1 AND NULLIF(LTRIM(RTRIM(q.Office)), N'') = @UserOffice)
    OR
       (@IsProjectManager = 1 AND q.RequestType = N'CREATE' AND EXISTS
       (
           SELECT 1
           FROM dbo.Projects AS p
           WHERE p.Active = 1
             AND p.Company = q.Company
             AND p.ProjectName = q.Department
             AND
             (
                  p.ProductionManager = @SamAccountName
               OR p.ProductionManager LIKE @DomainSlashSamAccountName
             )
       ))
  )
ORDER BY Office;";

        var result = new List<string>();
        await using var command = new SqlCommand(sql, connection);
        AddScopeParameters(command);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            result.Add(reader.GetString(0));
        }

        return result;
    }

    private async Task<List<string>> LoadStatusOptionsAsync(SqlConnection connection)
    {
        const string sql = @"
SELECT DISTINCT q.Status
FROM dbo.ADUserChangeQueue AS q
WHERE q.Status IN (N'Pending', N'Approved', N'Processing')
  AND
  (
       @IsIT = 1
    OR (@IsHR = 1 AND NULLIF(LTRIM(RTRIM(q.Office)), N'') = @UserOffice)
    OR
       (@IsProjectManager = 1 AND q.RequestType = N'CREATE' AND EXISTS
       (
           SELECT 1
           FROM dbo.Projects AS p
           WHERE p.Active = 1
             AND p.Company = q.Company
             AND p.ProjectName = q.Department
             AND
             (
                  p.ProductionManager = @SamAccountName
               OR p.ProductionManager LIKE @DomainSlashSamAccountName
             )
       ))
  )
ORDER BY q.Status;";

        var result = new List<string>();
        await using var command = new SqlCommand(sql, connection);
        AddScopeParameters(command);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            result.Add(reader.GetString(0));
        }

        return result;
    }

    private async Task<List<UpcomingChangeItem>> LoadItemsAsync(SqlConnection connection)
    {
        var sql = @"
SELECT
    q.RequestId,
    q.RequestType,
    q.Status,
    q.ExecuteAfter,
    q.NewSamAccountName,
    q.TargetSamAccountName,
    q.NewUserPrincipalName,
    q.NewDisplayName,
    q.TargetDisplayName,
    q.EmployeeType,
    q.Company,
    q.Department,
    q.Title,
    q.Office,
    q.PrivateEmail,
    q.Mail,
    q.OfficeLicense,
    q.ComputerType,
    q.AccessCard,
    q.RequestedBy,
    groupData.GroupCount,
    groupData.GroupSummary
FROM dbo.ADUserChangeQueue AS q
OUTER APPLY
(
    SELECT
        COUNT(1) AS GroupCount,
        STRING_AGG(CONCAT(qg.Action, N' ', COALESCE(g.SamAccountName, g.Name, CONVERT(nvarchar(36), qg.GroupObjectGUID))), N'; ') AS GroupSummary
    FROM dbo.ADUserChangeQueueGroups AS qg
    LEFT JOIN dbo.ADGroups AS g
        ON g.ObjectGUID = qg.GroupObjectGUID
    WHERE qg.RequestId = q.RequestId
      AND qg.Selected = 1
) AS groupData
WHERE q.Status IN (N'Pending', N'Approved', N'Processing')
  AND (@Office IS NULL OR NULLIF(LTRIM(RTRIM(q.Office)), N'') = @Office)
  AND (@Status IS NULL OR q.Status = @Status)
  AND (@RequestType IS NULL OR q.RequestType = @RequestType)
  AND (@ShowPast = 1 OR q.ExecuteAfter IS NULL OR CAST(q.ExecuteAfter AS date) >= CAST(SYSDATETIME() AS date))
  AND
  (
       @IsIT = 1
    OR (@IsHR = 1 AND NULLIF(LTRIM(RTRIM(q.Office)), N'') = @UserOffice)
    OR
       (@IsProjectManager = 1 AND q.RequestType = N'CREATE' AND EXISTS
       (
           SELECT 1
           FROM dbo.Projects AS p
           WHERE p.Active = 1
             AND p.Company = q.Company
             AND p.ProjectName = q.Department
             AND
             (
                  p.ProductionManager = @SamAccountName
               OR p.ProductionManager LIKE @DomainSlashSamAccountName
             )
       ))
  )
  AND
  (
        @Search IS NULL
     OR q.NewSamAccountName LIKE @SearchLike
     OR q.TargetSamAccountName LIKE @SearchLike
     OR q.NewDisplayName LIKE @SearchLike
     OR q.TargetDisplayName LIKE @SearchLike
     OR q.NewUserPrincipalName LIKE @SearchLike
     OR q.Mail LIKE @SearchLike
     OR q.Department LIKE @SearchLike
     OR q.Title LIKE @SearchLike
     OR q.Company LIKE @SearchLike
  )
ORDER BY
    q.ExecuteAfter,
    q.RequestType,
    COALESCE(q.NewDisplayName, q.TargetDisplayName, q.NewSamAccountName, q.TargetSamAccountName);";

        var result = new List<UpcomingChangeItem>();
        await using var command = new SqlCommand(sql, connection);
        AddScopeParameters(command);
        command.Parameters.Add("@Office", System.Data.SqlDbType.NVarChar, 300).Value = string.IsNullOrWhiteSpace(Office) ? DBNull.Value : Office.Trim();
        command.Parameters.Add("@Status", System.Data.SqlDbType.NVarChar, 50).Value = string.IsNullOrWhiteSpace(Status) ? DBNull.Value : Status.Trim();
        command.Parameters.Add("@RequestType", System.Data.SqlDbType.NVarChar, 20).Value = string.IsNullOrWhiteSpace(RequestType) ? DBNull.Value : RequestType.Trim().ToUpperInvariant();
        command.Parameters.Add("@ShowPast", System.Data.SqlDbType.Bit).Value = ShowPast;
        command.Parameters.Add("@Search", System.Data.SqlDbType.NVarChar, 300).Value = string.IsNullOrWhiteSpace(Search) ? DBNull.Value : Search.Trim();
        command.Parameters.Add("@SearchLike", System.Data.SqlDbType.NVarChar, 302).Value = string.IsNullOrWhiteSpace(Search) ? DBNull.Value : $"%{Search.Trim()}%";

        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var item = new UpcomingChangeItem
            {
                RequestId = GetInt64(reader, "RequestId"),
                RequestType = GetString(reader, "RequestType"),
                Status = GetString(reader, "Status"),
                ExecuteAfter = GetNullableDateTime(reader, "ExecuteAfter"),
                NewSamAccountName = GetString(reader, "NewSamAccountName"),
                TargetSamAccountName = GetString(reader, "TargetSamAccountName"),
                NewUserPrincipalName = GetString(reader, "NewUserPrincipalName"),
                NewDisplayName = GetString(reader, "NewDisplayName"),
                TargetDisplayName = GetString(reader, "TargetDisplayName"),
                EmployeeType = GetString(reader, "EmployeeType"),
                Company = GetString(reader, "Company"),
                Department = GetString(reader, "Department"),
                Title = GetString(reader, "Title"),
                Office = GetString(reader, "Office"),
                PrivateEmail = GetString(reader, "PrivateEmail"),
                Mail = GetString(reader, "Mail"),
                OfficeLicense = GetString(reader, "OfficeLicense"),
                ComputerType = GetString(reader, "ComputerType"),
                AccessCard = GetBoolean(reader, "AccessCard"),
                RequestedBy = GetString(reader, "RequestedBy"),
                GroupCount = GetNullableInt32(reader, "GroupCount") ?? 0,
                GroupSummary = GetString(reader, "GroupSummary")
            };

            item.Needs = BuildNeeds(item);
            result.Add(item);
        }

        return result;
    }

    private void AddScopeParameters(SqlCommand command)
    {
        command.Parameters.Add("@IsIT", System.Data.SqlDbType.Bit).Value = _scope.IsIT;
        command.Parameters.Add("@IsHR", System.Data.SqlDbType.Bit).Value = _scope.IsHR;
        command.Parameters.Add("@IsProjectManager", System.Data.SqlDbType.Bit).Value = _scope.IsProjectManager;
        command.Parameters.Add("@UserOffice", System.Data.SqlDbType.NVarChar, 300).Value =
            string.IsNullOrWhiteSpace(_scope.Office) ? DBNull.Value : _scope.Office;
        command.Parameters.Add("@SamAccountName", System.Data.SqlDbType.NVarChar, 256).Value = _scope.SamAccountName;
        command.Parameters.Add("@DomainSlashSamAccountName", System.Data.SqlDbType.NVarChar, 300).Value = @"%\" + _scope.SamAccountName;
    }

    private static List<UpcomingNeed> BuildNeeds(UpcomingChangeItem item)
    {
        var needs = new List<UpcomingNeed>();

        if (HasOfficeLicense(item.OfficeLicense))
        {
            needs.Add(new UpcomingNeed("Office license", "bg-primary", item.OfficeLicense));
            needs.Add(new UpcomingNeed("Mailbox", "bg-info text-dark", "Remote mailbox / Exchange Online provisioning"));
        }

        if (!string.IsNullOrWhiteSpace(item.ComputerType))
        {
            needs.Add(new UpcomingNeed("Computer", "bg-secondary", item.ComputerType));
        }

        if (item.AccessCard)
        {
            needs.Add(new UpcomingNeed("Access card", "bg-warning text-dark", "Access card requested"));
        }

        if (item.RequestType.Equals("CREATE", StringComparison.OrdinalIgnoreCase) && string.IsNullOrWhiteSpace(item.PrivateEmail))
        {
            needs.Add(new UpcomingNeed("Private email missing", "bg-danger", "Welcome email will fall back to corporate mail/UPN"));
        }

        if (item.GroupCount > 0)
        {
            needs.Add(new UpcomingNeed($"Groups ({item.GroupCount})", "bg-success", item.GroupSummary));
        }

        return needs;
    }

    private static bool HasOfficeLicense(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var normalized = value.Trim();
        return !normalized.Equals("No office license", StringComparison.OrdinalIgnoreCase)
            && !normalized.Equals("No license", StringComparison.OrdinalIgnoreCase)
            && !normalized.Equals("None", StringComparison.OrdinalIgnoreCase);
    }

    private static string GetString(SqlDataReader reader, string name)
    {
        var ordinal = reader.GetOrdinal(name);
        return reader.IsDBNull(ordinal) ? string.Empty : Convert.ToString(reader.GetValue(ordinal), CultureInfo.InvariantCulture) ?? string.Empty;
    }

    private static long GetInt64(SqlDataReader reader, string name)
    {
        var ordinal = reader.GetOrdinal(name);
        return Convert.ToInt64(reader.GetValue(ordinal), CultureInfo.InvariantCulture);
    }

    private static int? GetNullableInt32(SqlDataReader reader, string name)
    {
        var ordinal = reader.GetOrdinal(name);
        return reader.IsDBNull(ordinal) ? null : Convert.ToInt32(reader.GetValue(ordinal), CultureInfo.InvariantCulture);
    }

    private static DateTime? GetNullableDateTime(SqlDataReader reader, string name)
    {
        var ordinal = reader.GetOrdinal(name);
        return reader.IsDBNull(ordinal) ? null : Convert.ToDateTime(reader.GetValue(ordinal), CultureInfo.InvariantCulture);
    }

    private static bool GetBoolean(SqlDataReader reader, string name)
    {
        var ordinal = reader.GetOrdinal(name);
        if (reader.IsDBNull(ordinal))
        {
            return false;
        }

        return Convert.ToBoolean(reader.GetValue(ordinal), CultureInfo.InvariantCulture);
    }

    public sealed class UpcomingChangeItem
    {
        public long RequestId { get; set; }
        public string RequestType { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public DateTime? ExecuteAfter { get; set; }
        public string NewSamAccountName { get; set; } = string.Empty;
        public string TargetSamAccountName { get; set; } = string.Empty;
        public string NewUserPrincipalName { get; set; } = string.Empty;
        public string NewDisplayName { get; set; } = string.Empty;
        public string TargetDisplayName { get; set; } = string.Empty;
        public string EmployeeType { get; set; } = string.Empty;
        public string Company { get; set; } = string.Empty;
        public string Department { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string Office { get; set; } = string.Empty;
        public string PrivateEmail { get; set; } = string.Empty;
        public string Mail { get; set; } = string.Empty;
        public string OfficeLicense { get; set; } = string.Empty;
        public string ComputerType { get; set; } = string.Empty;
        public bool AccessCard { get; set; }
        public string RequestedBy { get; set; } = string.Empty;
        public int GroupCount { get; set; }
        public string GroupSummary { get; set; } = string.Empty;
        public List<UpcomingNeed> Needs { get; set; } = new();

        public string DisplayName => string.IsNullOrWhiteSpace(NewDisplayName) ? TargetDisplayName : NewDisplayName;
        public string AccountName => string.IsNullOrWhiteSpace(NewSamAccountName) ? TargetSamAccountName : NewSamAccountName;
        public string StartOnText => ExecuteAfter?.ToString("d", CultureInfo.CurrentCulture) ?? string.Empty;
        public string WorkerEligibleText
        {
            get
            {
                if (!ExecuteAfter.HasValue)
                {
                    return string.Empty;
                }

                var date = ExecuteAfter.Value.Date;
                if (RequestType.Equals("CREATE", StringComparison.OrdinalIgnoreCase))
                {
                    date = date.AddDays(-1);
                }

                return date.ToString("d", CultureInfo.CurrentCulture);
            }
        }

        public string StatusBadgeClass => Status.Equals("Approved", StringComparison.OrdinalIgnoreCase)
            ? "bg-success"
            : Status.Equals("Processing", StringComparison.OrdinalIgnoreCase)
                ? "bg-warning text-dark"
                : "bg-secondary";

        public string EditUrl => RequestType.Equals("CREATE", StringComparison.OrdinalIgnoreCase)
            ? $"/Requests/NewUser?requestId={RequestId}"
            : $"/Requests/UpdateUser?requestId={RequestId}";
    }

    public sealed record UpcomingNeed(string Label, string BadgeClass, string Tooltip);
}

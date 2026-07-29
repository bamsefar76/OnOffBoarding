using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Data.SqlClient;
using UserChangeQueueWeb.Services;

namespace UserChangeQueueWeb.Pages;

[Authorize]
public class IndexModel : PageModel
{
    private readonly ILogger<IndexModel> _logger;
    private readonly SqlConnectionFactory _connectionFactory;
    private readonly AccessScopeService _accessScopeService;

    public IndexModel(
        ILogger<IndexModel> logger,
        SqlConnectionFactory connectionFactory,
        AccessScopeService accessScopeService)
    {
        _logger = logger;
        _connectionFactory = connectionFactory;
        _accessScopeService = accessScopeService;
    }

    public int PendingApprovalsCount { get; private set; }
    public int ApprovedReadyCount { get; private set; }
    public int UpcomingOpenCount { get; private set; }
    public int TodayStartersCount { get; private set; }
    public int FailedCount { get; private set; }
    public int RecentlyCompletedCount { get; private set; }
    public List<DashboardUpcomingItem> UpcomingItems { get; private set; } = new();

    public async Task<IActionResult> OnGetAsync()
    {
        var scope = await _accessScopeService.GetCurrentAsync(User, HttpContext.RequestAborted);

        // The operational dashboard contains global queue, failure and worker information.
        // Non-IT users are redirected before any dashboard data is loaded.
        if (!scope.IsIT)
        {
            if (scope.HasScopedUpcomingAccess)
            {
                return RedirectToPage("/Requests/Upcoming");
            }

            return RedirectToPage("/TemporaryAccess/Index");
        }

        await using var connection = await _connectionFactory.OpenAsync(HttpContext.RequestAborted);

        PendingApprovalsCount = await CountAsync(connection, "Status = N'Pending'");
        ApprovedReadyCount = await CountAsync(connection,
            "Status = N'Approved' AND (ExecuteAfter IS NULL OR ExecuteAfter <= SYSDATETIME() OR (RequestType = N'CREATE' AND CONVERT(date, DATEADD(day, -1, ExecuteAfter)) <= CONVERT(date, SYSDATETIME())))");
        UpcomingOpenCount = await CountAsync(connection,
            "Status IN (N'Pending', N'Approved', N'Processing') AND (ExecuteAfter IS NULL OR ExecuteAfter < DATEADD(day, 31, SYSDATETIME()))");
        TodayStartersCount = await CountAsync(connection,
            "RequestType = N'CREATE' AND ExecuteAfter IS NOT NULL AND CONVERT(date, ExecuteAfter) = CONVERT(date, SYSDATETIME())");
        FailedCount = await CountAsync(connection, "Status = N'Failed'");
        RecentlyCompletedCount = await CountAsync(connection,
            "Status IN (N'Done', N'Completed', N'Implemented') AND FinishedAt >= DATEADD(day, -7, SYSDATETIME())");

        UpcomingItems = await LoadUpcomingItemsAsync(connection);
        return Page();
    }

    private static async Task<int> CountAsync(SqlConnection connection, string whereClause)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT_BIG(1) FROM dbo.ADUserChangeQueue WHERE {whereClause};";
        var value = await command.ExecuteScalarAsync();
        return Convert.ToInt32(value ?? 0);
    }

    private static async Task<List<DashboardUpcomingItem>> LoadUpcomingItemsAsync(SqlConnection connection)
    {
        var items = new List<DashboardUpcomingItem>();

        await using var command = connection.CreateCommand();
        command.CommandText = @"
SELECT TOP (8)
    RequestId,
    RequestType,
    Status,
    ExecuteAfter,
    COALESCE(NULLIF(NewDisplayName, N''), NULLIF(TargetDisplayName, N''), NULLIF(TargetSamAccountName, N''), NULLIF(NewSamAccountName, N'')) AS DisplayName,
    COALESCE(NULLIF(NewUserPrincipalName, N''), NULLIF(Mail, N''), NULLIF(NewSamAccountName, N''), NULLIF(TargetSamAccountName, N'')) AS AccountName,
    Office,
    OfficeLicense,
    ComputerType,
    AccessCard
FROM dbo.ADUserChangeQueue
WHERE Status IN (N'Pending', N'Approved', N'Processing')
ORDER BY
    CASE WHEN ExecuteAfter IS NULL THEN 1 ELSE 0 END,
    ExecuteAfter,
    RequestId;";

        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            items.Add(new DashboardUpcomingItem
            {
                RequestId = reader.GetInt64(reader.GetOrdinal("RequestId")),
                RequestType = GetNullableString(reader, "RequestType") ?? string.Empty,
                Status = GetNullableString(reader, "Status") ?? string.Empty,
                ExecuteAfter = GetNullableDateTime(reader, "ExecuteAfter"),
                DisplayName = GetNullableString(reader, "DisplayName") ?? string.Empty,
                AccountName = GetNullableString(reader, "AccountName") ?? string.Empty,
                Office = GetNullableString(reader, "Office") ?? string.Empty,
                OfficeLicense = GetNullableString(reader, "OfficeLicense") ?? string.Empty,
                ComputerType = GetNullableString(reader, "ComputerType") ?? string.Empty,
                AccessCard = GetNullableBool(reader, "AccessCard")
            });
        }

        return items;
    }

    private static string? GetNullableString(SqlDataReader reader, string name)
    {
        var ordinal = reader.GetOrdinal(name);
        return reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    }

    private static DateTime? GetNullableDateTime(SqlDataReader reader, string name)
    {
        var ordinal = reader.GetOrdinal(name);
        return reader.IsDBNull(ordinal) ? null : reader.GetDateTime(ordinal);
    }

    private static bool? GetNullableBool(SqlDataReader reader, string name)
    {
        var ordinal = reader.GetOrdinal(name);
        return reader.IsDBNull(ordinal) ? null : reader.GetBoolean(ordinal);
    }

    public sealed class DashboardUpcomingItem
    {
        public long RequestId { get; set; }
        public string RequestType { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public DateTime? ExecuteAfter { get; set; }
        public string DisplayName { get; set; } = string.Empty;
        public string AccountName { get; set; } = string.Empty;
        public string Office { get; set; } = string.Empty;
        public string OfficeLicense { get; set; } = string.Empty;
        public string ComputerType { get; set; } = string.Empty;
        public bool? AccessCard { get; set; }
    }
}

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Data.SqlClient;
using UserChangeQueueWeb.Services;

namespace UserChangeQueueWeb.Pages;

[Authorize]
public class SupervisorModel : PageModel
{
    private readonly SqlConnectionFactory _connectionFactory;
    private readonly ObjectAccessService _objectAccessService;

    public SupervisorModel(SqlConnectionFactory connectionFactory, ObjectAccessService objectAccessService)
    {
        _connectionFactory = connectionFactory;
        _objectAccessService = objectAccessService;
    }

    [BindProperty(SupportsGet = true)]
    public string? Manager { get; set; }

    public string ViewingManagerSamAccountName { get; set; } = "";
    public string LoginName { get; set; } = "";
    public string SamAccountName { get; set; } = "";
    public string? Message { get; set; }

    public List<DirectReportRow> DirectReports { get; set; } = new();
    public List<OrderedChangeRow> OrderedChanges { get; set; } = new();

    public async Task<IActionResult> OnGetAsync()
    {
        LoginName = User.Identity?.Name ?? Environment.UserName;
        SamAccountName = ObjectAccessService.ExtractSamAccountName(LoginName);

        ViewingManagerSamAccountName = string.IsNullOrWhiteSpace(Manager)
            ? SamAccountName
            : ObjectAccessService.ExtractSamAccountName(Manager);

        if (!await _objectAccessService.CanViewManagerAsync(User, ViewingManagerSamAccountName))
        {
            return Forbid();
        }

        await LoadDirectReportsAsync(ViewingManagerSamAccountName);
        await LoadOrderedChangesAsync();

        return Page();
    }

    public class DirectReportRow
    {
        public string SamAccountName { get; set; } = "";
        public string DisplayName { get; set; } = "";
        public string Mail { get; set; } = "";
        public string Title { get; set; } = "";
        public string Department { get; set; } = "";
        public string Company { get; set; } = "";
        public string ObjectGUID { get; set; } = "";
        public bool? Enabled { get; set; }
    }

    public class OrderedChangeRow
    {
        public long Id { get; set; }
        public string RequestType { get; set; } = "";
        public string Status { get; set; } = "";
        public DateTime? ExecuteAfter { get; set; }
        public string TargetSamAccountName { get; set; } = "";
        public string NewSamAccountName { get; set; } = "";
        public string NewDisplayName { get; set; } = "";
        public string Department { get; set; } = "";
        public string Title { get; set; } = "";
        public string RequestedBy { get; set; } = "";
        public DateTime? CreatedAt { get; set; }

        public string NormalizedRequestType => (RequestType ?? "").Trim().ToUpperInvariant();

        public bool IsCreateRequest => NormalizedRequestType == "CREATE";

        public bool IsUpdateRequest => NormalizedRequestType == "UPDATE";

        public string? EditUrl => IsCreateRequest
            ? $"/Requests/NewUser?requestId={Id}"
            : IsUpdateRequest
                ? $"/Requests/UpdateUser?requestId={Id}"
                : null;

        public string LinkText => !string.IsNullOrWhiteSpace(NewDisplayName)
            ? NewDisplayName
            : !string.IsNullOrWhiteSpace(NewSamAccountName)
                ? NewSamAccountName
                : !string.IsNullOrWhiteSpace(TargetSamAccountName)
                    ? TargetSamAccountName
                    : $"Request {Id}";
    }

    private async Task LoadDirectReportsAsync(string managerSamAccountName)
    {
        DirectReports.Clear();

        await using var cn = await _connectionFactory.OpenAsync();

        var columns = await GetColumnNamesAsync(cn, "dbo", "ADObjects");
        var managerColumn = PickFirstExistingColumn(columns,
            "ManagerSamAccountName",
            "ManagerSam",
            "Manager",
            "ManagerUserName");

        if (managerColumn == null)
        {
            Message = "Could not find a manager column in dbo.ADObjects. Expected one of: ManagerSamAccountName, ManagerSam, Manager, ManagerUserName.";
            return;
        }

        var displayNameColumn = PickFirstExistingColumn(columns, "DisplayName", "Name", "CN") ?? "SamAccountName";
        var mailColumn = PickFirstExistingColumn(columns, "Mail", "Email", "UserPrincipalName");
        var titleColumn = PickFirstExistingColumn(columns, "Title");
        var departmentColumn = PickFirstExistingColumn(columns, "Department");
        var companyColumn = PickFirstExistingColumn(columns, "Company");
        var enabledColumn = PickFirstExistingColumn(columns, "Enabled");
        var isDeletedColumn = PickFirstExistingColumn(columns, "IsDeleted");

        var whereParts = new List<string>
        {
            $"[{managerColumn}] = @SamAccountName"
        };

        if (isDeletedColumn != null)
        {
            whereParts.Add($"ISNULL([{isDeletedColumn}], 0) = 0");
        }

        var cmdText = $@"
SELECT
    CONVERT(nvarchar(36), [ObjectGUID]) AS ObjectGUID,
    ISNULL([SamAccountName], '') AS SamAccountName,
    ISNULL([{displayNameColumn}], ISNULL([SamAccountName], '')) AS DisplayName,
    {SelectNullableString(mailColumn, "Mail")},
    {SelectNullableString(titleColumn, "Title")},
    {SelectNullableString(departmentColumn, "Department")},
    {SelectNullableString(companyColumn, "Company")},
    {SelectNullableBit(enabledColumn, "Enabled")}
FROM dbo.ADObjects
WHERE {string.Join(" AND ", whereParts)}
ORDER BY DisplayName, SamAccountName;
";

        await using var cmd = cn.CreateCommand();
        cmd.CommandText = cmdText;
        cmd.Parameters.AddNVarChar("@SamAccountName", managerSamAccountName, 256);

        await using var reader = await cmd.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {
            DirectReports.Add(new DirectReportRow
            {
                ObjectGUID = reader.GetString(0),
                SamAccountName = reader.GetString(1),
                DisplayName = reader.GetString(2),
                Mail = reader.GetString(3),
                Title = reader.GetString(4),
                Department = reader.GetString(5),
                Company = reader.GetString(6),
                Enabled = reader.IsDBNull(7) ? null : Convert.ToBoolean(reader.GetValue(7))
            });
        }
    }

    private async Task LoadOrderedChangesAsync()
    {
        OrderedChanges.Clear();

        await using var cn = await _connectionFactory.OpenAsync();

        var columns = await GetColumnNamesAsync(cn, "dbo", "ADUserChangeQueue");
        var idColumn = PickFirstExistingColumn(columns, "Id", "QueueId", "RequestId");
        var createdAtColumn = PickFirstExistingColumn(columns, "CreatedAt", "Created", "CreatedDate", "LastUpdated");

        var orderBy = createdAtColumn != null
            ? $"[{createdAtColumn}] DESC"
            : idColumn != null ? $"[{idColumn}] DESC" : "ExecuteAfter DESC";

        var cmdText = $@"
SELECT
    {SelectNullableLong(idColumn, "Id")},
    {SelectNullableString(PickFirstExistingColumn(columns, "RequestType"), "RequestType")},
    {SelectNullableString(PickFirstExistingColumn(columns, "Status"), "Status")},
    {SelectNullableDate(PickFirstExistingColumn(columns, "ExecuteAfter"), "ExecuteAfter")},
    {SelectNullableString(PickFirstExistingColumn(columns, "TargetSamAccountName"), "TargetSamAccountName")},
    {SelectNullableString(PickFirstExistingColumn(columns, "NewSamAccountName"), "NewSamAccountName")},
    {SelectNullableString(PickFirstExistingColumn(columns, "NewDisplayName"), "NewDisplayName")},
    {SelectNullableString(PickFirstExistingColumn(columns, "Department"), "Department")},
    {SelectNullableString(PickFirstExistingColumn(columns, "Title"), "Title")},
    {SelectNullableString(PickFirstExistingColumn(columns, "RequestedBy"), "RequestedBy")},
    {SelectNullableDate(createdAtColumn, "CreatedAt")}
FROM dbo.ADUserChangeQueue
WHERE RequestedBy = @LoginName
   OR RequestedBy = @SamAccountName
   OR RequestedBy LIKE @DomainSlashSamAccountName
ORDER BY {orderBy};
";

        await using var cmd = cn.CreateCommand();
        cmd.CommandText = cmdText;
        cmd.Parameters.AddNVarChar("@LoginName", LoginName, 300);
        cmd.Parameters.AddNVarChar("@SamAccountName", SamAccountName, 256);
        cmd.Parameters.AddNVarChar("@DomainSlashSamAccountName", @"%\" + SamAccountName, 300);

        await using var reader = await cmd.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {
            OrderedChanges.Add(new OrderedChangeRow
            {
                Id = reader.IsDBNull(0) ? 0L : Convert.ToInt64(reader.GetValue(0)),
                RequestType = reader.GetString(1).Trim(),
                Status = reader.GetString(2),
                ExecuteAfter = reader.IsDBNull(3) ? null : reader.GetDateTime(3),
                TargetSamAccountName = reader.GetString(4),
                NewSamAccountName = reader.GetString(5),
                NewDisplayName = reader.GetString(6),
                Department = reader.GetString(7),
                Title = reader.GetString(8),
                RequestedBy = reader.GetString(9),
                CreatedAt = reader.IsDBNull(10) ? null : reader.GetDateTime(10)
            });
        }
    }

    private static async Task<HashSet<string>> GetColumnNamesAsync(SqlConnection cn, string schemaName, string tableName)
    {
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        await using var cmd = cn.CreateCommand();
        cmd.CommandText = @"
SELECT COLUMN_NAME
FROM INFORMATION_SCHEMA.COLUMNS
WHERE TABLE_SCHEMA = @SchemaName
  AND TABLE_NAME = @TableName;
";
        cmd.Parameters.AddNVarChar("@SchemaName", schemaName, 128);
        cmd.Parameters.AddNVarChar("@TableName", tableName, 128);

        await using var reader = await cmd.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {
            columns.Add(reader.GetString(0));
        }

        return columns;
    }

    private static string? PickFirstExistingColumn(HashSet<string> columns, params string[] candidates)
    {
        return candidates.FirstOrDefault(columns.Contains);
    }

    private static string SelectNullableString(string? columnName, string alias)
    {
        return columnName == null
            ? $"CAST('' AS nvarchar(4000)) AS [{alias}]"
            : $"ISNULL([{columnName}], '') AS [{alias}]";
    }

    private static string SelectNullableDate(string? columnName, string alias)
    {
        return columnName == null
            ? $"CAST(NULL AS datetime) AS [{alias}]"
            : $"[{columnName}] AS [{alias}]";
    }

    private static string SelectNullableLong(string? columnName, string alias)
    {
        return columnName == null
            ? $"CAST(0 AS bigint) AS [{alias}]"
            : $"ISNULL([{columnName}], 0) AS [{alias}]";
    }

    private static string SelectNullableBit(string? columnName, string alias)
    {
        return columnName == null
            ? $"CAST(NULL AS bit) AS [{alias}]"
            : $"[{columnName}] AS [{alias}]";
    }
}

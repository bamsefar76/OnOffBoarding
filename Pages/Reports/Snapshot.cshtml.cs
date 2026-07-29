using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.Data.SqlClient;
using UserChangeQueueWeb.Services;

namespace UserChangeQueueWeb.Pages;

[Authorize]
public class SnapshotModel : PageModel
{
    private readonly SqlConnectionFactory _connectionFactory;

    public SnapshotModel(SqlConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    [BindProperty(SupportsGet = true)]
public string? SelectedEmployeeType { get; set; }

public class CompanySummaryRow
{
    public string Company { get; set; } = "";
    public int Count { get; set; }
}

public List<CompanySummaryRow> CompanySummary { get; set; } = new();
    public List<SelectListItem> EmployeeTypes { get; set; } = new();
    [BindProperty(SupportsGet = true)]
    public string? SelectedSnapshotMonth { get; set; }

    public string EffectiveSnapshotMonth { get; set; } = "";
    public string? Message { get; set; }

    public List<SelectListItem> SnapshotMonths { get; set; } = new();
    public List<SnapshotRow> Rows { get; set; } = new();

    public async Task OnGetAsync()
    {
await LoadSnapshotMonthsAsync();

if (string.IsNullOrWhiteSpace(SelectedSnapshotMonth))
{
    SelectedSnapshotMonth = SnapshotMonths.FirstOrDefault()?.Value;
}

EffectiveSnapshotMonth = SelectedSnapshotMonth ?? "";

if (string.IsNullOrWhiteSpace(SelectedSnapshotMonth))
{
    Message = "No snapshots found in dbo.ADObjectsMonthlySnapshot.";
    return;
}

        await LoadEmployeeTypesAsync(SelectedSnapshotMonth);
await LoadCompanySummaryAsync(SelectedSnapshotMonth);
await LoadRowsAsync(SelectedSnapshotMonth);    }

    public class SnapshotRow
    {
        public string SnapshotMonth { get; set; } = "";
        public string Name { get; set; } = "";
        public string Department { get; set; } = "";
        public string Title { get; set; } = "";
        public bool? Enabled { get; set; }
        public DateTime? WhenCreated { get; set; }
        public DateTime? WhenChanged { get; set; }
        public string Company { get; set; } = "";
        public string EmployeeType { get; set; } = "";
        public DateTime? AccountExpirationDate { get; set; }
        public string Office { get; set; } = "";

        public string EnabledText =>
            Enabled.HasValue
                ? Enabled.Value ? "Yes" : "No"
                : "";
    }

    private async Task LoadEmployeeTypesAsync(string snapshotMonth)
    {
        EmployeeTypes.Clear();

        if (string.IsNullOrWhiteSpace(snapshotMonth))
        {
            return;
        }

        await using var cn = await _connectionFactory.OpenAsync();

        await using var cmd = cn.CreateCommand();

        cmd.CommandText = @"
SELECT DISTINCT
    LTRIM(RTRIM(EmployeeType)) AS EmployeeType
FROM dbo.ADObjectsMonthlySnapshot
WHERE CONVERT(nvarchar(20), SnapshotMonth) = @SnapshotMonth
  AND NULLIF(LTRIM(RTRIM(EmployeeType)), '') IS NOT NULL
ORDER BY
    LTRIM(RTRIM(EmployeeType));
";

        cmd.Parameters.Add("@SnapshotMonth", System.Data.SqlDbType.NVarChar, 20).Value = snapshotMonth;
        cmd.Parameters.Add("@EmployeeType", System.Data.SqlDbType.NVarChar, 100).Value =
    string.IsNullOrWhiteSpace(SelectedEmployeeType)
        ? DBNull.Value
        : SelectedEmployeeType.Trim();

        await using var reader = await cmd.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {
            var employeeType = reader.GetString(0);

            EmployeeTypes.Add(new SelectListItem
            {
                Value = employeeType,
                Text = employeeType
            });
        }
    }

    private async Task LoadCompanySummaryAsync(string snapshotMonth)
    {
        CompanySummary.Clear();

        await using var cn = await _connectionFactory.OpenAsync();

        await using var cmd = cn.CreateCommand();

        cmd.CommandText = @"
SELECT
    ISNULL(s.Company,'(Blank)') AS Company,
    COUNT(*) AS UserCount
FROM dbo.ADObjectsMonthlySnapshot AS s
WHERE CONVERT(nvarchar(20), s.SnapshotMonth) = @SnapshotMonth
  AND EXISTS
  (
      SELECT 1
      FROM dbo.domains AS d
      CROSS APPLY
      (
          SELECT LTRIM(RTRIM(d.DefaultSearchBase)) AS SearchBase
      ) AS b
      WHERE NULLIF(b.SearchBase, N'') IS NOT NULL
        AND s.CanonicalName LIKE
            CASE
                WHEN RIGHT(b.SearchBase, 1) = N'%' THEN b.SearchBase
                WHEN RIGHT(b.SearchBase, 1) = N'/' THEN b.SearchBase + N'%'
                ELSE b.SearchBase + N'/%'
            END
  )
  AND NULLIF(LTRIM(RTRIM(s.EmployeeType)), '') IS NOT NULL
  AND (
        @EmployeeType IS NULL
        OR @EmployeeType = ''
        OR s.EmployeeType = @EmployeeType
      )
GROUP BY s.Company
ORDER BY COUNT(*) DESC;
";

        cmd.Parameters.AddNVarChar("@SnapshotMonth", snapshotMonth, 20);
        cmd.Parameters.AddNVarChar("@EmployeeType", SelectedEmployeeType, 100);

        await using var reader = await cmd.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {
            CompanySummary.Add(new CompanySummaryRow
            {
                Company = reader.GetString(0),
                Count = reader.GetInt32(1)
            });
        }
    }

    private async Task LoadSnapshotMonthsAsync()
    {
        SnapshotMonths.Clear();

        await using var cn = await _connectionFactory.OpenAsync();

        await using var cmd = cn.CreateCommand();
        cmd.CommandText = @"
SELECT DISTINCT
    CONVERT(nvarchar(20), SnapshotMonth) AS SnapshotMonth
FROM dbo.ADObjectsMonthlySnapshot
WHERE SnapshotMonth IS NOT NULL
ORDER BY SnapshotMonth DESC;
";

        await using var reader = await cmd.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {
            var snapshotMonth = reader.GetString(0);

            SnapshotMonths.Add(new SelectListItem
            {
                Value = snapshotMonth,
                Text = snapshotMonth
            });
        }
    }

    private async Task LoadRowsAsync(string snapshotMonth)
    {
        Rows.Clear();

        await using var cn = await _connectionFactory.OpenAsync();

        await using var cmd = cn.CreateCommand();
        cmd.CommandText = @"
SELECT
    CONVERT(nvarchar(20), s.SnapshotMonth) AS SnapshotMonth,
    ISNULL(s.[Name], '') AS [Name],
    ISNULL(s.Department, '') AS Department,
    ISNULL(s.Title, '') AS Title,
    s.Enabled,
    s.WhenCreated,
    s.WhenChanged,
    ISNULL(s.Company, '') AS Company,
    ISNULL(s.EmployeeType, '') AS EmployeeType,
    s.AccountExpirationDate,
    ISNULL(s.Office, '') AS Office
FROM dbo.ADObjectsMonthlySnapshot AS s
WHERE CONVERT(nvarchar(20), s.SnapshotMonth) = @SnapshotMonth
  AND EXISTS
  (
      SELECT 1
      FROM dbo.domains AS d
      CROSS APPLY
      (
          SELECT LTRIM(RTRIM(d.DefaultSearchBase)) AS SearchBase
      ) AS b
      WHERE NULLIF(b.SearchBase, N'') IS NOT NULL
        AND s.CanonicalName LIKE
            CASE
                WHEN RIGHT(b.SearchBase, 1) = N'%' THEN b.SearchBase
                WHEN RIGHT(b.SearchBase, 1) = N'/' THEN b.SearchBase + N'%'
                ELSE b.SearchBase + N'/%'
            END
  )
  AND NULLIF(LTRIM(RTRIM(s.EmployeeType)), '') IS NOT NULL
  AND (
        @EmployeeType IS NULL
        OR @EmployeeType = ''
        OR s.EmployeeType = @EmployeeType
      )
ORDER BY
    s.Company,
    s.Department,
    s.[Name];
";

        cmd.Parameters.AddNVarChar("@SnapshotMonth", snapshotMonth, 20);
        cmd.Parameters.AddNVarChar("@EmployeeType", SelectedEmployeeType, 100);

        await using var reader = await cmd.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {
            Rows.Add(new SnapshotRow
            {
                SnapshotMonth = reader.GetString(0),
                Name = reader.GetString(1),
                Department = reader.GetString(2),
                Title = reader.GetString(3),
                Enabled = reader.IsDBNull(4) ? null : Convert.ToBoolean(reader.GetValue(4)),
                WhenCreated = reader.IsDBNull(5) ? null : reader.GetDateTime(5),
                WhenChanged = reader.IsDBNull(6) ? null : reader.GetDateTime(6),
                Company = reader.GetString(7),
                EmployeeType = reader.GetString(8),
                AccountExpirationDate = reader.IsDBNull(9) ? null : reader.GetDateTime(9),
                Office = reader.GetString(10)
            });
        }
    }
}
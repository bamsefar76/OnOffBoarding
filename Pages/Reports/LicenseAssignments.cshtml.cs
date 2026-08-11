using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Data.SqlClient;
using UserChangeQueueWeb.Services;

namespace UserChangeQueueWeb.Pages.Reports;

[Authorize]
public sealed class LicenseAssignmentsModel : PageModel
{
    private const int PageSize = 50;
    private readonly SqlConnectionFactory _connections;

    public LicenseAssignmentsModel(SqlConnectionFactory connections)
    {
        _connections = connections;
    }

    [BindProperty(SupportsGet = true)] public string? Search { get; set; }
    [BindProperty(SupportsGet = true)] public string? StatusFilter { get; set; }
    [BindProperty(SupportsGet = true)] public int PageNumber { get; set; } = 1;

    public List<Row> Assignments { get; } = new();
    public int TotalCount { get; private set; }
    public int TotalPages { get; private set; } = 1;
    public int PageSizeValue => PageSize;

    public async Task OnGetAsync()
    {
        Search = string.IsNullOrWhiteSpace(Search) ? null : Search.Trim();
        StatusFilter = string.IsNullOrWhiteSpace(StatusFilter) ? null : StatusFilter.Trim();
        PageNumber = Math.Max(1, PageNumber);

        await using var connection = await _connections.OpenAsync(HttpContext.RequestAborted);
        const string whereSql = @"
WHERE (@Status IS NULL OR assignment.Status=@Status)
  AND
  (
      @Search IS NULL
      OR assignment.UserSamAccountName LIKE N'%' + @Search + N'%'
      OR assignment.UserDisplayName LIKE N'%' + @Search + N'%'
      OR assignment.UserEmail LIKE N'%' + @Search + N'%'
      OR assignment.ProjectNumber LIKE N'%' + @Search + N'%'
      OR product.Name LIKE N'%' + @Search + N'%'
      OR assignment.AdGroupName LIKE N'%' + @Search + N'%'
  )";

        await using (var count = connection.CreateCommand())
        {
            count.CommandText = $@"
SELECT COUNT(*)
FROM dbo.LicenseAssignments AS assignment
INNER JOIN dbo.LicenseProducts AS product
    ON product.LicenseProductId=assignment.LicenseProductId
{whereSql};";
            AddParameters(count);
            TotalCount = Convert.ToInt32(await count.ExecuteScalarAsync(HttpContext.RequestAborted));
        }

        TotalPages = Math.Max(1, (int)Math.Ceiling(TotalCount / (double)PageSize));
        PageNumber = Math.Clamp(PageNumber, 1, TotalPages);

        await using var command = connection.CreateCommand();
        command.CommandText = $@"
SELECT
    assignment.LicenseAssignmentId,
    assignment.UserSamAccountName,
    ISNULL(assignment.UserDisplayName,N''),
    ISNULL(assignment.UserEmail,N''),
    product.Name,
    ISNULL(assignment.ProjectNumber,N''),
    assignment.StartDate,
    assignment.EndDate,
    assignment.IsPermanent,
    assignment.Status,
    assignment.FulfillmentType,
    ISNULL(assignment.AdGroupName,N''),
    assignment.ActivatedAt,
    assignment.EndedAt,
    assignment.LicenseApplicationId
FROM dbo.LicenseAssignments AS assignment
INNER JOIN dbo.LicenseProducts AS product
    ON product.LicenseProductId=assignment.LicenseProductId
{whereSql}
ORDER BY assignment.StartDate DESC, assignment.LicenseAssignmentId DESC
OFFSET @Offset ROWS FETCH NEXT @PageSize ROWS ONLY;";
        AddParameters(command);
        command.Parameters.AddInt("@Offset", (PageNumber - 1) * PageSize);
        command.Parameters.AddInt("@PageSize", PageSize);

        await using var reader = await command.ExecuteReaderAsync(HttpContext.RequestAborted);
        while (await reader.ReadAsync(HttpContext.RequestAborted))
        {
            Assignments.Add(new Row
            {
                Id = reader.GetInt64(0),
                SamAccountName = Get(reader, 1),
                DisplayName = Get(reader, 2),
                Email = Get(reader, 3),
                LicenseName = Get(reader, 4),
                ProjectNumber = Get(reader, 5),
                StartDate = reader.GetDateTime(6),
                EndDate = reader.IsDBNull(7) ? null : reader.GetDateTime(7),
                IsPermanent = reader.GetBoolean(8),
                Status = Get(reader, 9),
                FulfillmentType = Get(reader, 10),
                AdGroupName = Get(reader, 11),
                ActivatedAt = reader.IsDBNull(12) ? null : reader.GetDateTime(12),
                EndedAt = reader.IsDBNull(13) ? null : reader.GetDateTime(13),
                ApplicationId = reader.GetInt64(14)
            });
        }
    }

    private void AddParameters(SqlCommand command)
    {
        command.Parameters.AddNVarChar("@Search", Search, 300);
        command.Parameters.AddNVarChar("@Status", StatusFilter, 30);
    }

    private static string Get(SqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? "" : Convert.ToString(reader.GetValue(ordinal)) ?? "";

    public sealed class Row
    {
        public long Id { get; init; }
        public long ApplicationId { get; init; }
        public string SamAccountName { get; init; } = "";
        public string DisplayName { get; init; } = "";
        public string Email { get; init; } = "";
        public string LicenseName { get; init; } = "";
        public string ProjectNumber { get; init; } = "";
        public DateTime StartDate { get; init; }
        public DateTime? EndDate { get; init; }
        public bool IsPermanent { get; init; }
        public string Status { get; init; } = "";
        public string FulfillmentType { get; init; } = "";
        public string AdGroupName { get; init; } = "";
        public DateTime? ActivatedAt { get; init; }
        public DateTime? EndedAt { get; init; }
    }
}

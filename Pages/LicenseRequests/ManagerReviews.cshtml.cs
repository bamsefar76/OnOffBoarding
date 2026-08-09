using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Data.SqlClient;
using UserChangeQueueWeb.Services;

namespace UserChangeQueueWeb.Pages.LicenseRequests;

[Authorize]
public sealed class ManagerReviewsModel : PageModel
{
    private const int PageSize = 20;
    private static readonly HashSet<string> AllowedStatuses = new(StringComparer.OrdinalIgnoreCase)
    {
        "AwaitingManager",
        "AwaitingIT",
        "Approved",
        "PartiallyApproved",
        "Completed",
        "ManagerRejected",
        "ITRejected"
    };

    private readonly SqlConnectionFactory _connections;

    public ManagerReviewsModel(SqlConnectionFactory connections)
    {
        _connections = connections;
    }

    [BindProperty(SupportsGet = true)]
    public string? Search { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? StatusFilter { get; set; }

    [BindProperty(SupportsGet = true)]
    public int PageNumber { get; set; } = 1;

    public int PageSizeValue => PageSize;
    public int TotalCount { get; private set; }
    public int PendingCount { get; private set; }
    public int TotalPages => Math.Max(1, (int)Math.Ceiling(TotalCount / (double)PageSize));
    public List<ApplicationSummary> Applications { get; } = new();

    public async Task OnGetAsync()
    {
        Search = string.IsNullOrWhiteSpace(Search) ? null : Search.Trim();
        StatusFilter = NormalizeStatus(StatusFilter);
        PageNumber = Math.Max(1, PageNumber);

        var currentSam = AccessScopeService.ExtractSamAccountName(User.Identity?.Name);
        if (string.IsNullOrWhiteSpace(currentSam))
        {
            return;
        }

        await using var connection = await _connections.OpenAsync(HttpContext.RequestAborted);

        PendingCount = await CountPendingAsync(connection, currentSam);
        TotalCount = await CountApplicationsAsync(connection, currentSam);

        if (PageNumber > TotalPages)
        {
            PageNumber = TotalPages;
        }

        await LoadApplicationsAsync(connection, currentSam);
    }

    private async Task<int> CountPendingAsync(SqlConnection connection, string currentSam)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = @"
SELECT COUNT(*)
FROM dbo.LicenseApplications
WHERE ManagerSamAccountName = @ManagerSam
  AND Status = N'AwaitingManager';";
        command.Parameters.AddRequiredNVarChar("@ManagerSam", currentSam, 256);
        return Convert.ToInt32(await command.ExecuteScalarAsync(HttpContext.RequestAborted));
    }

    private async Task<int> CountApplicationsAsync(SqlConnection connection, string currentSam)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = @"
SELECT COUNT(*)
FROM dbo.LicenseApplications AS application
WHERE application.ManagerSamAccountName = @ManagerSam
  AND (@Status = N'' OR application.Status = @Status)
  AND
  (
      @Search = N''
      OR application.RequestedForDisplayName LIKE @SearchLike
      OR application.RequestedForEmail LIKE @SearchLike
      OR application.BusinessReason LIKE @SearchLike
      OR EXISTS
      (
          SELECT 1
          FROM dbo.LicenseApplicationItems AS searchItem
          INNER JOIN dbo.LicenseProducts AS searchProduct
              ON searchProduct.LicenseProductId = searchItem.LicenseProductId
          WHERE searchItem.LicenseApplicationId = application.LicenseApplicationId
            AND searchProduct.Name LIKE @SearchLike
      )
  );";
        AddFilterParameters(command, currentSam);
        return Convert.ToInt32(await command.ExecuteScalarAsync(HttpContext.RequestAborted));
    }

    private async Task LoadApplicationsAsync(SqlConnection connection, string currentSam)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = @"
SELECT application.LicenseApplicationId,
       application.RequestedForDisplayName,
       application.RequestedForEmail,
       application.BusinessReason,
       application.Status,
       application.ManagerDecision,
       application.ManagerReason,
       application.SubmittedAt,
       application.ManagerDecisionAt,
       STUFF
       (
           (
               SELECT N', ' + product.Name
               FROM dbo.LicenseApplicationItems AS item
               INNER JOIN dbo.LicenseProducts AS product
                   ON product.LicenseProductId = item.LicenseProductId
               WHERE item.LicenseApplicationId = application.LicenseApplicationId
               ORDER BY product.Name
               FOR XML PATH(N''), TYPE
           ).value(N'.', N'nvarchar(max)'),
           1,
           2,
           N''
       ) AS LicenseNames
FROM dbo.LicenseApplications AS application
WHERE application.ManagerSamAccountName = @ManagerSam
  AND (@Status = N'' OR application.Status = @Status)
  AND
  (
      @Search = N''
      OR application.RequestedForDisplayName LIKE @SearchLike
      OR application.RequestedForEmail LIKE @SearchLike
      OR application.BusinessReason LIKE @SearchLike
      OR EXISTS
      (
          SELECT 1
          FROM dbo.LicenseApplicationItems AS searchItem
          INNER JOIN dbo.LicenseProducts AS searchProduct
              ON searchProduct.LicenseProductId = searchItem.LicenseProductId
          WHERE searchItem.LicenseApplicationId = application.LicenseApplicationId
            AND searchProduct.Name LIKE @SearchLike
      )
  )
ORDER BY application.SubmittedAt DESC,
         application.LicenseApplicationId DESC
OFFSET @Offset ROWS FETCH NEXT @PageSize ROWS ONLY;";
        AddFilterParameters(command, currentSam);
        command.Parameters.AddInt("@Offset", (PageNumber - 1) * PageSize);
        command.Parameters.AddInt("@PageSize", PageSize);

        await using var reader = await command.ExecuteReaderAsync(HttpContext.RequestAborted);
        while (await reader.ReadAsync(HttpContext.RequestAborted))
        {
            Applications.Add(new ApplicationSummary
            {
                Id = reader.GetInt64(0),
                UserName = Get(reader, 1),
                UserEmail = Get(reader, 2),
                BusinessReason = Get(reader, 3),
                Status = Get(reader, 4),
                ManagerDecision = Get(reader, 5),
                ManagerReason = Get(reader, 6),
                SubmittedAt = reader.GetDateTime(7),
                ManagerDecisionAt = reader.IsDBNull(8) ? null : reader.GetDateTime(8),
                LicenseNames = Get(reader, 9)
            });
        }
    }

    private void AddFilterParameters(SqlCommand command, string currentSam)
    {
        var search = Search ?? string.Empty;
        command.Parameters.AddRequiredNVarChar("@ManagerSam", currentSam, 256);
        command.Parameters.AddRequiredNVarChar("@Status", StatusFilter ?? string.Empty, 40);
        command.Parameters.AddRequiredNVarChar("@Search", search, 500);
        command.Parameters.AddRequiredNVarChar("@SearchLike", string.IsNullOrWhiteSpace(search) ? string.Empty : $"%{search}%", 520);
    }

    private static string? NormalizeStatus(string? status)
    {
        if (string.IsNullOrWhiteSpace(status))
        {
            return null;
        }

        var trimmed = status.Trim();
        return AllowedStatuses.Contains(trimmed) ? trimmed : null;
    }

    private static string Get(SqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? string.Empty : Convert.ToString(reader.GetValue(ordinal)) ?? string.Empty;

    public sealed class ApplicationSummary
    {
        public long Id { get; init; }
        public string UserName { get; init; } = string.Empty;
        public string UserEmail { get; init; } = string.Empty;
        public string BusinessReason { get; init; } = string.Empty;
        public string Status { get; init; } = string.Empty;
        public string ManagerDecision { get; init; } = string.Empty;
        public string ManagerReason { get; init; } = string.Empty;
        public DateTime SubmittedAt { get; init; }
        public DateTime? ManagerDecisionAt { get; init; }
        public string LicenseNames { get; init; } = string.Empty;
    }
}

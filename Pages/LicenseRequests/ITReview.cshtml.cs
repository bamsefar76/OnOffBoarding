using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Data.SqlClient;
using UserChangeQueueWeb.Services;

namespace UserChangeQueueWeb.Pages.LicenseRequests;

[Authorize]
public sealed class ITReviewModel : PageModel
{
    private const int PageSize = 20;

    private readonly SqlConnectionFactory _connections;
    private readonly LicenseEmailService _emails;

    public ITReviewModel(
        SqlConnectionFactory connections,
        LicenseEmailService emails)
    {
        _connections = connections;
        _emails = emails;
    }

    [BindProperty]
    public string? DecisionReason { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? Search { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? StatusFilter { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? RequesterFilter { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? LicenseFilter { get; set; }

    [BindProperty(SupportsGet = true)]
    public DateTime? DateFilter { get; set; }

    [BindProperty(SupportsGet = true)]
    public int PageNumber { get; set; } = 1;

    [BindProperty(SupportsGet = true)]
    public long? OpenId { get; set; }

    [TempData]
    public string? StatusMessageKey { get; set; }

    [TempData]
    public string? StatusMessageArgument { get; set; }

    public List<ApplicationDetails> Applications { get; } = new();
    public List<string> LicenseOptions { get; } = new();
    public int TotalCount { get; private set; }
    public int TotalPages { get; private set; } = 1;
    public int PageSizeValue => PageSize;

    public async Task<IActionResult> OnGetAsync()
    {
        NormalizeFilters();

        await using var connection =
            await _connections.OpenAsync(HttpContext.RequestAborted);

        await LoadLicenseOptionsAsync(connection);
        await LoadApplicationsAsync(connection);
        return Page();
    }

    public Task<IActionResult> OnPostApproveAsync(long applicationId, long itemId) =>
        ItDecisionAsync(applicationId, itemId, "Approved", null);

    public async Task<IActionResult> OnPostRejectAsync(long applicationId, long itemId)
    {
        NormalizeFilters();
        DecisionReason = DecisionReason?.Trim() ?? "";

        if (string.IsNullOrWhiteSpace(DecisionReason))
        {
            StatusMessageKey = "licenseReview.message.rejectionReasonRequired";
            return RedirectToPage(GetRedirectValues(applicationId));
        }

        return await ItDecisionAsync(
            applicationId,
            itemId,
            "Rejected",
            DecisionReason);
    }

    public async Task<IActionResult> OnPostRetryProvisioningAsync(long applicationId, long itemId)
    {
        NormalizeFilters();

        await using var connection =
            await _connections.OpenAsync(HttpContext.RequestAborted);

        await using var transaction =
            (SqlTransaction)await connection.BeginTransactionAsync(HttpContext.RequestAborted);

        try
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = @"
UPDATE dbo.LicenseApplicationItems
SET ProvisioningStatus=N'Pending',
    ProvisioningLockId=NULL,
    ProvisioningLockedAt=NULL,
    ProvisioningLastError=NULL
WHERE LicenseApplicationItemId=@ItemId
  AND LicenseApplicationId=@ApplicationId
  AND FulfillmentType=N'AdGroup'
  AND ProvisioningStatus=N'Failed';

IF @@ROWCOUNT = 1
BEGIN
    UPDATE dbo.LicenseApplications
    SET Status = CASE
        WHEN EXISTS
        (
            SELECT 1 FROM dbo.LicenseApplicationItems
            WHERE LicenseApplicationId=@ApplicationId
              AND FulfillmentType=N'Manual'
              AND Status=N'Pending'
        ) THEN N'AwaitingIT'
        ELSE N'Provisioning'
    END
    WHERE LicenseApplicationId=@ApplicationId;
END;";
            command.Parameters.AddBigInt("@ApplicationId", applicationId);
            command.Parameters.AddBigInt("@ItemId", itemId);

            var affected = await command.ExecuteNonQueryAsync(HttpContext.RequestAborted);
            await transaction.CommitAsync(HttpContext.RequestAborted);

            StatusMessageKey = affected > 0
                ? "licenseReview.message.provisioningRetried"
                : "licenseReview.message.provisioningRetryUnavailable";
            StatusMessageArgument = itemId.ToString();
            return RedirectToPage(GetRedirectValues(applicationId));
        }
        catch
        {
            await transaction.RollbackAsync(HttpContext.RequestAborted);
            throw;
        }
    }

    public async Task<IActionResult> OnPostCompleteAsync(long applicationId)
    {
        NormalizeFilters();

        await using var connection =
            await _connections.OpenAsync(HttpContext.RequestAborted);

        await using var transaction =
            (SqlTransaction)await connection.BeginTransactionAsync(
                HttpContext.RequestAborted);

        try
        {
            var application = await LoadApplicationAsync(
                connection,
                transaction,
                applicationId);

            if (application is null)
                return NotFound();

            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = @"
UPDATE dbo.LicenseApplicationItems
SET
    Status = N'Completed',
    ItDecisionAt = COALESCE(ItDecisionAt, SYSDATETIME()),
    ItDecisionBy = COALESCE(ItDecisionBy, @ChangedBy)
WHERE LicenseApplicationId = @ApplicationId
  AND FulfillmentType = N'Manual'
  AND Status = N'Approved'
  AND EXISTS
  (
      SELECT 1
      FROM dbo.LicenseApplications
      WHERE LicenseApplicationId = @ApplicationId
        AND Status IN (N'Approved', N'PartiallyApproved')
  );

UPDATE dbo.LicenseApplications
SET
    Status = N'Completed',
    CompletedAt = SYSDATETIME()
WHERE LicenseApplicationId = @ApplicationId
  AND Status IN (N'Approved', N'PartiallyApproved')
  AND NOT EXISTS
  (
      SELECT 1
      FROM dbo.LicenseApplicationItems
      WHERE LicenseApplicationId = @ApplicationId
        AND FulfillmentType = N'AdGroup'
        AND ProvisioningStatus IN (N'Pending', N'Processing', N'Failed')
  );";
            command.Parameters.AddBigInt("@ApplicationId", applicationId);
            command.Parameters.AddRequiredNVarChar(
                "@ChangedBy",
                User.Identity?.Name ?? Environment.UserName,
                300);

            await command.ExecuteNonQueryAsync(HttpContext.RequestAborted);

            await using (var statusCommand = connection.CreateCommand())
            {
                statusCommand.Transaction = transaction;
                statusCommand.CommandText = @"
SELECT Status
FROM dbo.LicenseApplications
WHERE LicenseApplicationId=@ApplicationId;";
                statusCommand.Parameters.AddBigInt("@ApplicationId", applicationId);
                var completedStatus = Convert.ToString(
                    await statusCommand.ExecuteScalarAsync(HttpContext.RequestAborted));
                if (!string.Equals(completedStatus, "Completed", StringComparison.Ordinal))
                {
                    await transaction.RollbackAsync(HttpContext.RequestAborted);
                    StatusMessageKey = "licenseReview.message.completionUnavailable";
                    StatusMessageArgument = applicationId.ToString();
                    return RedirectToPage(GetRedirectValues(applicationId));
                }
            }

            var licenseText = string.Join(
                Environment.NewLine,
                application.Items.Select(x => "- " + x.Name));

            await _emails.QueueTemplateAsync(
                connection,
                transaction,
                "LicenseRequestCompleted",
                application.UserEmail,
                application.UserName,
                new Dictionary<string, string?>
                {
                    ["ApplicationId"] = applicationId.ToString(),
                    ["RequesterName"] = application.UserName,
                    ["LicenseList"] = licenseText
                },
                new Dictionary<string, string?>
                {
                    ["LicenseList"] = application.LicenseHtml
                },
                HttpContext.RequestAborted);

            await transaction.CommitAsync(HttpContext.RequestAborted);
            StatusMessageKey = "licenseReview.message.completed";
            StatusMessageArgument = applicationId.ToString();
            return RedirectToPage(GetRedirectValues(applicationId));
        }
        catch
        {
            await transaction.RollbackAsync(HttpContext.RequestAborted);
            throw;
        }
    }

    private async Task<IActionResult> ItDecisionAsync(
        long applicationId,
        long itemId,
        string decision,
        string? reason)
    {
        NormalizeFilters();

        await using var connection =
            await _connections.OpenAsync(HttpContext.RequestAborted);

        await using var transaction =
            (SqlTransaction)await connection.BeginTransactionAsync(
                HttpContext.RequestAborted);

        try
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = @"
UPDATE item
SET
    Status = @Decision,
    ItDecision = @Decision,
    ItReason = @Reason,
    ItDecisionBy = @ChangedBy,
    ItDecisionAt = SYSDATETIME()
FROM dbo.LicenseApplicationItems AS item
INNER JOIN dbo.LicenseApplications AS application
    ON application.LicenseApplicationId = item.LicenseApplicationId
WHERE item.LicenseApplicationItemId = @ItemId
  AND item.LicenseApplicationId = @ApplicationId
  AND item.FulfillmentType = N'Manual'
  AND item.Status = N'Pending'
  AND application.Status IN (N'AwaitingIT', N'PartiallyApproved');

UPDATE dbo.LicenseApplications
SET Status = CASE
    WHEN EXISTS
    (
        SELECT 1 FROM dbo.LicenseApplicationItems
        WHERE LicenseApplicationId=@ApplicationId
          AND FulfillmentType=N'Manual' AND Status=N'Pending'
    ) THEN N'AwaitingIT'
    WHEN EXISTS
    (
        SELECT 1 FROM dbo.LicenseApplicationItems
        WHERE LicenseApplicationId=@ApplicationId
          AND FulfillmentType=N'AdGroup'
          AND ProvisioningStatus IN (N'Pending', N'Processing')
    ) THEN N'Provisioning'
    WHEN EXISTS
    (
        SELECT 1 FROM dbo.LicenseApplicationItems
        WHERE LicenseApplicationId=@ApplicationId
          AND FulfillmentType=N'AdGroup'
          AND ProvisioningStatus=N'Failed'
    ) THEN N'ProvisioningFailed'
    WHEN EXISTS
    (
        SELECT 1 FROM dbo.LicenseApplicationItems
        WHERE LicenseApplicationId=@ApplicationId AND Status=N'Approved'
    )
     AND EXISTS
    (
        SELECT 1 FROM dbo.LicenseApplicationItems
        WHERE LicenseApplicationId=@ApplicationId AND Status=N'Rejected'
    ) THEN N'PartiallyApproved'
    WHEN EXISTS
    (
        SELECT 1 FROM dbo.LicenseApplicationItems
        WHERE LicenseApplicationId=@ApplicationId AND Status=N'Approved'
    ) THEN N'Approved'
    WHEN EXISTS
    (
        SELECT 1 FROM dbo.LicenseApplicationItems
        WHERE LicenseApplicationId=@ApplicationId AND Status=N'Completed'
    )
     AND EXISTS
    (
        SELECT 1 FROM dbo.LicenseApplicationItems
        WHERE LicenseApplicationId=@ApplicationId AND Status=N'Rejected'
    ) THEN N'PartiallyApproved'
    ELSE N'ITRejected'
END
WHERE LicenseApplicationId = @ApplicationId;";
            command.Parameters.AddRequiredNVarChar("@Decision", decision, 30);
            command.Parameters.AddNVarChar("@Reason", reason, 2000);
            command.Parameters.AddRequiredNVarChar(
                "@ChangedBy",
                User.Identity?.Name ?? Environment.UserName,
                300);
            command.Parameters.AddBigInt("@ItemId", itemId);
            command.Parameters.AddBigInt("@ApplicationId", applicationId);

            await command.ExecuteNonQueryAsync(HttpContext.RequestAborted);
            await transaction.CommitAsync(HttpContext.RequestAborted);

            StatusMessageKey = decision == "Approved"
                ? "licenseReview.message.itemApproved"
                : "licenseReview.message.itemRejected";
            StatusMessageArgument = itemId.ToString();

            return RedirectToPage(GetRedirectValues(applicationId));
        }
        catch
        {
            await transaction.RollbackAsync(HttpContext.RequestAborted);
            throw;
        }
    }

    private async Task LoadLicenseOptionsAsync(SqlConnection connection)
    {
        LicenseOptions.Clear();

        await using var command = connection.CreateCommand();
        command.CommandText = @"
SELECT DISTINCT Name
FROM dbo.LicenseProducts
WHERE NULLIF(LTRIM(RTRIM(Name)), N'') IS NOT NULL
ORDER BY Name;";

        await using var reader =
            await command.ExecuteReaderAsync(HttpContext.RequestAborted);

        while (await reader.ReadAsync(HttpContext.RequestAborted))
        {
            LicenseOptions.Add(reader.GetString(0));
        }
    }

    private async Task LoadApplicationsAsync(SqlConnection connection)
    {
        Applications.Clear();

        var whereSql = @"
WHERE (@Status IS NULL OR application.Status = @Status)
  AND
  (
      @Requester IS NULL
      OR application.RequestedForDisplayName LIKE N'%' + @Requester + N'%'
      OR application.RequestedForEmail LIKE N'%' + @Requester + N'%'
  )
  AND
  (
      @License IS NULL
      OR EXISTS
      (
          SELECT 1
          FROM dbo.LicenseApplicationItems AS filterItem
          INNER JOIN dbo.LicenseProducts AS filterProduct
              ON filterProduct.LicenseProductId = filterItem.LicenseProductId
          WHERE filterItem.LicenseApplicationId = application.LicenseApplicationId
            AND filterProduct.Name = @License
      )
  )
  AND
  (
      @SubmittedDate IS NULL
      OR
      (
          application.SubmittedAt >= @SubmittedDate
          AND application.SubmittedAt < DATEADD(day, 1, @SubmittedDate)
      )
  )
  AND
  (
      @Search IS NULL
      OR CONVERT(nvarchar(30), application.LicenseApplicationId) LIKE N'%' + @Search + N'%'
      OR application.RequestedForDisplayName LIKE N'%' + @Search + N'%'
      OR application.RequestedForEmail LIKE N'%' + @Search + N'%'
      OR application.ManagerDisplayName LIKE N'%' + @Search + N'%'
      OR application.BusinessReason LIKE N'%' + @Search + N'%'
      OR EXISTS
      (
          SELECT 1
          FROM dbo.LicenseApplicationItems AS searchItem
          INNER JOIN dbo.LicenseProducts AS searchProduct
              ON searchProduct.LicenseProductId = searchItem.LicenseProductId
          WHERE searchItem.LicenseApplicationId = application.LicenseApplicationId
            AND searchProduct.Name LIKE N'%' + @Search + N'%'
      )
  )";

        await using (var countCommand = connection.CreateCommand())
        {
            countCommand.CommandText = $@"
SELECT COUNT(*)
FROM dbo.LicenseApplications AS application
{whereSql};";
            AddFilterParameters(countCommand);
            TotalCount = Convert.ToInt32(
                await countCommand.ExecuteScalarAsync(HttpContext.RequestAborted));
        }

        TotalPages = Math.Max(1, (int)Math.Ceiling(TotalCount / (double)PageSize));
        PageNumber = Math.Clamp(PageNumber, 1, TotalPages);

        await using var command = connection.CreateCommand();
        command.CommandText = $@"
SELECT application.LicenseApplicationId
FROM dbo.LicenseApplications AS application
{whereSql}
ORDER BY application.SubmittedAt DESC, application.LicenseApplicationId DESC
OFFSET @Offset ROWS FETCH NEXT @PageSize ROWS ONLY;";
        AddFilterParameters(command);
        command.Parameters.AddInt("@Offset", (PageNumber - 1) * PageSize);
        command.Parameters.AddInt("@PageSize", PageSize);

        var ids = new List<long>();
        await using (var reader =
            await command.ExecuteReaderAsync(HttpContext.RequestAborted))
        {
            while (await reader.ReadAsync(HttpContext.RequestAborted))
                ids.Add(reader.GetInt64(0));
        }

        foreach (var id in ids)
        {
            var application = await LoadApplicationAsync(
                connection,
                null,
                id);

            if (application is not null)
                Applications.Add(application);
        }
    }

    private void AddFilterParameters(SqlCommand command)
    {
        command.Parameters.AddNVarChar("@Status", StatusFilter, 40);
        command.Parameters.AddNVarChar("@Requester", RequesterFilter, 300);
        command.Parameters.AddNVarChar("@License", LicenseFilter, 200);
        command.Parameters.AddNullableDate("@SubmittedDate", DateFilter);
        command.Parameters.AddNVarChar("@Search", Search, 300);
    }

    private async Task<ApplicationDetails?> LoadApplicationAsync(
        SqlConnection connection,
        SqlTransaction? transaction,
        long id)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = @"
SELECT
    application.LicenseApplicationId,
    application.RequestedForDisplayName,
    application.RequestedForEmail,
    application.ManagerSamAccountName,
    application.ManagerDisplayName,
    application.BusinessReason,
    application.Status,
    application.ManagerDecision,
    application.ManagerReason,
    application.SubmittedAt,
    item.LicenseApplicationItemId,
    product.Name,
    item.Status,
    item.ItReason,
    item.FulfillmentType,
    item.AdGroupName,
    item.ProvisioningStatus,
    item.ProvisioningLastError
FROM dbo.LicenseApplications AS application
INNER JOIN dbo.LicenseApplicationItems AS item
    ON item.LicenseApplicationId = application.LicenseApplicationId
INNER JOIN dbo.LicenseProducts AS product
    ON product.LicenseProductId = item.LicenseProductId
WHERE application.LicenseApplicationId = @Id
ORDER BY product.Name;";
        command.Parameters.AddBigInt("@Id", id);

        ApplicationDetails? result = null;

        await using var reader =
            await command.ExecuteReaderAsync(HttpContext.RequestAborted);

        while (await reader.ReadAsync(HttpContext.RequestAborted))
        {
            result ??= new ApplicationDetails
            {
                Id = reader.GetInt64(0),
                UserName = Get(reader, 1),
                UserEmail = Get(reader, 2),
                ManagerSam = Get(reader, 3),
                ManagerName = Get(reader, 4),
                BusinessReason = Get(reader, 5),
                Status = Get(reader, 6),
                ManagerDecision = Get(reader, 7),
                ManagerReason = Get(reader, 8),
                SubmittedAt = reader.GetDateTime(9)
            };

            result.Items.Add(new ItemDetails
            {
                Id = reader.GetInt64(10),
                Name = Get(reader, 11),
                Status = Get(reader, 12),
                Reason = Get(reader, 13),
                FulfillmentType = Get(reader, 14),
                AdGroupName = Get(reader, 15),
                ProvisioningStatus = Get(reader, 16),
                ProvisioningLastError = Get(reader, 17)
            });
        }

        return result;
    }

    private object GetRedirectValues(long? openId = null) => new
    {
        Search,
        StatusFilter,
        RequesterFilter,
        LicenseFilter,
        DateFilter = DateFilter?.ToString("yyyy-MM-dd"),
        PageNumber,
        OpenId = openId
    };

    private void NormalizeFilters()
    {
        Search = NullIfWhiteSpace(Search);
        StatusFilter = NullIfWhiteSpace(StatusFilter);
        RequesterFilter = NullIfWhiteSpace(RequesterFilter);
        LicenseFilter = NullIfWhiteSpace(LicenseFilter);
        PageNumber = Math.Max(1, PageNumber);
    }

    private static string? NullIfWhiteSpace(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string Get(SqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal)
            ? ""
            : Convert.ToString(reader.GetValue(ordinal)) ?? "";

    public sealed class ApplicationDetails
    {
        public long Id { get; init; }
        public string UserName { get; init; } = "";
        public string UserEmail { get; init; } = "";
        public string ManagerSam { get; init; } = "";
        public string ManagerName { get; init; } = "";
        public string BusinessReason { get; init; } = "";
        public string Status { get; init; } = "";
        public string ManagerDecision { get; init; } = "";
        public string ManagerReason { get; init; } = "";
        public DateTime SubmittedAt { get; init; }
        public List<ItemDetails> Items { get; } = new();
        public string LicenseHtml => string.Join(
            "<br />",
            Items.Select(x =>
                "&#8226; " + System.Net.WebUtility.HtmlEncode(x.Name)));
    }

    public sealed class ItemDetails
    {
        public long Id { get; init; }
        public string Name { get; init; } = "";
        public string Status { get; init; } = "";
        public string Reason { get; init; } = "";
        public string FulfillmentType { get; init; } = "Manual";
        public string AdGroupName { get; init; } = "";
        public string ProvisioningStatus { get; init; } = "";
        public string ProvisioningLastError { get; init; } = "";
    }
}

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Data.SqlClient;
using UserChangeQueueWeb.Services;

namespace UserChangeQueueWeb.Pages.LicenseRequests;

[Authorize]
public sealed class ITReviewModel : PageModel
{
    private readonly SqlConnectionFactory _connections;
    private readonly AccessScopeService _accessScope;
    private readonly LicenseEmailService _emails;

    public ITReviewModel(
        SqlConnectionFactory connections,
        AccessScopeService accessScope,
        LicenseEmailService emails)
    {
        _connections = connections;
        _accessScope = accessScope;
        _emails = emails;
    }

    [BindProperty]
    public string? DecisionReason { get; set; }

    [TempData]
    public string? StatusMessage { get; set; }

    public List<ApplicationDetails> Applications { get; } = new();

    public async Task<IActionResult> OnGetAsync()
    {
        if (!await IsItAsync())
            return Forbid();

        await using var connection =
            await _connections.OpenAsync(HttpContext.RequestAborted);

        await LoadApplicationsAsync(connection);
        return Page();
    }

    public Task<IActionResult> OnPostApproveAsync(long applicationId, long itemId) =>
        ItDecisionAsync(applicationId, itemId, "Approved", null);

    public async Task<IActionResult> OnPostRejectAsync(long applicationId, long itemId)
    {
        DecisionReason = DecisionReason?.Trim() ?? "";

        if (string.IsNullOrWhiteSpace(DecisionReason))
        {
            StatusMessage = "A rejection reason is required.";
            return RedirectToPage();
        }

        return await ItDecisionAsync(
            applicationId,
            itemId,
            "Rejected",
            DecisionReason);
    }

    public async Task<IActionResult> OnPostCompleteAsync(long applicationId)
    {
        if (!await IsItAsync())
            return Forbid();

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
  AND Status = N'Approved';

UPDATE dbo.LicenseApplications
SET
    Status = N'Completed',
    CompletedAt = SYSDATETIME()
WHERE LicenseApplicationId = @ApplicationId
  AND Status IN (N'Approved', N'PartiallyApproved');";
            command.Parameters.AddBigInt("@ApplicationId", applicationId);
            command.Parameters.AddRequiredNVarChar(
                "@ChangedBy",
                User.Identity?.Name ?? Environment.UserName,
                300);

            await command.ExecuteNonQueryAsync(HttpContext.RequestAborted);

            var body =
                "<p>Hello " +
                System.Net.WebUtility.HtmlEncode(application.UserName) +
                ",</p><p>IT marked application #" + applicationId +
                " as completed.</p><p>" + application.LicenseHtml + "</p>";

            await _emails.QueueAsync(
                connection,
                transaction,
                "LicenseRequestCompleted",
                application.UserEmail,
                application.UserName,
                $"License application {applicationId} completed",
                body,
                HttpContext.RequestAborted);

            await transaction.CommitAsync(HttpContext.RequestAborted);
            StatusMessage = $"Application {applicationId} was completed.";
            return RedirectToPage();
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
        if (!await IsItAsync())
            return Forbid();

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
  AND item.Status = N'Pending'
  AND application.Status IN (N'AwaitingIT', N'PartiallyApproved');

UPDATE dbo.LicenseApplications
SET Status =
(
    SELECT CASE
        WHEN SUM(CASE WHEN Status = N'Pending' THEN 1 ELSE 0 END) > 0
            THEN N'AwaitingIT'
        WHEN SUM(CASE WHEN Status = N'Approved' THEN 1 ELSE 0 END) > 0
         AND SUM(CASE WHEN Status = N'Rejected' THEN 1 ELSE 0 END) > 0
            THEN N'PartiallyApproved'
        WHEN SUM(CASE WHEN Status = N'Approved' THEN 1 ELSE 0 END) > 0
            THEN N'Approved'
        ELSE N'ITRejected'
    END
    FROM dbo.LicenseApplicationItems
    WHERE LicenseApplicationId = @ApplicationId
)
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

            StatusMessage =
                $"License item {itemId} was {decision.ToLowerInvariant()}.";

            return RedirectToPage();
        }
        catch
        {
            await transaction.RollbackAsync(HttpContext.RequestAborted);
            throw;
        }
    }

    private async Task<bool> IsItAsync() =>
        (await _accessScope.GetCurrentAsync(
            User,
            HttpContext.RequestAborted)).IsIT;

    private async Task LoadApplicationsAsync(SqlConnection connection)
    {
        Applications.Clear();

        await using var command = connection.CreateCommand();
        command.CommandText = @"
SELECT LicenseApplicationId
FROM dbo.LicenseApplications
WHERE Status IN
(
    N'AwaitingIT',
    N'PartiallyApproved',
    N'Approved',
    N'ITRejected'
)
ORDER BY LicenseApplicationId;";

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
    item.ItReason
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
                Reason = Get(reader, 13)
            });
        }

        return result;
    }

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
    }
}

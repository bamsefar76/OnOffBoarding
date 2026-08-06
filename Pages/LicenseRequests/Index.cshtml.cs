using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Data.SqlClient;
using UserChangeQueueWeb.Services;

namespace UserChangeQueueWeb.Pages.LicenseRequests;

[Authorize]
public sealed class IndexModel : PageModel
{
    private readonly SqlConnectionFactory _connections;
    private readonly AccessScopeService _accessScope;
    private readonly LicenseEmailService _emails;

    public IndexModel(
        SqlConnectionFactory connections,
        AccessScopeService accessScope,
        LicenseEmailService emails)
    {
        _connections = connections;
        _accessScope = accessScope;
        _emails = emails;
    }

    [BindProperty(SupportsGet = true)]
    public long? Review { get; set; }

    [BindProperty(SupportsGet = true)]
    public bool It { get; set; }

    [BindProperty]
    public List<int> SelectedLicenseIds { get; set; } = new();

    [BindProperty, Required, StringLength(2000)]
    public string BusinessReason { get; set; } = "";

    [BindProperty, Required, StringLength(2000)]
    public string DecisionReason { get; set; } = "";

    [TempData]
    public string? StatusMessage { get; set; }

    public string? ErrorMessage { get; private set; }
    public UserInfo? CurrentUser { get; private set; }
    public ApplicationDetails? ReviewApplication { get; private set; }
    public List<LicenseOption> Licenses { get; } = new();
    public List<ApplicationDetails> MyApplications { get; } = new();
    public List<ApplicationDetails> ItApplications { get; } = new();

    public async Task<IActionResult> OnGetAsync()
    {
        await using var connection =
            await _connections.OpenAsync(HttpContext.RequestAborted);

        if (Review.HasValue)
        {
            ReviewApplication = await LoadApplicationAsync(
                connection, null, Review.Value);

            var currentSam = AccessScopeService.ExtractSamAccountName(
                User.Identity?.Name);

            if (ReviewApplication is null
                || !string.Equals(
                    ReviewApplication.ManagerSam,
                    currentSam,
                    StringComparison.OrdinalIgnoreCase))
            {
                return Forbid();
            }

            return Page();
        }

        if (It)
        {
            if (!await IsItAsync())
            {
                return Forbid();
            }

            await LoadItApplicationsAsync(connection);
            return Page();
        }

        CurrentUser = await LoadCurrentUserAsync(connection);
        await LoadLicensesAsync(connection);
        await LoadMyApplicationsAsync(connection);
        return Page();
    }

    public async Task<IActionResult> OnPostSubmitAsync()
    {
        BusinessReason = BusinessReason?.Trim() ?? "";
        SelectedLicenseIds = SelectedLicenseIds
            .Where(x => x > 0)
            .Distinct()
            .ToList();

        await using var connection =
            await _connections.OpenAsync(HttpContext.RequestAborted);

        CurrentUser = await LoadCurrentUserAsync(connection);
        var selected = await LoadSelectedLicensesAsync(
            connection, SelectedLicenseIds);

        if (CurrentUser is null)
            ModelState.AddModelError("", "Your user record could not be found.");
        else if (string.IsNullOrWhiteSpace(CurrentUser.ManagerSam)
                 || string.IsNullOrWhiteSpace(CurrentUser.ManagerEmail))
            ModelState.AddModelError("", "Your manager or manager email address is missing.");

        if (selected.Count == 0)
            ModelState.AddModelError(nameof(SelectedLicenseIds), "Select at least one license.");

        if (selected.Count != SelectedLicenseIds.Count)
            ModelState.AddModelError(nameof(SelectedLicenseIds), "One or more selected licenses are unavailable.");

        var duplicateFamily = selected
            .Where(x => !string.IsNullOrWhiteSpace(x.ProductFamily))
            .GroupBy(x => x.ProductFamily, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(x => x.Count() > 1);

        if (duplicateFamily is not null)
            ModelState.AddModelError(
                nameof(SelectedLicenseIds),
                $"Select only one license from product family '{duplicateFamily.Key}'.");

        if (string.IsNullOrWhiteSpace(BusinessReason))
            ModelState.AddModelError(nameof(BusinessReason), "Business reason is required.");

        if (!ModelState.IsValid || CurrentUser is null)
        {
            await LoadLicensesAsync(connection);
            await LoadMyApplicationsAsync(connection);
            return Page();
        }

        await using var transaction =
            (SqlTransaction)await connection.BeginTransactionAsync(
                HttpContext.RequestAborted);

        try
        {
            long applicationId;

            await using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
INSERT INTO dbo.LicenseApplications
(
    RequestedForSamAccountName,
    RequestedForDisplayName,
    RequestedForEmail,
    ManagerSamAccountName,
    ManagerDisplayName,
    ManagerEmail,
    BusinessReason,
    Status,
    SubmittedAt
)
OUTPUT inserted.LicenseApplicationId
VALUES
(
    @UserSam,
    @UserName,
    @UserEmail,
    @ManagerSam,
    @ManagerName,
    @ManagerEmail,
    @Reason,
    N'AwaitingManager',
    SYSDATETIME()
);";
                command.Parameters.AddRequiredNVarChar("@UserSam", CurrentUser.Sam, 256);
                command.Parameters.AddNVarChar("@UserName", CurrentUser.DisplayName, 300);
                command.Parameters.AddNVarChar("@UserEmail", CurrentUser.Email, 320);
                command.Parameters.AddRequiredNVarChar("@ManagerSam", CurrentUser.ManagerSam, 256);
                command.Parameters.AddNVarChar("@ManagerName", CurrentUser.ManagerName, 300);
                command.Parameters.AddRequiredNVarChar("@ManagerEmail", CurrentUser.ManagerEmail, 320);
                command.Parameters.AddRequiredNVarChar("@Reason", BusinessReason, 2000);
                applicationId = Convert.ToInt64(
                    await command.ExecuteScalarAsync(HttpContext.RequestAborted));
            }

            foreach (var license in selected)
            {
                await using var itemCommand = connection.CreateCommand();
                itemCommand.Transaction = transaction;
                itemCommand.CommandText = @"
INSERT INTO dbo.LicenseApplicationItems
(
    LicenseApplicationId,
    LicenseProductId,
    Status
)
VALUES
(
    @ApplicationId,
    @ProductId,
    N'Pending'
);";
                itemCommand.Parameters.AddBigInt("@ApplicationId", applicationId);
                itemCommand.Parameters.AddInt("@ProductId", license.Id);
                await itemCommand.ExecuteNonQueryAsync(HttpContext.RequestAborted);
            }

            var reviewUrl = Url.Page(
                "/LicenseRequests/Index",
                pageHandler: null,
                values: new { review = applicationId },
                protocol: Request.Scheme)
                ?? throw new InvalidOperationException("Could not create review URL.");

            var licenseHtml = string.Join(
                "<br />",
                selected.Select(x =>
                    "&#8226; " + System.Net.WebUtility.HtmlEncode(x.Name)));

            var subject =
                $"License request from {CurrentUser.DisplayName}";

            var body =
                "<p>Hello " + System.Net.WebUtility.HtmlEncode(CurrentUser.ManagerName) + ",</p>" +
                "<p>" + System.Net.WebUtility.HtmlEncode(CurrentUser.DisplayName) +
                " requested the following licenses:</p><p>" +
                licenseHtml + "</p><p><strong>Business reason</strong><br />" +
                System.Net.WebUtility.HtmlEncode(BusinessReason) +
                "</p><p><a href=\"" +
                System.Net.WebUtility.HtmlEncode(reviewUrl) +
                "\">Review and decide</a></p><p>Application #" +
                applicationId + "</p>";

            await _emails.QueueAsync(
                connection,
                transaction,
                "LicenseRequestManagerReview",
                CurrentUser.ManagerEmail,
                CurrentUser.ManagerName,
                subject,
                body,
                HttpContext.RequestAborted);

            await transaction.CommitAsync(HttpContext.RequestAborted);

            StatusMessage =
                $"Application {applicationId} was sent to your manager.";

            return RedirectToPage();
        }
        catch (Exception ex)
        {
            await transaction.RollbackAsync(HttpContext.RequestAborted);
            ErrorMessage = ex.Message;
            await LoadLicensesAsync(connection);
            await LoadMyApplicationsAsync(connection);
            return Page();
        }
    }

    public Task<IActionResult> OnPostManagerApproveAsync(long id) =>
        ManagerDecisionAsync(id, "Approved", "AwaitingIT");

    public Task<IActionResult> OnPostManagerRejectAsync(long id) =>
        ManagerDecisionAsync(id, "Rejected", "ManagerRejected");

    public async Task<IActionResult> OnPostItApproveAsync(long applicationId, long itemId)
    {
        return await ItDecisionAsync(applicationId, itemId, "Approved", null);
    }

    public async Task<IActionResult> OnPostItRejectAsync(long applicationId, long itemId)
    {
        DecisionReason = DecisionReason?.Trim() ?? "";

        if (string.IsNullOrWhiteSpace(DecisionReason))
        {
            StatusMessage = "A rejection reason is required.";
            return RedirectToPage(new { it = true });
        }

        return await ItDecisionAsync(
            applicationId, itemId, "Rejected", DecisionReason);
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
                connection, transaction, applicationId);

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
            return RedirectToPage(new { it = true });
        }
        catch
        {
            await transaction.RollbackAsync(HttpContext.RequestAborted);
            throw;
        }
    }

    private async Task<IActionResult> ManagerDecisionAsync(
        long id,
        string decision,
        string newStatus)
    {
        DecisionReason = DecisionReason?.Trim() ?? "";

        if (string.IsNullOrWhiteSpace(DecisionReason))
        {
            ModelState.AddModelError(
                nameof(DecisionReason),
                "A reason is required for both approval and rejection.");

            Review = id;
            return await OnGetAsync();
        }

        var currentSam = AccessScopeService.ExtractSamAccountName(
            User.Identity?.Name);

        await using var connection =
            await _connections.OpenAsync(HttpContext.RequestAborted);

        await using var transaction =
            (SqlTransaction)await connection.BeginTransactionAsync(
                HttpContext.RequestAborted);

        try
        {
            var application = await LoadApplicationAsync(
                connection, transaction, id);

            if (application is null
                || !string.Equals(
                    application.ManagerSam,
                    currentSam,
                    StringComparison.OrdinalIgnoreCase))
            {
                return Forbid();
            }

            await using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = @"
UPDATE dbo.LicenseApplications
SET
    Status = @Status,
    ManagerDecision = @Decision,
    ManagerReason = @Reason,
    ManagerDecisionAt = SYSDATETIME(),
    ManagerDecisionBy = @ChangedBy
WHERE LicenseApplicationId = @Id
  AND Status = N'AwaitingManager';";
            update.Parameters.AddRequiredNVarChar("@Status", newStatus, 40);
            update.Parameters.AddRequiredNVarChar("@Decision", decision, 20);
            update.Parameters.AddRequiredNVarChar("@Reason", DecisionReason, 2000);
            update.Parameters.AddRequiredNVarChar(
                "@ChangedBy",
                User.Identity?.Name ?? currentSam,
                300);
            update.Parameters.AddBigInt("@Id", id);

            if (await update.ExecuteNonQueryAsync(HttpContext.RequestAborted) != 1)
                throw new InvalidOperationException(
                    "This application has already been decided.");

            var body =
                "<p>Hello " +
                System.Net.WebUtility.HtmlEncode(application.UserName) +
                ",</p><p>Your manager decision is <strong>" +
                decision + "</strong>.</p><p>" +
                application.LicenseHtml +
                "</p><p><strong>Reason</strong><br />" +
                System.Net.WebUtility.HtmlEncode(DecisionReason) +
                "</p>";

            await _emails.QueueAsync(
                connection,
                transaction,
                decision == "Approved"
                    ? "LicenseRequestManagerApproved"
                    : "LicenseRequestManagerRejected",
                application.UserEmail,
                application.UserName,
                $"License application {id}: {decision}",
                body,
                HttpContext.RequestAborted);

            await transaction.CommitAsync(HttpContext.RequestAborted);
            StatusMessage = decision == "Approved"
                ? "Approved and sent to IT."
                : "Application rejected.";

            return RedirectToPage(new { review = id });
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

            return RedirectToPage(new { it = true });
        }
        catch
        {
            await transaction.RollbackAsync(HttpContext.RequestAborted);
            throw;
        }
    }

    private async Task<bool> IsItAsync() =>
        (await _accessScope.GetCurrentAsync(
            User, HttpContext.RequestAborted)).IsIT;

    private async Task<UserInfo?> LoadCurrentUserAsync(SqlConnection connection)
    {
        var sam = AccessScopeService.ExtractSamAccountName(User.Identity?.Name);

        await using var command = connection.CreateCommand();
        command.CommandText = @"
SELECT TOP (1)
    ad.SamAccountName,
    COALESCE(NULLIF(ad.DisplayName, N''), ad.SamAccountName),
    ISNULL(ad.Mail, N''),
    ISNULL(ad.ManagerSamAccountName, N''),
    COALESCE(NULLIF(manager.DisplayName, N''), ad.ManagerSamAccountName, N''),
    ISNULL(manager.Mail, N'')
FROM dbo.ADObjects AS ad
LEFT JOIN dbo.ADObjects AS manager
    ON manager.SamAccountName = ad.ManagerSamAccountName
   AND ISNULL(manager.IsDeleted, 0) = 0
WHERE ad.SamAccountName = @Sam
  AND ISNULL(ad.IsDeleted, 0) = 0;";
        command.Parameters.AddRequiredNVarChar("@Sam", sam, 256);

        await using var reader =
            await command.ExecuteReaderAsync(HttpContext.RequestAborted);

        if (!await reader.ReadAsync(HttpContext.RequestAborted))
            return null;

        return new UserInfo
        {
            Sam = Get(reader, 0),
            DisplayName = Get(reader, 1),
            Email = Get(reader, 2),
            ManagerSam = Get(reader, 3),
            ManagerName = Get(reader, 4),
            ManagerEmail = Get(reader, 5)
        };
    }

    private async Task LoadLicensesAsync(SqlConnection connection)
    {
        Licenses.Clear();

        await using var command = connection.CreateCommand();
        command.CommandText = @"
SELECT
    LicenseProductId,
    Name,
    Description,
    ProductFamily,
    LicenseLevel
FROM dbo.LicenseProducts
WHERE Active = 1
ORDER BY SortOrder, COALESCE(ProductFamily, Name), LicenseLevel, Name;";

        await using var reader =
            await command.ExecuteReaderAsync(HttpContext.RequestAborted);

        while (await reader.ReadAsync(HttpContext.RequestAborted))
            Licenses.Add(ReadLicense(reader));
    }

    private async Task<List<LicenseOption>> LoadSelectedLicensesAsync(
        SqlConnection connection,
        IReadOnlyList<int> ids)
    {
        if (ids.Count == 0)
            return new();

        await using var command = connection.CreateCommand();
        var parameterNames = new List<string>();

        for (var i = 0; i < ids.Count; i++)
        {
            var name = "@Id" + i;
            parameterNames.Add(name);
            command.Parameters.AddInt(name, ids[i]);
        }

        command.CommandText = $@"
SELECT
    LicenseProductId,
    Name,
    Description,
    ProductFamily,
    LicenseLevel
FROM dbo.LicenseProducts
WHERE Active = 1
  AND LicenseProductId IN ({string.Join(",", parameterNames)});";

        var result = new List<LicenseOption>();
        await using var reader =
            await command.ExecuteReaderAsync(HttpContext.RequestAborted);

        while (await reader.ReadAsync(HttpContext.RequestAborted))
            result.Add(ReadLicense(reader));

        return result;
    }

    private async Task LoadMyApplicationsAsync(SqlConnection connection)
    {
        MyApplications.Clear();
        var sam = AccessScopeService.ExtractSamAccountName(User.Identity?.Name);

        await using var command = connection.CreateCommand();
        command.CommandText = @"
SELECT
    application.LicenseApplicationId
FROM dbo.LicenseApplications AS application
WHERE application.RequestedForSamAccountName = @Sam
ORDER BY application.LicenseApplicationId DESC;";
        command.Parameters.AddRequiredNVarChar("@Sam", sam, 256);

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
                connection, null, id);

            if (application is not null)
                MyApplications.Add(application);
        }
    }

    private async Task LoadItApplicationsAsync(SqlConnection connection)
    {
        ItApplications.Clear();

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
                connection, null, id);

            if (application is not null)
                ItApplications.Add(application);
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

    private static LicenseOption ReadLicense(SqlDataReader reader) => new()
    {
        Id = reader.GetInt32(0),
        Name = Get(reader, 1),
        Description = Get(reader, 2),
        ProductFamily = Get(reader, 3),
        LicenseLevel = reader.IsDBNull(4) ? null : reader.GetInt32(4)
    };

    private static string Get(SqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal)
            ? ""
            : Convert.ToString(reader.GetValue(ordinal)) ?? "";

    public sealed class UserInfo
    {
        public string Sam { get; init; } = "";
        public string DisplayName { get; init; } = "";
        public string Email { get; init; } = "";
        public string ManagerSam { get; init; } = "";
        public string ManagerName { get; init; } = "";
        public string ManagerEmail { get; init; } = "";
    }

    public sealed class LicenseOption
    {
        public int Id { get; init; }
        public string Name { get; init; } = "";
        public string Description { get; init; } = "";
        public string ProductFamily { get; init; } = "";
        public int? LicenseLevel { get; init; }
    }

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

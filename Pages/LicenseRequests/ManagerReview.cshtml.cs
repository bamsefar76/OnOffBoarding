using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Data.SqlClient;
using UserChangeQueueWeb.Services;

namespace UserChangeQueueWeb.Pages.LicenseRequests;

[Authorize]
public sealed class ManagerReviewModel : PageModel
{
    private readonly SqlConnectionFactory _connections;
    private readonly LicenseEmailService _emails;

    public ManagerReviewModel(SqlConnectionFactory connections, LicenseEmailService emails)
    {
        _connections = connections;
        _emails = emails;
    }

    [BindProperty(SupportsGet = true)]
    public long Id { get; set; }

    [BindProperty]
    public string DecisionReason { get; set; } = "";

    [TempData]
    public string? StatusMessageKey { get; set; }

    public string? ValidationMessageKey { get; private set; }
    public ApplicationDetails? Application { get; private set; }

    public async Task<IActionResult> OnGetAsync() =>
        await LoadAuthorizedAsync() ? Page() : Forbid();

    public Task<IActionResult> OnPostApproveAsync() =>
        ManagerDecisionAsync("Approved", "AwaitingIT");

    public Task<IActionResult> OnPostRejectAsync() =>
        ManagerDecisionAsync("Rejected", "ManagerRejected");

    private async Task<IActionResult> ManagerDecisionAsync(string decision, string newStatus)
    {
        DecisionReason = DecisionReason?.Trim() ?? "";

        if (string.IsNullOrWhiteSpace(DecisionReason))
        {
            ValidationMessageKey = "managerLicenseReview.validation.reasonRequired";
            return await LoadAuthorizedAsync() ? Page() : Forbid();
        }

        if (DecisionReason.Length > 2000)
        {
            ValidationMessageKey = "managerLicenseReview.validation.reasonTooLong";
            return await LoadAuthorizedAsync() ? Page() : Forbid();
        }

        var currentSam = AccessScopeService.ExtractSamAccountName(User.Identity?.Name);
        await using var connection = await _connections.OpenAsync(HttpContext.RequestAborted);
        await using var transaction =
            (SqlTransaction)await connection.BeginTransactionAsync(HttpContext.RequestAborted);

        try
        {
            Application = await LoadApplicationAsync(connection, transaction, Id);
            if (Application is null ||
                !string.Equals(Application.ManagerSam, currentSam, StringComparison.OrdinalIgnoreCase))
            {
                return Forbid();
            }

            await using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = @"
UPDATE dbo.LicenseApplications
SET Status=@Status,
    ManagerDecision=@Decision,
    ManagerReason=@Reason,
    ManagerDecisionAt=SYSDATETIME(),
    ManagerDecisionBy=@ChangedBy
WHERE LicenseApplicationId=@Id
  AND Status=N'AwaitingManager';";
            update.Parameters.AddRequiredNVarChar("@Status", newStatus, 40);
            update.Parameters.AddRequiredNVarChar("@Decision", decision, 20);
            update.Parameters.AddRequiredNVarChar("@Reason", DecisionReason, 2000);
            update.Parameters.AddRequiredNVarChar(
                "@ChangedBy",
                User.Identity?.Name ?? currentSam,
                300);
            update.Parameters.AddBigInt("@Id", Id);

            if (await update.ExecuteNonQueryAsync(HttpContext.RequestAborted) != 1)
                throw new InvalidOperationException("This application has already been decided.");

            var licenseText = string.Join(
                Environment.NewLine,
                Application.Items.Select(x => "- " + x.Name));

            await _emails.QueueTemplateAsync(
                connection,
                transaction,
                decision == "Approved"
                    ? "LicenseRequestManagerApproved"
                    : "LicenseRequestManagerRejected",
                Application.UserEmail,
                Application.UserName,
                new Dictionary<string, string?>
                {
                    ["ApplicationId"] = Id.ToString(),
                    ["RequesterName"] = Application.UserName,
                    ["ManagerName"] = Application.ManagerName,
                    ["ManagerDecision"] = decision,
                    ["ManagerReason"] = DecisionReason,
                    ["LicenseList"] = licenseText
                },
                new Dictionary<string, string?>
                {
                    ["LicenseList"] = Application.LicenseHtml
                },
                HttpContext.RequestAborted);

            await transaction.CommitAsync(HttpContext.RequestAborted);
            StatusMessageKey = decision == "Approved"
                ? "managerLicenseReview.message.approved"
                : "managerLicenseReview.message.rejected";
            return RedirectToPage(new { id = Id });
        }
        catch
        {
            await transaction.RollbackAsync(HttpContext.RequestAborted);
            throw;
        }
    }

    private async Task<bool> LoadAuthorizedAsync()
    {
        var currentSam = AccessScopeService.ExtractSamAccountName(User.Identity?.Name);
        await using var connection = await _connections.OpenAsync(HttpContext.RequestAborted);
        Application = await LoadApplicationAsync(connection, null, Id);

        return Application is not null &&
            string.Equals(Application.ManagerSam, currentSam, StringComparison.OrdinalIgnoreCase);
    }

    private async Task<ApplicationDetails?> LoadApplicationAsync(
        SqlConnection connection,
        SqlTransaction? transaction,
        long id)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = @"
SELECT application.LicenseApplicationId,
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
JOIN dbo.LicenseApplicationItems AS item
  ON item.LicenseApplicationId=application.LicenseApplicationId
JOIN dbo.LicenseProducts AS product
  ON product.LicenseProductId=item.LicenseProductId
WHERE application.LicenseApplicationId=@Id
ORDER BY product.Name;";
        command.Parameters.AddBigInt("@Id", id);

        ApplicationDetails? result = null;
        await using var reader = await command.ExecuteReaderAsync(HttpContext.RequestAborted);

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
        reader.IsDBNull(ordinal) ? "" : Convert.ToString(reader.GetValue(ordinal)) ?? "";

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
            Items.Select(x => "&#8226; " + System.Net.WebUtility.HtmlEncode(x.Name)));
    }

    public sealed class ItemDetails
    {
        public long Id { get; init; }
        public string Name { get; init; } = "";
        public string Status { get; init; } = "";
        public string Reason { get; init; } = "";
    }
}

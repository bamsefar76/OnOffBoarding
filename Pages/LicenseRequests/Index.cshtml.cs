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
    private readonly LicenseEmailService _emails;

    public IndexModel(
        SqlConnectionFactory connections,
        LicenseEmailService emails)
    {
        _connections = connections;
        _emails = emails;
    }

    [BindProperty]
    public List<int> SelectedLicenseIds { get; set; } = new();

    [BindProperty, Required, StringLength(2000)]
    public string BusinessReason { get; set; } = "";

    [TempData]
    public string? StatusMessage { get; set; }

    public string? ErrorMessage { get; private set; }
    public UserInfo? CurrentUser { get; private set; }
    public List<LicenseOption> Licenses { get; } = new();
    public List<ApplicationDetails> MyApplications { get; } = new();

    public async Task OnGetAsync()
    {
        await using var connection =
            await _connections.OpenAsync(HttpContext.RequestAborted);

        CurrentUser = await LoadCurrentUserAsync(connection);
        await LoadLicensesAsync(connection);
        await LoadMyApplicationsAsync(connection);
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
            connection,
            SelectedLicenseIds);

        if (CurrentUser is null)
        {
            ModelState.AddModelError(
                "",
                "Your user record could not be found.");
        }
        else if (string.IsNullOrWhiteSpace(CurrentUser.ManagerSam)
                 || string.IsNullOrWhiteSpace(CurrentUser.ManagerEmail))
        {
            ModelState.AddModelError(
                "",
                "Your manager or manager email address is missing.");
        }

        if (selected.Count == 0)
        {
            ModelState.AddModelError(
                nameof(SelectedLicenseIds),
                "Select at least one license.");
        }

        if (selected.Count != SelectedLicenseIds.Count)
        {
            ModelState.AddModelError(
                nameof(SelectedLicenseIds),
                "One or more selected licenses are unavailable.");
        }

        var duplicateFamily = selected
            .Where(x => !string.IsNullOrWhiteSpace(x.ProductFamily))
            .GroupBy(
                x => x.ProductFamily,
                StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(x => x.Count() > 1);

        if (duplicateFamily is not null)
        {
            ModelState.AddModelError(
                nameof(SelectedLicenseIds),
                $"Select only one license from product family '{duplicateFamily.Key}'.");
        }

        if (string.IsNullOrWhiteSpace(BusinessReason))
        {
            ModelState.AddModelError(
                nameof(BusinessReason),
                "Business reason is required.");
        }

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

                command.Parameters.AddRequiredNVarChar(
                    "@UserSam",
                    CurrentUser.Sam,
                    256);
                command.Parameters.AddNVarChar(
                    "@UserName",
                    CurrentUser.DisplayName,
                    300);
                command.Parameters.AddNVarChar(
                    "@UserEmail",
                    CurrentUser.Email,
                    320);
                command.Parameters.AddRequiredNVarChar(
                    "@ManagerSam",
                    CurrentUser.ManagerSam,
                    256);
                command.Parameters.AddNVarChar(
                    "@ManagerName",
                    CurrentUser.ManagerName,
                    300);
                command.Parameters.AddRequiredNVarChar(
                    "@ManagerEmail",
                    CurrentUser.ManagerEmail,
                    320);
                command.Parameters.AddRequiredNVarChar(
                    "@Reason",
                    BusinessReason,
                    2000);

                applicationId = Convert.ToInt64(
                    await command.ExecuteScalarAsync(
                        HttpContext.RequestAborted));
            }

            foreach (var license in selected)
            {
                await using var itemCommand =
                    connection.CreateCommand();

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

                itemCommand.Parameters.AddBigInt(
                    "@ApplicationId",
                    applicationId);
                itemCommand.Parameters.AddInt(
                    "@ProductId",
                    license.Id);

                await itemCommand.ExecuteNonQueryAsync(
                    HttpContext.RequestAborted);
            }

            var reviewUrl = await BuildPublicReviewUrlAsync(
                connection,
                transaction,
                applicationId);

            var licenseHtml = string.Join(
                "<br />",
                selected.Select(
                    x => "&#8226; "
                         + System.Net.WebUtility.HtmlEncode(x.Name)));

            var subject =
                $"License request from {CurrentUser.DisplayName}";

            var body =
                "<p>Hello "
                + System.Net.WebUtility.HtmlEncode(
                    CurrentUser.ManagerName)
                + ",</p>"
                + "<p>"
                + System.Net.WebUtility.HtmlEncode(
                    CurrentUser.DisplayName)
                + " requested the following licenses:</p>"
                + "<p>"
                + licenseHtml
                + "</p>"
                + "<p><strong>Business reason</strong><br />"
                + System.Net.WebUtility.HtmlEncode(BusinessReason)
                + "</p>"
                + "<p><a href=\""
                + System.Net.WebUtility.HtmlEncode(reviewUrl)
                + "\">Review and decide</a></p>"
                + "<p>Application #"
                + applicationId
                + "</p>";

            await _emails.QueueAsync(
                connection,
                transaction,
                "LicenseRequestManagerReview",
                CurrentUser.ManagerEmail,
                CurrentUser.ManagerName,
                subject,
                body,
                HttpContext.RequestAborted);

            await transaction.CommitAsync(
                HttpContext.RequestAborted);

            StatusMessage =
                $"Application {applicationId} was sent to your manager.";

            return RedirectToPage(
                "/LicenseRequests/Index",
                pageHandler: null,
                routeValues: null,
                fragment: "my-license-applications");
        }
        catch (Exception ex)
        {
            await transaction.RollbackAsync(
                HttpContext.RequestAborted);

            ErrorMessage = ex.Message;

            await LoadLicensesAsync(connection);
            await LoadMyApplicationsAsync(connection);

            return Page();
        }
    }

    private async Task<string> BuildPublicReviewUrlAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        long applicationId)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = @"
SELECT TOP (1) SettingValue
FROM dbo.ApplicationSettings
WHERE SettingKey = N'PublicBaseUrl'
  AND Active = 1;";

        var value = await command.ExecuteScalarAsync(
            HttpContext.RequestAborted);

        var publicBaseUrl =
            value is null || value is DBNull
                ? null
                : Convert.ToString(value)?.Trim();

        if (string.IsNullOrWhiteSpace(publicBaseUrl))
        {
            throw new InvalidOperationException(
                "Application setting 'PublicBaseUrl' is missing or inactive.");
        }

        if (!Uri.TryCreate(
                publicBaseUrl.TrimEnd('/') + "/",
                UriKind.Absolute,
                out var baseUri))
        {
            throw new InvalidOperationException(
                "Application setting 'PublicBaseUrl' is not a valid absolute URL.");
        }

        return new Uri(
            baseUri,
            $"LicenseRequests/ManagerReview?id={applicationId}")
            .ToString();
    }

    private async Task<UserInfo?> LoadCurrentUserAsync(
        SqlConnection connection)
    {
        var sam =
            AccessScopeService.ExtractSamAccountName(
                User.Identity?.Name);

        await using var command =
            connection.CreateCommand();

        command.CommandText = @"
SELECT TOP (1)
    ad.SamAccountName,
    COALESCE(NULLIF(ad.DisplayName, N''), ad.SamAccountName),
    ISNULL(ad.Mail, N''),
    ISNULL(ad.ManagerSamAccountName, N''),
    COALESCE(
        NULLIF(manager.DisplayName, N''),
        ad.ManagerSamAccountName,
        N''
    ),
    ISNULL(manager.Mail, N'')
FROM dbo.ADObjects AS ad
LEFT JOIN dbo.ADObjects AS manager
    ON manager.SamAccountName = ad.ManagerSamAccountName
   AND ISNULL(manager.IsDeleted, 0) = 0
WHERE ad.SamAccountName = @Sam
  AND ISNULL(ad.IsDeleted, 0) = 0;";

        command.Parameters.AddRequiredNVarChar(
            "@Sam",
            sam,
            256);

        await using var reader =
            await command.ExecuteReaderAsync(
                HttpContext.RequestAborted);

        if (!await reader.ReadAsync(
                HttpContext.RequestAborted))
        {
            return null;
        }

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

    private async Task LoadLicensesAsync(
        SqlConnection connection)
    {
        Licenses.Clear();

        await using var command =
            connection.CreateCommand();

        command.CommandText = @"
SELECT
    LicenseProductId,
    Name,
    Description,
    ProductFamily,
    LicenseLevel
FROM dbo.LicenseProducts
WHERE Active = 1
ORDER BY
    SortOrder,
    COALESCE(ProductFamily, Name),
    LicenseLevel,
    Name;";

        await using var reader =
            await command.ExecuteReaderAsync(
                HttpContext.RequestAborted);

        while (await reader.ReadAsync(
                   HttpContext.RequestAborted))
        {
            Licenses.Add(ReadLicense(reader));
        }
    }

    private async Task<List<LicenseOption>>
        LoadSelectedLicensesAsync(
            SqlConnection connection,
            IReadOnlyList<int> ids)
    {
        if (ids.Count == 0)
        {
            return new List<LicenseOption>();
        }

        await using var command =
            connection.CreateCommand();

        var parameterNames =
            new List<string>();

        for (var i = 0; i < ids.Count; i++)
        {
            var name = "@Id" + i;
            parameterNames.Add(name);
            command.Parameters.AddInt(
                name,
                ids[i]);
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
  AND LicenseProductId IN
      ({string.Join(",", parameterNames)});";

        var result =
            new List<LicenseOption>();

        await using var reader =
            await command.ExecuteReaderAsync(
                HttpContext.RequestAborted);

        while (await reader.ReadAsync(
                   HttpContext.RequestAborted))
        {
            result.Add(ReadLicense(reader));
        }

        return result;
    }

    private async Task LoadMyApplicationsAsync(
        SqlConnection connection)
    {
        MyApplications.Clear();

        var sam =
            AccessScopeService.ExtractSamAccountName(
                User.Identity?.Name);

        await using var command =
            connection.CreateCommand();

        command.CommandText = @"
SELECT
    application.LicenseApplicationId
FROM dbo.LicenseApplications AS application
WHERE application.RequestedForSamAccountName = @Sam
ORDER BY application.LicenseApplicationId DESC;";

        command.Parameters.AddRequiredNVarChar(
            "@Sam",
            sam,
            256);

        var ids =
            new List<long>();

        await using (var reader =
            await command.ExecuteReaderAsync(
                HttpContext.RequestAborted))
        {
            while (await reader.ReadAsync(
                       HttpContext.RequestAborted))
            {
                ids.Add(reader.GetInt64(0));
            }
        }

        foreach (var id in ids)
        {
            var application =
                await LoadApplicationAsync(
                    connection,
                    id);

            if (application is not null)
            {
                MyApplications.Add(application);
            }
        }
    }

    private async Task<ApplicationDetails?>
        LoadApplicationAsync(
            SqlConnection connection,
            long id)
    {
        await using var command =
            connection.CreateCommand();

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
    ON item.LicenseApplicationId =
       application.LicenseApplicationId
INNER JOIN dbo.LicenseProducts AS product
    ON product.LicenseProductId =
       item.LicenseProductId
WHERE application.LicenseApplicationId = @Id
ORDER BY product.Name;";

        command.Parameters.AddBigInt(
            "@Id",
            id);

        ApplicationDetails? result = null;

        await using var reader =
            await command.ExecuteReaderAsync(
                HttpContext.RequestAborted);

        while (await reader.ReadAsync(
                   HttpContext.RequestAborted))
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

            result.Items.Add(
                new ItemDetails
                {
                    Id = reader.GetInt64(10),
                    Name = Get(reader, 11),
                    Status = Get(reader, 12),
                    Reason = Get(reader, 13)
                });
        }

        return result;
    }

    private static LicenseOption ReadLicense(
        SqlDataReader reader) =>
        new()
        {
            Id = reader.GetInt32(0),
            Name = Get(reader, 1),
            Description = Get(reader, 2),
            ProductFamily = Get(reader, 3),
            LicenseLevel =
                reader.IsDBNull(4)
                    ? null
                    : reader.GetInt32(4)
        };

    private static string Get(
        SqlDataReader reader,
        int ordinal) =>
        reader.IsDBNull(ordinal)
            ? ""
            : Convert.ToString(
                  reader.GetValue(ordinal))
              ?? "";

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
    }

    public sealed class ItemDetails
    {
        public long Id { get; init; }
        public string Name { get; init; } = "";
        public string Status { get; init; } = "";
        public string Reason { get; init; } = "";
    }
}

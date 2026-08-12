using System.Globalization;
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
    private readonly LicenseCapacityService _capacity;

    public IndexModel(
        SqlConnectionFactory connections,
        LicenseEmailService emails,
        LicenseCapacityService capacity)
    {
        _connections = connections;
        _emails = emails;
        _capacity = capacity;
    }

    [BindProperty]
    public List<int> SelectedLicenseIds { get; set; } = new();

    [BindProperty]
    public string BusinessReason { get; set; } = "";

    [BindProperty]
    public string StartDateText { get; set; } = DateTime.Today.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture);

    [BindProperty]
    public string? EndDateText { get; set; }

    [BindProperty]
    public string PeriodType { get; set; } = "";

    [TempData]
    public string? StatusMessageKey { get; set; }

    [TempData]
    public string? StatusMessageArgument { get; set; }

    public string? ErrorMessageKey { get; private set; }
    public List<ValidationIssue> ValidationIssues { get; } = new();
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
        StartDateText = StartDateText?.Trim() ?? "";
        EndDateText = string.IsNullOrWhiteSpace(EndDateText) ? null : EndDateText.Trim();
        PeriodType = PeriodType?.Trim() ?? "";

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
            ValidationIssues.Add(new ValidationIssue(
                "licenseRequests.validation.userNotFound"));
        }
        else if (string.IsNullOrWhiteSpace(CurrentUser.ManagerSam)
                 || string.IsNullOrWhiteSpace(CurrentUser.ManagerEmail))
        {
            ValidationIssues.Add(new ValidationIssue(
                "licenseRequests.validation.managerMissing"));
        }

        if (selected.Count == 0)
        {
            ValidationIssues.Add(new ValidationIssue(
                "licenseRequests.validation.selectLicense"));
        }

        if (selected.Count != SelectedLicenseIds.Count)
        {
            ValidationIssues.Add(new ValidationIssue(
                "licenseRequests.validation.unavailableLicense"));
        }

        if (selected.Any(x =>
                x.FulfillmentType == "AdGroup" &&
                string.IsNullOrWhiteSpace(x.AdGroupName)))
        {
            ValidationIssues.Add(new ValidationIssue(
                "licenseRequests.validation.adGroupConfigurationMissing"));
        }

        var familyViolations = await LoadFamilyRuleViolationsAsync(connection, selected);
        foreach (var violation in familyViolations)
        {
            ValidationIssues.Add(new ValidationIssue(
                "licenseRequests.validation.familyLimit",
                violation.ProductFamily,
                violation.SelectedCount,
                violation.MaxSelectable,
                violation.ReplacementName));
        }

        DateTime? startDate = null;
        DateTime? endDate = null;
        var isPermanent = string.Equals(PeriodType, "Permanent", StringComparison.OrdinalIgnoreCase);
        var isFixedPeriod = string.Equals(PeriodType, "Fixed", StringComparison.OrdinalIgnoreCase);

        if (!isPermanent && !isFixedPeriod)
        {
            ValidationIssues.Add(new ValidationIssue(
                "licenseRequests.validation.periodTypeInvalid"));
        }

        if (!TryParseDate(StartDateText, required: true, out startDate))
        {
            ValidationIssues.Add(new ValidationIssue(
                "licenseRequests.validation.startDateInvalid"));
        }

        if (isFixedPeriod)
        {
            if (string.IsNullOrWhiteSpace(EndDateText))
            {
                ValidationIssues.Add(new ValidationIssue(
                    "licenseRequests.validation.endDateRequired"));
            }
            else if (!TryParseDate(EndDateText, required: true, out endDate))
            {
                ValidationIssues.Add(new ValidationIssue(
                    "licenseRequests.validation.endDateInvalid"));
            }
        }
        else
        {
            endDate = null;
            EndDateText = null;
        }

        if (startDate.HasValue && endDate.HasValue && endDate.Value.Date < startDate.Value.Date)
        {
            ValidationIssues.Add(new ValidationIssue(
                "licenseRequests.validation.endBeforeStart"));
        }

        if (CurrentUser is not null && string.IsNullOrWhiteSpace(CurrentUser.ProjectNumber))
        {
            ValidationIssues.Add(new ValidationIssue(
                "licenseRequests.validation.projectNumberMissing"));
        }

        if (string.IsNullOrWhiteSpace(BusinessReason))
        {
            ValidationIssues.Add(new ValidationIssue(
                "licenseRequests.validation.businessReasonRequired"));
        }
        else if (BusinessReason.Length > 2000)
        {
            ValidationIssues.Add(new ValidationIssue(
                "licenseRequests.validation.businessReasonTooLong"));
        }

        if (ValidationIssues.Count > 0 || CurrentUser is null)
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
            var capacityViolations = await _capacity.CheckCapacityAsync(
                connection,
                transaction,
                selected.Select(x => x.Id).ToArray(),
                startDate!.Value,
                endDate,
                HttpContext.RequestAborted);

            if (capacityViolations.Count > 0)
            {
                await transaction.RollbackAsync(HttpContext.RequestAborted);

                foreach (var violation in capacityViolations)
                {
                    ValidationIssues.Add(new ValidationIssue(
                        "licenseRequests.validation.capacityExceeded",
                        violation.LicenseName,
                        violation.ReservedCount,
                        violation.LicenseCount));
                }

                await LoadLicensesAsync(connection);
                await LoadMyApplicationsAsync(connection);
                return Page();
            }

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
    ProjectNumber,
    StartDate,
    EndDate,
    IsPermanent,
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
    @ProjectNumber,
    @StartDate,
    @EndDate,
    @IsPermanent,
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
                command.Parameters.AddRequiredNVarChar(
                    "@ProjectNumber",
                    CurrentUser.ProjectNumber,
                    100);
                command.Parameters.AddNullableDate(
                    "@StartDate",
                    startDate);
                command.Parameters.AddNullableDate(
                    "@EndDate",
                    endDate);
                command.Parameters.AddBit(
                    "@IsPermanent",
                    isPermanent);

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
    Status,
    FulfillmentType,
    AdGroupName,
    StartDate,
    EndDate,
    IsPermanent,
    ProvisioningStatus
)
VALUES
(
    @ApplicationId,
    @ProductId,
    N'Pending',
    @FulfillmentType,
    @AdGroupName,
    @StartDate,
    @EndDate,
    @IsPermanent,
    NULL
);";

                itemCommand.Parameters.AddBigInt(
                    "@ApplicationId",
                    applicationId);
                itemCommand.Parameters.AddInt(
                    "@ProductId",
                    license.Id);
                itemCommand.Parameters.AddRequiredNVarChar(
                    "@FulfillmentType",
                    license.FulfillmentType,
                    20);
                itemCommand.Parameters.AddNVarChar(
                    "@AdGroupName",
                    license.AdGroupName,
                    300);
                itemCommand.Parameters.AddNullableDate(
                    "@StartDate",
                    startDate);
                itemCommand.Parameters.AddNullableDate(
                    "@EndDate",
                    endDate);
                itemCommand.Parameters.AddBit(
                    "@IsPermanent",
                    isPermanent);

                await itemCommand.ExecuteNonQueryAsync(
                    HttpContext.RequestAborted);
            }

            var reviewUrl = await BuildPublicReviewUrlAsync(
                connection,
                transaction,
                applicationId);

            var periodText = startDate!.Value.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture)
                + (endDate.HasValue ? " – " + endDate.Value.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture) : "");
            var licenseText = string.Join(
                Environment.NewLine,
                selected.Select(x => "- " + x.Name + " (" + periodText + ")"));

            var licenseHtml = string.Join(
                "<br />",
                selected.Select(
                    x => "&#8226; "
                         + System.Net.WebUtility.HtmlEncode(x.Name + " (" + periodText + ")")));

            await _emails.QueueTemplateAsync(
                connection,
                transaction,
                "LicenseRequestManagerReview",
                CurrentUser.ManagerEmail,
                CurrentUser.ManagerName,
                new Dictionary<string, string?>
                {
                    ["ApplicationId"] = applicationId.ToString(),
                    ["ManagerName"] = CurrentUser.ManagerName,
                    ["RequesterName"] = CurrentUser.DisplayName,
                    ["RequesterEmail"] = CurrentUser.Email,
                    ["BusinessReason"] = BusinessReason,
                    ["ProjectNumber"] = CurrentUser.ProjectNumber,
                    ["StartDate"] = startDate.Value.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture),
                    ["EndDate"] = endDate?.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture) ?? "",
                    ["LicenseList"] = licenseText,
                    ["ReviewUrl"] = reviewUrl
                },
                new Dictionary<string, string?>
                {
                    ["LicenseList"] = licenseHtml
                },
                HttpContext.RequestAborted);

            await transaction.CommitAsync(
                HttpContext.RequestAborted);

            StatusMessageKey = "licenseRequests.message.submitted";
            StatusMessageArgument = applicationId.ToString();

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

            ErrorMessageKey = ex is InvalidOperationException
                ? ex.Message switch
                {
                    "PublicBaseUrlMissing" => "licenseRequests.error.publicBaseUrlMissing",
                    "PublicBaseUrlInvalid" => "licenseRequests.error.publicBaseUrlInvalid",
                    _ => "licenseRequests.error.submitFailed"
                }
                : "licenseRequests.error.submitFailed";

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
            throw new InvalidOperationException("PublicBaseUrlMissing");
        }

        if (!Uri.TryCreate(
                publicBaseUrl.TrimEnd('/') + "/",
                UriKind.Absolute,
                out var baseUri))
        {
            throw new InvalidOperationException("PublicBaseUrlInvalid");
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
    ISNULL(manager.Mail, N''),
    COALESCE(currentAssignment.ProjectNumber, latestRequest.ProjectNumber, N''),
    ISNULL(userDomain.[domain], N''),
    COALESCE(NULLIF(userDomain.Label,N''), userDomain.[domain], N'')
FROM dbo.ADObjects AS ad
LEFT JOIN dbo.ADObjects AS manager
    ON manager.SamAccountName = ad.ManagerSamAccountName
   AND ISNULL(manager.IsDeleted, 0) = 0
OUTER APPLY
(
    SELECT TOP (1) assignment.ProjectNumber
    FROM dbo.Employees AS employee
    INNER JOIN dbo.Assignments AS assignment
        ON assignment.EmployeeId = employee.EmployeeId
    WHERE employee.CurrentSamAccountName = ad.SamAccountName
      AND assignment.StartDate <= CAST(SYSDATETIME() AS date)
      AND (assignment.EndDate IS NULL OR assignment.EndDate >= CAST(SYSDATETIME() AS date))
      AND NULLIF(LTRIM(RTRIM(assignment.ProjectNumber)), N'') IS NOT NULL
    ORDER BY assignment.StartDate DESC, assignment.AssignmentId DESC
) AS currentAssignment
OUTER APPLY
(
    SELECT TOP (1) NULLIF(LTRIM(RTRIM(queueItem.ProjectNumber)), N'') AS ProjectNumber
    FROM dbo.ADUserChangeQueue AS queueItem
    WHERE NULLIF(LTRIM(RTRIM(queueItem.ProjectNumber)), N'') IS NOT NULL
      AND
      (
          queueItem.TargetSamAccountName = ad.SamAccountName
          OR queueItem.NewSamAccountName = ad.SamAccountName
      )
    ORDER BY queueItem.RequestId DESC
) AS latestRequest
OUTER APPLY
(
    SELECT TOP (1) d.[domain], d.Label
    FROM dbo.domains d
    WHERE RIGHT(LOWER(ISNULL(NULLIF(ad.UserPrincipalName,N''),ad.Mail)), LEN(d.[domain])+1)=N'@'+LOWER(d.[domain])
    ORDER BY LEN(d.[domain]) DESC
) AS userDomain
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
            ManagerEmail = Get(reader, 5),
            ProjectNumber = Get(reader, 6),
            Domain = Get(reader, 7),
            Label = Get(reader, 8)
        };
    }

    private async Task LoadLicensesAsync(
        SqlConnection connection)
    {
        Licenses.Clear();

        await using var command =
            connection.CreateCommand();
        command.Parameters.AddNVarChar("@UserDomain", CurrentUser?.Domain, 320);
        command.Parameters.AddNVarChar("@UserLabel", CurrentUser?.Label, 320);

        command.CommandText = @"
SELECT
    LicenseProductId,
    Name,
    Description,
    ProductFamily,
    LicenseLevel,
    FulfillmentType,
    AdGroupName,
    LicenseCount,
    (
        SELECT COUNT(*)
        FROM dbo.LicenseAssignments AS assignment
        WHERE assignment.LicenseProductId = dbo.LicenseProducts.LicenseProductId
          AND assignment.Status = N'Active'
          AND assignment.StartDate <= CAST(SYSDATETIME() AS date)
          AND (assignment.IsPermanent = 1 OR assignment.EndDate IS NULL OR assignment.EndDate >= CAST(SYSDATETIME() AS date))
    ) AS CurrentInUse
FROM dbo.LicenseProducts
WHERE Active = 1
  AND
  (
      NOT EXISTS (SELECT 1 FROM dbo.LicenseProductScopes scope WHERE scope.LicenseProductId=dbo.LicenseProducts.LicenseProductId)
      OR EXISTS
      (
          SELECT 1 FROM dbo.LicenseProductScopes scope
          WHERE scope.LicenseProductId=dbo.LicenseProducts.LicenseProductId
            AND ((scope.ScopeType=N'Domain' AND scope.ScopeValue=@UserDomain)
                 OR (scope.ScopeType=N'Label' AND scope.ScopeValue=@UserLabel))
      )
  )
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
        command.Parameters.AddNVarChar("@UserDomain", CurrentUser?.Domain, 320);
        command.Parameters.AddNVarChar("@UserLabel", CurrentUser?.Label, 320);

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
    LicenseLevel,
    FulfillmentType,
    AdGroupName,
    LicenseCount,
    (
        SELECT COUNT(*)
        FROM dbo.LicenseAssignments AS assignment
        WHERE assignment.LicenseProductId = dbo.LicenseProducts.LicenseProductId
          AND assignment.Status = N'Active'
          AND assignment.StartDate <= CAST(SYSDATETIME() AS date)
          AND (assignment.IsPermanent = 1 OR assignment.EndDate IS NULL OR assignment.EndDate >= CAST(SYSDATETIME() AS date))
    ) AS CurrentInUse
FROM dbo.LicenseProducts
WHERE Active = 1
  AND
  (
      NOT EXISTS (SELECT 1 FROM dbo.LicenseProductScopes scope WHERE scope.LicenseProductId=dbo.LicenseProducts.LicenseProductId)
      OR EXISTS
      (
          SELECT 1 FROM dbo.LicenseProductScopes scope
          WHERE scope.LicenseProductId=dbo.LicenseProducts.LicenseProductId
            AND ((scope.ScopeType=N'Domain' AND scope.ScopeValue=@UserDomain)
                 OR (scope.ScopeType=N'Label' AND scope.ScopeValue=@UserLabel))
      )
  )
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

    private async Task<List<FamilyViolation>> LoadFamilyRuleViolationsAsync(SqlConnection connection, IReadOnlyCollection<LicenseOption> selected)
    {
        var result = new List<FamilyViolation>();
        var families = selected.Where(x=>!string.IsNullOrWhiteSpace(x.ProductFamily))
            .GroupBy(x=>x.ProductFamily,StringComparer.OrdinalIgnoreCase).ToList();
        foreach (var family in families)
        {
            await using var cmd=connection.CreateCommand();
            cmd.Parameters.AddNVarChar("@Family",family.Key,100);
            cmd.CommandText=@"
SELECT r.MaxSelectable, p.Name
FROM dbo.LicenseFamilyRules r
JOIN dbo.LicenseProducts p ON p.LicenseProductId=r.ReplacementLicenseProductId
WHERE r.ProductFamily=@Family;";
            await using var reader=await cmd.ExecuteReaderAsync(HttpContext.RequestAborted);
            if(await reader.ReadAsync(HttpContext.RequestAborted))
            {
                var max=reader.GetInt32(0);
                if(family.Count()>max) result.Add(new FamilyViolation(family.Key,family.Count(),max,reader.GetString(1)));
            }
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
    application.ProjectNumber,
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
    item.StartDate,
    item.EndDate,
    item.IsPermanent,
    item.ProvisioningStatus,
    item.ProvisioningLastError
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
                ProjectNumber = Get(reader, 6),
                Status = Get(reader, 7),
                ManagerDecision = Get(reader, 8),
                ManagerReason = Get(reader, 9),
                SubmittedAt = reader.GetDateTime(10)
            };

            result.Items.Add(
                new ItemDetails
                {
                    Id = reader.GetInt64(11),
                    Name = Get(reader, 12),
                    Status = Get(reader, 13),
                    Reason = Get(reader, 14),
                    FulfillmentType = Get(reader, 15),
                    AdGroupName = Get(reader, 16),
                    StartDate = reader.GetDateTime(17),
                    EndDate = reader.IsDBNull(18) ? null : reader.GetDateTime(18),
                    IsPermanent = reader.GetBoolean(19),
                    ProvisioningStatus = Get(reader, 20),
                    ProvisioningLastError = Get(reader, 21)
                });
        }

        return result;
    }

    private static bool TryParseDate(string? value, bool required, out DateTime? result)
    {
        result = null;
        if (string.IsNullOrWhiteSpace(value)) return !required;
        var formats = new[] { "dd.MM.yyyy", "d.M.yyyy", "yyyy-MM-dd" };
        if (!DateTime.TryParseExact(value.Trim(), formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
            return false;
        result = parsed.Date;
        return true;
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
                    : reader.GetInt32(4),
            FulfillmentType = Get(reader, 5),
            AdGroupName = Get(reader, 6),
            LicenseCount = reader.IsDBNull(7) ? null : reader.GetInt32(7),
            CurrentInUse = reader.GetInt32(8)
        };

    private static string Get(
        SqlDataReader reader,
        int ordinal) =>
        reader.IsDBNull(ordinal)
            ? ""
            : Convert.ToString(
                  reader.GetValue(ordinal))
              ?? "";


    public sealed record ValidationIssue(
        string Key,
        string? Argument = null,
        int? Current = null,
        int? Limit = null,
        string? Replacement = null);

    private sealed record FamilyViolation(string ProductFamily,int SelectedCount,int MaxSelectable,string ReplacementName);

    public sealed class UserInfo
    {
        public string Sam { get; init; } = "";
        public string DisplayName { get; init; } = "";
        public string Email { get; init; } = "";
        public string ManagerSam { get; init; } = "";
        public string ManagerName { get; init; } = "";
        public string ManagerEmail { get; init; } = "";
        public string ProjectNumber { get; init; } = "";
        public string Domain { get; init; } = "";
        public string Label { get; init; } = "";
    }

    public sealed class LicenseOption
    {
        public int Id { get; init; }
        public string Name { get; init; } = "";
        public string Description { get; init; } = "";
        public string ProductFamily { get; init; } = "";
        public int? LicenseLevel { get; init; }
        public string FulfillmentType { get; init; } = "Manual";
        public string AdGroupName { get; init; } = "";
        public int? LicenseCount { get; init; }
        public int CurrentInUse { get; init; }
    }

    public sealed class ApplicationDetails
    {
        public long Id { get; init; }
        public string UserName { get; init; } = "";
        public string UserEmail { get; init; } = "";
        public string ManagerSam { get; init; } = "";
        public string ManagerName { get; init; } = "";
        public string BusinessReason { get; init; } = "";
        public string ProjectNumber { get; init; } = "";
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
        public string FulfillmentType { get; init; } = "Manual";
        public string AdGroupName { get; init; } = "";
        public DateTime StartDate { get; init; }
        public DateTime? EndDate { get; init; }
        public bool IsPermanent { get; init; }
        public string ProvisioningStatus { get; init; } = "";
        public string ProvisioningLastError { get; init; } = "";
    }
}

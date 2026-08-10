using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Data.SqlClient;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using UserChangeQueueWeb.Services;

namespace UserChangeQueueWeb.Pages;

[Authorize]
public class UserChangeQueueModel : PageModel
{
    private readonly SqlConnectionFactory _connectionFactory;
    private readonly ObjectAccessService _objectAccessService;
    private readonly QueueAuditService _queueAuditService;
    private readonly ADGroupRuleService _groupRuleService;
    private readonly OfficeLicenseRuleService _officeLicenseRuleService;
    private readonly AccessCardGroupService _accessCardGroupService;
    private readonly UiTextService _uiTextService;

    public UserChangeQueueModel(
        SqlConnectionFactory connectionFactory,
        ObjectAccessService objectAccessService,
        QueueAuditService queueAuditService,
        ADGroupRuleService groupRuleService,
        OfficeLicenseRuleService officeLicenseRuleService,
        AccessCardGroupService accessCardGroupService,
        UiTextService uiTextService)
    {
        _connectionFactory = connectionFactory;
        _objectAccessService = objectAccessService;
        _queueAuditService = queueAuditService;
        _groupRuleService = groupRuleService;
        _officeLicenseRuleService = officeLicenseRuleService;
        _accessCardGroupService = accessCardGroupService;
        _uiTextService = uiTextService;
    }

public class DomainOption
{
    public string Domain { get; set; } = "";
    public string OU { get; set; } = "";
    public string Company { get; set; } = "";
    public string Street { get; set; } = "";
    public string Zipcode { get; set; } = "";
    public string City { get; set; } = "";
    public string Country { get; set; } = "";
    public string Office { get; set; } = "";
}
    public List<string> Departments { get; set; } = new();
    public List<string> Titles { get; set; } = new();
    public List<ComputerTypeOption> ComputerTypes { get; set; } = new();

public class ComputerTypeOption
{
    public string ComputerType { get; set; } = "";
    public string Domain { get; set; } = "";
}
public List<EmployeeTypeOption> EmployeeTypes { get; set; } = new();

public class EmployeeTypeOption
{
    public string EmployeeType { get; set; } = "";
    public bool RequiresEndDate { get; set; }
}
    public List<ManagerOption> Managers { get; set; } = new();
    public List<DomainOption> Domains { get; set; } = new();
public List<ProjectOption> Projects { get; set; } = new();

public class ProjectOption
{
    public string ProjectName { get; set; } = "";
    public string ProjectNumber { get; set; } = "";
    public string Company { get; set; } = "";
}
    public class ManagerOption
    {
        public string SamAccountName { get; set; } = "";
        public string DisplayName { get; set; } = "";
        public string Mail { get; set; } = "";
        public string Domain { get; set; } = "";
        public string EmployeeType { get; set; } = "";
    }

 
    [BindProperty] public string RequestType { get; set; } = "CREATE";
    [BindProperty] [DataType(DataType.Date)] public DateTime ExecuteAfter { get; set; } = DateTime.Today;
    [BindProperty] public string? TargetSamAccountName { get; set; }
    [BindProperty] public string? NewSamAccountName { get; set; }
    [BindProperty] public string? NewUserPrincipalName { get; set; }
    [BindProperty] public string? NewDisplayName { get; set; }
    [BindProperty] public string? NewGivenName { get; set; }
    [BindProperty] public string? NewSurname { get; set; }
    [BindProperty] public string? NewOU { get; set; }
    [BindProperty] public string? Company { get; set; }
    [BindProperty] public string? ManagerSamAccountName { get; set; }
    [BindProperty] public string? Department { get; set; }
    [BindProperty] public string? ProjectNumber { get; set; }
    [BindProperty] public string? Title { get; set; }
    [BindProperty] public string? EmployeeType { get; set; }
    [BindProperty] public string? Mail { get; set; }
    [BindProperty] public string? PrivateEmail { get; set; }
    [BindProperty] public string? SubmitAction { get; set; }
    [BindProperty] public bool Enabled { get; set; } = true;
    [BindProperty] public string? AttributeJson { get; set; }
    [BindProperty] public string? SelectedDomain { get; set; }
    [BindProperty] public string? ComputerType { get; set; }
    [BindProperty] public string? StreetAddress { get; set; }
    [BindProperty] public string? PostalCode { get; set; }
    [BindProperty] public string? City { get; set; }
    [BindProperty] public string? Country { get; set; }
    [BindProperty] public string? MobilePhone { get; set; }
    [BindProperty] [DataType(DataType.Date)] public DateTime? AccountExpirationDate { get; set; }
    [BindProperty] public string? Office { get; set; }
    [BindProperty] public bool AccessCard { get; set; }
    [BindProperty] public string? OfficeLicense { get; set; }
    [BindProperty(SupportsGet = true)] public long? RequestId { get; set; }

    public List<OfficeLicenseRuleService.TitleOfficeLicenseRule> TitleOfficeLicenseRules { get; set; } = new();
    public List<ADGroupRuleService.RecommendedGroup> RecommendedGroups { get; set; } = new();
    public List<AccessCardGroupService.AccessCardGroupOption> AccessCardGroups { get; set; } = new();

    [BindProperty]
    public List<int> SelectedAccessCardGroupIds { get; set; } = new();

    public string? OfficeLicenseRuleMessage { get; set; }
    public string? Message { get; set; }

    public async Task<IActionResult> OnGetAsync()
    {
        RequestType = "CREATE";
        await LoadDomainsAsync();
        await LoadDropdownsAsync();
        await LoadManagersAsync();

        if (RequestId.HasValue)
        {
            if (!await _objectAccessService.CanAccessRequestAsync(User, RequestId.Value, "CREATE"))
            {
                return Forbid();
            }

            await LoadExistingCreateRequestAsync(RequestId.Value);
        }
        else
        {
            await ApplyDefaultManagerToCurrentUserAsync();
        }

        await ApplyOfficeLicenseFromTitleAsync(requireRule: false);
        await LoadGroupRecommendationsAsync();
        await LoadAccessCardGroupsAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        RequestType = "CREATE";

        if (RequestId.HasValue &&
            !await _objectAccessService.CanAccessRequestAsync(User, RequestId.Value, "CREATE"))
        {
            return Forbid();
        }

        if (!TryReadPostedDates())
        {
            await LoadDomainsAsync();
            await LoadDropdownsAsync();
            await LoadManagersAsync();
            await LoadAccessCardGroupsAsync();
            await ApplyOfficeLicenseFromTitleAsync(requireRule: false);
            await LoadGroupRecommendationsAsync(preferQueuedGroups: false);
            return Page();
        }
        await LoadDomainsAsync();
        await LoadDropdownsAsync();
        await LoadManagersAsync();
        await LoadAccessCardGroupsAsync();
        await ApplyOfficeLicenseFromTitleAsync(requireRule: false);
        if (SubmitAction != "SubmitRequest")
        {
            await LoadGroupRecommendationsAsync(preferQueuedGroups: false);
            return Page();
        }

        if (!string.IsNullOrWhiteSpace(NewSamAccountName) &&
            !string.IsNullOrWhiteSpace(SelectedDomain))
        {
            NewUserPrincipalName = $"{NewSamAccountName}@{SelectedDomain}";
        }

        if (string.IsNullOrWhiteSpace(NewOU) &&
            !string.IsNullOrWhiteSpace(SelectedDomain))
        {
            NewOU = Domains
                .FirstOrDefault(d => d.Domain.Equals(SelectedDomain, StringComparison.OrdinalIgnoreCase))
                ?.OU;
        }

        if (!TryResolveSelectedProjectNumber())
        {
            var projectTexts = await _uiTextService.GetTextsAsync(
                HttpContext,
                new Dictionary<string, string> { ["select.project"] = "Select project" });
            Message = projectTexts.T("select.project", "Select project");
            await LoadGroupRecommendationsAsync(preferQueuedGroups: false);
            return Page();
        }

        if (RequestType != "CREATE" && RequestType != "UPDATE")
        {
            Message = "Invalid request type.";
            await LoadGroupRecommendationsAsync(preferQueuedGroups: false);
            return Page();
        }
if (RequestType == "CREATE")
{
    if (string.IsNullOrWhiteSpace(NewSamAccountName))
    {
        Message = "New sAMAccountName is required for CREATE.";
        await LoadGroupRecommendationsAsync(preferQueuedGroups: false);
        return Page();
    }

    await using var checkCn = await _connectionFactory.OpenAsync();

    await using (var checkCmd = checkCn.CreateCommand())
    {
        checkCmd.CommandText = @"
SELECT COUNT(*)
FROM dbo.ADObjects
WHERE IsDeleted = 0
  AND SamAccountName = @SamAccountName;
";
        checkCmd.Parameters.AddNVarChar("@SamAccountName", NewSamAccountName, 256);

        var existingAdCount = Convert.ToInt32(await checkCmd.ExecuteScalarAsync());

        if (existingAdCount > 0)
        {
            Message = $"Cannot create user. sAMAccountName '{NewSamAccountName}' already exists in AD.";
            await LoadGroupRecommendationsAsync(preferQueuedGroups: false);
            return Page();
        }
    }

    await using (var checkCmd = checkCn.CreateCommand())
    {
        checkCmd.CommandText = @"
SELECT COUNT(*)
FROM dbo.ADUserChangeQueue
WHERE RequestType = 'CREATE'
  AND Status IN ('Pending', 'Approved', 'Processing')
  AND NewSamAccountName = @SamAccountName
  AND (@RequestId IS NULL OR RequestId <> @RequestId);
";
        checkCmd.Parameters.AddNVarChar("@SamAccountName", NewSamAccountName, 256);
        checkCmd.Parameters.AddNullableBigInt("@RequestId", RequestId);

        var pendingQueueCount = Convert.ToInt32(await checkCmd.ExecuteScalarAsync());

        if (pendingQueueCount > 0)
        {
            Message = $"Cannot create user. There is already a pending create request for sAMAccountName '{NewSamAccountName}'.";
            await LoadGroupRecommendationsAsync(preferQueuedGroups: false);
            return Page();
        }
    }
}
        ExecuteAfter = ExecuteAfter.Date;
        if (!string.IsNullOrWhiteSpace(MobilePhone))
{
    MobilePhone = MobilePhone
        .Replace(" ", "")
        .Replace("-", "");

    if (System.Text.RegularExpressions.Regex.IsMatch(MobilePhone, @"^\d{8}$"))
    {
        MobilePhone = "+47" + MobilePhone;
    }
}


        if (!await ApplyOfficeLicenseFromTitleAsync(requireRule: true))
        {
            await LoadGroupRecommendationsAsync(preferQueuedGroups: false);
            return Page();
        }

        await using var cn = await _connectionFactory.OpenAsync();

        if (RequestId.HasValue)
        {
            using var editTx = cn.BeginTransaction();
            var editChangedBy = GetCurrentUserName();
            var oldJson = await _queueAuditService.ReadQueueRowJsonAsync(cn, RequestId.Value, editTx);
            var updatedRows = await UpdateExistingCreateRequestAsync(cn, editTx);

            if (updatedRows > 0)
            {
                var editRecommendedGroups = await _groupRuleService.GetRecommendedGroupsAsync(cn, BuildGroupRuleContext(), editTx);
                await _groupRuleService.ReplaceRuleGeneratedQueueGroupsAsync(cn, RequestId.Value, editRecommendedGroups, editChangedBy, editTx);
                await _accessCardGroupService.ReplaceSelectionsAsync(cn, RequestId.Value, AccessCard, SelectedAccessCardGroupIds, User, Office, editChangedBy, editTx);
                await _queueAuditService.MarkRequestUpdatedAsync(cn, RequestId.Value, editChangedBy, editTx);
                var editedJson = await _queueAuditService.ReadQueueRowJsonAsync(cn, RequestId.Value, editTx);
                await _queueAuditService.WriteHistoryAsync(
                    cn,
                    RequestId.Value,
                    "CREATE_EDITED",
                    editChangedBy,
                    oldJson,
                    editedJson,
                    transaction: editTx);
            }

            editTx.Commit();

            if (updatedRows == 0)
            {
                Message = "The pending create request could not be updated. It may have been completed, cancelled, or changed.";
                await LoadExistingCreateRequestAsync(RequestId.Value);
                await ApplyOfficeLicenseFromTitleAsync(requireRule: false);
                await LoadGroupRecommendationsAsync();
                return Page();
            }

            await LoadExistingCreateRequestAsync(RequestId.Value);
            await ApplyOfficeLicenseFromTitleAsync(requireRule: false);
            await LoadGroupRecommendationsAsync();
            Message = $"Pending create request {RequestId} saved.";
            return Page();
        }

        await using var cmd = cn.CreateCommand();
        cmd.CommandText = @"
INSERT INTO dbo.ADUserChangeQueue (
    RequestType,
    Status,
    ExecuteAfter,
    TargetSamAccountName,
    NewSamAccountName,
    NewUserPrincipalName,
    NewDisplayName,
    NewGivenName,
    NewSurname,
    NewOU,
    ManagerSamAccountName,
    Department,
    ProjectNumber,
    Title,
    EmployeeType,
    AccountExpirationDate,
    Company,
    StreetAddress,
    PostalCode,
    City,
    Country,
    Office,
    Mail,
    PrivateEmail,
    Enabled,
    AttributeJson,
    MobilePhone,
    OfficeLicense,
    ComputerType,
    AccessCard,
    RequestedBy
)
OUTPUT INSERTED.RequestId
VALUES (
    @RequestType,
    'Pending',
    @ExecuteAfter,
    @TargetSamAccountName,
    @NewSamAccountName,
    @NewUserPrincipalName,
    @NewDisplayName,
    @NewGivenName,
    @NewSurname,
    @NewOU,
    @ManagerSamAccountName,
    @Department,
    @ProjectNumber,
    @Title,
    @EmployeeType,
    @AccountExpirationDate,
    @Company,
    @StreetAddress,
    @PostalCode,
    @City,
    @Country,
    @Office,
    @Mail,
    @PrivateEmail,
    @Enabled,
    @AttributeJson,
    @MobilePhone,
    @OfficeLicense,
    @ComputerType,
    @AccessCard,
    @RequestedBy
);";

        using var insertTx = cn.BeginTransaction();
        cmd.Transaction = insertTx;

        var insertChangedBy = GetCurrentUserName();
        cmd.Parameters.AddRequiredNVarChar("@RequestType", RequestType, 20);
        AddCreateQueueParameters(cmd);
        cmd.Parameters.AddRequiredNVarChar("@RequestedBy", insertChangedBy, 300);

        var newRequestId = Convert.ToInt64(await cmd.ExecuteScalarAsync());
        RequestId = newRequestId;

        var insertRecommendedGroups = await _groupRuleService.GetRecommendedGroupsAsync(cn, BuildGroupRuleContext(), insertTx);
        await _groupRuleService.ReplaceRuleGeneratedQueueGroupsAsync(cn, newRequestId, insertRecommendedGroups, insertChangedBy, insertTx);
        await _accessCardGroupService.ReplaceSelectionsAsync(cn, newRequestId, AccessCard, SelectedAccessCardGroupIds, User, Office, insertChangedBy, insertTx);

        var createdJson = await _queueAuditService.ReadQueueRowJsonAsync(cn, newRequestId, insertTx);
        await _queueAuditService.WriteHistoryAsync(
            cn,
            newRequestId,
            "CREATE_CREATED",
            insertChangedBy,
            oldJson: null,
            newJson: createdJson,
            transaction: insertTx);

        insertTx.Commit();

        await LoadGroupRecommendationsAsync();
        Message = $"Request {newRequestId} submitted.";
        return Page();
    }

    private async Task LoadExistingCreateRequestAsync(long requestId)
    {
        await using var cn = await _connectionFactory.OpenAsync();

        await using var cmd = cn.CreateCommand();
        cmd.CommandText = @"
SELECT TOP 1
    RequestId,
    ExecuteAfter,
    TargetSamAccountName,
    NewSamAccountName,
    NewUserPrincipalName,
    NewDisplayName,
    NewGivenName,
    NewSurname,
    NewOU,
    ManagerSamAccountName,
    Department,
    Title,
    EmployeeType,
    AccountExpirationDate,
    Company,
    StreetAddress,
    PostalCode,
    City,
    Country,
    Office,
    Mail,
    PrivateEmail,
    Enabled,
    AttributeJson,
    MobilePhone,
    OfficeLicense,
    ComputerType,
    AccessCard,
    ProjectNumber
FROM dbo.ADUserChangeQueue
WHERE RequestId = @RequestId
  AND RequestType = 'CREATE'
  AND ISNULL(Status, '') NOT IN ('Implemented', 'Completed', 'Done', 'Cancelled', 'Rejected');
";
        cmd.Parameters.AddBigInt("@RequestId", requestId);

        await using var reader = await cmd.ExecuteReaderAsync();

        if (!await reader.ReadAsync())
        {
            Message = $"Could not find editable pending create request {requestId}.";
            return;
        }

        RequestId = reader.GetInt64(0);
        ExecuteAfter = reader.IsDBNull(1) ? DateTime.Today : reader.GetDateTime(1);
        TargetSamAccountName = GetNullableString(reader, 2);
        NewSamAccountName = GetNullableString(reader, 3);
        NewUserPrincipalName = GetNullableString(reader, 4);
        NewDisplayName = GetNullableString(reader, 5);
        NewGivenName = GetNullableString(reader, 6);
        NewSurname = GetNullableString(reader, 7);
        NewOU = GetNullableString(reader, 8);
        ManagerSamAccountName = GetNullableString(reader, 9);
        Department = GetNullableString(reader, 10);
        Title = GetNullableString(reader, 11);
        EmployeeType = GetNullableString(reader, 12);
        AccountExpirationDate = reader.IsDBNull(13) ? null : reader.GetDateTime(13);
        Company = GetNullableString(reader, 14);
        StreetAddress = GetNullableString(reader, 15);
        PostalCode = GetNullableString(reader, 16);
        City = GetNullableString(reader, 17);
        Country = GetNullableString(reader, 18);
        Office = GetNullableString(reader, 19);
        Mail = GetNullableString(reader, 20);
        PrivateEmail = GetNullableString(reader, 21);
        Enabled = reader.IsDBNull(22) || Convert.ToBoolean(reader.GetValue(22));
        AttributeJson = GetNullableString(reader, 23);
        MobilePhone = GetNullableString(reader, 24);
        OfficeLicense = GetNullableString(reader, 25);
        ComputerType = GetNullableString(reader, 26);
        AccessCard = !reader.IsDBNull(27) && Convert.ToBoolean(reader.GetValue(27));
        ProjectNumber = GetNullableString(reader, 28);
        SelectedDomain = GetDomainFromAddress(NewUserPrincipalName) ?? GetDomainFromAddress(Mail);
        RequestType = "CREATE";

        await reader.DisposeAsync();
        SelectedAccessCardGroupIds = await _accessCardGroupService.LoadSelectedGroupIdsAsync(requestId, cn);

        Message ??= $"Editing pending create request {RequestId}.";
    }

    private async Task<int> UpdateExistingCreateRequestAsync(SqlConnection cn, SqlTransaction tx)
    {
        await using var cmd = cn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = @"
UPDATE dbo.ADUserChangeQueue
SET
    ExecuteAfter = @ExecuteAfter,
    TargetSamAccountName = @TargetSamAccountName,
    NewSamAccountName = @NewSamAccountName,
    NewUserPrincipalName = @NewUserPrincipalName,
    NewDisplayName = @NewDisplayName,
    NewGivenName = @NewGivenName,
    NewSurname = @NewSurname,
    NewOU = @NewOU,
    ManagerSamAccountName = @ManagerSamAccountName,
    Department = @Department,
    ProjectNumber = @ProjectNumber,
    Title = @Title,
    EmployeeType = @EmployeeType,
    AccountExpirationDate = @AccountExpirationDate,
    Company = @Company,
    StreetAddress = @StreetAddress,
    PostalCode = @PostalCode,
    City = @City,
    Country = @Country,
    Office = @Office,
    Mail = @Mail,
    PrivateEmail = @PrivateEmail,
    Enabled = @Enabled,
    AttributeJson = @AttributeJson,
    MobilePhone = @MobilePhone,
    OfficeLicense = @OfficeLicense,
    ComputerType = @ComputerType,
    AccessCard = @AccessCard
WHERE RequestId = @RequestId
  AND RequestType = 'CREATE'
  AND Status IN ('Pending', 'Approved', 'Processing');
";

        cmd.Parameters.AddBigInt("@RequestId", RequestId!.Value);
        AddCreateQueueParameters(cmd);

        return await cmd.ExecuteNonQueryAsync();
    }

    private void AddCreateQueueParameters(SqlCommand cmd)
    {
        cmd.Parameters.AddDate("@ExecuteAfter", ExecuteAfter.Date);
        cmd.Parameters.AddNVarChar("@TargetSamAccountName", TargetSamAccountName, 256);
        cmd.Parameters.AddNVarChar("@NewSamAccountName", NewSamAccountName, 256);
        cmd.Parameters.AddNVarChar("@NewUserPrincipalName", NewUserPrincipalName, 320);
        cmd.Parameters.AddNVarChar("@NewDisplayName", NewDisplayName, 256);
        cmd.Parameters.AddNVarChar("@NewGivenName", NewGivenName, 128);
        cmd.Parameters.AddNVarChar("@NewSurname", NewSurname, 128);
        cmd.Parameters.AddNVarChar("@NewOU", NewOU, 1024);
        cmd.Parameters.AddNVarChar("@ManagerSamAccountName", ManagerSamAccountName, 256);
        cmd.Parameters.AddNVarChar("@Department", Department, 256);
        cmd.Parameters.AddNVarChar("@ProjectNumber", ProjectNumber, 100);
        cmd.Parameters.AddNVarChar("@Title", Title, 256);
        cmd.Parameters.AddNVarChar("@EmployeeType", EmployeeType, 100);
        cmd.Parameters.AddNullableDate("@AccountExpirationDate", AccountExpirationDate?.Date);
        cmd.Parameters.AddNVarChar("@Company", Company, 256);
        cmd.Parameters.AddNVarChar("@StreetAddress", StreetAddress, 256);
        cmd.Parameters.AddNVarChar("@PostalCode", PostalCode, 50);
        cmd.Parameters.AddNVarChar("@City", City, 100);
        cmd.Parameters.AddNVarChar("@Country", Country, 100);
        cmd.Parameters.AddNVarChar("@Office", Office, 100);
        cmd.Parameters.AddNVarChar("@Mail", Mail, 320);
        cmd.Parameters.AddNVarChar("@PrivateEmail", PrivateEmail, 320);
        cmd.Parameters.AddBit("@Enabled", Enabled);
        cmd.Parameters.AddNVarCharMax("@AttributeJson", AttributeJson);
        cmd.Parameters.AddNVarChar("@MobilePhone", MobilePhone, 50);
        cmd.Parameters.AddNVarChar("@OfficeLicense", OfficeLicense, 100);
        cmd.Parameters.AddNVarChar("@ComputerType", ComputerType, 100);
        cmd.Parameters.AddBit("@AccessCard", AccessCard);
    }


    private bool TryResolveSelectedProjectNumber()
    {
        ProjectNumber = null;

        if (string.IsNullOrWhiteSpace(Department))
        {
            return false;
        }

        var selectedCompany = Domains
            .FirstOrDefault(d => string.Equals(d.Domain, SelectedDomain, StringComparison.OrdinalIgnoreCase))
            ?.Company;

        var matches = Projects
            .Where(p =>
                string.Equals(p.ProjectName?.Trim(), Department.Trim(), StringComparison.OrdinalIgnoreCase) &&
                (string.IsNullOrWhiteSpace(selectedCompany) ||
                 string.Equals(p.Company?.Trim(), selectedCompany.Trim(), StringComparison.OrdinalIgnoreCase)))
            .ToList();

        if (matches.Count != 1 || string.IsNullOrWhiteSpace(matches[0].ProjectNumber))
        {
            return false;
        }

        ProjectNumber = matches[0].ProjectNumber.Trim();
        return true;
    }


    private bool TryReadPostedDates()
    {
        if (!TryReadPostedDate("ExecuteAfter", required: true, out var executeAfter))
        {
            return false;
        }

        ExecuteAfter = (executeAfter ?? DateTime.Today).Date;

        if (!TryReadPostedDate("AccountExpirationDate", required: false, out var accountExpirationDate))
        {
            return false;
        }

        AccountExpirationDate = accountExpirationDate?.Date;
        return true;
    }

    private bool TryReadPostedDate(string formKey, bool required, out DateTime? value)
    {
        value = null;

        var rawValue = Request.Form[formKey].ToString().Trim();

        if (string.IsNullOrWhiteSpace(rawValue))
        {
            if (required)
            {
                Message = formKey == "ExecuteAfter"
                    ? "Execute after date is required. Use dd.MM.yyyy."
                    : $"{formKey} is required. Use dd.MM.yyyy.";

                return false;
            }

            return true;
        }

        var formats = new[]
        {
            "dd.MM.yyyy",
            "d.M.yyyy",
            "dd.MM.yy",
            "d.M.yy",
            "yyyy-MM-dd",
            "dd/MM/yyyy",
            "d/M/yyyy",
            "dd/MM/yy",
            "d/M/yy"
        };

        var parseCulture = (CultureInfo)CultureInfo.InvariantCulture.Clone();
        parseCulture.DateTimeFormat.Calendar.TwoDigitYearMax = 2069;

        if (DateTime.TryParseExact(
                rawValue,
                formats,
                parseCulture,
                DateTimeStyles.None,
                out var parsedValue))
        {
            value = parsedValue.Date;
            return true;
        }

        Message = formKey == "ExecuteAfter"
            ? "Invalid execute date. Use dd.MM.yyyy."
            : "Invalid account expiration date. Use dd.MM.yyyy.";

        return false;
    }

    private ADGroupRuleService.GroupRuleContext BuildGroupRuleContext()
    {
        return new ADGroupRuleService.GroupRuleContext
        {
            Domain = SelectedDomain,
            Company = Company,
            Department = Department,
            Title = Title,
            EmployeeType = EmployeeType,
            Office = Office,
            Country = Country,
            City = City,
            ComputerType = ComputerType,
            OfficeLicense = OfficeLicense,
            ManagerSamAccountName = ManagerSamAccountName,
            AccessCard = AccessCard,
            Enabled = Enabled
        };
    }

    public bool HasOfficeLicenseRuleForTitle(string? title)
    {
        return TitleOfficeLicenseRules.Any(rule =>
            string.Equals(rule.Title?.Trim(), title?.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    public string? GetConfiguredOfficeLicenseForTitle(string? title)
    {
        return TitleOfficeLicenseRules
            .FirstOrDefault(rule =>
                string.Equals(rule.Title?.Trim(), title?.Trim(), StringComparison.OrdinalIgnoreCase))
            ?.LicenseName;
    }

    private async Task<bool> ApplyOfficeLicenseFromTitleAsync(bool requireRule)
    {
        var result = await _officeLicenseRuleService.ResolveLicenseForTitleAsync(Title);

        OfficeLicense = result.HasRule ? result.LicenseName : null;
        OfficeLicenseRuleMessage = BuildOfficeLicenseRuleMessage(result);

        if (requireRule && !string.IsNullOrWhiteSpace(Title) && !result.HasRule)
        {
            Message = result.RuleTableExists
                ? $"No Office license rule is configured for title '{Title}'. Add the title to dbo.TitleOfficeLicenseRules before submitting."
                : "The title-to-Office-license rule table has not been installed yet. Run Database/TitleOfficeLicenseRules.Required.sql before submitting requests.";

            return false;
        }

        return true;
    }

    private static string BuildOfficeLicenseRuleMessage(OfficeLicenseRuleService.OfficeLicenseRuleResult result)
    {
        if (string.IsNullOrWhiteSpace(result.Title))
        {
            return "Select a title to calculate the Office license.";
        }

        if (!result.RuleTableExists)
        {
            return "Office license cannot be calculated because dbo.TitleOfficeLicenseRules is not installed.";
        }

        if (!result.HasRule)
        {
            return $"No Office license rule is configured for title '{result.Title}'.";
        }

        return result.HasOfficeLicense
            ? $"Calculated from title: {result.LicenseName}"
            : "Calculated from title: no Office license.";
    }

    private async Task LoadGroupRecommendationsAsync(bool preferQueuedGroups = true)
    {
        if (preferQueuedGroups && RequestId.HasValue)
        {
            RecommendedGroups = await _groupRuleService.LoadQueuedGroupsAsync(RequestId.Value);

            if (RecommendedGroups.Count > 0)
            {
                return;
            }
        }

        RecommendedGroups = await _groupRuleService.GetRecommendedGroupsAsync(BuildGroupRuleContext());
    }


    private async Task LoadAccessCardGroupsAsync()
    {
        AccessCardGroups = await _accessCardGroupService.GetAvailableGroupsAsync(User, Office);
    }

    private string GetCurrentUserName()
    {
        return User.Identity?.Name ?? Environment.UserName;
    }

    private async Task ApplyDefaultManagerToCurrentUserAsync()
    {
        if (!string.IsNullOrWhiteSpace(ManagerSamAccountName))
        {
            return;
        }

        var managerSamAccountName = await ResolveCurrentUserManagerSamAccountNameAsync();

        if (!string.IsNullOrWhiteSpace(managerSamAccountName))
        {
            ManagerSamAccountName = managerSamAccountName;
        }
    }

    private async Task<string?> ResolveCurrentUserManagerSamAccountNameAsync()
    {
        var identityName = User.Identity?.Name;

        if (string.IsNullOrWhiteSpace(identityName))
        {
            return null;
        }

        var samCandidate = ExtractSamAccountName(identityName);

        if (string.IsNullOrWhiteSpace(samCandidate))
        {
            return null;
        }

        await using var cn = await _connectionFactory.OpenAsync();

        await using var cmd = cn.CreateCommand();
        cmd.CommandText = @"
SELECT TOP 1
    ad.SamAccountName
FROM dbo.ADObjects AS ad
INNER JOIN dbo.Employeetype AS et
    ON et.employeetype = ad.EmployeeType
WHERE ad.IsDeleted = 0
  AND ad.Enabled = 1
  AND ad.SamAccountName IS NOT NULL
  AND ISNULL(et.CanBeManager, 0) = 1
  AND
  (
      ad.SamAccountName = @SamCandidate
      OR ad.UserPrincipalName = @IdentityName
      OR ad.Mail = @IdentityName
  )
ORDER BY
    CASE
        WHEN ad.SamAccountName = @SamCandidate THEN 0
        WHEN ad.UserPrincipalName = @IdentityName THEN 1
        WHEN ad.Mail = @IdentityName THEN 2
        ELSE 3
    END;
";
        cmd.Parameters.AddNVarChar("@SamCandidate", samCandidate, 256);
        cmd.Parameters.AddNVarChar("@IdentityName", identityName, 512);

        var value = await cmd.ExecuteScalarAsync();

        return value as string;
    }

    private static string? ExtractSamAccountName(string? identityName)
    {
        if (string.IsNullOrWhiteSpace(identityName))
        {
            return null;
        }

        var trimmed = identityName.Trim();
        var slashIndex = trimmed.LastIndexOf('\\');

        if (slashIndex >= 0 && slashIndex < trimmed.Length - 1)
        {
            trimmed = trimmed[(slashIndex + 1)..];
        }

        var atIndex = trimmed.IndexOf('@');

        if (atIndex > 0)
        {
            trimmed = trimmed[..atIndex];
        }

        return string.IsNullOrWhiteSpace(trimmed) ? null : trimmed;
    }

    private static string? GetNullableString(SqlDataReader reader, int ordinal)
    {
        return reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    }

    private static string? GetDomainFromAddress(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var atIndex = value.LastIndexOf('@');

        return atIndex >= 0 && atIndex < value.Length - 1
            ? value[(atIndex + 1)..]
            : null;
    }

    private async Task LoadDropdownsAsync()
    {
        Departments.Clear();
        Titles.Clear();
        TitleOfficeLicenseRules.Clear();
        EmployeeTypes.Clear();
        ComputerTypes.Clear();
        Projects.Clear();

        await using var cn = await _connectionFactory.OpenAsync();

        await using (var cmd = cn.CreateCommand())
        {
            cmd.CommandText = "SELECT Department FROM dbo.Departments WHERE IsActive = 1 ORDER BY Department;";

            await using var reader = await cmd.ExecuteReaderAsync();

            while (await reader.ReadAsync())
            {
                Departments.Add(reader.GetString(0));
            }
        }

        await using (var cmd = cn.CreateCommand())
        {
            cmd.CommandText = @"
SELECT
    ProjectName,
    ISNULL(ProjectNumber, '') AS ProjectNumber,
    Company
FROM dbo.Projects
WHERE Active = 1
ORDER BY Company, ProjectName;
";

            await using var reader = await cmd.ExecuteReaderAsync();

            while (await reader.ReadAsync())
            {
                Projects.Add(new ProjectOption
                {
                    ProjectName = reader.GetString(0),
                    ProjectNumber = reader.GetString(1),
                    Company = reader.GetString(2)
                });
            }
        }

        await using (var cmd = cn.CreateCommand())
        {
            cmd.CommandText = "SELECT Title FROM dbo.Titles WHERE IsActive = 1 ORDER BY Title;";

            await using var reader = await cmd.ExecuteReaderAsync();

            while (await reader.ReadAsync())
            {
                Titles.Add(reader.GetString(0));
            }
        }

        TitleOfficeLicenseRules = await _officeLicenseRuleService.LoadActiveTitleRulesAsync(cn);

        await using (var cmd = cn.CreateCommand())
        {
            cmd.CommandText = @"
SELECT
    ComputerType,
    ISNULL(Domain, '') AS Domain
FROM dbo.ComputerTypes
WHERE IsActive = 1
ORDER BY ComputerType;
";

            await using var reader = await cmd.ExecuteReaderAsync();

            while (await reader.ReadAsync())
            {
                ComputerTypes.Add(new ComputerTypeOption
                {
                    ComputerType = reader.GetString(0),
                    Domain = reader.GetString(1)
                });
            }
        }

        await using (var cmd = cn.CreateCommand())
        {
            cmd.CommandText = @"
SELECT
    employeetype,
    ISNULL(enddate, 0)
FROM dbo.Employeetype
ORDER BY employeetype;
";

            await using var reader = await cmd.ExecuteReaderAsync();

            while (await reader.ReadAsync())
            {
                EmployeeTypes.Add(new EmployeeTypeOption
                {
                    EmployeeType = reader.GetString(0),
                    RequiresEndDate = !reader.IsDBNull(1) && reader.GetBoolean(1)
                });
            }
        }
    }

    private async Task LoadManagersAsync()
    {
        Managers.Clear();

        await using var cn = await _connectionFactory.OpenAsync();

        await using var cmd = cn.CreateCommand();
        cmd.CommandText = @"
SELECT
    ad.SamAccountName,
    ISNULL(ad.DisplayName, ad.SamAccountName) AS DisplayName,
    ISNULL(ad.Mail, '') AS Mail,
    LOWER(RIGHT(ad.Mail, LEN(ad.Mail) - CHARINDEX('@', ad.Mail))) AS Domain,
    ISNULL(ad.EmployeeType, '') AS EmployeeType
FROM dbo.ADObjects AS ad
INNER JOIN dbo.Employeetype AS et
    ON et.employeetype = ad.EmployeeType
WHERE ad.IsDeleted = 0
  AND ad.Enabled = 1
  AND ad.SamAccountName IS NOT NULL
  AND ad.Mail LIKE '%@%'
  AND ISNULL(et.CanBeManager, 0) = 1
ORDER BY DisplayName;
";

        await using var reader = await cmd.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {
            Managers.Add(new ManagerOption
            {
                SamAccountName = reader.GetString(0),
                DisplayName = reader.GetString(1),
                Mail = reader.GetString(2),
                Domain = reader.GetString(3),
                EmployeeType = reader.GetString(4)
            });
        }
    }

    private async Task LoadDomainsAsync()
    {
        Domains.Clear();

        await using var cn = await _connectionFactory.OpenAsync();

        await using var cmd = cn.CreateCommand();
cmd.CommandText = @"
SELECT
    [domain],
    [OU],
    [company],
    [Street],
    [Zipcode],
    [City],
    [Country],
    [Office]
FROM dbo.domains
ORDER BY [domain];
";

        await using var reader = await cmd.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {
Domains.Add(new DomainOption
{
    Domain = reader.IsDBNull(0) ? "" : reader.GetString(0),
    OU = reader.IsDBNull(1) ? "" : reader.GetString(1),
    Company = reader.IsDBNull(2) ? "" : reader.GetString(2),
    Street = reader.IsDBNull(3) ? "" : reader.GetString(3),
    Zipcode = reader.IsDBNull(4) ? "" : reader.GetString(4),
    City = reader.IsDBNull(5) ? "" : reader.GetString(5),
    Country = reader.IsDBNull(6) ? "" : reader.GetString(6),
    Office = reader.IsDBNull(7) ? "" : reader.GetString(7)
});
        }
    }

}

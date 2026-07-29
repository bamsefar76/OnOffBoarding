using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Data.SqlClient;
using System.Globalization;
using UserChangeQueueWeb.Services;

namespace UserChangeQueueWeb.Pages;

[Authorize]
public class UpdateUserModel : PageModel
{
    private readonly SqlConnectionFactory _connectionFactory;
    private readonly ObjectAccessService _objectAccessService;
    private readonly QueueAuditService _queueAuditService;
    private readonly ADGroupRuleService _groupRuleService;
    private readonly OfficeLicenseRuleService _officeLicenseRuleService;
    private readonly AccessCardGroupService _accessCardGroupService;
    private readonly AccessScopeService _accessScopeService;

    public UpdateUserModel(
        SqlConnectionFactory connectionFactory,
        ObjectAccessService objectAccessService,
        QueueAuditService queueAuditService,
        ADGroupRuleService groupRuleService,
        OfficeLicenseRuleService officeLicenseRuleService,
        AccessCardGroupService accessCardGroupService,
        AccessScopeService accessScopeService)
    {
        _connectionFactory = connectionFactory;
        _objectAccessService = objectAccessService;
        _queueAuditService = queueAuditService;
        _groupRuleService = groupRuleService;
        _officeLicenseRuleService = officeLicenseRuleService;
        _accessCardGroupService = accessCardGroupService;
        _accessScopeService = accessScopeService;
    }
public class ManagerOption
{
    public string SamAccountName { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Domain { get; set; } = "";
    public string EmployeeType { get; set; } = "";
}
    public class SearchResult
    {
        public Guid ObjectGuid { get; set; }
        public string SamAccountName { get; set; } = "";
        public string DisplayName { get; set; } = "";
        public string Mail { get; set; } = "";
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

    public class EmployeeTypeOption
    {
        public string EmployeeType { get; set; } = "";
        public bool RequiresEndDate { get; set; }
    }

    public class ComputerTypeOption
    {
        public string ComputerType { get; set; } = "";
        public string Domain { get; set; } = "";
    }

    public class ProjectOption
    {
        public string ProjectName { get; set; } = "";
        public string ProjectNumber { get; set; } = "";
        public string Company { get; set; } = "";
    }

    public List<DomainOption> Domains { get; set; } = new();
    public List<ManagerOption> Managers { get; set; } = new();
    public List<string> Titles { get; set; } = new();
    public List<EmployeeTypeOption> EmployeeTypes { get; set; } = new();
    public List<ComputerTypeOption> ComputerTypes { get; set; } = new();
    public List<ProjectOption> Projects { get; set; } = new();
    public List<OfficeLicenseRuleService.TitleOfficeLicenseRule> TitleOfficeLicenseRules { get; set; } = new();
    public List<ADGroupRuleService.RecommendedGroup> RecommendedGroups { get; set; } = new();
    public List<AccessCardGroupService.AccessCardGroupOption> AccessCardGroups { get; set; } = new();

    [BindProperty]
    public List<int> SelectedAccessCardGroupIds { get; set; } = new();

    [BindProperty] public string SearchText { get; set; } = "";
    [BindProperty] public Guid SelectedObjectGuid { get; set; }
[BindProperty(SupportsGet = true)]
public long? RequestId { get; set; }
    public List<SearchResult> SearchResults { get; set; } = new();

    [BindProperty] public string? NewDisplayName { get; set; }
    [BindProperty] public string? NewGivenName { get; set; }
    [BindProperty] public string? NewSurname { get; set; }
    [BindProperty] public string? Title { get; set; }
    [BindProperty] public string? EmployeeType { get; set; }
    [BindProperty] public string? ManagerSamAccountName { get; set; }
    [BindProperty] public string? MobilePhone { get; set; }
    [BindProperty] public string? OfficeLicense { get; set; }
    [BindProperty] public string? NewUserPrincipalName { get; set; }
    [BindProperty] public string? Mail { get; set; }
    [BindProperty] public string? PrivateEmail { get; set; }
    [BindProperty] public string? NewOU { get; set; }
    [BindProperty] public string? Company { get; set; }
    [BindProperty] public string? Department { get; set; }
    [BindProperty] public string? StreetAddress { get; set; }
    [BindProperty] public string? PostalCode { get; set; }
    [BindProperty] public string? City { get; set; }
    [BindProperty] public string? Country { get; set; }
    [BindProperty] public string? Office { get; set; }
    [BindProperty] public string? ComputerType { get; set; }
    [BindProperty] public bool AccessCard { get; set; }
    [BindProperty] public bool Enabled { get; set; } = true;
    [BindProperty] public DateTime ExecuteAfter { get; set; } = DateTime.Today;
    [BindProperty] public DateTime? AccountExpirationDate { get; set; }
    [BindProperty] public string? SelectedDomain { get; set; }

    public string? OfficeLicenseRuleMessage { get; set; }
    public string? Message { get; set; }

    public bool HasSelectedUser { get; set; }

    public string? CurrentSamAccountName { get; set; }
    public string? CurrentDisplayName { get; set; }
    public string? CurrentMail { get; set; }
    public string? CurrentTitle { get; set; }
    public string? CurrentEmployeeType { get; set; }
    public string? CurrentMobilePhone { get; set; }

    public async Task<IActionResult> OnGetAsync(Guid? user)
    {
        if (RequestId.HasValue)
        {
            if (!await _objectAccessService.CanAccessRequestAsync(User, RequestId.Value, "UPDATE"))
            {
                return Forbid();
            }

            await LoadDomainsAsync();
            await LoadDropdownsAsync();
            await LoadExistingUpdateRequestAsync(RequestId.Value);
            await ApplyOfficeLicenseFromTitleAsync(requireRule: false);
            await LoadGroupRecommendationsAsync();
            await LoadAccessCardGroupsAsync();
            return Page();
        }

        await LoadDomainsAsync();
        await LoadDropdownsAsync();

        if (user == null)
        {
            return Page();
        }

        if (!await _objectAccessService.CanViewUserAsync(User, user.Value))
        {
            return Forbid();
        }

        SelectedObjectGuid = user.Value;

        await using var cn = await _connectionFactory.OpenAsync();

        var existingPendingRequestId = await FindOpenUpdateRequestAsync(cn, SelectedObjectGuid);

        if (existingPendingRequestId.HasValue)
        {
            return RedirectToPage("/Requests/UpdateUser", new { requestId = existingPendingRequestId.Value });
        }

        await using var cmd = cn.CreateCommand();
cmd.CommandText = @"
SELECT TOP 1
    SamAccountName,
    ISNULL(DisplayName, ''),
    ISNULL(Mail, ''),
    ISNULL(Title, ''),
    ISNULL(EmployeeType, ''),
    ISNULL(Mobile, ''),
    ISNULL(GivenName, ''),
    ISNULL(Surname, ''),
    ISNULL(UserPrincipalName, ''),
    ISNULL(DistinguishedName, ''),
    ISNULL(Company, ''),
    ISNULL(Department, ''),
    ISNULL(StreetAddress, ''),
    ISNULL(PostalCode, ''),
    ISNULL(City, ''),
    ISNULL(Country, ''),
    ISNULL(Office, ''),
    ISNULL(ManagerSamAccountName, ''),
    ISNULL(Enabled, 1)
FROM dbo.ADObjects
WHERE ObjectGUID = @ObjectGuid
  AND IsDeleted = 0;";

        cmd.Parameters.AddUniqueIdentifier("@ObjectGuid", SelectedObjectGuid);

        await using var reader = await cmd.ExecuteReaderAsync();

        if (!await reader.ReadAsync())
        {
            return Page();
        }

        HasSelectedUser = true;

        CurrentSamAccountName = reader.GetString(0);
        CurrentDisplayName = reader.GetString(1);
        CurrentMail = reader.GetString(2);
        CurrentTitle = reader.GetString(3);
        CurrentEmployeeType = reader.GetString(4);
        CurrentMobilePhone = reader.GetString(5);

        NewDisplayName = CurrentDisplayName;
        Title = CurrentTitle;
        EmployeeType = CurrentEmployeeType;
        MobilePhone = CurrentMobilePhone;
        NewGivenName = reader.GetString(6);
        NewSurname = reader.GetString(7);
        NewUserPrincipalName = reader.GetString(8);
        NewOU = reader.GetString(9);
        Company = reader.GetString(10);
        Department = reader.GetString(11);
        StreetAddress = reader.GetString(12);
        PostalCode = reader.GetString(13);
        City = reader.GetString(14);
        Country = reader.GetString(15);
        Office = reader.GetString(16);
        ManagerSamAccountName = reader.GetString(17);
        Enabled = reader.GetBoolean(18);
        Mail = CurrentMail;
        SelectedDomain = GetDomainFromAddress(NewUserPrincipalName) ?? GetDomainFromAddress(CurrentMail);
        await ApplyOfficeLicenseFromTitleAsync(requireRule: false);
        await LoadGroupRecommendationsAsync();
        await LoadAccessCardGroupsAsync();

        return Page();
    }

    public async Task<IActionResult> OnPostSearchAsync()
    {
        await LoadDomainsAsync();
        await LoadDropdownsAsync();
        await LoadAccessCardGroupsAsync();

        await using var cn = await _connectionFactory.OpenAsync();

        await using var cmd = cn.CreateCommand();

        var hasAccessAll = await _objectAccessService.UserHasAccessAllAsync(User);
        var accessScope = await _accessScopeService.GetCurrentAsync(User, HttpContext.RequestAborted);
        var currentSamAccountName = ObjectAccessService.ExtractSamAccountName(User.Identity?.Name ?? Environment.UserName);

        if (hasAccessAll)
        {
            cmd.CommandText = @"
SELECT TOP 25
    ObjectGUID,
    SamAccountName,
    ISNULL(DisplayName, '') AS DisplayName,
    ISNULL(Mail,'') AS Mail
FROM dbo.ADObjects
WHERE IsDeleted = 0
  AND
  (
      SamAccountName LIKE @Search
      OR DisplayName LIKE @Search
      OR Mail LIKE @Search
  )
ORDER BY DisplayName;
";
        }
        else if (accessScope.IsHR && !string.IsNullOrWhiteSpace(accessScope.Office))
        {
            cmd.CommandText = @"
SELECT TOP 25
    ObjectGUID,
    SamAccountName,
    ISNULL(DisplayName, '') AS DisplayName,
    ISNULL(Mail,'') AS Mail
FROM dbo.ADObjects
WHERE IsDeleted = 0
  AND NULLIF(LTRIM(RTRIM(Office)), N'') = @UserOffice
  AND
  (
      SamAccountName LIKE @Search
      OR DisplayName LIKE @Search
      OR Mail LIKE @Search
  )
ORDER BY DisplayName;";
            cmd.Parameters.AddNVarChar("@UserOffice", accessScope.Office, 300);
        }
        else if (!string.IsNullOrWhiteSpace(currentSamAccountName))
        {
            cmd.CommandText = @"
WITH ManagedUsers AS
(
    SELECT
        ObjectGUID,
        SamAccountName,
        ManagerSamAccountName,
        CAST(LOWER(SamAccountName) AS nvarchar(max)) AS SamPath
    FROM dbo.ADObjects
    WHERE ManagerSamAccountName = @RootManagerSamAccountName
      AND SamAccountName IS NOT NULL
      AND ISNULL(IsDeleted, 0) = 0

    UNION ALL

    SELECT
        child.ObjectGUID,
        child.SamAccountName,
        child.ManagerSamAccountName,
        CAST(parent.SamPath + N'|' + LOWER(child.SamAccountName) AS nvarchar(max)) AS SamPath
    FROM dbo.ADObjects child
    INNER JOIN ManagedUsers parent
        ON child.ManagerSamAccountName = parent.SamAccountName
    WHERE child.SamAccountName IS NOT NULL
      AND ISNULL(child.IsDeleted, 0) = 0
      AND CHARINDEX(N'|' + LOWER(child.SamAccountName) + N'|', N'|' + parent.SamPath + N'|') = 0
)
SELECT TOP 25
    a.ObjectGUID,
    a.SamAccountName,
    ISNULL(a.DisplayName, '') AS DisplayName,
    ISNULL(a.Mail,'') AS Mail
FROM dbo.ADObjects a
INNER JOIN ManagedUsers managed
    ON managed.ObjectGUID = a.ObjectGUID
WHERE a.IsDeleted = 0
  AND
  (
      a.SamAccountName LIKE @Search
      OR a.DisplayName LIKE @Search
      OR a.Mail LIKE @Search
  )
ORDER BY a.DisplayName
OPTION (MAXRECURSION 32767);
";
            cmd.Parameters.AddNVarChar("@RootManagerSamAccountName", currentSamAccountName, 256);
        }
        else
        {
            return Page();
        }

        cmd.Parameters.AddNVarChar("@Search", "%" + SearchText + "%", 256);

        await using var reader = await cmd.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {
            SearchResults.Add(new SearchResult
            {
                ObjectGuid = reader.GetGuid(0),
                SamAccountName = reader.GetString(1),
                DisplayName = reader.GetString(2),
                Mail = reader.GetString(3)
            });
        }

        return Page();
    }

    public async Task<IActionResult> OnPostPreviewGroupsAsync()
    {
        await LoadDomainsAsync();
        await LoadDropdownsAsync();
        await LoadAccessCardGroupsAsync();
        ApplySelectedDomainValues();

        await using var cn = await _connectionFactory.OpenAsync();

        if (RequestId.HasValue)
        {
            if (!await _objectAccessService.CanAccessRequestAsync(User, RequestId.Value, "UPDATE"))
            {
                return Forbid();
            }

            var requestTargetObjectGuid = await GetRequestTargetObjectGuidAsync(cn, RequestId.Value, "UPDATE");

            if (!requestTargetObjectGuid.HasValue)
            {
                Message = "Pending update request was not found.";
                await LoadGroupRecommendationsAsync(preferQueuedGroups: false);
                return Page();
            }

            SelectedObjectGuid = requestTargetObjectGuid.Value;
            await LoadCurrentAdUserValuesAsync(cn, SelectedObjectGuid, initializeRequestedFields: false);
        }
        else if (SelectedObjectGuid != Guid.Empty)
        {
            if (!await _objectAccessService.CanViewUserAsync(User, SelectedObjectGuid))
            {
                return Forbid();
            }

            await LoadCurrentAdUserValuesAsync(cn, SelectedObjectGuid, initializeRequestedFields: false);
        }
        else
        {
            Message = "Select a user before previewing automatic groups.";
            return Page();
        }

        await ApplyOfficeLicenseFromTitleAsync(requireRule: false);
        await LoadGroupRecommendationsAsync(preferQueuedGroups: false);
        Message = "Automatic group preview refreshed.";
        return Page();
    }

    public async Task<IActionResult> OnPostSubmitUpdateAsync()
    {
        await LoadDomainsAsync();
        await LoadDropdownsAsync();

        await using var cn = await _connectionFactory.OpenAsync();

        if (!TryReadPostedDates())
        {
            var validationMessage = Message;

            if (RequestId.HasValue)
            {
                if (!await _objectAccessService.CanAccessRequestAsync(User, RequestId.Value, "UPDATE"))
                {
                    return Forbid();
                }

                await LoadExistingUpdateRequestAsync(RequestId.Value);
            }
            else if (SelectedObjectGuid != Guid.Empty)
            {
                if (!await _objectAccessService.CanViewUserAsync(User, SelectedObjectGuid))
                {
                    return Forbid();
                }

                await LoadCurrentAdUserValuesAsync(cn, SelectedObjectGuid, initializeRequestedFields: false);
            }

            await ApplyOfficeLicenseFromTitleAsync(requireRule: false);
            await LoadGroupRecommendationsAsync(preferQueuedGroups: false);
            Message = validationMessage;
            return Page();
        }

        ApplySelectedDomainValues();

        if (!await ApplyOfficeLicenseFromTitleAsync(requireRule: true))
        {
            var officeLicenseValidationMessage = Message;

            if (RequestId.HasValue)
            {
                if (!await _objectAccessService.CanAccessRequestAsync(User, RequestId.Value, "UPDATE"))
                {
                    return Forbid();
                }

                var requestTargetObjectGuid = await GetRequestTargetObjectGuidAsync(cn, RequestId.Value, "UPDATE");

                if (requestTargetObjectGuid.HasValue)
                {
                    await LoadCurrentAdUserValuesAsync(cn, requestTargetObjectGuid.Value, initializeRequestedFields: false);
                }
            }
            else if (SelectedObjectGuid != Guid.Empty)
            {
                if (!await _objectAccessService.CanViewUserAsync(User, SelectedObjectGuid))
                {
                    return Forbid();
                }

                await LoadCurrentAdUserValuesAsync(cn, SelectedObjectGuid, initializeRequestedFields: false);
            }

            await LoadGroupRecommendationsAsync(preferQueuedGroups: false);
            Message = officeLicenseValidationMessage;
            return Page();
        }

        if (RequestId.HasValue)
        {
            if (!await _objectAccessService.CanAccessRequestAsync(User, RequestId.Value, "UPDATE"))
            {
                return Forbid();
            }

            var requestTargetObjectGuid = await GetRequestTargetObjectGuidAsync(cn, RequestId.Value, "UPDATE");

            if (!requestTargetObjectGuid.HasValue)
            {
                Message = "Pending update request was not found.";
                await LoadGroupRecommendationsAsync(preferQueuedGroups: false);
                return Page();
            }

            // In edit mode the target user comes from the queue row, not the hidden form field.
            SelectedObjectGuid = requestTargetObjectGuid.Value;

            using var editTx = cn.BeginTransaction();
            var editChangedBy = GetCurrentUserName();
            var oldJson = await _queueAuditService.ReadQueueRowJsonAsync(cn, RequestId.Value, editTx);
            var updatedRows = await UpdateExistingUpdateRequestAsync(cn, editTx);

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
                    "UPDATE_EDITED",
                    editChangedBy,
                    oldJson,
                    editedJson,
                    transaction: editTx);
            }

            editTx.Commit();

            await LoadExistingUpdateRequestAsync(RequestId.Value);
            await ApplyOfficeLicenseFromTitleAsync(requireRule: false);
            await LoadGroupRecommendationsAsync();

            Message = updatedRows == 0
                ? "Pending update request was not updated. It may already be completed or cancelled."
                : $"Pending update request {RequestId} updated.";

            return Page();
        }

        if (!await _objectAccessService.CanViewUserAsync(User, SelectedObjectGuid))
        {
            return Forbid();
        }

        var duplicateRequestId = await FindOpenUpdateRequestAsync(cn, SelectedObjectGuid);

        if (duplicateRequestId.HasValue)
        {
            return RedirectToPage("/Requests/UpdateUser", new { requestId = duplicateRequestId.Value });
        }

        string? targetSam = null;
        string? targetDisplayName = null;

        await using (var getCmd = cn.CreateCommand())
        {
            getCmd.CommandText = @"
SELECT TOP 1
    SamAccountName,
    ISNULL(DisplayName, '')
FROM dbo.ADObjects
WHERE ObjectGUID = @ObjectGuid
  AND IsDeleted = 0;
";
            getCmd.Parameters.AddUniqueIdentifier("@ObjectGuid", SelectedObjectGuid);

            await using var reader = await getCmd.ExecuteReaderAsync();

            if (!await reader.ReadAsync())
            {
                Message = "Selected user was not found.";
                await LoadGroupRecommendationsAsync(preferQueuedGroups: false);
                return Page();
            }

            targetSam = reader.GetString(0);
            targetDisplayName = reader.GetString(1);
        }

        await using var cmd = cn.CreateCommand();

        cmd.CommandText = @"
INSERT INTO dbo.ADUserChangeQueue
(
    RequestType,
    Status,
    ExecuteAfter,
    TargetObjectGuid,
    TargetSamAccountName,
    TargetDisplayName,
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
    MobilePhone,
    ComputerType,
    OfficeLicense,
    AccessCard,
    RequestedBy
)
OUTPUT INSERTED.RequestId
VALUES
(
    'UPDATE',
    'Pending',
    @ExecuteAfter,
    @TargetObjectGuid,
    @TargetSamAccountName,
    @TargetDisplayName,
    @NewUserPrincipalName,
    @NewDisplayName,
    @NewGivenName,
    @NewSurname,
    @NewOU,
    @ManagerSamAccountName,
    @Department,
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
    @MobilePhone,
    @ComputerType,
    @OfficeLicense,
    @AccessCard,
    @RequestedBy
);
";

        using var insertTx = cn.BeginTransaction();
        cmd.Transaction = insertTx;

        var insertChangedBy = GetCurrentUserName();
        AddUpdateInsertParameters(cmd, targetSam, targetDisplayName);
        cmd.Parameters.AddRequiredNVarChar("@RequestedBy", insertChangedBy, 300);

        var newRequestId = Convert.ToInt64(await cmd.ExecuteScalarAsync());
        var insertRecommendedGroups = await _groupRuleService.GetRecommendedGroupsAsync(cn, BuildGroupRuleContext(), insertTx);
        await _groupRuleService.ReplaceRuleGeneratedQueueGroupsAsync(cn, newRequestId, insertRecommendedGroups, insertChangedBy, insertTx);
        await _accessCardGroupService.ReplaceSelectionsAsync(cn, newRequestId, AccessCard, SelectedAccessCardGroupIds, User, Office, insertChangedBy, insertTx);

        var createdJson = await _queueAuditService.ReadQueueRowJsonAsync(cn, newRequestId, insertTx);
        await _queueAuditService.WriteHistoryAsync(
            cn,
            newRequestId,
            "UPDATE_CREATED",
            insertChangedBy,
            oldJson: null,
            newJson: createdJson,
            transaction: insertTx);

        insertTx.Commit();

        RequestId = newRequestId;
        await LoadCurrentAdUserValuesAsync(cn, SelectedObjectGuid, initializeRequestedFields: false);
        await ApplyOfficeLicenseFromTitleAsync(requireRule: false);
        await LoadGroupRecommendationsAsync();
        Message = $"Update request {newRequestId} submitted.";
        return Page();
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
        ? "Invalid execute after date. Use dd.MM.yyyy."
        : "Invalid account expiration date. Use dd.MM.yyyy.";

    return false;
}

private void ApplySelectedDomainValues()
{
    if (string.IsNullOrWhiteSpace(SelectedDomain))
    {
        return;
    }

    var selectedDomain = Domains.FirstOrDefault(d =>
        d.Domain.Equals(SelectedDomain, StringComparison.OrdinalIgnoreCase));

    if (selectedDomain == null)
    {
        return;
    }

    NewOU = selectedDomain.OU;
    Company = selectedDomain.Company;
    StreetAddress = selectedDomain.Street;
    PostalCode = selectedDomain.Zipcode;
    City = selectedDomain.City;
    Country = selectedDomain.Country;
    Office = selectedDomain.Office;
}

private static async Task<Guid?> GetRequestTargetObjectGuidAsync(SqlConnection cn, long requestId, string requestType)
{
    await using var cmd = cn.CreateCommand();
    cmd.CommandText = @"
SELECT TOP 1 TargetObjectGUID
FROM dbo.ADUserChangeQueue
WHERE RequestId = @RequestId
  AND RequestType = @RequestType;
";

    cmd.Parameters.AddBigInt("@RequestId", requestId);
    cmd.Parameters.AddNVarChar("@RequestType", requestType, 20);

    var result = await cmd.ExecuteScalarAsync();

    return result is Guid objectGuid
        ? objectGuid
        : null;
}

private async Task<long?> FindOpenUpdateRequestAsync(SqlConnection cn, Guid targetObjectGuid, long? excludeRequestId = null)
{
    await using var cmd = cn.CreateCommand();
    cmd.CommandText = @"
SELECT TOP 1 RequestId
FROM dbo.ADUserChangeQueue
WHERE RequestType = 'UPDATE'
  AND TargetObjectGUID = @TargetObjectGUID
  AND ISNULL(Status, '') NOT IN ('Implemented', 'Completed', 'Done', 'Cancelled', 'Rejected')
  AND (@ExcludeRequestId IS NULL OR RequestId <> @ExcludeRequestId)
ORDER BY
    CreatedAt DESC,
    RequestId DESC;
";

    cmd.Parameters.AddUniqueIdentifier("@TargetObjectGUID", targetObjectGuid);
    cmd.Parameters.AddNullableBigInt("@ExcludeRequestId", excludeRequestId);

    var result = await cmd.ExecuteScalarAsync();

    return result == null || result == DBNull.Value
        ? null
        : Convert.ToInt64(result);
}

private async Task<bool> LoadCurrentAdUserValuesAsync(SqlConnection cn, Guid objectGuid, bool initializeRequestedFields)
{
    await using var cmd = cn.CreateCommand();
    cmd.CommandText = @"
SELECT TOP 1
    SamAccountName,
    ISNULL(DisplayName, ''),
    ISNULL(Mail, ''),
    ISNULL(Title, ''),
    ISNULL(EmployeeType, ''),
    ISNULL(Mobile, ''),
    ISNULL(GivenName, ''),
    ISNULL(Surname, ''),
    ISNULL(UserPrincipalName, ''),
    ISNULL(DistinguishedName, ''),
    ISNULL(Company, ''),
    ISNULL(Department, ''),
    ISNULL(StreetAddress, ''),
    ISNULL(PostalCode, ''),
    ISNULL(City, ''),
    ISNULL(Country, ''),
    ISNULL(Office, ''),
    ISNULL(ManagerSamAccountName, ''),
    ISNULL(Enabled, 1)
FROM dbo.ADObjects
WHERE ObjectGUID = @ObjectGuid
  AND IsDeleted = 0;
";

    cmd.Parameters.AddUniqueIdentifier("@ObjectGuid", objectGuid);

    await using var reader = await cmd.ExecuteReaderAsync();

    if (!await reader.ReadAsync())
    {
        return false;
    }

    HasSelectedUser = true;
    SelectedObjectGuid = objectGuid;

    CurrentSamAccountName = reader.GetString(0);
    CurrentDisplayName = reader.GetString(1);
    CurrentMail = reader.GetString(2);
    CurrentTitle = reader.GetString(3);
    CurrentEmployeeType = reader.GetString(4);
    CurrentMobilePhone = reader.GetString(5);

    if (initializeRequestedFields)
    {
        NewDisplayName = CurrentDisplayName;
        Title = CurrentTitle;
        EmployeeType = CurrentEmployeeType;
        MobilePhone = CurrentMobilePhone;
        NewGivenName = reader.GetString(6);
        NewSurname = reader.GetString(7);
        NewUserPrincipalName = reader.GetString(8);
        NewOU = reader.GetString(9);
        Company = reader.GetString(10);
        Department = reader.GetString(11);
        StreetAddress = reader.GetString(12);
        PostalCode = reader.GetString(13);
        City = reader.GetString(14);
        Country = reader.GetString(15);
        Office = reader.GetString(16);
        ManagerSamAccountName = reader.GetString(17);
        Enabled = reader.GetBoolean(18);
        Mail = CurrentMail;
        SelectedDomain = GetDomainFromAddress(NewUserPrincipalName) ?? GetDomainFromAddress(CurrentMail);
    }

    return true;
}

private async Task LoadExistingUpdateRequestAsync(long requestId)
{
    await using var cn = await _connectionFactory.OpenAsync();

    await using var cmd = cn.CreateCommand();

    cmd.CommandText = @"
SELECT TOP 1
    q.RequestId,
    q.TargetObjectGUID,
    ISNULL(q.TargetSamAccountName, ISNULL(a.SamAccountName, '')) AS TargetSamAccountName,
    ISNULL(a.DisplayName, '') AS CurrentDisplayName,
    ISNULL(a.Mail, '') AS CurrentMail,
    ISNULL(a.Title, '') AS CurrentTitle,
    ISNULL(a.EmployeeType, '') AS CurrentEmployeeType,
    ISNULL(a.Mobile, '') AS CurrentMobilePhone,

    ISNULL(q.NewDisplayName, '') AS NewDisplayName,
    ISNULL(q.NewGivenName, '') AS NewGivenName,
    ISNULL(q.NewSurname, '') AS NewSurname,
    ISNULL(q.Title, '') AS Title,
    ISNULL(q.EmployeeType, '') AS EmployeeType,
    ISNULL(q.ManagerSamAccountName, '') AS ManagerSamAccountName,
    ISNULL(q.MobilePhone, '') AS MobilePhone,
    ISNULL(q.OfficeLicense, '') AS OfficeLicense,
    ISNULL(q.NewUserPrincipalName, '') AS NewUserPrincipalName,
    ISNULL(q.Mail, '') AS Mail,
    ISNULL(q.PrivateEmail, '') AS PrivateEmail,
    ISNULL(q.NewOU, '') AS NewOU,
    ISNULL(q.Company, '') AS Company,
    ISNULL(q.Department, '') AS Department,
    ISNULL(q.StreetAddress, '') AS StreetAddress,
    ISNULL(q.PostalCode, '') AS PostalCode,
    ISNULL(q.City, '') AS City,
    ISNULL(q.Country, '') AS Country,
    ISNULL(q.Office, '') AS Office,
    ISNULL(q.ComputerType, '') AS ComputerType,
    ISNULL(q.AccessCard, 0) AS AccessCard,
    ISNULL(q.Enabled, 1) AS Enabled,
    q.ExecuteAfter,
    q.AccountExpirationDate
FROM dbo.ADUserChangeQueue q
LEFT JOIN dbo.ADObjects a
    ON a.ObjectGUID = q.TargetObjectGUID
WHERE q.RequestId = @RequestId
  AND q.RequestType = 'UPDATE';
";

    cmd.Parameters.AddBigInt("@RequestId", requestId);

    await using var reader = await cmd.ExecuteReaderAsync();

    if (!await reader.ReadAsync())
    {
        Message = "Pending update request was not found.";
        return;
    }

    RequestId = reader.GetInt64(0);
    SelectedObjectGuid = reader.GetGuid(1);

    CurrentSamAccountName = reader.GetString(2);
    CurrentDisplayName = reader.GetString(3);
    CurrentMail = reader.GetString(4);
    CurrentTitle = reader.GetString(5);
    CurrentEmployeeType = reader.GetString(6);
    CurrentMobilePhone = reader.GetString(7);

    NewDisplayName = reader.GetString(8);
    NewGivenName = reader.GetString(9);
    NewSurname = reader.GetString(10);
    Title = reader.GetString(11);
    EmployeeType = reader.GetString(12);
    ManagerSamAccountName = reader.GetString(13);
    MobilePhone = reader.GetString(14);
    OfficeLicense = reader.GetString(15);
    NewUserPrincipalName = reader.GetString(16);
    Mail = reader.GetString(17);
    PrivateEmail = reader.GetString(18);
    NewOU = reader.GetString(19);
    Company = reader.GetString(20);
    Department = reader.GetString(21);
    StreetAddress = reader.GetString(22);
    PostalCode = reader.GetString(23);
    City = reader.GetString(24);
    Country = reader.GetString(25);
    Office = reader.GetString(26);
    ComputerType = reader.GetString(27);
    AccessCard = reader.GetBoolean(28);
    Enabled = reader.GetBoolean(29);
    ExecuteAfter = reader.IsDBNull(30) ? DateTime.Today : reader.GetDateTime(30);
    AccountExpirationDate = reader.IsDBNull(31) ? null : reader.GetDateTime(31);

    SelectedDomain = GetDomainFromAddress(NewUserPrincipalName) ?? GetDomainFromAddress(Mail);

    HasSelectedUser = true;
    await reader.DisposeAsync();
    SelectedAccessCardGroupIds = await _accessCardGroupService.LoadSelectedGroupIdsAsync(requestId, cn);
    Message = $"Editing pending update request {RequestId}.";
}

private async Task<int> UpdateExistingUpdateRequestAsync(SqlConnection cn, SqlTransaction tx)
{
    await using var cmd = cn.CreateCommand();
    cmd.Transaction = tx;

    cmd.CommandText = @"
UPDATE dbo.ADUserChangeQueue
SET
    ExecuteAfter = @ExecuteAfter,
    NewUserPrincipalName = @NewUserPrincipalName,
    NewDisplayName = @NewDisplayName,
    NewGivenName = @NewGivenName,
    NewSurname = @NewSurname,
    NewOU = @NewOU,
    ManagerSamAccountName = @ManagerSamAccountName,
    Department = @Department,
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
    MobilePhone = @MobilePhone,
    ComputerType = @ComputerType,
    OfficeLicense = @OfficeLicense,
    AccessCard = @AccessCard
WHERE RequestId = @RequestId
  AND RequestType = 'UPDATE'
  AND TargetObjectGUID = @TargetObjectGUID
  AND ISNULL(Status, '') NOT IN ('Implemented', 'Completed', 'Done', 'Cancelled', 'Rejected');
";

    cmd.Parameters.AddBigInt("@RequestId", RequestId!.Value);
    cmd.Parameters.AddUniqueIdentifier("@TargetObjectGUID", SelectedObjectGuid);
    AddUpdateParameters(cmd);

    return await cmd.ExecuteNonQueryAsync();
}

private void AddUpdateInsertParameters(SqlCommand cmd, string? targetSamAccountName, string? targetDisplayName)
{
    cmd.Parameters.AddUniqueIdentifier("@TargetObjectGuid", SelectedObjectGuid);
    cmd.Parameters.AddNVarChar("@TargetSamAccountName", targetSamAccountName, 256);
    cmd.Parameters.AddNVarChar("@TargetDisplayName", targetDisplayName, 256);
    AddUpdateParameters(cmd);
}

private void AddUpdateParameters(SqlCommand cmd)
{
    cmd.Parameters.AddDate("@ExecuteAfter", ExecuteAfter.Date);
    cmd.Parameters.AddNVarChar("@NewUserPrincipalName", NewUserPrincipalName, 320);
    cmd.Parameters.AddNVarChar("@NewDisplayName", NewDisplayName, 256);
    cmd.Parameters.AddNVarChar("@NewGivenName", NewGivenName, 128);
    cmd.Parameters.AddNVarChar("@NewSurname", NewSurname, 128);
    cmd.Parameters.AddNVarChar("@NewOU", NewOU, 1024);
    cmd.Parameters.AddNVarChar("@ManagerSamAccountName", ManagerSamAccountName, 256);
    cmd.Parameters.AddNVarChar("@Department", Department, 256);
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
    cmd.Parameters.AddNVarChar("@MobilePhone", MobilePhone, 50);
    cmd.Parameters.AddNVarChar("@ComputerType", ComputerType, 100);
    cmd.Parameters.AddNVarChar("@OfficeLicense", OfficeLicense, 100);
    cmd.Parameters.AddBit("@AccessCard", AccessCard);
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

    if (HasSelectedUser || RequestId.HasValue)
    {
        RecommendedGroups = await _groupRuleService.GetRecommendedGroupsAsync(BuildGroupRuleContext());
    }
    else
    {
        RecommendedGroups = new List<ADGroupRuleService.RecommendedGroup>();
    }
}

private async Task LoadAccessCardGroupsAsync()
{
    AccessCardGroups = await _accessCardGroupService.GetAvailableGroupsAsync(User, Office);
}

private string GetCurrentUserName()
{
    return User.Identity?.Name ?? Environment.UserName;
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

 private async Task LoadDropdownsAsync()
{
    Titles.Clear();
    TitleOfficeLicenseRules.Clear();
    EmployeeTypes.Clear();
    ComputerTypes.Clear();
    Projects.Clear();
    Managers.Clear();

    await using var cn = await _connectionFactory.OpenAsync();

    await using (var titleCmd = cn.CreateCommand())
    {
        titleCmd.CommandText = @"
SELECT Title
FROM dbo.Titles
WHERE IsActive = 1
ORDER BY Title;
";

        await using var titleReader = await titleCmd.ExecuteReaderAsync();

        while (await titleReader.ReadAsync())
        {
            Titles.Add(titleReader.IsDBNull(0) ? "" : titleReader.GetString(0));
        }
    }

    TitleOfficeLicenseRules = await _officeLicenseRuleService.LoadActiveTitleRulesAsync(cn);

    await using (var employeeTypeCmd = cn.CreateCommand())
    {
        employeeTypeCmd.CommandText = @"
SELECT
    employeetype,
    ISNULL(enddate, 0)
FROM dbo.Employeetype
ORDER BY employeetype;
";

        await using var employeeTypeReader = await employeeTypeCmd.ExecuteReaderAsync();

        while (await employeeTypeReader.ReadAsync())
        {
            EmployeeTypes.Add(new EmployeeTypeOption
            {
                EmployeeType = employeeTypeReader.IsDBNull(0) ? "" : employeeTypeReader.GetString(0),
                RequiresEndDate = !employeeTypeReader.IsDBNull(1) && employeeTypeReader.GetBoolean(1)
            });
        }
    }

    await using (var computerTypeCmd = cn.CreateCommand())
    {
        computerTypeCmd.CommandText = @"
SELECT
    ComputerType,
    ISNULL(Domain, '') AS Domain
FROM dbo.ComputerTypes
WHERE IsActive = 1
ORDER BY
    CASE WHEN Domain IS NULL THEN 0 ELSE 1 END,
    ComputerType;
";

        await using var computerTypeReader = await computerTypeCmd.ExecuteReaderAsync();

        while (await computerTypeReader.ReadAsync())
        {
            ComputerTypes.Add(new ComputerTypeOption
            {
                ComputerType = computerTypeReader.IsDBNull(0) ? "" : computerTypeReader.GetString(0),
                Domain = computerTypeReader.IsDBNull(1) ? "" : computerTypeReader.GetString(1)
            });
        }
    }


    await using (var projectCmd = cn.CreateCommand())
    {
        projectCmd.CommandText = @"
SELECT
    ProjectName,
    ISNULL(ProjectNumber, '') AS ProjectNumber,
    ISNULL(Company, '') AS Company
FROM dbo.Projects
WHERE Active = 1
ORDER BY Company, ProjectName;
";

        await using var projectReader = await projectCmd.ExecuteReaderAsync();

        while (await projectReader.ReadAsync())
        {
            Projects.Add(new ProjectOption
            {
                ProjectName = projectReader.IsDBNull(0) ? "" : projectReader.GetString(0),
                ProjectNumber = projectReader.IsDBNull(1) ? "" : projectReader.GetString(1),
                Company = projectReader.IsDBNull(2) ? "" : projectReader.GetString(2)
            });
        }
    }

await using (var managerCmd = cn.CreateCommand())
{
    managerCmd.CommandText = @"
SELECT
    ad.SamAccountName,
    ISNULL(ad.DisplayName, ''),
    LOWER(
        RIGHT(
            ISNULL(ad.UserPrincipalName, ISNULL(ad.Mail, '')),
            LEN(ISNULL(ad.UserPrincipalName, ISNULL(ad.Mail, ''))) - CHARINDEX('@', ISNULL(ad.UserPrincipalName, ISNULL(ad.Mail, '')))
        )
    ) AS Domain,
    ISNULL(ad.EmployeeType, '') AS EmployeeType
FROM dbo.ADObjects AS ad
INNER JOIN dbo.Employeetype AS et
    ON et.employeetype = ad.EmployeeType
WHERE ad.IsDeleted = 0
  AND ad.Enabled = 1
  AND ad.SamAccountName IS NOT NULL
  AND ad.DisplayName IS NOT NULL
  AND CHARINDEX('@', ISNULL(ad.UserPrincipalName, ISNULL(ad.Mail, ''))) > 0
  AND ISNULL(et.CanBeManager, 0) = 1
ORDER BY DisplayName;
";

    await using var managerReader = await managerCmd.ExecuteReaderAsync();

    while (await managerReader.ReadAsync())
    {
        Managers.Add(new ManagerOption
        {
            SamAccountName = managerReader.GetString(0),
            DisplayName = managerReader.GetString(1),
            Domain = managerReader.GetString(2),
            EmployeeType = managerReader.GetString(3)
        });
    }
}}

private static string? GetDomainFromAddress(string? address)
{
    if (string.IsNullOrWhiteSpace(address))
    {
        return null;
    }

    var at = address.LastIndexOf('@');
    if (at < 0 || at == address.Length - 1)
    {
        return null;
    }

    return address[(at + 1)..].ToLowerInvariant();
}
}
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Data.SqlClient;
using UserChangeQueueWeb.Services;

namespace UserChangeQueueWeb.Pages.Assignments;

[Authorize]
public sealed class CreateModel : PageModel
{
    private static readonly string[] DateFormats =
    {
        "dd.MM.yyyy", "d.M.yyyy", "dd.MM.yy", "d.M.yy", "yyyy-MM-dd"
    };

    private readonly SqlConnectionFactory _connectionFactory;
    private readonly PersonMatchingService _personMatchingService;

    public CreateModel(SqlConnectionFactory connectionFactory, PersonMatchingService personMatchingService)
    {
        _connectionFactory = connectionFactory;
        _personMatchingService = personMatchingService;
    }

    [BindProperty] public long? SelectedPersonId { get; set; }
    [BindProperty] public long? SelectedArchiveRequestId { get; set; }
    [BindProperty, Required] public string GivenName { get; set; } = "";
    [BindProperty, Required] public string Surname { get; set; } = "";
    [BindProperty, EmailAddress] public string? PrivateEmail { get; set; }
    [BindProperty] public string? MobilePhone { get; set; }
    [BindProperty] public string? SelectedDomain { get; set; }
    [BindProperty] public int? ProjectId { get; set; }
    [BindProperty] public string? ManagerSamAccountName { get; set; }
    [BindProperty] public string? Office { get; set; }
    [BindProperty] public string? Department { get; set; }
    [BindProperty] public string? Title { get; set; }
    [BindProperty] public string? EmployeeType { get; set; }
    [BindProperty, Required] public string StartDateText { get; set; } = DateTime.Today.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture);
    [BindProperty] public string? EndDateText { get; set; }

    public string? ErrorMessage { get; private set; }
    public List<DomainOption> Domains { get; } = new();
    public List<ProjectOption> Projects { get; } = new();
    public List<ManagerOption> Managers { get; } = new();
    public List<EmployeeTypeOption> EmployeeTypes { get; } = new();
    public List<string> Titles { get; } = new();

    public async Task OnGetAsync()
    {
        await LoadOptionsAsync();
        ManagerSamAccountName ??= ExtractSamAccountName(User.Identity?.Name);
    }

    public async Task<IActionResult> OnGetProjectsAsync(string? domain)
    {
        if (string.IsNullOrWhiteSpace(domain)) return new JsonResult(Array.Empty<object>());
        await using var cn = await _connectionFactory.OpenAsync(HttpContext.RequestAborted);
        await using var cmd = cn.CreateCommand();
        cmd.CommandText = @"
DECLARE @Company nvarchar(300), @Label nvarchar(300);
SELECT TOP (1)
    @Company = ISNULL(company, N''),
    @Label = ISNULL(NULLIF(Label, N''), [domain])
FROM dbo.domains
WHERE LOWER([domain]) = LOWER(@Domain);

SELECT Id,
       ISNULL(ProjectNumber, N''),
       ISNULL(ProjectName, N''),
       CONCAT(NULLIF(ProjectNumber, N''), CASE WHEN NULLIF(ProjectNumber, N'') IS NULL THEN N'' ELSE N' — ' END, ProjectName)
FROM dbo.Projects
WHERE Active = 1
  AND (Company = @Company OR Company = @Label)
ORDER BY ProjectName;";
        cmd.Parameters.AddNVarChar("@Domain", domain.Trim(), 320);
        var rows = new List<object>();
        await using var reader = await cmd.ExecuteReaderAsync(HttpContext.RequestAborted);
        while (await reader.ReadAsync(HttpContext.RequestAborted))
        {
            rows.Add(new
            {
                id = reader.GetInt32(0),
                projectNumber = reader.IsDBNull(1) ? "" : reader.GetString(1),
                projectName = reader.IsDBNull(2) ? "" : reader.GetString(2),
                text = reader.IsDBNull(3) ? "" : reader.GetString(3)
            });
        }
        return new JsonResult(rows);
    }

    public async Task<IActionResult> OnGetManagersAsync(string? domain)
    {
        if (string.IsNullOrWhiteSpace(domain)) return new JsonResult(Array.Empty<object>());
        await using var cn = await _connectionFactory.OpenAsync(HttpContext.RequestAborted);
        await using var cmd = cn.CreateCommand();
        cmd.CommandText = @"
DECLARE @Company nvarchar(300), @Label nvarchar(300);
SELECT TOP (1)
    @Company = ISNULL(company, N''),
    @Label = ISNULL(NULLIF(Label, N''), [domain])
FROM dbo.domains
WHERE LOWER([domain]) = LOWER(@Domain);

SELECT DISTINCT a.SamAccountName, ISNULL(NULLIF(a.DisplayName, N''), a.SamAccountName)
FROM dbo.ADObjects AS a
INNER JOIN dbo.Employeetype AS et
    ON et.employeetype = a.EmployeeType
   AND ISNULL(et.CanBeManager, 0) = 1
WHERE a.IsDeleted = 0
  AND a.Enabled = 1
  AND a.SamAccountName IS NOT NULL
  AND (a.Company = @Company OR a.Company = @Label)
ORDER BY ISNULL(NULLIF(a.DisplayName, N''), a.SamAccountName);";
        cmd.Parameters.AddNVarChar("@Domain", domain.Trim(), 320);
        var rows = new List<object>();
        await using var reader = await cmd.ExecuteReaderAsync(HttpContext.RequestAborted);
        while (await reader.ReadAsync(HttpContext.RequestAborted))
        {
            rows.Add(new
            {
                samAccountName = reader.GetString(0),
                displayName = reader.GetString(1)
            });
        }
        return new JsonResult(rows);
    }

    public async Task<IActionResult> OnGetLookupAsync(string? kind, string? term)
    {
        if (string.IsNullOrWhiteSpace(term) || term.Trim().Length < 2)
            return new JsonResult(Array.Empty<string>());

        var column = kind?.Equals("title", StringComparison.OrdinalIgnoreCase) == true
            ? "Title"
            : kind?.Equals("department", StringComparison.OrdinalIgnoreCase) == true
                ? "Department"
                : null;
        if (column is null) return BadRequest();

        await using var cn = await _connectionFactory.OpenAsync(HttpContext.RequestAborted);
        await using var cmd = cn.CreateCommand();
        cmd.CommandText = $@"
SELECT TOP (25) {column}
FROM dbo.ADObjects
WHERE IsDeleted = 0
  AND NULLIF(LTRIM(RTRIM({column})), N'') IS NOT NULL
  AND {column} LIKE @Term
GROUP BY {column}
ORDER BY {column};";
        cmd.Parameters.AddNVarChar("@Term", "%" + term.Trim() + "%", 320);
        var values = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync(HttpContext.RequestAborted);
        while (await reader.ReadAsync(HttpContext.RequestAborted)) values.Add(reader.GetString(0));
        return new JsonResult(values);
    }

    public async Task<IActionResult> OnGetSearchAsync(string? givenName, string? surname, string? privateEmail, string? mobilePhone)
    {
        if (new[] { givenName, surname, privateEmail, mobilePhone }.All(string.IsNullOrWhiteSpace))
            return new JsonResult(Array.Empty<object>());

        var candidates = await _personMatchingService.FindCandidatesAsync(
            givenName, surname, privateEmail, mobilePhone, HttpContext.RequestAborted);

        var result = new List<EmployeeMatchRow>();
        await using var cn = await _connectionFactory.OpenAsync(HttpContext.RequestAborted);
        foreach (var candidate in candidates.Take(20))
        {
            EmployeeDetails? details = null;
            if (candidate.PersonId.HasValue)
                details = await ReadEmployeeAsync(cn, candidate.PersonId.Value);
            else if (candidate.ArchiveRequestId.HasValue)
                details = await ReadArchiveAsync(cn, candidate.ArchiveRequestId.Value);

            result.Add(new EmployeeMatchRow
            {
                PersonId = candidate.PersonId,
                ArchiveRequestId = candidate.ArchiveRequestId,
                DisplayName = details?.DisplayName ?? candidate.DisplayName,
                GivenName = details?.GivenName ?? "",
                Surname = details?.Surname ?? "",
                PrivateEmail = details?.PrivateEmail,
                MobilePhone = details?.MobilePhone,
                UserPrincipalName = details?.UserPrincipalName ?? candidate.UserPrincipalName,
                EmailMatch = candidate.EmailMatch,
                PhoneMatch = candidate.PhoneMatch,
                ExactNameMatch = candidate.ExactNameMatch
            });
        }
        return new JsonResult(result);
    }

    public async Task<IActionResult> OnPostAsync()
    {
        await LoadOptionsAsync();
        NormalizePostedValues();

        if (!TryParseDate(StartDateText, required: true, out var startDate))
            ModelState.AddModelError(nameof(StartDateText), "Use dd.MM.yyyy.");
        if (!TryParseDate(EndDateText, required: false, out var endDate))
            ModelState.AddModelError(nameof(EndDateText), "Use dd.MM.yyyy.");
        if (startDate.HasValue && endDate.HasValue && endDate.Value < startDate.Value)
            ModelState.AddModelError(nameof(EndDateText), "End date cannot be before start date.");

        var employeeTypeOption = EmployeeTypes.FirstOrDefault(x =>
            string.Equals(x.Name, EmployeeType, StringComparison.OrdinalIgnoreCase));
        if (employeeTypeOption?.RequiresEndDate == true && !endDate.HasValue)
            ModelState.AddModelError(nameof(EndDateText), "End date is required for this employee type.");
        if (employeeTypeOption?.RequiresEndDate != true) endDate = null;

        if (!SelectedPersonId.HasValue && !SelectedArchiveRequestId.HasValue
            && string.IsNullOrWhiteSpace(PrivateEmail) && string.IsNullOrWhiteSpace(MobilePhone))
            ModelState.AddModelError(nameof(PrivateEmail), "Enter a private email or mobile number for a new employee.");

        await using var cn = await _connectionFactory.OpenAsync(HttpContext.RequestAborted);
        var domain = await ReadDomainAsync(cn, SelectedDomain);
        if (domain is null) ModelState.AddModelError(nameof(SelectedDomain), "Select a valid label.");

        var project = domain is null || !ProjectId.HasValue
            ? null
            : await ReadProjectAsync(cn, ProjectId.Value, domain);
        if (ProjectId.HasValue && project is null)
            ModelState.AddModelError(nameof(ProjectId), "Select a project belonging to the selected label.");

        if (!string.IsNullOrWhiteSpace(ManagerSamAccountName)
            && !Managers.Any(x => string.Equals(x.SamAccountName, ManagerSamAccountName, StringComparison.OrdinalIgnoreCase)))
            ModelState.AddModelError(nameof(ManagerSamAccountName), "Select a manager belonging to the selected label and allowed to be a manager.");

        if (!string.IsNullOrWhiteSpace(Title)
            && Titles.Count > 0
            && !Titles.Contains(Title, StringComparer.OrdinalIgnoreCase))
            ModelState.AddModelError(nameof(Title), "Select a title from the database.");

        if (!ModelState.IsValid) return Page();

        await using var tx = (SqlTransaction)await cn.BeginTransactionAsync(HttpContext.RequestAborted);
        try
        {
            var employeeId = await ResolveEmployeeAsync(cn, tx);
            var employee = await ReadEmployeeAsync(cn, employeeId, tx)
                ?? throw new InvalidOperationException("The employee could not be loaded.");
            var overlaps = await FindOverlapsAsync(cn, tx, employeeId, startDate!.Value, endDate);
            var overlapStatus = overlaps.Count == 0 ? "None" : "ReviewRequired";
            var identityChange = !string.IsNullOrWhiteSpace(employee.UserPrincipalName)
                && !employee.UserPrincipalName.EndsWith("@" + domain!.Domain, StringComparison.OrdinalIgnoreCase);
            var proposedUpn = BuildMailLocalPart(GivenName, Surname) + "@" + domain!.Domain;
            var requestType = employee.ObjectGuid.HasValue ? "UPDATE" : "CREATE";

            await using var cmd = cn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = @"
INSERT INTO dbo.ADUserChangeQueue
(
    RequestType, Status, ExecuteAfter, TargetObjectGUID, TargetSamAccountName,
    NewUserPrincipalName, NewDisplayName, NewGivenName, NewSurname,
    ManagerSamAccountName, Department, Title, EmployeeType,
    Company, Office, Mail, PrivateEmail, MobilePhone, RequestedBy,
    EmployeeId, RequestCategory, ProjectId, StartDate, EndDate,
    AssignmentLabel, AssignmentDomain, ProjectNumber, ProjectName,
    OverlapStatus, OverlapDetails, RequiresIdentityChange
)
OUTPUT INSERTED.RequestId
VALUES
(
    @RequestType, N'Pending', @StartDate, @TargetObjectGuid, @TargetSam,
    @NewUpn, @DisplayName, @GivenName, @Surname,
    @Manager, @Department, @Title, @EmployeeType,
    @Company, @Office, @Mail, @PrivateEmail, @MobilePhone, @RequestedBy,
    @EmployeeId, N'NewAssignment', @ProjectId, @StartDate, @EndDate,
    @Label, @Domain, @ProjectNumber, @ProjectName,
    @OverlapStatus, @OverlapDetails, @RequiresIdentityChange
);";
            cmd.Parameters.AddNVarChar("@RequestType", requestType, 20);
            cmd.Parameters.Add(new SqlParameter("@StartDate", System.Data.SqlDbType.Date) { Value = startDate.Value });
            cmd.Parameters.Add(new SqlParameter("@EndDate", System.Data.SqlDbType.Date) { Value = (object?)endDate ?? DBNull.Value });
            cmd.Parameters.Add(new SqlParameter("@TargetObjectGuid", System.Data.SqlDbType.UniqueIdentifier) { Value = (object?)employee.ObjectGuid ?? DBNull.Value });
            cmd.Parameters.AddNVarChar("@TargetSam", employee.SamAccountName, 256);
            cmd.Parameters.AddNVarChar("@NewUpn", proposedUpn, 320);
            cmd.Parameters.AddNVarChar("@DisplayName", $"{GivenName} {Surname}".Trim(), 300);
            cmd.Parameters.AddNVarChar("@GivenName", GivenName, 200);
            cmd.Parameters.AddNVarChar("@Surname", Surname, 200);
            cmd.Parameters.AddNVarChar("@Manager", ManagerSamAccountName, 256);
            cmd.Parameters.AddNVarChar("@Department", Department, 300);
            cmd.Parameters.AddNVarChar("@Title", Title, 300);
            cmd.Parameters.AddNVarChar("@EmployeeType", EmployeeType, 100);
            cmd.Parameters.AddNVarChar("@Company", project?.Company ?? domain.Company, 300);
            cmd.Parameters.AddNVarChar("@Office", Office ?? domain.Office, 300);
            cmd.Parameters.AddNVarChar("@Mail", proposedUpn, 320);
            cmd.Parameters.AddNVarChar("@PrivateEmail", PrivateEmail, 320);
            cmd.Parameters.AddNVarChar("@MobilePhone", MobilePhone, 100);
            cmd.Parameters.AddNVarChar("@RequestedBy", User.Identity?.Name ?? Environment.UserName, 300);
            cmd.Parameters.Add(new SqlParameter("@EmployeeId", System.Data.SqlDbType.BigInt) { Value = employeeId });
            cmd.Parameters.Add(new SqlParameter("@ProjectId", System.Data.SqlDbType.Int) { Value = (object?)project?.Id ?? DBNull.Value });
            cmd.Parameters.AddNVarChar("@Label", domain.Label, 300);
            cmd.Parameters.AddNVarChar("@Domain", domain.Domain, 320);
            cmd.Parameters.AddNVarChar("@ProjectNumber", project?.ProjectNumber, 100);
            cmd.Parameters.AddNVarChar("@ProjectName", project?.ProjectName, 300);
            cmd.Parameters.AddNVarChar("@OverlapStatus", overlapStatus, 30);
            cmd.Parameters.AddNVarCharMax("@OverlapDetails", overlaps.Count == 0 ? null : JsonSerializer.Serialize(overlaps));
            cmd.Parameters.AddBit("@RequiresIdentityChange", identityChange);
            var requestId = Convert.ToInt64(await cmd.ExecuteScalarAsync(HttpContext.RequestAborted));

            await tx.CommitAsync(HttpContext.RequestAborted);
            return RedirectToPage("/Approvals", new { requestId });
        }
        catch (Exception ex)
        {
            await tx.RollbackAsync(HttpContext.RequestAborted);
            ErrorMessage = ex.Message;
            return Page();
        }
    }

    private async Task LoadOptionsAsync()
    {
        Domains.Clear();
        Projects.Clear();
        Managers.Clear();
        EmployeeTypes.Clear();
        Titles.Clear();

        await using var cn = await _connectionFactory.OpenAsync(HttpContext.RequestAborted);
        await using var cmd = cn.CreateCommand();
        cmd.CommandText = @"
SELECT [domain], ISNULL(NULLIF(Label, N''), [domain]), ISNULL(company, N''), ISNULL(Office, N'')
FROM dbo.domains
ORDER BY ISNULL(NULLIF(Label, N''), [domain]);

DECLARE @Company nvarchar(300), @Label nvarchar(300);
SELECT TOP (1)
    @Company = ISNULL(company, N''),
    @Label = ISNULL(NULLIF(Label, N''), [domain])
FROM dbo.domains
WHERE LOWER([domain]) = LOWER(@SelectedDomain);

SELECT Id, ISNULL(ProjectNumber, N''), ISNULL(ProjectName, N''), ISNULL(Company, N'')
FROM dbo.Projects
WHERE Active = 1
  AND @SelectedDomain <> N''
  AND (Company = @Company OR Company = @Label)
ORDER BY ProjectName, ProjectNumber;

SELECT DISTINCT
    a.SamAccountName,
    ISNULL(NULLIF(a.DisplayName, N''), a.SamAccountName)
FROM dbo.ADObjects AS a
INNER JOIN dbo.Employeetype AS et
    ON et.employeetype = a.EmployeeType
   AND ISNULL(et.CanBeManager, 0) = 1
WHERE a.IsDeleted = 0
  AND a.Enabled = 1
  AND a.SamAccountName IS NOT NULL
  AND @SelectedDomain <> N''
  AND (a.Company = @Company OR a.Company = @Label)
ORDER BY ISNULL(NULLIF(a.DisplayName, N''), a.SamAccountName);

SELECT employeetype, ISNULL(enddate, 0)
FROM dbo.Employeetype
ORDER BY employeetype;

SELECT Title
FROM dbo.Titles
WHERE ISNULL(IsActive, 1) = 1
ORDER BY Title;";
        cmd.Parameters.AddNVarChar("@SelectedDomain", SelectedDomain ?? string.Empty, 320);

        await using var reader = await cmd.ExecuteReaderAsync(HttpContext.RequestAborted);
        while (await reader.ReadAsync(HttpContext.RequestAborted))
            Domains.Add(new DomainOption(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3)));

        await reader.NextResultAsync(HttpContext.RequestAborted);
        while (await reader.ReadAsync(HttpContext.RequestAborted))
            Projects.Add(new ProjectOption(reader.GetInt32(0), reader.GetString(1), reader.GetString(2), reader.GetString(3)));

        await reader.NextResultAsync(HttpContext.RequestAborted);
        while (await reader.ReadAsync(HttpContext.RequestAborted))
            Managers.Add(new ManagerOption(reader.GetString(0), reader.GetString(1)));

        await reader.NextResultAsync(HttpContext.RequestAborted);
        while (await reader.ReadAsync(HttpContext.RequestAborted))
            EmployeeTypes.Add(new EmployeeTypeOption(reader.GetString(0), Convert.ToBoolean(reader.GetValue(1))));

        await reader.NextResultAsync(HttpContext.RequestAborted);
        while (await reader.ReadAsync(HttpContext.RequestAborted))
            Titles.Add(reader.GetString(0));
    }

    private async Task<long> ResolveEmployeeAsync(SqlConnection cn, SqlTransaction tx)
    {
        if (SelectedPersonId.HasValue)
        {
            await using var verify = cn.CreateCommand();
            verify.Transaction = tx;
            verify.CommandText = "SELECT COUNT(*) FROM dbo.Employees WHERE EmployeeId=@Id AND Status<>N'Merged';";
            verify.Parameters.Add(new SqlParameter("@Id", System.Data.SqlDbType.BigInt) { Value = SelectedPersonId.Value });
            if (Convert.ToInt32(await verify.ExecuteScalarAsync(HttpContext.RequestAborted)) != 1)
                throw new InvalidOperationException("The selected employee no longer exists.");
            return SelectedPersonId.Value;
        }

        if (SelectedArchiveRequestId.HasValue)
        {
            var archive = await ReadArchiveAsync(cn, SelectedArchiveRequestId.Value, tx)
                ?? throw new InvalidOperationException("The selected archived employee could not be found.");
            GivenName = archive.GivenName;
            Surname = archive.Surname;
            PrivateEmail ??= archive.PrivateEmail;
            MobilePhone ??= archive.MobilePhone;
            return await InsertEmployeeAsync(cn, tx, archive.ObjectGuid, archive.SamAccountName, archive.UserPrincipalName);
        }
        return await InsertEmployeeAsync(cn, tx, null, null, null);
    }

    private async Task<long> InsertEmployeeAsync(SqlConnection cn, SqlTransaction tx, Guid? objectGuid, string? sam, string? upn)
    {
        await using var cmd = cn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = @"
INSERT dbo.Employees
(CanonicalGivenName, CanonicalSurname, PrivateEmail, NormalizedPrivateEmail,
 MobilePhone, NormalizedMobilePhone, CurrentADObjectGuid, CurrentSamAccountName,
 CurrentUPN, Status, CreatedBy, UpdatedBy)
OUTPUT INSERTED.EmployeeId
VALUES
(@GivenName, @Surname, @PrivateEmail, @NormalizedPrivateEmail,
 @MobilePhone, @NormalizedMobilePhone, @ObjectGuid, @Sam, @Upn,
 CASE WHEN @ObjectGuid IS NULL THEN N'Prospective' ELSE N'Inactive' END,
 @ChangedBy, @ChangedBy);";
        cmd.Parameters.AddNVarChar("@GivenName", GivenName, 200);
        cmd.Parameters.AddNVarChar("@Surname", Surname, 200);
        cmd.Parameters.AddNVarChar("@PrivateEmail", PrivateEmail, 320);
        cmd.Parameters.AddNVarChar("@NormalizedPrivateEmail", PersonMatchingService.NormalizeEmail(PrivateEmail), 320);
        cmd.Parameters.AddNVarChar("@MobilePhone", MobilePhone, 100);
        cmd.Parameters.AddNVarChar("@NormalizedMobilePhone", PersonMatchingService.NormalizePhone(MobilePhone), 50);
        cmd.Parameters.Add(new SqlParameter("@ObjectGuid", System.Data.SqlDbType.UniqueIdentifier) { Value = (object?)objectGuid ?? DBNull.Value });
        cmd.Parameters.AddNVarChar("@Sam", sam, 256);
        cmd.Parameters.AddNVarChar("@Upn", upn, 320);
        cmd.Parameters.AddNVarChar("@ChangedBy", User.Identity?.Name ?? Environment.UserName, 300);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(HttpContext.RequestAborted));
    }

    private static async Task<EmployeeDetails?> ReadEmployeeAsync(SqlConnection cn, long employeeId, SqlTransaction? tx = null)
    {
        await using var cmd = cn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = @"
SELECT CanonicalGivenName, CanonicalSurname, PrivateEmail, MobilePhone,
       CurrentUPN, CurrentADObjectGuid, CurrentSamAccountName
FROM dbo.Employees WHERE EmployeeId=@Id;";
        cmd.Parameters.Add(new SqlParameter("@Id", System.Data.SqlDbType.BigInt) { Value = employeeId });
        await using var r = await cmd.ExecuteReaderAsync();
        if (!await r.ReadAsync()) return null;
        return new EmployeeDetails(
            r.IsDBNull(0) ? "" : r.GetString(0), r.IsDBNull(1) ? "" : r.GetString(1),
            r.IsDBNull(2) ? null : r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3),
            r.IsDBNull(4) ? null : r.GetString(4), r.IsDBNull(5) ? null : r.GetGuid(5),
            r.IsDBNull(6) ? null : r.GetString(6));
    }

    private static async Task<EmployeeDetails?> ReadArchiveAsync(SqlConnection cn, long requestId, SqlTransaction? tx = null)
    {
        await using var cmd = cn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = @"
SELECT NewGivenName, NewSurname, PrivateEmail, MobilePhone, NewUserPrincipalName,
       TargetObjectGUID, COALESCE(TargetSamAccountName, NewSamAccountName)
FROM dbo.ADUserChangeQueue WHERE RequestId=@Id;";
        cmd.Parameters.Add(new SqlParameter("@Id", System.Data.SqlDbType.BigInt) { Value = requestId });
        await using var r = await cmd.ExecuteReaderAsync();
        if (!await r.ReadAsync()) return null;
        return new EmployeeDetails(
            r.IsDBNull(0) ? "" : r.GetString(0), r.IsDBNull(1) ? "" : r.GetString(1),
            r.IsDBNull(2) ? null : r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3),
            r.IsDBNull(4) ? null : r.GetString(4), r.IsDBNull(5) ? null : r.GetGuid(5),
            r.IsDBNull(6) ? null : r.GetString(6));
    }

    private static async Task<DomainOption?> ReadDomainAsync(SqlConnection cn, string? domain)
    {
        if (string.IsNullOrWhiteSpace(domain)) return null;
        await using var cmd = cn.CreateCommand();
        cmd.CommandText = @"SELECT TOP 1 [domain], ISNULL(NULLIF(Label,N''),[domain]), ISNULL(company,N''), ISNULL(Office,N'') FROM dbo.domains WHERE LOWER([domain])=LOWER(@Domain);";
        cmd.Parameters.AddNVarChar("@Domain", domain, 320);
        await using var r = await cmd.ExecuteReaderAsync();
        return await r.ReadAsync() ? new DomainOption(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3)) : null;
    }

    private static async Task<ProjectOption?> ReadProjectAsync(SqlConnection cn, int id, DomainOption domain)
    {
        await using var cmd = cn.CreateCommand();
        cmd.CommandText = @"SELECT TOP 1 Id, ProjectNumber, ProjectName, Company FROM dbo.Projects WHERE Id=@Id AND Active=1 AND (Company=@Company OR Company=@Label);";
        cmd.Parameters.Add(new SqlParameter("@Id", System.Data.SqlDbType.Int) { Value = id });
        cmd.Parameters.AddNVarChar("@Company", domain.Company, 300);
        cmd.Parameters.AddNVarChar("@Label", domain.Label, 300);
        await using var r = await cmd.ExecuteReaderAsync();
        return await r.ReadAsync() ? new ProjectOption(r.GetInt32(0), r.IsDBNull(1) ? "" : r.GetString(1), r.IsDBNull(2) ? "" : r.GetString(2), r.IsDBNull(3) ? "" : r.GetString(3)) : null;
    }

    private static async Task<List<object>> FindOverlapsAsync(SqlConnection cn, SqlTransaction tx, long employeeId, DateTime start, DateTime? end)
    {
        var rows = new List<object>();
        await using var cmd = cn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = @"
SELECT AssignmentId, ProjectNumber, ProjectName, StartDate, EndDate
FROM dbo.Assignments
WHERE EmployeeId=@EmployeeId
  AND @StartDate <= ISNULL(EndDate, CONVERT(date,'99991231'))
  AND ISNULL(@EndDate, CONVERT(date,'99991231')) >= StartDate
ORDER BY StartDate;";
        cmd.Parameters.Add(new SqlParameter("@EmployeeId", System.Data.SqlDbType.BigInt) { Value = employeeId });
        cmd.Parameters.Add(new SqlParameter("@StartDate", System.Data.SqlDbType.Date) { Value = start });
        cmd.Parameters.Add(new SqlParameter("@EndDate", System.Data.SqlDbType.Date) { Value = (object?)end ?? DBNull.Value });
        await using var r = await cmd.ExecuteReaderAsync();
while (await r.ReadAsync())
{
    rows.Add(new
    {
        assignmentId = r.GetInt64(0),
        projectNumber = r.IsDBNull(1) ? null : r.GetString(1),
        projectName = r.IsDBNull(2) ? null : r.GetString(2),
        startDate = r.GetDateTime(3),
        endDate = r.IsDBNull(4)
            ? (DateTime?)null
            : r.GetDateTime(4)
    });
}
        return rows;
    }

    private void NormalizePostedValues()
    {
        GivenName = GivenName.Trim();
        Surname = Surname.Trim();
        PrivateEmail = NullIfWhiteSpace(PrivateEmail);
        MobilePhone = NullIfWhiteSpace(MobilePhone);
        SelectedDomain = NullIfWhiteSpace(SelectedDomain)?.ToLowerInvariant();
        ManagerSamAccountName = NullIfWhiteSpace(ManagerSamAccountName);
        Office = NullIfWhiteSpace(Office);
        Department = NullIfWhiteSpace(Department);
        Title = NullIfWhiteSpace(Title);
        EmployeeType = NullIfWhiteSpace(EmployeeType);
    }

    private static bool TryParseDate(string? value, bool required, out DateTime? result)
    {
        result = null;
        if (string.IsNullOrWhiteSpace(value)) return !required;
        if (!DateTime.TryParseExact(value.Trim(), DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)) return false;
        result = parsed.Date;
        return true;
    }

    private static string? NullIfWhiteSpace(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static string BuildMailLocalPart(string given, string surname) => PersonMatchingService.NormalizeName(given, surname).Replace(' ', '.');
    private static string ExtractSamAccountName(string? value) => string.IsNullOrWhiteSpace(value) ? "" : value.Contains('\\') ? value.Split('\\').Last() : value.Contains('@') ? value.Split('@')[0] : value;

    public sealed record DomainOption(string Domain, string Label, string Company, string Office);
    public sealed record ProjectOption(int Id, string ProjectNumber, string ProjectName, string Company);
    public sealed record ManagerOption(string SamAccountName, string DisplayName);
    public sealed record EmployeeTypeOption(string Name, bool RequiresEndDate);
    private sealed record EmployeeDetails(string GivenName, string Surname, string? PrivateEmail, string? MobilePhone, string? UserPrincipalName, Guid? ObjectGuid, string? SamAccountName)
    {
        public string DisplayName => $"{GivenName} {Surname}".Trim();
    }
    private sealed class EmployeeMatchRow
    {
        public long? PersonId { get; init; }
        public long? ArchiveRequestId { get; init; }
        public string DisplayName { get; init; } = "";
        public string GivenName { get; init; } = "";
        public string Surname { get; init; } = "";
        public string? PrivateEmail { get; init; }
        public string? MobilePhone { get; init; }
        public string? UserPrincipalName { get; init; }
        public bool EmailMatch { get; init; }
        public bool PhoneMatch { get; init; }
        public bool ExactNameMatch { get; init; }
    }
}

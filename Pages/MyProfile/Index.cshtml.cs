using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Data.SqlClient;
using UserChangeQueueWeb.Services;

namespace UserChangeQueueWeb.Pages.MyProfile;

[Authorize]
public sealed class IndexModel : PageModel
{
    private readonly SqlConnectionFactory _connectionFactory;
    public IndexModel(SqlConnectionFactory connectionFactory) => _connectionFactory = connectionFactory;

    public bool EmployeeFound { get; private set; }
    public EmployeeProfile? Profile { get; private set; }
    public List<UpcomingAssignment> Assignments { get; private set; } = new();

    public async Task OnGetAsync()
    {
        var samAccountName = ObjectAccessService.ExtractSamAccountName(User.Identity?.Name ?? "");
        if (string.IsNullOrWhiteSpace(samAccountName))
        {
            return;
        }

        await using var cn = await _connectionFactory.OpenAsync(HttpContext.RequestAborted);

        Profile = await LoadProfileAsync(cn, samAccountName);
        if (Profile is null)
        {
            EmployeeFound = false;
            return;
        }

        EmployeeFound = true;

        var confirmed = await LoadUpcomingAssignmentsAsync(cn, Profile.EmployeeId, Profile.CurrentManagerSamAccountName);
        var pending = await LoadPendingAssignmentRequestsAsync(cn, Profile.EmployeeId, Profile.CurrentManagerSamAccountName);

        Assignments = confirmed.Concat(pending)
            .OrderBy(a => a.StartDate ?? DateTime.MaxValue)
            .ToList();
    }

    private async Task<EmployeeProfile?> LoadProfileAsync(SqlConnection cn, string samAccountName)
    {
        await using var cmd = cn.CreateCommand();
        cmd.Parameters.AddNVarChar("@SamAccountName", samAccountName, 256);
        cmd.CommandText = @"
SELECT
    e.EmployeeId, e.CanonicalGivenName, e.CanonicalSurname, e.PrivateEmail, e.MobilePhone,
    e.CurrentSamAccountName, e.CurrentUPN, e.Status,
    ISNULL(ad.Title, N''), ISNULL(ad.Department, N''), ISNULL(ad.Office, N''), ISNULL(ad.Company, N''),
    ISNULL(ad.ManagerSamAccountName, N''),
    COALESCE(mgr.DisplayName, ad.ManagerSamAccountName, N'')
FROM dbo.Employees e
LEFT JOIN dbo.ADObjects ad ON ad.ObjectGUID = e.CurrentADObjectGuid
LEFT JOIN dbo.ADObjects mgr ON mgr.SamAccountName = ad.ManagerSamAccountName
WHERE e.CurrentSamAccountName = @SamAccountName
  AND e.Status <> N'Merged';";

        await using var reader = await cmd.ExecuteReaderAsync(HttpContext.RequestAborted);
        if (!await reader.ReadAsync(HttpContext.RequestAborted))
        {
            return null;
        }

        return new EmployeeProfile
        {
            EmployeeId = reader.GetInt64(0),
            GivenName = reader.GetString(1),
            Surname = reader.GetString(2),
            PrivateEmail = Get(reader, 3),
            MobilePhone = Get(reader, 4),
            CurrentSamAccountName = Get(reader, 5),
            CurrentUPN = Get(reader, 6),
            Status = reader.GetString(7),
            Title = Get(reader, 8),
            Department = Get(reader, 9),
            Office = Get(reader, 10),
            Company = Get(reader, 11),
            CurrentManagerSamAccountName = Get(reader, 12),
            CurrentManagerDisplayName = Get(reader, 13)
        };
    }

    private async Task<List<UpcomingAssignment>> LoadUpcomingAssignmentsAsync(
        SqlConnection cn, long employeeId, string currentManagerSamAccountName)
    {
        var results = new List<UpcomingAssignment>();

        await using var cmd = cn.CreateCommand();
        cmd.Parameters.Add(new SqlParameter("@EmployeeId", System.Data.SqlDbType.BigInt) { Value = employeeId });
        cmd.CommandText = @"
SELECT
    a.AssignmentId, a.Label, a.Domain, a.ProjectNumber, a.ProjectName, a.Company,
    a.Office, a.Department, a.Title, a.StartDate, a.EndDate, a.Status,
    ISNULL(a.ManagerSamAccountName, N''), COALESCE(mgr.DisplayName, a.ManagerSamAccountName, N'')
FROM dbo.Assignments a
LEFT JOIN dbo.ADObjects mgr ON mgr.SamAccountName = a.ManagerSamAccountName
WHERE a.EmployeeId = @EmployeeId
  AND (a.EndDate IS NULL OR a.EndDate >= CAST(SYSDATETIME() AS date))
ORDER BY a.StartDate;";
        // Deliberately not filtering by Label/Domain -- an employee should see every
        // assignment that applies to them, regardless of which label it's under.

        await using var reader = await cmd.ExecuteReaderAsync(HttpContext.RequestAborted);
        while (await reader.ReadAsync(HttpContext.RequestAborted))
        {
            var managerSam = Get(reader, 12);
            results.Add(new UpcomingAssignment
            {
                AssignmentId = reader.GetInt64(0),
                Label = Get(reader, 1),
                Domain = Get(reader, 2),
                ProjectNumber = Get(reader, 3),
                ProjectName = Get(reader, 4),
                Company = Get(reader, 5),
                Office = Get(reader, 6),
                Department = Get(reader, 7),
                Title = Get(reader, 8),
                StartDate = reader.GetDateTime(9),
                EndDate = reader.IsDBNull(10) ? null : reader.GetDateTime(10),
                Status = Get(reader, 11),
                ManagerSamAccountName = managerSam,
                ManagerDisplayName = Get(reader, 13),
                IsNewManager = !string.IsNullOrWhiteSpace(managerSam)
                    && !string.Equals(managerSam, currentManagerSamAccountName, StringComparison.OrdinalIgnoreCase)
            });
        }

        return results;
    }

    private async Task<List<UpcomingAssignment>> LoadPendingAssignmentRequestsAsync(
        SqlConnection cn, long employeeId, string currentManagerSamAccountName)
    {
        var results = new List<UpcomingAssignment>();

        await using var cmd = cn.CreateCommand();
        cmd.Parameters.Add(new SqlParameter("@EmployeeId", System.Data.SqlDbType.BigInt) { Value = employeeId });
        cmd.CommandText = @"
SELECT
    q.RequestId,
    ISNULL(q.AssignmentLabel, N''), ISNULL(q.AssignmentDomain, N''),
    ISNULL(q.ProjectNumber, N''), ISNULL(q.ProjectName, N''), ISNULL(q.Company, N''),
    ISNULL(q.Office, N''), ISNULL(q.Department, N''), ISNULL(q.Title, N''),
    COALESCE(q.StartDate, q.AssignmentStartDate) AS EffectiveStartDate,
    COALESCE(q.EndDate, q.AssignmentEndDate) AS EffectiveEndDate,
    q.Status,
    ISNULL(q.ManagerSamAccountName, N''), COALESCE(mgr.DisplayName, q.ManagerSamAccountName, N'')
FROM dbo.ADUserChangeQueue q
LEFT JOIN dbo.ADObjects mgr ON mgr.SamAccountName = q.ManagerSamAccountName
WHERE q.EmployeeId = @EmployeeId
  AND q.RequestCategory = N'NewAssignment'
  AND q.Status IN (N'Pending', N'Approved')
  -- Current Create.cshtml.cs writes StartDate/EndDate; older rows may only have
  -- AssignmentStartDate/AssignmentEndDate populated -- COALESCE covers both.
ORDER BY COALESCE(q.StartDate, q.AssignmentStartDate);";

        await using var reader = await cmd.ExecuteReaderAsync(HttpContext.RequestAborted);
        while (await reader.ReadAsync(HttpContext.RequestAborted))
        {
            var managerSam = Get(reader, 12);
            results.Add(new UpcomingAssignment
            {
                RequestId = reader.GetInt64(0),
                Label = Get(reader, 1),
                Domain = Get(reader, 2),
                ProjectNumber = Get(reader, 3),
                ProjectName = Get(reader, 4),
                Company = Get(reader, 5),
                Office = Get(reader, 6),
                Department = Get(reader, 7),
                Title = Get(reader, 8),
                StartDate = reader.IsDBNull(9) ? null : reader.GetDateTime(9),
                EndDate = reader.IsDBNull(10) ? null : reader.GetDateTime(10),
                Status = Get(reader, 11),
                ManagerSamAccountName = managerSam,
                ManagerDisplayName = Get(reader, 13),
                IsNewManager = !string.IsNullOrWhiteSpace(managerSam)
                    && !string.Equals(managerSam, currentManagerSamAccountName, StringComparison.OrdinalIgnoreCase)
            });
        }

        return results;
    }

    private static string Get(SqlDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? "" : reader.GetString(ordinal);

    public sealed class EmployeeProfile
    {
        public long EmployeeId { get; init; }
        public string GivenName { get; init; } = "";
        public string Surname { get; init; } = "";
        public string DisplayName => $"{GivenName} {Surname}".Trim();
        public string PrivateEmail { get; init; } = "";
        public string MobilePhone { get; init; } = "";
        public string CurrentSamAccountName { get; init; } = "";
        public string CurrentUPN { get; init; } = "";
        public string Status { get; init; } = "";
        public string Title { get; init; } = "";
        public string Department { get; init; } = "";
        public string Office { get; init; } = "";
        public string Company { get; init; } = "";
        public string CurrentManagerSamAccountName { get; init; } = "";
        public string CurrentManagerDisplayName { get; init; } = "";
    }

    public sealed class UpcomingAssignment
    {
        public long? AssignmentId { get; init; }
        public long? RequestId { get; init; }
        public bool IsPendingRequest => RequestId.HasValue;
        public string Label { get; init; } = "";
        public string Domain { get; init; } = "";
        public string ProjectNumber { get; init; } = "";
        public string ProjectName { get; init; } = "";
        public string Company { get; init; } = "";
        public string Office { get; init; } = "";
        public string Department { get; init; } = "";
        public string Title { get; init; } = "";
        public DateTime? StartDate { get; init; }
        public DateTime? EndDate { get; init; }
        public string Status { get; init; } = "";
        public string ManagerSamAccountName { get; init; } = "";
        public string ManagerDisplayName { get; init; } = "";
        public bool IsNewManager { get; init; }
        public bool IsFuture => StartDate.HasValue && StartDate.Value.Date > DateTime.Today;
    }
}

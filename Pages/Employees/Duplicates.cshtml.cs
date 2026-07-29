using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Data.SqlClient;
using UserChangeQueueWeb.Services;

namespace UserChangeQueueWeb.Pages.Employees;

[Authorize]
public sealed class DuplicatesModel : PageModel
{
    private readonly SqlConnectionFactory _connectionFactory;
    public DuplicatesModel(SqlConnectionFactory connectionFactory) => _connectionFactory = connectionFactory;

    [BindProperty] public long RetainedEmployeeId { get; set; }
    [BindProperty] public long MergedEmployeeId { get; set; }
    [BindProperty] public string? Reason { get; set; }
    [TempData] public string? StatusMessage { get; set; }
    public List<DuplicatePair> Pairs { get; } = new();

    public async Task OnGetAsync() => await LoadAsync();

    public async Task<IActionResult> OnPostMergeAsync()
    {
        if (RetainedEmployeeId <= 0 || MergedEmployeeId <= 0 || RetainedEmployeeId == MergedEmployeeId)
        {
            StatusMessage = "Select two different employees.";
            return RedirectToPage();
        }

        await using var cn = await _connectionFactory.OpenAsync(HttpContext.RequestAborted);
        await using var cmd = cn.CreateCommand();
        cmd.CommandText = "dbo.MergeEmployees";
        cmd.CommandType = System.Data.CommandType.StoredProcedure;
        cmd.Parameters.Add(new SqlParameter("@RetainedEmployeeId", System.Data.SqlDbType.BigInt) { Value = RetainedEmployeeId });
        cmd.Parameters.Add(new SqlParameter("@MergedEmployeeId", System.Data.SqlDbType.BigInt) { Value = MergedEmployeeId });
        cmd.Parameters.AddNVarChar("@MergedBy", User.Identity?.Name ?? Environment.UserName, 300);
        cmd.Parameters.AddNVarChar("@Reason", Reason, 1000);
        await cmd.ExecuteNonQueryAsync(HttpContext.RequestAborted);
        StatusMessage = $"Employee {MergedEmployeeId} was merged into employee {RetainedEmployeeId}.";
        return RedirectToPage();
    }

    private async Task LoadAsync()
    {
        await using var cn = await _connectionFactory.OpenAsync(HttpContext.RequestAborted);
        await using var cmd = cn.CreateCommand();
        cmd.CommandText = @"
WITH candidates AS
(
    SELECT
        a.EmployeeId AS EmployeeId1,
        b.EmployeeId AS EmployeeId2,
        CASE WHEN NULLIF(a.NormalizedPrivateEmail,N'') = NULLIF(b.NormalizedPrivateEmail,N'') THEN 1 ELSE 0 END AS EmailMatch,
        CASE WHEN NULLIF(a.NormalizedMobilePhone,N'') = NULLIF(b.NormalizedMobilePhone,N'') THEN 1 ELSE 0 END AS PhoneMatch,
        CASE WHEN LOWER(CONCAT(a.CanonicalGivenName,N' ',a.CanonicalSurname)) = LOWER(CONCAT(b.CanonicalGivenName,N' ',b.CanonicalSurname)) THEN 1 ELSE 0 END AS NameMatch
    FROM dbo.Employees a
    JOIN dbo.Employees b ON b.EmployeeId > a.EmployeeId
    WHERE a.Status <> N'Merged' AND b.Status <> N'Merged'
      AND
      (
          (NULLIF(a.NormalizedPrivateEmail,N'') IS NOT NULL AND a.NormalizedPrivateEmail=b.NormalizedPrivateEmail)
       OR (NULLIF(a.NormalizedMobilePhone,N'') IS NOT NULL AND a.NormalizedMobilePhone=b.NormalizedMobilePhone)
       OR (LOWER(CONCAT(a.CanonicalGivenName,N' ',a.CanonicalSurname)) = LOWER(CONCAT(b.CanonicalGivenName,N' ',b.CanonicalSurname)))
      )
)
SELECT TOP (200)
    a.EmployeeId, CONCAT(a.CanonicalGivenName,N' ',a.CanonicalSurname), a.PrivateEmail, a.MobilePhone, a.CurrentUPN,
    b.EmployeeId, CONCAT(b.CanonicalGivenName,N' ',b.CanonicalSurname), b.PrivateEmail, b.MobilePhone, b.CurrentUPN,
    c.EmailMatch, c.PhoneMatch, c.NameMatch,
    (c.EmailMatch*100 + c.PhoneMatch*100 + c.NameMatch*35) AS Score
FROM candidates c
JOIN dbo.Employees a ON a.EmployeeId=c.EmployeeId1
JOIN dbo.Employees b ON b.EmployeeId=c.EmployeeId2
ORDER BY Score DESC, a.EmployeeId, b.EmployeeId;";

        await using var reader = await cmd.ExecuteReaderAsync(HttpContext.RequestAborted);
        while (await reader.ReadAsync(HttpContext.RequestAborted))
        {
            Pairs.Add(new DuplicatePair
            {
                EmployeeId1 = reader.GetInt64(0), Name1 = reader.GetString(1), Email1 = Get(reader,2), Phone1 = Get(reader,3), Upn1 = Get(reader,4),
                EmployeeId2 = reader.GetInt64(5), Name2 = reader.GetString(6), Email2 = Get(reader,7), Phone2 = Get(reader,8), Upn2 = Get(reader,9),
                EmailMatch = reader.GetInt32(10)==1, PhoneMatch = reader.GetInt32(11)==1, NameMatch = reader.GetInt32(12)==1, Score = reader.GetInt32(13)
            });
        }
    }

    private static string Get(SqlDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? "" : reader.GetString(ordinal);

    public sealed class DuplicatePair
    {
        public long EmployeeId1 { get; init; } public string Name1 { get; init; }=""; public string Email1 { get; init; }=""; public string Phone1 { get; init; }=""; public string Upn1 { get; init; }="";
        public long EmployeeId2 { get; init; } public string Name2 { get; init; }=""; public string Email2 { get; init; }=""; public string Phone2 { get; init; }=""; public string Upn2 { get; init; }="";
        public bool EmailMatch { get; init; } public bool PhoneMatch { get; init; } public bool NameMatch { get; init; } public int Score { get; init; }
    }
}

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
    [BindProperty] public long DismissEmployeeId1 { get; set; }
    [BindProperty] public long DismissEmployeeId2 { get; set; }
    [BindProperty] public string? DismissReason { get; set; }
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

    public async Task<IActionResult> OnPostDismissAsync()
    {
        if (DismissEmployeeId1 <= 0 || DismissEmployeeId2 <= 0 || DismissEmployeeId1 == DismissEmployeeId2)
        {
            StatusMessage = "Select two different employees.";
            return RedirectToPage();
        }

        // EmployeeDuplicateDismissals always stores the smaller EmployeeId first (see its
        // CK_EmployeeDuplicateDismissals_Order check constraint), regardless of which order
        // the pair appears in on this page.
        var lowerId = Math.Min(DismissEmployeeId1, DismissEmployeeId2);
        var higherId = Math.Max(DismissEmployeeId1, DismissEmployeeId2);

        await using var cn = await _connectionFactory.OpenAsync(HttpContext.RequestAborted);
        await using var cmd = cn.CreateCommand();
        cmd.Parameters.Add(new SqlParameter("@EmployeeId1", System.Data.SqlDbType.BigInt) { Value = lowerId });
        cmd.Parameters.Add(new SqlParameter("@EmployeeId2", System.Data.SqlDbType.BigInt) { Value = higherId });
        cmd.Parameters.AddNVarChar("@DismissedBy", User.Identity?.Name ?? Environment.UserName, 300);
        cmd.Parameters.AddNVarChar("@Reason", DismissReason, 1000);
        cmd.CommandText = @"
IF NOT EXISTS
(
    SELECT 1 FROM dbo.EmployeeDuplicateDismissals
    WHERE EmployeeId1 = @EmployeeId1 AND EmployeeId2 = @EmployeeId2
)
BEGIN
    INSERT INTO dbo.EmployeeDuplicateDismissals (EmployeeId1, EmployeeId2, DismissedBy, Reason)
    VALUES (@EmployeeId1, @EmployeeId2, @DismissedBy, @Reason);
END;";
        await cmd.ExecuteNonQueryAsync(HttpContext.RequestAborted);
        StatusMessage = $"Employees {lowerId} and {higherId} were marked as not duplicates.";
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
        CASE WHEN LOWER(CONCAT(a.CanonicalGivenName,N' ',a.CanonicalSurname)) = LOWER(CONCAT(b.CanonicalGivenName,N' ',b.CanonicalSurname)) THEN 1 ELSE 0 END AS NameMatch,
        CASE WHEN NULLIF(telA.Value,N'') = NULLIF(telB.Value,N'') THEN 1 ELSE 0 END AS TelephoneMatch
    FROM dbo.Employees a
    JOIN dbo.Employees b ON b.EmployeeId > a.EmployeeId
    LEFT JOIN dbo.ADObjects adA ON adA.ObjectGUID = a.CurrentADObjectGuid
    LEFT JOIN dbo.ADObjects adB ON adB.ObjectGUID = b.CurrentADObjectGuid
    -- ADObjects.TelephoneNumber is the current AD snapshot's office phone number.
    -- Normalized the same way PersonMatchingService normalizes phone numbers elsewhere in this app.
    CROSS APPLY (SELECT REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(ISNULL(adA.TelephoneNumber, N''), N' ', N''), N'+', N''), N'-', N''), N'(', N''), N')', N'') AS Value) telA
    CROSS APPLY (SELECT REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(ISNULL(adB.TelephoneNumber, N''), N' ', N''), N'+', N''), N'-', N''), N'(', N''), N')', N'') AS Value) telB
    WHERE a.Status <> N'Merged' AND b.Status <> N'Merged'
      AND NOT EXISTS
      (
          SELECT 1 FROM dbo.EmployeeDuplicateDismissals d
          WHERE d.EmployeeId1 = a.EmployeeId AND d.EmployeeId2 = b.EmployeeId
      )
      AND
      (
          (NULLIF(a.NormalizedPrivateEmail,N'') IS NOT NULL AND a.NormalizedPrivateEmail=b.NormalizedPrivateEmail)
       OR (NULLIF(a.NormalizedMobilePhone,N'') IS NOT NULL AND a.NormalizedMobilePhone=b.NormalizedMobilePhone)
       OR (LOWER(CONCAT(a.CanonicalGivenName,N' ',a.CanonicalSurname)) = LOWER(CONCAT(b.CanonicalGivenName,N' ',b.CanonicalSurname)))
       OR (NULLIF(telA.Value,N'') IS NOT NULL AND telA.Value = telB.Value)
      )
)
SELECT TOP (200)
    a.EmployeeId, CONCAT(a.CanonicalGivenName,N' ',a.CanonicalSurname), a.PrivateEmail, a.MobilePhone, a.CurrentUPN, ISNULL(adA.TelephoneNumber, N''),
    b.EmployeeId, CONCAT(b.CanonicalGivenName,N' ',b.CanonicalSurname), b.PrivateEmail, b.MobilePhone, b.CurrentUPN, ISNULL(adB.TelephoneNumber, N''),
    c.EmailMatch, c.PhoneMatch, c.NameMatch, c.TelephoneMatch,
    (c.EmailMatch*100 + c.PhoneMatch*100 + c.NameMatch*35 + c.TelephoneMatch*100) AS Score
FROM candidates c
JOIN dbo.Employees a ON a.EmployeeId=c.EmployeeId1
JOIN dbo.Employees b ON b.EmployeeId=c.EmployeeId2
LEFT JOIN dbo.ADObjects adA ON adA.ObjectGUID = a.CurrentADObjectGuid
LEFT JOIN dbo.ADObjects adB ON adB.ObjectGUID = b.CurrentADObjectGuid
ORDER BY Score DESC, a.EmployeeId, b.EmployeeId;";

        await using var reader = await cmd.ExecuteReaderAsync(HttpContext.RequestAborted);
        while (await reader.ReadAsync(HttpContext.RequestAborted))
        {
            Pairs.Add(new DuplicatePair
            {
                EmployeeId1 = reader.GetInt64(0), Name1 = reader.GetString(1), Email1 = Get(reader,2), Phone1 = Get(reader,3), Upn1 = Get(reader,4), Telephone1 = Get(reader,5),
                EmployeeId2 = reader.GetInt64(6), Name2 = reader.GetString(7), Email2 = Get(reader,8), Phone2 = Get(reader,9), Upn2 = Get(reader,10), Telephone2 = Get(reader,11),
                EmailMatch = reader.GetInt32(12)==1, PhoneMatch = reader.GetInt32(13)==1, NameMatch = reader.GetInt32(14)==1, TelephoneMatch = reader.GetInt32(15)==1, Score = reader.GetInt32(16)
            });
        }
    }

    private static string Get(SqlDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? "" : reader.GetString(ordinal);

    public sealed class DuplicatePair
    {
        public long EmployeeId1 { get; init; } public string Name1 { get; init; }=""; public string Email1 { get; init; }=""; public string Phone1 { get; init; }=""; public string Upn1 { get; init; }=""; public string Telephone1 { get; init; }="";
        public long EmployeeId2 { get; init; } public string Name2 { get; init; }=""; public string Email2 { get; init; }=""; public string Phone2 { get; init; }=""; public string Upn2 { get; init; }=""; public string Telephone2 { get; init; }="";
        public bool EmailMatch { get; init; } public bool PhoneMatch { get; init; } public bool NameMatch { get; init; } public bool TelephoneMatch { get; init; } public int Score { get; init; }
    }
}

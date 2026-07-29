using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Data.SqlClient;
using UserChangeQueueWeb.Services;

namespace UserChangeQueueWeb.Pages.TemporaryAccess;

[Authorize]
public sealed class IndexModel : PageModel
{
    private readonly SqlConnectionFactory _connectionFactory;

    public IndexModel(SqlConnectionFactory connectionFactory) => _connectionFactory = connectionFactory;

    [BindProperty]
    public int GroupId { get; set; }

    [BindProperty]
    public string? Reason { get; set; }

    [TempData]
    public string? StatusMessage { get; set; }

    public string? ErrorMessage { get; set; }
    public List<GroupRow> Groups { get; } = new();
    public List<MembershipRow> History { get; } = new();

    public async Task OnGetAsync() => await LoadAsync();

    public async Task<IActionResult> OnPostRequestAsync()
    {
        var identity = GetIdentity();
        await using var cn = await _connectionFactory.OpenAsync(HttpContext.RequestAborted);
        await using var tx = (SqlTransaction)await cn.BeginTransactionAsync(HttpContext.RequestAborted);

        try
        {
            int durationDays;
            bool requireReason;
            string displayName;

            await using (var groupCmd = cn.CreateCommand())
            {
                groupCmd.Transaction = tx;
                groupCmd.CommandText = @"
SELECT DurationDays, RequireReason, DisplayName
FROM dbo.TemporaryAccessGroups WITH (UPDLOCK, HOLDLOCK)
WHERE Id = @Id AND Active = 1;";
                groupCmd.Parameters.Add(new SqlParameter("@Id", System.Data.SqlDbType.Int) { Value = GroupId });
                await using var reader = await groupCmd.ExecuteReaderAsync(HttpContext.RequestAborted);
                if (!await reader.ReadAsync(HttpContext.RequestAborted))
                {
                    ErrorMessage = "The selected access group is not available.";
                    await tx.RollbackAsync(HttpContext.RequestAborted);
                    await LoadAsync(cn);
                    return Page();
                }

                durationDays = reader.GetInt32(0);
                requireReason = reader.GetBoolean(1);
                displayName = reader.GetString(2);
            }

            Reason = Reason?.Trim();
            if (requireReason && string.IsNullOrWhiteSpace(Reason))
            {
                ErrorMessage = "A reason is required for this access group.";
                await tx.RollbackAsync(HttpContext.RequestAborted);
                await LoadAsync(cn);
                return Page();
            }

            await using var cmd = cn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = @"
INSERT INTO dbo.TemporaryGroupMemberships
(
    TemporaryAccessGroupId, UserSamAccountName, UserLoginName, UserDisplayName,
    Reason, RequestedBy, ExpiresAt, Status
)
VALUES
(
    @GroupId, @Sam, @Login, @DisplayName,
    @Reason, @RequestedBy, DATEADD(DAY, @DurationDays, SYSDATETIME()), N'PendingAdd'
);";
            cmd.Parameters.Add(new SqlParameter("@GroupId", System.Data.SqlDbType.Int) { Value = GroupId });
            cmd.Parameters.Add(new SqlParameter("@Sam", System.Data.SqlDbType.NVarChar, 256) { Value = identity.SamAccountName });
            cmd.Parameters.Add(new SqlParameter("@Login", System.Data.SqlDbType.NVarChar, 300) { Value = identity.LoginName });
            cmd.Parameters.Add(new SqlParameter("@DisplayName", System.Data.SqlDbType.NVarChar, 300) { Value = (object?)identity.DisplayName ?? DBNull.Value });
            cmd.Parameters.Add(new SqlParameter("@Reason", System.Data.SqlDbType.NVarChar, 1000) { Value = (object?)Reason ?? DBNull.Value });
            cmd.Parameters.Add(new SqlParameter("@RequestedBy", System.Data.SqlDbType.NVarChar, 300) { Value = identity.LoginName });
            cmd.Parameters.Add(new SqlParameter("@DurationDays", System.Data.SqlDbType.Int) { Value = durationDays });
            await cmd.ExecuteNonQueryAsync(HttpContext.RequestAborted);
            await tx.CommitAsync(HttpContext.RequestAborted);
            StatusMessage = $"Access to '{displayName}' has been queued.";
            return RedirectToPage();
        }
        catch (SqlException ex) when (ex.Number is 2601 or 2627)
        {
            await tx.RollbackAsync(HttpContext.RequestAborted);
            ErrorMessage = "You already have a pending or active request for this group.";
            await LoadAsync(cn);
            return Page();
        }
    }

    public async Task<IActionResult> OnPostRenewAsync(long id)
    {
        var identity = GetIdentity();
        await using var cn = await _connectionFactory.OpenAsync(HttpContext.RequestAborted);
        await using var cmd = cn.CreateCommand();
        cmd.CommandText = @"
UPDATE m
SET ExpiresAt = DATEADD(DAY, g.DurationDays, SYSDATETIME()),
    UpdatedAt = SYSDATETIME(), LastError = NULL
FROM dbo.TemporaryGroupMemberships m
JOIN dbo.TemporaryAccessGroups g ON g.Id = m.TemporaryAccessGroupId
WHERE m.Id = @Id
  AND m.UserSamAccountName = @Sam
  AND m.Status = N'Active'
  AND g.Active = 1
  AND g.AllowRenewal = 1;";
        cmd.Parameters.Add(new SqlParameter("@Id", System.Data.SqlDbType.BigInt) { Value = id });
        cmd.Parameters.Add(new SqlParameter("@Sam", System.Data.SqlDbType.NVarChar, 256) { Value = identity.SamAccountName });
        var affected = await cmd.ExecuteNonQueryAsync(HttpContext.RequestAborted);
        StatusMessage = affected == 1 ? "Temporary access was renewed." : "This membership cannot be renewed.";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostRemoveAsync(long id)
    {
        var identity = GetIdentity();
        await using var cn = await _connectionFactory.OpenAsync(HttpContext.RequestAborted);
        await using var cmd = cn.CreateCommand();
        cmd.CommandText = @"
UPDATE dbo.TemporaryGroupMemberships
SET Status = CASE WHEN Status = N'PendingAdd' THEN N'Cancelled' ELSE N'PendingRemove' END,
    CancelledAt = SYSDATETIME(),
    UpdatedAt = SYSDATETIME(), LastError = NULL
WHERE Id = @Id
  AND UserSamAccountName = @Sam
  AND Status IN (N'PendingAdd', N'Active');";
        cmd.Parameters.Add(new SqlParameter("@Id", System.Data.SqlDbType.BigInt) { Value = id });
        cmd.Parameters.Add(new SqlParameter("@Sam", System.Data.SqlDbType.NVarChar, 256) { Value = identity.SamAccountName });
        var affected = await cmd.ExecuteNonQueryAsync(HttpContext.RequestAborted);
        StatusMessage = affected == 1 ? "Removal has been queued." : "The membership could not be changed.";
        return RedirectToPage();
    }

    private async Task LoadAsync(SqlConnection? existingConnection = null)
    {
        var identity = GetIdentity();
        var ownsConnection = existingConnection is null;
        var cn = existingConnection ?? await _connectionFactory.OpenAsync(HttpContext.RequestAborted);
        try
        {
            await using var cmd = cn.CreateCommand();
            cmd.CommandText = @"
SELECT
    g.Id, g.DisplayName, g.AdGroupName, g.Description, g.DurationDays,
    g.AllowRenewal, g.RequireReason,
    m.Id, m.Status, m.ExpiresAt, m.LastError, m.WasMemberBefore
FROM dbo.TemporaryAccessGroups g
OUTER APPLY
(
    SELECT TOP (1) x.Id, x.Status, x.ExpiresAt, x.LastError, x.WasMemberBefore
    FROM dbo.TemporaryGroupMemberships x
    WHERE x.TemporaryAccessGroupId = g.Id
      AND x.UserSamAccountName = @Sam
      AND x.Status IN (N'PendingAdd', N'ProcessingAdd', N'Active', N'PendingRemove', N'ProcessingRemove')
    ORDER BY x.Id DESC
) m
WHERE g.Active = 1
ORDER BY g.SortOrder, g.DisplayName;

SELECT TOP (25)
    m.Id, g.DisplayName, m.Status, m.RequestedAt, m.ExpiresAt, m.RemovedAt, m.LastError
FROM dbo.TemporaryGroupMemberships m
JOIN dbo.TemporaryAccessGroups g ON g.Id = m.TemporaryAccessGroupId
WHERE m.UserSamAccountName = @Sam
ORDER BY m.Id DESC;";
            cmd.Parameters.Add(new SqlParameter("@Sam", System.Data.SqlDbType.NVarChar, 256) { Value = identity.SamAccountName });
            await using var reader = await cmd.ExecuteReaderAsync(HttpContext.RequestAborted);
            while (await reader.ReadAsync(HttpContext.RequestAborted))
            {
                Groups.Add(new GroupRow
                {
                    Id = reader.GetInt32(0), DisplayName = reader.GetString(1), AdGroupName = reader.GetString(2),
                    Description = reader.IsDBNull(3) ? null : reader.GetString(3), DurationDays = reader.GetInt32(4),
                    AllowRenewal = reader.GetBoolean(5), RequireReason = reader.GetBoolean(6),
                    MembershipId = reader.IsDBNull(7) ? null : reader.GetInt64(7), Status = reader.IsDBNull(8) ? null : reader.GetString(8),
                    ExpiresAt = reader.IsDBNull(9) ? null : reader.GetDateTime(9), LastError = reader.IsDBNull(10) ? null : reader.GetString(10),
                    WasMemberBefore = reader.IsDBNull(11) ? null : reader.GetBoolean(11)
                });
            }
            await reader.NextResultAsync(HttpContext.RequestAborted);
            while (await reader.ReadAsync(HttpContext.RequestAborted))
            {
                History.Add(new MembershipRow
                {
                    Id = reader.GetInt64(0), GroupName = reader.GetString(1), Status = reader.GetString(2),
                    RequestedAt = reader.GetDateTime(3), ExpiresAt = reader.GetDateTime(4),
                    RemovedAt = reader.IsDBNull(5) ? null : reader.GetDateTime(5), LastError = reader.IsDBNull(6) ? null : reader.GetString(6)
                });
            }
        }
        finally
        {
            if (ownsConnection) await cn.DisposeAsync();
        }
    }

    private (string LoginName, string SamAccountName, string? DisplayName) GetIdentity()
    {
        var login = User.Identity?.Name ?? throw new InvalidOperationException("Authenticated identity is missing.");
        var sam = login.Contains('\\') ? login.Split('\\').Last() : login.Contains('@') ? login.Split('@')[0] : login;
        return (login, sam, User.FindFirst("name")?.Value ?? User.Identity?.Name);
    }

    public sealed class GroupRow
    {
        public int Id { get; init; } public string DisplayName { get; init; } = ""; public string AdGroupName { get; init; } = "";
        public string? Description { get; init; } public int DurationDays { get; init; } public bool AllowRenewal { get; init; }
        public bool RequireReason { get; init; } public long? MembershipId { get; init; } public string? Status { get; init; }
        public DateTime? ExpiresAt { get; init; } public string? LastError { get; init; } public bool? WasMemberBefore { get; init; }
    }
    public sealed class MembershipRow
    {
        public long Id { get; init; } public string GroupName { get; init; } = ""; public string Status { get; init; } = "";
        public DateTime RequestedAt { get; init; } public DateTime ExpiresAt { get; init; } public DateTime? RemovedAt { get; init; }
        public string? LastError { get; init; }
    }
}

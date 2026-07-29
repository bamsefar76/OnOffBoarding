using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Data.SqlClient;
using System.Data;
using UserChangeQueueWeb.Services;

namespace UserChangeQueueWeb.Pages;

[Authorize]
public class UserPendingChangesModel : PageModel
{
    private readonly SqlConnectionFactory _connectionFactory;
    private readonly ObjectAccessService _objectAccessService;

    public UserPendingChangesModel(SqlConnectionFactory connectionFactory, ObjectAccessService objectAccessService)
    {
        _connectionFactory = connectionFactory;
        _objectAccessService = objectAccessService;
    }

    public string UserObjectGuid { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string? Message { get; set; }

    public List<PendingChangeRow> PendingChanges { get; set; } = new();

    public async Task<IActionResult> OnGetAsync(string? user)
    {
        if (string.IsNullOrWhiteSpace(user))
        {
            Message = "No user selected.";
            return Page();
        }

        if (!Guid.TryParse(user, out var objectGuid))
        {
            Message = "Invalid user id.";
            return Page();
        }

        if (!await _objectAccessService.CanViewUserAsync(User, objectGuid))
        {
            return Forbid();
        }

        UserObjectGuid = objectGuid.ToString("D");

        await LoadUserAsync(objectGuid);
        await LoadPendingChangesAsync(objectGuid);

        return Page();
    }

    private async Task LoadUserAsync(Guid objectGuid)
    {
        await using var cn = await _connectionFactory.OpenAsync();

        await using var cmd = cn.CreateCommand();
        cmd.CommandText = @"
SELECT
    ISNULL(DisplayName, SamAccountName) AS DisplayName
FROM dbo.ADObjects
WHERE ObjectGUID = @ObjectGUID;
";

        cmd.Parameters.Add("@ObjectGUID", SqlDbType.UniqueIdentifier).Value = objectGuid;

        var result = await cmd.ExecuteScalarAsync();
        DisplayName = result?.ToString() ?? objectGuid.ToString("D");
    }

    private async Task LoadPendingChangesAsync(Guid objectGuid)
    {
        PendingChanges.Clear();

        await using var cn = await _connectionFactory.OpenAsync();

        await using var cmd = cn.CreateCommand();
        cmd.CommandText = @"
SELECT
    q.RequestId,
    ISNULL(q.TargetDisplayName, ISNULL(a.DisplayName, q.TargetSamAccountName)) AS TargetDisplayName,
    q.RequestType,
    q.Status,
    q.RequestedBy,
    q.CreatedAt
FROM dbo.ADUserChangeQueue q
LEFT JOIN dbo.ADObjects a
    ON a.ObjectGUID = q.TargetObjectGUID
WHERE q.RequestType = 'UPDATE'
  AND q.TargetObjectGUID = @ObjectGUID
  AND ISNULL(q.Status, '') NOT IN ('Implemented', 'Completed', 'Done', 'Cancelled', 'Rejected')
ORDER BY q.CreatedAt DESC;
";

        cmd.Parameters.Add("@ObjectGUID", SqlDbType.UniqueIdentifier).Value = objectGuid;

        await using var reader = await cmd.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {
            PendingChanges.Add(new PendingChangeRow
            {
                Id = Convert.ToInt64(reader.GetValue(0)),
                TargetDisplayName = reader.IsDBNull(1) ? "" : reader.GetString(1),
                RequestType = reader.IsDBNull(2) ? "" : reader.GetString(2),
                Status = reader.IsDBNull(3) ? "" : reader.GetString(3),
                RequestedBy = reader.IsDBNull(4) ? "" : reader.GetString(4),
                CreatedAt = reader.IsDBNull(5) ? null : reader.GetDateTime(5)
            });
        }
    }

    public class PendingChangeRow
    {
        public long Id { get; set; }
        public string TargetDisplayName { get; set; } = "";
        public string RequestType { get; set; } = "";
        public string Status { get; set; } = "";
        public string RequestedBy { get; set; } = "";
        public DateTime? CreatedAt { get; set; }
    }
}

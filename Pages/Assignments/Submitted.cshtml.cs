using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Data.SqlClient;
using UserChangeQueueWeb.Services;

namespace UserChangeQueueWeb.Pages.Assignments;

[Authorize]
public sealed class SubmittedModel : PageModel
{
    private readonly SqlConnectionFactory _connectionFactory;
    public SubmittedModel(SqlConnectionFactory connectionFactory) => _connectionFactory = connectionFactory;

    public long AssignmentId { get; private set; }
    public string PersonName { get; private set; } = "";
    public string? ProjectName { get; private set; }
    public DateTime StartDate { get; private set; }
    public DateTime? EndDate { get; private set; }
    public bool HasOverlap { get; private set; }
    public bool RequiresIdentityChange { get; private set; }

    public async Task OnGetAsync(long id)
    {
        AssignmentId = id;
        await using var cn = await _connectionFactory.OpenAsync(HttpContext.RequestAborted);
        await using var cmd = cn.CreateCommand();
        cmd.CommandText = @"
SELECT CONCAT(p.CanonicalGivenName, N' ', p.CanonicalSurname), a.ProjectName,
       a.StartDate, a.EndDate, a.OverlapStatus, a.RequiresIdentityChange
FROM dbo.Assignments a
JOIN dbo.People p ON p.PersonId = a.PersonId
WHERE a.AssignmentId = @Id AND a.RequestedBy = @RequestedBy;";
        cmd.Parameters.Add(new SqlParameter("@Id", System.Data.SqlDbType.BigInt) { Value = id });
        cmd.Parameters.AddNVarChar("@RequestedBy", User.Identity?.Name, 300);
        await using var reader = await cmd.ExecuteReaderAsync(HttpContext.RequestAborted);
        if (!await reader.ReadAsync(HttpContext.RequestAborted))
        {
            Response.StatusCode = 404;
            return;
        }
        PersonName = reader.GetString(0);
        ProjectName = reader.IsDBNull(1) ? null : reader.GetString(1);
        StartDate = reader.GetDateTime(2);
        EndDate = reader.IsDBNull(3) ? null : reader.GetDateTime(3);
        HasOverlap = string.Equals(reader.GetString(4), "ReviewRequired", StringComparison.OrdinalIgnoreCase);
        RequiresIdentityChange = reader.GetBoolean(5);
    }
}

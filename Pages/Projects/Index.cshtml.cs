using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Data.SqlClient;
using UserChangeQueueWeb.Services;

namespace UserChangeQueueWeb.Pages;

[Authorize]
public class ProjectsModel : PageModel
{
    private readonly SqlConnectionFactory _connectionFactory;
    private readonly AccessScopeService _accessScopeService;

    public ProjectsModel(
        SqlConnectionFactory connectionFactory,
        AccessScopeService accessScopeService)
    {
        _connectionFactory = connectionFactory;
        _accessScopeService = accessScopeService;
    }

    [BindProperty(SupportsGet = true)]
    public bool ShowDisabled { get; set; }

    public List<ProjectRow> Projects { get; set; } = new();
    public bool CanEditProjects { get; private set; }

    public async Task OnGetAsync()
    {
        await LoadProjectsAsync();
    }

    private async Task LoadProjectsAsync()
    {
        Projects.Clear();

        var scope = await _accessScopeService.GetCurrentAsync(User, HttpContext.RequestAborted);
        CanEditProjects = scope.IsIT;
        await using var cn = await _connectionFactory.OpenAsync(HttpContext.RequestAborted);

        await using var cmd = cn.CreateCommand();

        cmd.CommandText = @"
SELECT
    p.Id,
    p.ProjectName,
    p.ProjectNumber,
    p.Company,
    ISNULL
    (
        (
            SELECT STRING_AGG(COALESCE(NULLIF(ad.DisplayName, N''), NULLIF(LTRIM(RTRIM(CONCAT(emp.CanonicalGivenName,N' ',emp.CanonicalSurname))),N'')), N', ')
            FROM dbo.ProjectManagers pm
            LEFT JOIN dbo.ADObjects ad
                ON ad.SamAccountName = pm.SamAccountName
               AND ISNULL(ad.IsDeleted, 0) = 0
            LEFT JOIN dbo.Employees emp
                ON emp.CurrentSamAccountName = pm.SamAccountName
               AND emp.Status <> N'Merged'
            WHERE pm.ProjectId = p.Id
        ),
        N''
    ) AS ProjectManagerNames,
    COALESCE(NULLIF(producer.DisplayName, N''), NULLIF(LTRIM(RTRIM(CONCAT(producerEmp.CanonicalGivenName,N' ',producerEmp.CanonicalSurname))),N''), N'') AS ProducerName,
    COALESCE(NULLIF(executive.DisplayName, N''), NULLIF(LTRIM(RTRIM(CONCAT(executiveEmp.CanonicalGivenName,N' ',executiveEmp.CanonicalSurname))),N''), N'') AS ExecutiveName,
    p.Active,
    p.LastUpdated
FROM dbo.Projects p
LEFT JOIN dbo.ADObjects producer
    ON producer.SamAccountName = CASE WHEN CHARINDEX(N'\',ISNULL(p.Producer,N''))>0 THEN RIGHT(p.Producer,CHARINDEX(N'\',REVERSE(p.Producer))-1) ELSE p.Producer END
   AND ISNULL(producer.IsDeleted, 0) = 0
LEFT JOIN dbo.Employees producerEmp
    ON producerEmp.CurrentSamAccountName = CASE WHEN CHARINDEX(N'\',ISNULL(p.Producer,N''))>0 THEN RIGHT(p.Producer,CHARINDEX(N'\',REVERSE(p.Producer))-1) ELSE p.Producer END
   AND producerEmp.Status <> N'Merged'
LEFT JOIN dbo.ADObjects executive
    ON executive.SamAccountName = CASE WHEN CHARINDEX(N'\',ISNULL(p.Executive,N''))>0 THEN RIGHT(p.Executive,CHARINDEX(N'\',REVERSE(p.Executive))-1) ELSE p.Executive END
   AND ISNULL(executive.IsDeleted, 0) = 0
LEFT JOIN dbo.Employees executiveEmp
    ON executiveEmp.CurrentSamAccountName = CASE WHEN CHARINDEX(N'\',ISNULL(p.Executive,N''))>0 THEN RIGHT(p.Executive,CHARINDEX(N'\',REVERSE(p.Executive))-1) ELSE p.Executive END
   AND executiveEmp.Status <> N'Merged'
WHERE
    (@ShowDisabled = 1 OR p.Active = 1)
    AND
    (
         @IsIT = 1
      OR EXISTS
         (
             SELECT 1
             FROM dbo.ProjectManagers pm
             WHERE pm.ProjectId = p.Id
               AND pm.SamAccountName = @SamAccountName
         )
      OR p.ProductionManager = @SamAccountName
      OR p.ProductionManager LIKE @DomainSlashSamAccountName
    )
ORDER BY
    p.Active DESC,
    p.Company,
    p.ProjectName;";

        cmd.Parameters.AddBit("@ShowDisabled", ShowDisabled);
        cmd.Parameters.AddBit("@IsIT", scope.IsIT);
        cmd.Parameters.AddNVarChar("@SamAccountName", scope.SamAccountName, 256);
        cmd.Parameters.AddNVarChar("@DomainSlashSamAccountName", @"%\" + scope.SamAccountName, 300);

        await using var reader = await cmd.ExecuteReaderAsync(HttpContext.RequestAborted);

        while (await reader.ReadAsync(HttpContext.RequestAborted))
        {
            Projects.Add(new ProjectRow
            {
                Id = reader.GetInt32(0),
                ProjectName = reader.IsDBNull(1) ? "" : reader.GetString(1),
                ProjectNumber = reader.IsDBNull(2) ? "" : reader.GetString(2),
                Company = reader.IsDBNull(3) ? "" : reader.GetString(3),
                ProjectManagers = reader.IsDBNull(4) ? "" : reader.GetString(4),
                Producer = reader.IsDBNull(5) ? "" : reader.GetString(5),
                Executive = reader.IsDBNull(6) ? "" : reader.GetString(6),
                Active = !reader.IsDBNull(7) && reader.GetBoolean(7),
                LastUpdated = reader.IsDBNull(8) ? null : reader.GetDateTime(8)
            });
        }
    }

    public class ProjectRow
    {
        public int Id { get; set; }
        public string ProjectName { get; set; } = "";
        public string ProjectNumber { get; set; } = "";
        public string Company { get; set; } = "";
        public string ProjectManagers { get; set; } = "";
        public string Producer { get; set; } = "";
        public string Executive { get; set; } = "";
        public bool Active { get; set; }
        public DateTime? LastUpdated { get; set; }
    }
}

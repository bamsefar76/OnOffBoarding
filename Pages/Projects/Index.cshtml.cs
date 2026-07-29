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
    Id,
    ProjectName,
    ProjectNumber,
    Company,
    ProductionManager,
    Producer,
    Executive,
    Active,
    LastUpdated
FROM dbo.Projects
WHERE
    (@ShowDisabled = 1 OR Active = 1)
    AND
    (
         @IsIT = 1
      OR ProductionManager = @SamAccountName
      OR ProductionManager LIKE @DomainSlashSamAccountName
    )
ORDER BY
    Active DESC,
    Company,
    ProjectName;
";

        cmd.Parameters.AddBit("@ShowDisabled", ShowDisabled);
        cmd.Parameters.AddBit("@IsIT", scope.IsIT);
        cmd.Parameters.AddNVarChar("@SamAccountName", scope.SamAccountName, 256);
        cmd.Parameters.AddNVarChar("@DomainSlashSamAccountName", @"%\" + scope.SamAccountName, 300);

        await using var reader = await cmd.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {
            Projects.Add(new ProjectRow
            {
                Id = reader.GetInt32(0),
                ProjectName = reader.IsDBNull(1) ? "" : reader.GetString(1),
                ProjectNumber = reader.IsDBNull(2) ? "" : reader.GetString(2),
                Company = reader.IsDBNull(3) ? "" : reader.GetString(3),
                ProductionManager = reader.IsDBNull(4) ? "" : reader.GetString(4),
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
        public string ProductionManager { get; set; } = "";
        public string Producer { get; set; } = "";
        public string Executive { get; set; } = "";
        public bool Active { get; set; }
        public DateTime? LastUpdated { get; set; }
    }
}
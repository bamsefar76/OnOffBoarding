using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.Data.SqlClient;
using UserChangeQueueWeb.Services;

namespace UserChangeQueueWeb.Pages;

[Authorize]
public class ProjectEditModel : PageModel
{
    private readonly SqlConnectionFactory _connectionFactory;

    public ProjectEditModel(SqlConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    [BindProperty]
    public ProjectRow Project { get; set; } = new()
    {
        Active = true
    };

    public string? Message { get; set; }

    public List<SelectListItem> Companies { get; set; } = new();
    public List<SelectListItem> CompanyUsers { get; set; } = new();

    public async Task OnGetAsync(int? id)
    {
        await LoadCompaniesAsync();

        if (id.HasValue)
        {
            await LoadProjectAsync(id.Value);
        }

        await LoadCompanyUsersAsync(Project.Company);
    }

    public async Task<IActionResult> OnPostAsync()
    {
        await LoadCompaniesAsync();
        await LoadCompanyUsersAsync(Project.Company);
        return Page();
    }

    public async Task<IActionResult> OnPostSaveAsync()
    {
        if (string.IsNullOrWhiteSpace(Project.ProjectName))
        {
            Message = "Project name is required.";
            await LoadCompaniesAsync();
            await LoadCompanyUsersAsync(Project.Company);
            return Page();
        }

        if (Project.Id == 0)
        {
            await InsertProjectAsync();
        }
        else
        {
            await UpdateProjectAsync();
        }

        return RedirectToPage("/Projects/Index");
    }

    private async Task LoadProjectAsync(int id)
    {
        await using var cn = await _connectionFactory.OpenAsync();

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
    Active
FROM dbo.Projects
WHERE Id = @Id;
";

        cmd.Parameters.AddInt("@Id", id);

        await using var reader = await cmd.ExecuteReaderAsync();

        if (await reader.ReadAsync())
        {
            Project = new ProjectRow
            {
                Id = reader.GetInt32(0),
                ProjectName = reader.IsDBNull(1) ? "" : reader.GetString(1),
                ProjectNumber = reader.IsDBNull(2) ? "" : reader.GetString(2),
                Company = reader.IsDBNull(3) ? "" : reader.GetString(3),
                ProductionManager = reader.IsDBNull(4) ? "" : reader.GetString(4),
                Producer = reader.IsDBNull(5) ? "" : reader.GetString(5),
                Executive = reader.IsDBNull(6) ? "" : reader.GetString(6),
                Active = !reader.IsDBNull(7) && reader.GetBoolean(7)
            };
        }
    }

    private async Task InsertProjectAsync()
    {
        await using var cn = await _connectionFactory.OpenAsync();

        await using var cmd = cn.CreateCommand();
        cmd.CommandText = @"
INSERT INTO dbo.Projects
(
    ProjectName,
    ProjectNumber,
    Company,
    ProductionManager,
    Producer,
    Executive,
    Active,
    LastUpdated
)
VALUES
(
    @ProjectName,
    @ProjectNumber,
    @Company,
    @ProductionManager,
    @Producer,
    @Executive,
    @Active,
    SYSUTCDATETIME()
);
";

        AddSaveParameters(cmd);

        await cmd.ExecuteNonQueryAsync();
    }

    private async Task UpdateProjectAsync()
    {
        await using var cn = await _connectionFactory.OpenAsync();

        await using var cmd = cn.CreateCommand();
        cmd.CommandText = @"
UPDATE dbo.Projects
SET
    ProjectName = @ProjectName,
    ProjectNumber = @ProjectNumber,
    Company = @Company,
    ProductionManager = @ProductionManager,
    Producer = @Producer,
    Executive = @Executive,
    Active = @Active,
    LastUpdated = SYSUTCDATETIME()
WHERE Id = @Id;
";

        cmd.Parameters.AddInt("@Id", Project.Id);
        AddSaveParameters(cmd);

        await cmd.ExecuteNonQueryAsync();
    }

    private void AddSaveParameters(SqlCommand cmd)
    {
        cmd.Parameters.AddNVarChar("@ProjectName", Project.ProjectName, 256);
        cmd.Parameters.AddNVarChar("@ProjectNumber", Project.ProjectNumber, 100);
        cmd.Parameters.AddNVarChar("@Company", Project.Company, 256);
        cmd.Parameters.AddNVarChar("@ProductionManager", Project.ProductionManager, 256);
        cmd.Parameters.AddNVarChar("@Producer", Project.Producer, 256);
        cmd.Parameters.AddNVarChar("@Executive", Project.Executive, 256);
        cmd.Parameters.AddBit("@Active", Project.Active);
    }

    private async Task LoadCompaniesAsync()
    {
        Companies.Clear();

        await using var cn = await _connectionFactory.OpenAsync();

        await using var cmd = cn.CreateCommand();
        cmd.CommandText = @"
SELECT DISTINCT
    LTRIM(RTRIM(Company)) AS Company
FROM dbo.ADObjects
WHERE Enabled = 1
  AND NULLIF(LTRIM(RTRIM(Company)), '') IS NOT NULL
ORDER BY Company;
";

        await using var reader = await cmd.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {
            var company = reader.GetString(0);

            Companies.Add(new SelectListItem
            {
                Value = company,
                Text = company
            });
        }
    }

    private async Task LoadCompanyUsersAsync(string? company)
    {
        CompanyUsers.Clear();

        if (string.IsNullOrWhiteSpace(company))
        {
            return;
        }

        await using var cn = await _connectionFactory.OpenAsync();

        await using var cmd = cn.CreateCommand();
        cmd.CommandText = @"
SELECT
    SamAccountName,
    DisplayName
FROM dbo.ADObjects
WHERE Enabled = 1
  AND LTRIM(RTRIM(Company)) = @Company
  AND NULLIF(LTRIM(RTRIM(SamAccountName)), '') IS NOT NULL
ORDER BY DisplayName;
";

        cmd.Parameters.AddNVarChar("@Company", company.Trim(), 256);

        await using var reader = await cmd.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {
            var sam = reader.GetString(0);
            var displayName = reader.IsDBNull(1) ? sam : reader.GetString(1);

            CompanyUsers.Add(new SelectListItem
            {
                Value = sam,
                Text = $"{displayName} ({sam})"
            });
        }
    }

    private static object DbValue(string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? DBNull.Value
            : value.Trim();
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
    }
}
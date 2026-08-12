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
    public ProjectRow Project { get; set; } = new() { Active = true };

    [BindProperty]
    public List<string> SelectedProjectManagers { get; set; } = new();

    public string? MessageKey { get; set; }
    public List<SelectListItem> Companies { get; set; } = new();
    public List<SelectListItem> CompanyUsers { get; set; } = new();

    public async Task OnGetAsync(int? id)
    {
        await LoadCompaniesAsync();
        if (id.HasValue)
        {
            await LoadProjectAsync(id.Value);
            await LoadSelectedProjectManagersAsync(id.Value);
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
        NormalizeManagers();

        if (string.IsNullOrWhiteSpace(Project.ProjectName))
        {
            MessageKey = "projectedit.projectNameRequired";
            await LoadCompaniesAsync();
            await LoadCompanyUsersAsync(Project.Company);
            return Page();
        }

        await using var cn = await _connectionFactory.OpenAsync(HttpContext.RequestAborted);
        await using var tx = (SqlTransaction)await cn.BeginTransactionAsync(HttpContext.RequestAborted);
        try
        {
            if (Project.Id == 0)
                Project.Id = await InsertProjectAsync(cn, tx);
            else
                await UpdateProjectAsync(cn, tx);

            await ReplaceProjectManagersAsync(cn, tx, Project.Id);
            await tx.CommitAsync(HttpContext.RequestAborted);
        }
        catch
        {
            await tx.RollbackAsync(HttpContext.RequestAborted);
            throw;
        }

        return RedirectToPage("/Projects/Index");
    }

    private void NormalizeManagers()
    {
        SelectedProjectManagers = SelectedProjectManagers
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => AccessScopeService.ExtractSamAccountName(x.Trim()))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private async Task LoadProjectAsync(int id)
    {
        await using var cn = await _connectionFactory.OpenAsync(HttpContext.RequestAborted);
        await using var cmd = cn.CreateCommand();
        cmd.CommandText = @"
SELECT Id, ProjectName, ProjectNumber, Company, Producer, Executive, Active
FROM dbo.Projects
WHERE Id = @Id;";
        cmd.Parameters.AddInt("@Id", id);

        await using var reader = await cmd.ExecuteReaderAsync(HttpContext.RequestAborted);
        if (await reader.ReadAsync(HttpContext.RequestAborted))
        {
            Project = new ProjectRow
            {
                Id = reader.GetInt32(0),
                ProjectName = reader.IsDBNull(1) ? "" : reader.GetString(1),
                ProjectNumber = reader.IsDBNull(2) ? "" : reader.GetString(2),
                Company = reader.IsDBNull(3) ? "" : reader.GetString(3),
                Producer = reader.IsDBNull(4) ? "" : reader.GetString(4),
                Executive = reader.IsDBNull(5) ? "" : reader.GetString(5),
                Active = !reader.IsDBNull(6) && reader.GetBoolean(6)
            };
        }
    }

    private async Task LoadSelectedProjectManagersAsync(int id)
    {
        await using var cn = await _connectionFactory.OpenAsync(HttpContext.RequestAborted);
        await using var cmd = cn.CreateCommand();
        cmd.CommandText = @"
SELECT SamAccountName
FROM dbo.ProjectManagers
WHERE ProjectId = @Id
ORDER BY SortOrder, SamAccountName;";
        cmd.Parameters.AddInt("@Id", id);
        await using var reader = await cmd.ExecuteReaderAsync(HttpContext.RequestAborted);
        while (await reader.ReadAsync(HttpContext.RequestAborted))
            SelectedProjectManagers.Add(reader.GetString(0));
    }

    private async Task<int> InsertProjectAsync(SqlConnection cn, SqlTransaction tx)
    {
        await using var cmd = cn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = @"
INSERT INTO dbo.Projects
(ProjectName, ProjectNumber, Company, ProductionManager, Producer, Executive, Active, LastUpdated)
OUTPUT INSERTED.Id
VALUES
(@ProjectName, @ProjectNumber, @Company, @ProductionManager, @Producer, @Executive, @Active, SYSUTCDATETIME());";
        AddSaveParameters(cmd);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(HttpContext.RequestAborted));
    }

    private async Task UpdateProjectAsync(SqlConnection cn, SqlTransaction tx)
    {
        await using var cmd = cn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = @"
UPDATE dbo.Projects
SET ProjectName=@ProjectName, ProjectNumber=@ProjectNumber, Company=@Company,
    ProductionManager=@ProductionManager, Producer=@Producer, Executive=@Executive,
    Active=@Active, LastUpdated=SYSUTCDATETIME()
WHERE Id=@Id;";
        cmd.Parameters.AddInt("@Id", Project.Id);
        AddSaveParameters(cmd);
        await cmd.ExecuteNonQueryAsync(HttpContext.RequestAborted);
    }

    private async Task ReplaceProjectManagersAsync(SqlConnection cn, SqlTransaction tx, int projectId)
    {
        await using (var delete = cn.CreateCommand())
        {
            delete.Transaction = tx;
            delete.CommandText = "DELETE FROM dbo.ProjectManagers WHERE ProjectId=@ProjectId;";
            delete.Parameters.AddInt("@ProjectId", projectId);
            await delete.ExecuteNonQueryAsync(HttpContext.RequestAborted);
        }

        for (var i = 0; i < SelectedProjectManagers.Count; i++)
        {
            await using var insert = cn.CreateCommand();
            insert.Transaction = tx;
            insert.CommandText = @"
INSERT INTO dbo.ProjectManagers(ProjectId, SamAccountName, SortOrder)
VALUES(@ProjectId, @Sam, @SortOrder);";
            insert.Parameters.AddInt("@ProjectId", projectId);
            insert.Parameters.AddNVarChar("@Sam", SelectedProjectManagers[i], 256);
            insert.Parameters.AddInt("@SortOrder", (i + 1) * 10);
            await insert.ExecuteNonQueryAsync(HttpContext.RequestAborted);
        }
    }

    private void AddSaveParameters(SqlCommand cmd)
    {
        cmd.Parameters.AddNVarChar("@ProjectName", Project.ProjectName, 256);
        cmd.Parameters.AddNVarChar("@ProjectNumber", Project.ProjectNumber, 100);
        cmd.Parameters.AddNVarChar("@Company", Project.Company, 256);
        // Keep the legacy column synchronized for existing integrations.
        cmd.Parameters.AddNVarChar("@ProductionManager", SelectedProjectManagers.FirstOrDefault(), 256);
        cmd.Parameters.AddNVarChar("@Producer", Project.Producer, 256);
        cmd.Parameters.AddNVarChar("@Executive", Project.Executive, 256);
        cmd.Parameters.AddBit("@Active", Project.Active);
    }

    private async Task LoadCompaniesAsync()
    {
        Companies.Clear();
        await using var cn = await _connectionFactory.OpenAsync(HttpContext.RequestAborted);
        await using var cmd = cn.CreateCommand();
        cmd.CommandText = @"
SELECT DISTINCT LTRIM(RTRIM(Company))
FROM dbo.ADObjects
WHERE Enabled=1 AND NULLIF(LTRIM(RTRIM(Company)), N'') IS NOT NULL
ORDER BY LTRIM(RTRIM(Company));";
        await using var reader = await cmd.ExecuteReaderAsync(HttpContext.RequestAborted);
        while (await reader.ReadAsync(HttpContext.RequestAborted))
        {
            var company = reader.GetString(0);
            Companies.Add(new SelectListItem(company, company));
        }
    }

    private async Task LoadCompanyUsersAsync(string? company)
    {
        CompanyUsers.Clear();
        if (string.IsNullOrWhiteSpace(company)) return;

        await using var cn = await _connectionFactory.OpenAsync(HttpContext.RequestAborted);
        await using var cmd = cn.CreateCommand();
        cmd.CommandText = @"
SELECT ad.SamAccountName,
       COALESCE(
           NULLIF(ad.DisplayName,N''),
           NULLIF(LTRIM(RTRIM(CONCAT(emp.CanonicalGivenName,N' ',emp.CanonicalSurname))),N''),
           NULLIF(ad.Mail,N''),
           N'') AS VisibleName
FROM dbo.ADObjects ad
LEFT JOIN dbo.Employees emp
    ON emp.CurrentSamAccountName=ad.SamAccountName
   AND emp.Status<>N'Merged'
WHERE ad.Enabled=1 AND ISNULL(ad.IsDeleted,0)=0
  AND LTRIM(RTRIM(ad.Company))=@Company
  AND NULLIF(LTRIM(RTRIM(ad.SamAccountName)),N'') IS NOT NULL
ORDER BY VisibleName;";
        cmd.Parameters.AddNVarChar("@Company", company.Trim(), 256);
        await using var reader = await cmd.ExecuteReaderAsync(HttpContext.RequestAborted);
        while (await reader.ReadAsync(HttpContext.RequestAborted))
        {
            var sam = reader.GetString(0);
            var display = reader.GetString(1);
            CompanyUsers.Add(new SelectListItem(display, sam));
        }
    }

    public class ProjectRow
    {
        public int Id { get; set; }
        public string ProjectName { get; set; } = "";
        public string ProjectNumber { get; set; } = "";
        public string Company { get; set; } = "";
        public string Producer { get; set; } = "";
        public string Executive { get; set; } = "";
        public bool Active { get; set; }
    }
}

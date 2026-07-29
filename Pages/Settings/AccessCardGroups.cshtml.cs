using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Data.SqlClient;
using UserChangeQueueWeb.Services;

namespace UserChangeQueueWeb.Pages.Settings;

[Authorize]
public sealed class AccessCardGroupsModel : PageModel
{
    private readonly SqlConnectionFactory _connectionFactory;

    public AccessCardGroupsModel(SqlConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    [BindProperty(SupportsGet = true, Name = "id")]
    public int? SelectedGroupId { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? Search { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? Site { get; set; }

    [BindProperty(SupportsGet = true)]
    public bool ShowInactive { get; set; }

    [BindProperty]
    public GroupEditModel EditGroup { get; set; } = new() { Active = true, SortOrder = 100 };

    [BindProperty]
    public AccessRuleEditModel NewAccessRule { get; set; } = new() { Active = true };

    [TempData]
    public string? StatusMessage { get; set; }

    public string? ErrorMessage { get; set; }

    public List<GroupListItem> Groups { get; } = new();
    public List<AccessRuleRow> AccessRules { get; } = new();
    public List<string> Sites { get; } = new();

    public async Task OnGetAsync()
    {
        await LoadPageAsync();
    }

    public async Task<IActionResult> OnGetNewAsync()
    {
        SelectedGroupId = null;
        EditGroup = new GroupEditModel { Active = true, SortOrder = 100, Site = Site };
        await LoadPageAsync(loadSelected: false);
        return Page();
    }

    public async Task<IActionResult> OnPostSaveGroupAsync()
    {
        NormalizeGroup(EditGroup);
        var validationError = ValidateGroup(EditGroup);
        if (validationError is not null)
        {
            ErrorMessage = validationError;
            SelectedGroupId = EditGroup.Id > 0 ? EditGroup.Id : null;
            await LoadPageAsync(loadSelected: false);
            return Page();
        }

        var changedBy = User.Identity?.Name ?? Environment.UserName;
        await using var cn = await _connectionFactory.OpenAsync(HttpContext.RequestAborted);

        try
        {
            if (EditGroup.Id > 0)
            {
                await using var cmd = cn.CreateCommand();
                cmd.Parameters.AddInt("@Id", EditGroup.Id);
                AddGroupParameters(cmd, EditGroup, changedBy);
                cmd.CommandText = @"
UPDATE dbo.AccessCardGroups
SET
    Site = @Site,
    DisplayName = @DisplayName,
    ExternalGroupName = @ExternalGroupName,
    Description = @Description,
    Active = @Active,
    SortOrder = @SortOrder,
    UpdatedAt = SYSDATETIME(),
    UpdatedBy = @ChangedBy
WHERE Id = @Id;
";
                var affected = await cmd.ExecuteNonQueryAsync(HttpContext.RequestAborted);
                if (affected == 0)
                {
                    ErrorMessage = "The access-card group was not found.";
                    await LoadPageAsync(cn, loadSelected: false);
                    return Page();
                }

                StatusMessage = $"Saved access-card group '{EditGroup.DisplayName}'.";
                SelectedGroupId = EditGroup.Id;
            }
            else
            {
                await using var cmd = cn.CreateCommand();
                AddGroupParameters(cmd, EditGroup, changedBy);
                cmd.CommandText = @"
INSERT INTO dbo.AccessCardGroups
(
    Site,
    DisplayName,
    ExternalGroupName,
    Description,
    Active,
    SortOrder,
    CreatedBy
)
OUTPUT INSERTED.Id
VALUES
(
    @Site,
    @DisplayName,
    @ExternalGroupName,
    @Description,
    @Active,
    @SortOrder,
    @ChangedBy
);
";
                SelectedGroupId = Convert.ToInt32(await cmd.ExecuteScalarAsync(HttpContext.RequestAborted));
                StatusMessage = $"Created access-card group '{EditGroup.DisplayName}'.";
            }
        }
        catch (SqlException ex) when (ex.Number is 2601 or 2627)
        {
            ErrorMessage = "An access-card group with the same site and external group name already exists.";
            SelectedGroupId = EditGroup.Id > 0 ? EditGroup.Id : null;
            await LoadPageAsync(cn, loadSelected: false);
            return Page();
        }

        return RedirectToPage(new { id = SelectedGroupId, Search, Site, ShowInactive });
    }

    public async Task<IActionResult> OnPostSetGroupActiveAsync(int id, bool active)
    {
        await using var cn = await _connectionFactory.OpenAsync(HttpContext.RequestAborted);
        await using var cmd = cn.CreateCommand();
        cmd.Parameters.AddInt("@Id", id);
        cmd.Parameters.AddBit("@Active", active);
        cmd.Parameters.AddRequiredNVarChar("@ChangedBy", User.Identity?.Name ?? Environment.UserName, 300);
        cmd.CommandText = @"
UPDATE dbo.AccessCardGroups
SET
    Active = @Active,
    UpdatedAt = SYSDATETIME(),
    UpdatedBy = @ChangedBy
WHERE Id = @Id;
";
        await cmd.ExecuteNonQueryAsync(HttpContext.RequestAborted);
        StatusMessage = active ? "Access-card group was activated." : "Access-card group was disabled.";
        return RedirectToPage(new { id, Search, Site, ShowInactive });
    }

    public async Task<IActionResult> OnPostDeleteGroupAsync(int id)
    {
        await using var cn = await _connectionFactory.OpenAsync(HttpContext.RequestAborted);

        await using (var usageCmd = cn.CreateCommand())
        {
            usageCmd.Parameters.AddInt("@Id", id);
            usageCmd.CommandText = @"
SELECT COUNT_BIG(1)
FROM dbo.ADUserChangeQueueAccessCardGroups
WHERE AccessCardGroupId = @Id;
";
            var usageCount = Convert.ToInt64(await usageCmd.ExecuteScalarAsync(HttpContext.RequestAborted));
            if (usageCount > 0)
            {
                ErrorMessage = $"This group is referenced by {usageCount} request(s) and cannot be deleted. Disable it instead.";
                SelectedGroupId = id;
                await LoadPageAsync(cn);
                return Page();
            }
        }

        await using var cmd = cn.CreateCommand();
        cmd.Parameters.AddInt("@Id", id);
        cmd.CommandText = "DELETE FROM dbo.AccessCardGroups WHERE Id = @Id;";
        await cmd.ExecuteNonQueryAsync(HttpContext.RequestAborted);

        StatusMessage = "Access-card group was deleted.";
        return RedirectToPage(new { Search, Site, ShowInactive });
    }

    public async Task<IActionResult> OnPostAddAccessRuleAsync(int id)
    {
        NewAccessRule.AdGroupName = NewAccessRule.AdGroupName?.Trim();
        if (id <= 0)
        {
            ErrorMessage = "Select or save an access-card group first.";
            await LoadPageAsync();
            return Page();
        }

        if (string.IsNullOrWhiteSpace(NewAccessRule.AdGroupName))
        {
            ErrorMessage = "AD group name is required.";
            SelectedGroupId = id;
            await LoadPageAsync();
            return Page();
        }

        await using var cn = await _connectionFactory.OpenAsync(HttpContext.RequestAborted);
        await using var cmd = cn.CreateCommand();
        cmd.Parameters.AddInt("@AccessCardGroupId", id);
        cmd.Parameters.AddRequiredNVarChar("@AdGroupName", NewAccessRule.AdGroupName, 300);
        cmd.Parameters.AddBit("@Active", NewAccessRule.Active);
        cmd.Parameters.AddRequiredNVarChar("@ChangedBy", User.Identity?.Name ?? Environment.UserName, 300);
        cmd.CommandText = @"
IF EXISTS
(
    SELECT 1
    FROM dbo.AccessCardGroupAccessRules
    WHERE AccessCardGroupId = @AccessCardGroupId
      AND AdGroupName = @AdGroupName
)
BEGIN
    UPDATE dbo.AccessCardGroupAccessRules
    SET
        Active = @Active,
        UpdatedAt = SYSDATETIME(),
        UpdatedBy = @ChangedBy
    WHERE AccessCardGroupId = @AccessCardGroupId
      AND AdGroupName = @AdGroupName;
END
ELSE
BEGIN
    INSERT INTO dbo.AccessCardGroupAccessRules
    (
        AccessCardGroupId,
        AdGroupName,
        Active,
        CreatedBy
    )
    VALUES
    (
        @AccessCardGroupId,
        @AdGroupName,
        @Active,
        @ChangedBy
    );
END;
";
        await cmd.ExecuteNonQueryAsync(HttpContext.RequestAborted);
        StatusMessage = $"AD access rule for '{NewAccessRule.AdGroupName}' was saved.";
        return RedirectToPage(new { id, Search, Site, ShowInactive });
    }

    public async Task<IActionResult> OnPostSetAccessRuleActiveAsync(int id, int ruleId, bool active)
    {
        await using var cn = await _connectionFactory.OpenAsync(HttpContext.RequestAborted);
        await using var cmd = cn.CreateCommand();
        cmd.Parameters.AddInt("@RuleId", ruleId);
        cmd.Parameters.AddInt("@AccessCardGroupId", id);
        cmd.Parameters.AddBit("@Active", active);
        cmd.Parameters.AddRequiredNVarChar("@ChangedBy", User.Identity?.Name ?? Environment.UserName, 300);
        cmd.CommandText = @"
UPDATE dbo.AccessCardGroupAccessRules
SET
    Active = @Active,
    UpdatedAt = SYSDATETIME(),
    UpdatedBy = @ChangedBy
WHERE Id = @RuleId
  AND AccessCardGroupId = @AccessCardGroupId;
";
        await cmd.ExecuteNonQueryAsync(HttpContext.RequestAborted);
        StatusMessage = active ? "AD access rule was activated." : "AD access rule was disabled.";
        return RedirectToPage(new { id, Search, Site, ShowInactive });
    }

    public async Task<IActionResult> OnPostDeleteAccessRuleAsync(int id, int ruleId)
    {
        await using var cn = await _connectionFactory.OpenAsync(HttpContext.RequestAborted);
        await using var cmd = cn.CreateCommand();
        cmd.Parameters.AddInt("@RuleId", ruleId);
        cmd.Parameters.AddInt("@AccessCardGroupId", id);
        cmd.CommandText = @"
DELETE FROM dbo.AccessCardGroupAccessRules
WHERE Id = @RuleId
  AND AccessCardGroupId = @AccessCardGroupId;
";
        await cmd.ExecuteNonQueryAsync(HttpContext.RequestAborted);
        StatusMessage = "AD access rule was deleted.";
        return RedirectToPage(new { id, Search, Site, ShowInactive });
    }

    private async Task LoadPageAsync(SqlConnection? existingConnection = null, bool loadSelected = true)
    {
        if (existingConnection is not null)
        {
            await LoadSitesAsync(existingConnection);
            await LoadGroupsAsync(existingConnection);
            if (loadSelected && SelectedGroupId.HasValue)
            {
                await LoadSelectedGroupAsync(existingConnection, SelectedGroupId.Value);
            }
            return;
        }

        await using var cn = await _connectionFactory.OpenAsync(HttpContext.RequestAborted);
        await LoadSitesAsync(cn);
        await LoadGroupsAsync(cn);
        if (loadSelected && SelectedGroupId.HasValue)
        {
            await LoadSelectedGroupAsync(cn, SelectedGroupId.Value);
        }
    }

    private async Task LoadSitesAsync(SqlConnection cn)
    {
        Sites.Clear();
        await using var cmd = cn.CreateCommand();
        cmd.CommandText = @"
SELECT DISTINCT Site
FROM dbo.AccessCardGroups
WHERE LEN(LTRIM(RTRIM(Site))) > 0
ORDER BY Site;
";
        await using var reader = await cmd.ExecuteReaderAsync(HttpContext.RequestAborted);
        while (await reader.ReadAsync(HttpContext.RequestAborted))
        {
            Sites.Add(reader.GetString(0));
        }
    }

    private async Task LoadGroupsAsync(SqlConnection cn)
    {
        Groups.Clear();
        await using var cmd = cn.CreateCommand();
        cmd.Parameters.AddNVarChar("@Search", Search, 400);
        cmd.Parameters.AddNVarChar("@Site", Site, 200);
        cmd.Parameters.AddBit("@ShowInactive", ShowInactive);
        cmd.CommandText = @"
SELECT
    g.Id,
    g.Site,
    g.DisplayName,
    g.ExternalGroupName,
    g.Description,
    g.Active,
    g.SortOrder,
    (SELECT COUNT_BIG(1) FROM dbo.AccessCardGroupAccessRules r WHERE r.AccessCardGroupId = g.Id AND r.Active = 1) AS ActiveRuleCount,
    (SELECT COUNT_BIG(1) FROM dbo.ADUserChangeQueueAccessCardGroups q WHERE q.AccessCardGroupId = g.Id) AS UsageCount
FROM dbo.AccessCardGroups g
WHERE (@ShowInactive = 1 OR g.Active = 1)
  AND (NULLIF(LTRIM(RTRIM(@Site)), N'') IS NULL OR g.Site = @Site)
  AND
  (
      NULLIF(LTRIM(RTRIM(@Search)), N'') IS NULL
      OR g.Site LIKE N'%' + @Search + N'%'
      OR g.DisplayName LIKE N'%' + @Search + N'%'
      OR g.ExternalGroupName LIKE N'%' + @Search + N'%'
      OR g.Description LIKE N'%' + @Search + N'%'
  )
ORDER BY g.Site, g.SortOrder, g.DisplayName;
";
        await using var reader = await cmd.ExecuteReaderAsync(HttpContext.RequestAborted);
        while (await reader.ReadAsync(HttpContext.RequestAborted))
        {
            Groups.Add(new GroupListItem
            {
                Id = reader.GetInt32(0),
                Site = reader.GetString(1),
                DisplayName = reader.GetString(2),
                ExternalGroupName = reader.GetString(3),
                Description = reader.IsDBNull(4) ? null : reader.GetString(4),
                Active = reader.GetBoolean(5),
                SortOrder = reader.GetInt32(6),
                ActiveRuleCount = Convert.ToInt64(reader.GetValue(7)),
                UsageCount = Convert.ToInt64(reader.GetValue(8))
            });
        }
    }

    private async Task LoadSelectedGroupAsync(SqlConnection cn, int id)
    {
        AccessRules.Clear();
        await using (var cmd = cn.CreateCommand())
        {
            cmd.Parameters.AddInt("@Id", id);
            cmd.CommandText = @"
SELECT
    Id,
    Site,
    DisplayName,
    ExternalGroupName,
    Description,
    Active,
    SortOrder
FROM dbo.AccessCardGroups
WHERE Id = @Id;
";
            await using var reader = await cmd.ExecuteReaderAsync(HttpContext.RequestAborted);
            if (await reader.ReadAsync(HttpContext.RequestAborted))
            {
                EditGroup = new GroupEditModel
                {
                    Id = reader.GetInt32(0),
                    Site = reader.GetString(1),
                    DisplayName = reader.GetString(2),
                    ExternalGroupName = reader.GetString(3),
                    Description = reader.IsDBNull(4) ? null : reader.GetString(4),
                    Active = reader.GetBoolean(5),
                    SortOrder = reader.GetInt32(6)
                };
                SelectedGroupId = EditGroup.Id;
            }
            else
            {
                SelectedGroupId = null;
                EditGroup = new GroupEditModel { Active = true, SortOrder = 100 };
                return;
            }
        }

        await using (var cmd = cn.CreateCommand())
        {
            cmd.Parameters.AddInt("@Id", id);
            cmd.CommandText = @"
SELECT
    Id,
    AdGroupName,
    Active,
    CreatedAt,
    CreatedBy,
    UpdatedAt,
    UpdatedBy
FROM dbo.AccessCardGroupAccessRules
WHERE AccessCardGroupId = @Id
ORDER BY Active DESC, AdGroupName;
";
            await using var reader = await cmd.ExecuteReaderAsync(HttpContext.RequestAborted);
            while (await reader.ReadAsync(HttpContext.RequestAborted))
            {
                AccessRules.Add(new AccessRuleRow
                {
                    Id = reader.GetInt32(0),
                    AdGroupName = reader.GetString(1),
                    Active = reader.GetBoolean(2),
                    CreatedAt = reader.GetDateTime(3),
                    CreatedBy = reader.IsDBNull(4) ? null : reader.GetString(4),
                    UpdatedAt = reader.IsDBNull(5) ? null : reader.GetDateTime(5),
                    UpdatedBy = reader.IsDBNull(6) ? null : reader.GetString(6)
                });
            }
        }
    }

    private static void AddGroupParameters(SqlCommand cmd, GroupEditModel group, string changedBy)
    {
        cmd.Parameters.AddRequiredNVarChar("@Site", group.Site!, 200);
        cmd.Parameters.AddRequiredNVarChar("@DisplayName", group.DisplayName!, 200);
        cmd.Parameters.AddRequiredNVarChar("@ExternalGroupName", group.ExternalGroupName!, 300);
        cmd.Parameters.AddNVarChar("@Description", group.Description, 1000);
        cmd.Parameters.AddBit("@Active", group.Active);
        cmd.Parameters.AddInt("@SortOrder", group.SortOrder);
        cmd.Parameters.AddRequiredNVarChar("@ChangedBy", changedBy, 300);
    }

    private static void NormalizeGroup(GroupEditModel group)
    {
        group.Site = group.Site?.Trim();
        group.DisplayName = group.DisplayName?.Trim();
        group.ExternalGroupName = group.ExternalGroupName?.Trim();
        group.Description = string.IsNullOrWhiteSpace(group.Description) ? null : group.Description.Trim();
    }

    private static string? ValidateGroup(GroupEditModel group)
    {
        if (string.IsNullOrWhiteSpace(group.Site)) return "Site is required.";
        if (string.IsNullOrWhiteSpace(group.DisplayName)) return "Display name is required.";
        if (string.IsNullOrWhiteSpace(group.ExternalGroupName)) return "External access-card group name is required.";
        return null;
    }

    public sealed class GroupListItem
    {
        public int Id { get; set; }
        public string Site { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public string ExternalGroupName { get; set; } = string.Empty;
        public string? Description { get; set; }
        public bool Active { get; set; }
        public int SortOrder { get; set; }
        public long ActiveRuleCount { get; set; }
        public long UsageCount { get; set; }
        public bool Restricted => ActiveRuleCount > 0;
    }

    public sealed class GroupEditModel
    {
        public int Id { get; set; }
        public string? Site { get; set; }
        public string? DisplayName { get; set; }
        public string? ExternalGroupName { get; set; }
        public string? Description { get; set; }
        public bool Active { get; set; } = true;
        public int SortOrder { get; set; } = 100;
    }

    public sealed class AccessRuleEditModel
    {
        public string? AdGroupName { get; set; }
        public bool Active { get; set; } = true;
    }

    public sealed class AccessRuleRow
    {
        public int Id { get; set; }
        public string AdGroupName { get; set; } = string.Empty;
        public bool Active { get; set; }
        public DateTime CreatedAt { get; set; }
        public string? CreatedBy { get; set; }
        public DateTime? UpdatedAt { get; set; }
        public string? UpdatedBy { get; set; }
    }
}

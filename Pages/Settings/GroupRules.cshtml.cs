using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Data.SqlClient;
using UserChangeQueueWeb.Services;

namespace UserChangeQueueWeb.Pages.Settings;

[Authorize]
public class GroupRulesModel : PageModel
{
    private readonly SqlConnectionFactory _connectionFactory;

    public GroupRulesModel(SqlConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    [BindProperty(SupportsGet = true)]
    public int? RuleSetId { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? Search { get; set; }

    [BindProperty(SupportsGet = true)]
    public bool ShowInactive { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? GroupSearch { get; set; }

    [BindProperty]
    public RuleSetEditModel Rule { get; set; } = new();

    [BindProperty]
    public ConditionEditModel NewCondition { get; set; } = new();

    [BindProperty]
    public TargetEditModel NewTarget { get; set; } = new();

    [TempData]
    public string? StatusMessage { get; set; }

    public List<RuleSetListItem> RuleSets { get; set; } = new();
    public List<RuleConditionItem> Conditions { get; set; } = new();
    public List<RuleTargetItem> Targets { get; set; } = new();
    public List<GroupSearchResult> GroupSearchResults { get; set; } = new();

    public IReadOnlyList<string> FieldNames { get; } = new[]
    {
        "Domain",
        "Company",
        "Department",
        "Title",
        "EmployeeType",
        "Office",
        "Country",
        "City",
        "ComputerType",
        "OfficeLicense",
        "AccessCard",
        "Enabled",
        "ManagerSamAccountName"
    };

    public IReadOnlyList<string> Operators { get; } = new[]
    {
        "Equals",
        "NotEquals",
        "Contains",
        "StartsWith",
        "EndsWith",
        "In",
        "IsEmpty",
        "IsNotEmpty"
    };

    public IReadOnlyList<string> MatchModes { get; } = new[] { "ALL", "ANY" };
    public IReadOnlyList<string> TargetActions { get; } = new[] { "INCLUDE", "EXCLUDE" };

    public async Task OnGetAsync()
    {
        await LoadPageAsync();
    }

    public async Task<IActionResult> OnGetNewAsync()
    {
        RuleSetId = null;
        GroupSearch = null;
        Rule = new RuleSetEditModel
        {
            Active = true,
            Priority = 100,
            MatchMode = "ALL",
            AppliesToAllUsers = false
        };

        await LoadPageAsync(loadSelectedRule: false);
        return Page();
    }

    public async Task<IActionResult> OnPostSaveRuleAsync()
    {
        NormalizeRuleForSave();

        if (string.IsNullOrWhiteSpace(Rule.RuleSetName))
        {
            StatusMessage = "Rule name is required.";
            await LoadPageAsync(loadSelectedRule: false);
            return Page();
        }

        if (!MatchModes.Contains(Rule.MatchMode, StringComparer.OrdinalIgnoreCase))
        {
            StatusMessage = "Match mode must be ALL or ANY.";
            await LoadPageAsync(loadSelectedRule: false);
            return Page();
        }

        var changedBy = User.Identity?.Name ?? Environment.UserName;
        await using var cn = await _connectionFactory.OpenAsync();

        if (Rule.RuleSetId > 0)
        {
            await using var cmd = cn.CreateCommand();
            cmd.CommandText = @"
UPDATE dbo.ADGroupRuleSets
SET
    RuleSetName = @RuleSetName,
    Description = @Description,
    Active = @Active,
    Priority = @Priority,
    MatchMode = @MatchMode,
    AppliesToAllUsers = @AppliesToAllUsers,
    EffectiveFrom = @EffectiveFrom,
    EffectiveTo = @EffectiveTo,
    UpdatedAt = SYSDATETIME(),
    UpdatedBy = @ChangedBy
WHERE RuleSetId = @RuleSetId;
";
            AddRuleParameters(cmd, Rule, changedBy);
            cmd.Parameters.AddInt("@RuleSetId", Rule.RuleSetId);
            await cmd.ExecuteNonQueryAsync();
            StatusMessage = $"Saved rule '{Rule.RuleSetName}'.";
        }
        else
        {
            await using var cmd = cn.CreateCommand();
            cmd.CommandText = @"
INSERT INTO dbo.ADGroupRuleSets
(
    RuleSetName,
    Description,
    Active,
    Priority,
    MatchMode,
    AppliesToAllUsers,
    EffectiveFrom,
    EffectiveTo,
    CreatedBy
)
OUTPUT INSERTED.RuleSetId
VALUES
(
    @RuleSetName,
    @Description,
    @Active,
    @Priority,
    @MatchMode,
    @AppliesToAllUsers,
    @EffectiveFrom,
    @EffectiveTo,
    @ChangedBy
);
";
            AddRuleParameters(cmd, Rule, changedBy);
            Rule.RuleSetId = Convert.ToInt32(await cmd.ExecuteScalarAsync());
            StatusMessage = $"Created rule '{Rule.RuleSetName}'.";
        }

        return RedirectToPage(new
        {
            ruleSetId = Rule.RuleSetId,
            Search,
            ShowInactive,
            GroupSearch
        });
    }

    public async Task<IActionResult> OnPostSetActiveAsync(int ruleSetId, bool active)
    {
        await using var cn = await _connectionFactory.OpenAsync();

        await using var cmd = cn.CreateCommand();
        cmd.CommandText = @"
UPDATE dbo.ADGroupRuleSets
SET
    Active = @Active,
    UpdatedAt = SYSDATETIME(),
    UpdatedBy = @ChangedBy
WHERE RuleSetId = @RuleSetId;
";
        cmd.Parameters.AddInt("@RuleSetId", ruleSetId);
        cmd.Parameters.AddBit("@Active", active);
        cmd.Parameters.AddRequiredNVarChar("@ChangedBy", User.Identity?.Name ?? Environment.UserName, 300);
        await cmd.ExecuteNonQueryAsync();

        StatusMessage = active ? "Rule was activated." : "Rule was disabled.";
        return RedirectToPage(new { ruleSetId, Search, ShowInactive, GroupSearch });
    }

    public async Task<IActionResult> OnPostAddConditionAsync()
    {
        if (!RuleSetId.HasValue || RuleSetId.Value <= 0)
        {
            StatusMessage = "Save or select a rule before adding conditions.";
            return RedirectToPage(new { Search, ShowInactive });
        }

        NormalizeConditionForSave();

        if (!FieldNames.Contains(NewCondition.FieldName, StringComparer.OrdinalIgnoreCase))
        {
            StatusMessage = "Invalid condition field.";
            return RedirectToPage(new { ruleSetId = RuleSetId, Search, ShowInactive, GroupSearch });
        }

        if (!Operators.Contains(NewCondition.Operator, StringComparer.OrdinalIgnoreCase))
        {
            StatusMessage = "Invalid condition operator.";
            return RedirectToPage(new { ruleSetId = RuleSetId, Search, ShowInactive, GroupSearch });
        }

        var valueRequired = !NewCondition.Operator.Equals("IsEmpty", StringComparison.OrdinalIgnoreCase)
            && !NewCondition.Operator.Equals("IsNotEmpty", StringComparison.OrdinalIgnoreCase);

        if (valueRequired && string.IsNullOrWhiteSpace(NewCondition.MatchValue))
        {
            StatusMessage = "Match value is required for this operator.";
            return RedirectToPage(new { ruleSetId = RuleSetId, Search, ShowInactive, GroupSearch });
        }

        await using var cn = await _connectionFactory.OpenAsync();

        await using var cmd = cn.CreateCommand();
        cmd.CommandText = @"
INSERT INTO dbo.ADGroupRuleConditions
(
    RuleSetId,
    FieldName,
    Operator,
    MatchValue,
    MatchValue2
)
VALUES
(
    @RuleSetId,
    @FieldName,
    @Operator,
    @MatchValue,
    @MatchValue2
);
";
        cmd.Parameters.AddInt("@RuleSetId", RuleSetId.Value);
        cmd.Parameters.AddRequiredNVarChar("@FieldName", NewCondition.FieldName, 100);
        cmd.Parameters.AddRequiredNVarChar("@Operator", NewCondition.Operator, 30);
        cmd.Parameters.AddNVarChar("@MatchValue", NewCondition.MatchValue, 400);
        cmd.Parameters.AddNVarChar("@MatchValue2", NewCondition.MatchValue2, 400);
        await cmd.ExecuteNonQueryAsync();

        StatusMessage = "Condition added.";
        return RedirectToPage(new { ruleSetId = RuleSetId, Search, ShowInactive, GroupSearch });
    }

    public async Task<IActionResult> OnPostDeleteConditionAsync(int conditionId, int ruleSetId)
    {
        await using var cn = await _connectionFactory.OpenAsync();

        await using var cmd = cn.CreateCommand();
        cmd.CommandText = @"
DELETE FROM dbo.ADGroupRuleConditions
WHERE ConditionId = @ConditionId
  AND RuleSetId = @RuleSetId;
";
        cmd.Parameters.AddInt("@ConditionId", conditionId);
        cmd.Parameters.AddInt("@RuleSetId", ruleSetId);
        await cmd.ExecuteNonQueryAsync();

        StatusMessage = "Condition deleted.";
        return RedirectToPage(new { ruleSetId, Search, ShowInactive, GroupSearch });
    }

    public async Task<IActionResult> OnPostAddTargetAsync(int ruleSetId, Guid groupObjectGuid)
    {
        if (ruleSetId <= 0 || groupObjectGuid == Guid.Empty)
        {
            StatusMessage = "Select a saved rule and a valid AD group.";
            return RedirectToPage(new { ruleSetId, Search, ShowInactive, GroupSearch });
        }

        NormalizeTargetForSave();

        if (!TargetActions.Contains(NewTarget.Action, StringComparer.OrdinalIgnoreCase))
        {
            StatusMessage = "Invalid target action.";
            return RedirectToPage(new { ruleSetId, Search, ShowInactive, GroupSearch });
        }

        await using var cn = await _connectionFactory.OpenAsync();

        using var tx = (SqlTransaction)await cn.BeginTransactionAsync();

        try
        {
            await UpsertGroupMetadataAsync(cn, groupObjectGuid, NewTarget.ApprovalRequired, tx);

            await using (var deleteOtherAction = cn.CreateCommand())
            {
                deleteOtherAction.Transaction = tx;
                deleteOtherAction.CommandText = @"
DELETE FROM dbo.ADGroupRuleTargets
WHERE RuleSetId = @RuleSetId
  AND GroupObjectGUID = @GroupObjectGUID
  AND Action <> @Action;
";
                deleteOtherAction.Parameters.AddInt("@RuleSetId", ruleSetId);
                deleteOtherAction.Parameters.AddUniqueIdentifier("@GroupObjectGUID", groupObjectGuid);
                deleteOtherAction.Parameters.AddRequiredNVarChar("@Action", NewTarget.Action, 20);
                await deleteOtherAction.ExecuteNonQueryAsync();
            }

            await using (var cmd = cn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = @"
IF EXISTS
(
    SELECT 1
    FROM dbo.ADGroupRuleTargets
    WHERE RuleSetId = @RuleSetId
      AND GroupObjectGUID = @GroupObjectGUID
      AND Action = @Action
)
BEGIN
    UPDATE dbo.ADGroupRuleTargets
    SET
        Required = @Required,
        Notes = @Notes
    WHERE RuleSetId = @RuleSetId
      AND GroupObjectGUID = @GroupObjectGUID
      AND Action = @Action;
END
ELSE
BEGIN
    INSERT INTO dbo.ADGroupRuleTargets
    (
        RuleSetId,
        GroupObjectGUID,
        Action,
        Required,
        Notes
    )
    VALUES
    (
        @RuleSetId,
        @GroupObjectGUID,
        @Action,
        @Required,
        @Notes
    );
END;
";
                cmd.Parameters.AddInt("@RuleSetId", ruleSetId);
                cmd.Parameters.AddUniqueIdentifier("@GroupObjectGUID", groupObjectGuid);
                cmd.Parameters.AddRequiredNVarChar("@Action", NewTarget.Action, 20);
                cmd.Parameters.AddBit("@Required", NewTarget.Required);
                cmd.Parameters.AddNVarChar("@Notes", NewTarget.Notes, 1000);
                await cmd.ExecuteNonQueryAsync();
            }

            tx.Commit();
            StatusMessage = "Target group added to rule.";
        }
        catch
        {
            try
            {
                tx.Rollback();
            }
            catch
            {
                // Ignore rollback errors and rethrow the original exception.
            }

            throw;
        }

        return RedirectToPage(new { ruleSetId, Search, ShowInactive, GroupSearch });
    }

    public async Task<IActionResult> OnPostUpdateTargetAsync(
        int targetId,
        int ruleSetId,
        string action,
        bool required,
        bool approvalRequired,
        string? notes)
    {
        var normalizedAction = NormalizeAction(action);

        if (!TargetActions.Contains(normalizedAction, StringComparer.OrdinalIgnoreCase))
        {
            StatusMessage = "Invalid target action.";
            return RedirectToPage(new { ruleSetId, Search, ShowInactive, GroupSearch });
        }

        await using var cn = await _connectionFactory.OpenAsync();

        using var tx = (SqlTransaction)await cn.BeginTransactionAsync();

        try
        {
            Guid groupObjectGuid;

            await using (var lookup = cn.CreateCommand())
            {
                lookup.Transaction = tx;
                lookup.CommandText = @"
SELECT GroupObjectGUID
FROM dbo.ADGroupRuleTargets
WHERE TargetId = @TargetId
  AND RuleSetId = @RuleSetId;
";
                lookup.Parameters.AddInt("@TargetId", targetId);
                lookup.Parameters.AddInt("@RuleSetId", ruleSetId);
                var value = await lookup.ExecuteScalarAsync();

                if (value == null || value == DBNull.Value)
                {
                    tx.Rollback();
                    StatusMessage = "Target was not found.";
                    return RedirectToPage(new { ruleSetId, Search, ShowInactive, GroupSearch });
                }

                groupObjectGuid = (Guid)value;
            }

            await UpsertGroupMetadataAsync(cn, groupObjectGuid, approvalRequired, tx);

            await using (var cmd = cn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = @"
UPDATE dbo.ADGroupRuleTargets
SET
    Action = @Action,
    Required = @Required,
    Notes = @Notes
WHERE TargetId = @TargetId
  AND RuleSetId = @RuleSetId;
";
                cmd.Parameters.AddInt("@TargetId", targetId);
                cmd.Parameters.AddInt("@RuleSetId", ruleSetId);
                cmd.Parameters.AddRequiredNVarChar("@Action", normalizedAction, 20);
                cmd.Parameters.AddBit("@Required", required);
                cmd.Parameters.AddNVarChar("@Notes", notes, 1000);
                await cmd.ExecuteNonQueryAsync();
            }

            tx.Commit();
            StatusMessage = "Target group updated.";
        }
        catch
        {
            try
            {
                tx.Rollback();
            }
            catch
            {
                // Ignore rollback errors and rethrow the original exception.
            }

            throw;
        }

        return RedirectToPage(new { ruleSetId, Search, ShowInactive, GroupSearch });
    }

    public async Task<IActionResult> OnPostDeleteTargetAsync(int targetId, int ruleSetId)
    {
        await using var cn = await _connectionFactory.OpenAsync();

        await using var cmd = cn.CreateCommand();
        cmd.CommandText = @"
DELETE FROM dbo.ADGroupRuleTargets
WHERE TargetId = @TargetId
  AND RuleSetId = @RuleSetId;
";
        cmd.Parameters.AddInt("@TargetId", targetId);
        cmd.Parameters.AddInt("@RuleSetId", ruleSetId);
        await cmd.ExecuteNonQueryAsync();

        StatusMessage = "Target group removed from rule.";
        return RedirectToPage(new { ruleSetId, Search, ShowInactive, GroupSearch });
    }

    private async Task LoadPageAsync(bool loadSelectedRule = true)
    {
        await using var cn = await _connectionFactory.OpenAsync();

        RuleSets = await LoadRuleSetsAsync(cn);

        if (loadSelectedRule && RuleSetId.HasValue)
        {
            var selectedRule = await LoadRuleAsync(cn, RuleSetId.Value);
            if (selectedRule != null)
            {
                Rule = selectedRule;
                Conditions = await LoadConditionsAsync(cn, RuleSetId.Value);
                Targets = await LoadTargetsAsync(cn, RuleSetId.Value);
            }
        }

        if (!string.IsNullOrWhiteSpace(GroupSearch))
        {
            GroupSearchResults = await SearchGroupsAsync(cn, GroupSearch);
        }
    }

    private async Task<List<RuleSetListItem>> LoadRuleSetsAsync(SqlConnection cn)
    {
        await using var cmd = cn.CreateCommand();
        cmd.CommandText = @"
SELECT
    rs.RuleSetId,
    rs.RuleSetName,
    ISNULL(rs.Description, '') AS Description,
    rs.Active,
    rs.Priority,
    rs.MatchMode,
    rs.AppliesToAllUsers,
    COUNT(DISTINCT c.ConditionId) AS ConditionCount,
    COUNT(DISTINCT t.TargetId) AS TargetCount
FROM dbo.ADGroupRuleSets rs
LEFT JOIN dbo.ADGroupRuleConditions c
    ON c.RuleSetId = rs.RuleSetId
LEFT JOIN dbo.ADGroupRuleTargets t
    ON t.RuleSetId = rs.RuleSetId
WHERE (@ShowInactive = 1 OR rs.Active = 1)
  AND
  (
      @Search IS NULL
      OR rs.RuleSetName LIKE '%' + @Search + '%'
      OR ISNULL(rs.Description, '') LIKE '%' + @Search + '%'
  )
GROUP BY
    rs.RuleSetId,
    rs.RuleSetName,
    rs.Description,
    rs.Active,
    rs.Priority,
    rs.MatchMode,
    rs.AppliesToAllUsers
ORDER BY
    rs.Active DESC,
    rs.Priority,
    rs.RuleSetName;
";
        cmd.Parameters.AddBit("@ShowInactive", ShowInactive);
        cmd.Parameters.AddNVarChar("@Search", Search, 200);

        var rows = new List<RuleSetListItem>();
        await using var reader = await cmd.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {
            rows.Add(new RuleSetListItem
            {
                RuleSetId = reader.GetInt32(0),
                RuleSetName = reader.GetString(1),
                Description = reader.GetString(2),
                Active = reader.GetBoolean(3),
                Priority = reader.GetInt32(4),
                MatchMode = reader.GetString(5),
                AppliesToAllUsers = reader.GetBoolean(6),
                ConditionCount = reader.GetInt32(7),
                TargetCount = reader.GetInt32(8)
            });
        }

        return rows;
    }

    private static async Task<RuleSetEditModel?> LoadRuleAsync(SqlConnection cn, int ruleSetId)
    {
        await using var cmd = cn.CreateCommand();
        cmd.CommandText = @"
SELECT
    RuleSetId,
    RuleSetName,
    ISNULL(Description, '') AS Description,
    Active,
    Priority,
    MatchMode,
    AppliesToAllUsers,
    EffectiveFrom,
    EffectiveTo
FROM dbo.ADGroupRuleSets
WHERE RuleSetId = @RuleSetId;
";
        cmd.Parameters.AddInt("@RuleSetId", ruleSetId);

        await using var reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            return null;
        }

        return new RuleSetEditModel
        {
            RuleSetId = reader.GetInt32(0),
            RuleSetName = reader.GetString(1),
            Description = reader.GetString(2),
            Active = reader.GetBoolean(3),
            Priority = reader.GetInt32(4),
            MatchMode = reader.GetString(5),
            AppliesToAllUsers = reader.GetBoolean(6),
            EffectiveFrom = reader.IsDBNull(7) ? null : reader.GetDateTime(7),
            EffectiveTo = reader.IsDBNull(8) ? null : reader.GetDateTime(8)
        };
    }

    private static async Task<List<RuleConditionItem>> LoadConditionsAsync(SqlConnection cn, int ruleSetId)
    {
        await using var cmd = cn.CreateCommand();
        cmd.CommandText = @"
SELECT
    ConditionId,
    FieldName,
    Operator,
    MatchValue,
    MatchValue2
FROM dbo.ADGroupRuleConditions
WHERE RuleSetId = @RuleSetId
ORDER BY ConditionId;
";
        cmd.Parameters.AddInt("@RuleSetId", ruleSetId);

        var rows = new List<RuleConditionItem>();
        await using var reader = await cmd.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {
            rows.Add(new RuleConditionItem
            {
                ConditionId = reader.GetInt32(0),
                FieldName = reader.GetString(1),
                Operator = reader.GetString(2),
                MatchValue = reader.IsDBNull(3) ? null : reader.GetString(3),
                MatchValue2 = reader.IsDBNull(4) ? null : reader.GetString(4)
            });
        }

        return rows;
    }

    private static async Task<List<RuleTargetItem>> LoadTargetsAsync(SqlConnection cn, int ruleSetId)
    {
        await using var cmd = cn.CreateCommand();
        cmd.CommandText = @"
SELECT
    t.TargetId,
    t.GroupObjectGUID,
    t.Action,
    t.Required,
    ISNULL(t.Notes, '') AS Notes,
    ISNULL(g.SamAccountName, '') AS SamAccountName,
    ISNULL(g.Name, '') AS GroupName,
    ISNULL(g.DistinguishedName, '') AS DistinguishedName,
    ISNULL(m.ApprovalRequired, 1) AS ApprovalRequired,
    ISNULL(m.RiskLevel, '') AS RiskLevel,
    ISNULL(m.Purpose, '') AS Purpose
FROM dbo.ADGroupRuleTargets t
INNER JOIN dbo.ADGroups g
    ON g.ObjectGUID = t.GroupObjectGUID
LEFT JOIN dbo.ADGroupMetadata m
    ON m.GroupObjectGUID = t.GroupObjectGUID
WHERE t.RuleSetId = @RuleSetId
ORDER BY
    ISNULL(g.SamAccountName, ''),
    ISNULL(g.Name, '');
";
        cmd.Parameters.AddInt("@RuleSetId", ruleSetId);

        var rows = new List<RuleTargetItem>();
        await using var reader = await cmd.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {
            rows.Add(new RuleTargetItem
            {
                TargetId = reader.GetInt32(0),
                GroupObjectGuid = reader.GetGuid(1),
                Action = reader.GetString(2),
                Required = reader.GetBoolean(3),
                Notes = reader.GetString(4),
                SamAccountName = reader.GetString(5),
                Name = reader.GetString(6),
                DistinguishedName = reader.GetString(7),
                ApprovalRequired = reader.GetBoolean(8),
                RiskLevel = reader.GetString(9),
                Purpose = reader.GetString(10)
            });
        }

        return rows;
    }

    private static async Task<List<GroupSearchResult>> SearchGroupsAsync(SqlConnection cn, string search)
    {
        await using var cmd = cn.CreateCommand();
        cmd.CommandText = @"
SELECT TOP (50)
    ObjectGUID,
    ISNULL(SamAccountName, '') AS SamAccountName,
    ISNULL(Name, '') AS GroupName,
    ISNULL(CN, '') AS CN,
    ISNULL(DistinguishedName, '') AS DistinguishedName,
    ISNULL(Description, '') AS Description
FROM dbo.ADGroups
WHERE ISNULL(IsDeleted, 0) = 0
  AND
  (
      SamAccountName LIKE '%' + @Search + '%'
      OR Name LIKE '%' + @Search + '%'
      OR CN LIKE '%' + @Search + '%'
      OR DistinguishedName LIKE '%' + @Search + '%'
      OR Description LIKE '%' + @Search + '%'
  )
ORDER BY
    ISNULL(SamAccountName, ''),
    ISNULL(Name, '');
";
        cmd.Parameters.AddRequiredNVarChar("@Search", search.Trim(), 300);

        var rows = new List<GroupSearchResult>();
        await using var reader = await cmd.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {
            rows.Add(new GroupSearchResult
            {
                ObjectGuid = reader.GetGuid(0),
                SamAccountName = reader.GetString(1),
                Name = reader.GetString(2),
                CN = reader.GetString(3),
                DistinguishedName = reader.GetString(4),
                Description = reader.GetString(5)
            });
        }

        return rows;
    }

    private async Task UpsertGroupMetadataAsync(
        SqlConnection cn,
        Guid groupObjectGuid,
        bool approvalRequired,
        SqlTransaction tx)
    {
        await using var cmd = cn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = @"
IF EXISTS
(
    SELECT 1
    FROM dbo.ADGroupMetadata
    WHERE GroupObjectGUID = @GroupObjectGUID
)
BEGIN
    UPDATE dbo.ADGroupMetadata
    SET
        VisibleInRequestForms = 1,
        AllowAutomaticRecommendation = 1,
        ApprovalRequired = @ApprovalRequired,
        Active = 1,
        UpdatedAt = SYSDATETIME(),
        UpdatedBy = @ChangedBy
    WHERE GroupObjectGUID = @GroupObjectGUID;
END
ELSE
BEGIN
    INSERT INTO dbo.ADGroupMetadata
    (
        GroupObjectGUID,
        VisibleInRequestForms,
        AllowAutomaticRecommendation,
        AllowManualSelection,
        ApprovalRequired,
        Active,
        CreatedBy
    )
    VALUES
    (
        @GroupObjectGUID,
        1,
        1,
        0,
        @ApprovalRequired,
        1,
        @ChangedBy
    );
END;
";
        cmd.Parameters.AddUniqueIdentifier("@GroupObjectGUID", groupObjectGuid);
        cmd.Parameters.AddBit("@ApprovalRequired", approvalRequired);
        cmd.Parameters.AddRequiredNVarChar("@ChangedBy", User.Identity?.Name ?? Environment.UserName, 300);
        await cmd.ExecuteNonQueryAsync();
    }

    private static void AddRuleParameters(SqlCommand cmd, RuleSetEditModel rule, string changedBy)
    {
        cmd.Parameters.AddRequiredNVarChar("@RuleSetName", rule.RuleSetName.Trim(), 200);
        cmd.Parameters.AddNVarChar("@Description", rule.Description, 1000);
        cmd.Parameters.AddBit("@Active", rule.Active);
        cmd.Parameters.AddInt("@Priority", rule.Priority);
        cmd.Parameters.AddRequiredNVarChar("@MatchMode", rule.MatchMode, 10);
        cmd.Parameters.AddBit("@AppliesToAllUsers", rule.AppliesToAllUsers);
        cmd.Parameters.AddNullableDate("@EffectiveFrom", rule.EffectiveFrom);
        cmd.Parameters.AddNullableDate("@EffectiveTo", rule.EffectiveTo);
        cmd.Parameters.AddRequiredNVarChar("@ChangedBy", changedBy, 300);
    }

    private void NormalizeRuleForSave()
    {
        Rule.RuleSetName = Rule.RuleSetName?.Trim() ?? "";
        Rule.Description = string.IsNullOrWhiteSpace(Rule.Description) ? null : Rule.Description.Trim();
        Rule.MatchMode = string.IsNullOrWhiteSpace(Rule.MatchMode) ? "ALL" : Rule.MatchMode.Trim().ToUpperInvariant();
    }

    private void NormalizeConditionForSave()
    {
        NewCondition.FieldName = NewCondition.FieldName?.Trim() ?? "";
        NewCondition.Operator = NewCondition.Operator?.Trim() ?? "";
        NewCondition.MatchValue = string.IsNullOrWhiteSpace(NewCondition.MatchValue) ? null : NewCondition.MatchValue.Trim();
        NewCondition.MatchValue2 = string.IsNullOrWhiteSpace(NewCondition.MatchValue2) ? null : NewCondition.MatchValue2.Trim();
    }

    private void NormalizeTargetForSave()
    {
        NewTarget.Action = NormalizeAction(NewTarget.Action);
        NewTarget.Notes = string.IsNullOrWhiteSpace(NewTarget.Notes) ? null : NewTarget.Notes.Trim();
    }

    private static string NormalizeAction(string? action)
    {
        return string.IsNullOrWhiteSpace(action)
            ? "INCLUDE"
            : action.Trim().ToUpperInvariant();
    }

    public sealed class RuleSetListItem
    {
        public int RuleSetId { get; set; }
        public string RuleSetName { get; set; } = "";
        public string Description { get; set; } = "";
        public bool Active { get; set; }
        public int Priority { get; set; }
        public string MatchMode { get; set; } = "ALL";
        public bool AppliesToAllUsers { get; set; }
        public int ConditionCount { get; set; }
        public int TargetCount { get; set; }
    }

    public sealed class RuleSetEditModel
    {
        public int RuleSetId { get; set; }
        public string RuleSetName { get; set; } = "";
        public string? Description { get; set; }
        public bool Active { get; set; } = true;
        public int Priority { get; set; } = 100;
        public string MatchMode { get; set; } = "ALL";
        public bool AppliesToAllUsers { get; set; }
        public DateTime? EffectiveFrom { get; set; }
        public DateTime? EffectiveTo { get; set; }
    }

    public class ConditionEditModel
    {
        public string FieldName { get; set; } = "Domain";
        public string Operator { get; set; } = "Equals";
        public string? MatchValue { get; set; }
        public string? MatchValue2 { get; set; }
    }

    public sealed class RuleConditionItem : ConditionEditModel
    {
        public int ConditionId { get; set; }
    }

    public class TargetEditModel
    {
        public string Action { get; set; } = "INCLUDE";
        public bool Required { get; set; } = true;
        public bool ApprovalRequired { get; set; }
        public string? Notes { get; set; }
    }

    public sealed class RuleTargetItem : TargetEditModel
    {
        public int TargetId { get; set; }
        public Guid GroupObjectGuid { get; set; }
        public string SamAccountName { get; set; } = "";
        public string Name { get; set; } = "";
        public string DistinguishedName { get; set; } = "";
        public string RiskLevel { get; set; } = "";
        public string Purpose { get; set; } = "";
    }

    public sealed class GroupSearchResult
    {
        public Guid ObjectGuid { get; set; }
        public string SamAccountName { get; set; } = "";
        public string Name { get; set; } = "";
        public string CN { get; set; } = "";
        public string DistinguishedName { get; set; } = "";
        public string Description { get; set; } = "";
    }
}

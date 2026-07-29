using Microsoft.Data.SqlClient;

namespace UserChangeQueueWeb.Services;

public sealed class ADGroupRuleService
{
    private readonly SqlConnectionFactory _connectionFactory;

    public ADGroupRuleService(SqlConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    public sealed class GroupRuleContext
    {
        public string? Domain { get; set; }
        public string? Company { get; set; }
        public string? Department { get; set; }
        public string? Title { get; set; }
        public string? EmployeeType { get; set; }
        public string? Office { get; set; }
        public string? Country { get; set; }
        public string? City { get; set; }
        public string? ComputerType { get; set; }
        public string? OfficeLicense { get; set; }
        public string? ManagerSamAccountName { get; set; }
        public bool AccessCard { get; set; }
        public bool Enabled { get; set; } = true;
    }

    public sealed class RecommendedGroup
    {
        public Guid GroupObjectGuid { get; set; }
        public string SamAccountName { get; set; } = "";
        public string Name { get; set; } = "";
        public string DistinguishedName { get; set; } = "";
        public string RuleSetName { get; set; } = "";
        public int? RuleSetId { get; set; }
        public string Action { get; set; } = "ADD";
        public bool Required { get; set; }
        public bool Selected { get; set; } = true;
        public bool ApprovalRequired { get; set; }
        public string Reason { get; set; } = "";
    }

    private sealed class RuleSet
    {
        public int RuleSetId { get; set; }
        public string RuleSetName { get; set; } = "";
        public string Description { get; set; } = "";
        public int Priority { get; set; }
        public string MatchMode { get; set; } = "ALL";
        public bool AppliesToAllUsers { get; set; }
        public List<RuleCondition> Conditions { get; } = new();
        public List<RuleTarget> Targets { get; } = new();
    }

    private sealed class RuleCondition
    {
        public string FieldName { get; set; } = "";
        public string Operator { get; set; } = "";
        public string? MatchValue { get; set; }
        public string? MatchValue2 { get; set; }
    }

    private sealed class RuleTarget
    {
        public Guid GroupObjectGuid { get; set; }
        public string SamAccountName { get; set; } = "";
        public string Name { get; set; } = "";
        public string DistinguishedName { get; set; } = "";
        public string TargetAction { get; set; } = "INCLUDE";
        public bool Required { get; set; }
        public bool ApprovalRequired { get; set; }
        public string Notes { get; set; } = "";
    }

    public async Task<List<RecommendedGroup>> GetRecommendedGroupsAsync(GroupRuleContext context)
    {
        await using var connection = await _connectionFactory.OpenAsync();

        return await GetRecommendedGroupsAsync(connection, context);
    }

    public async Task<List<RecommendedGroup>> GetRecommendedGroupsAsync(
        SqlConnection connection,
        GroupRuleContext context,
        SqlTransaction? transaction = null)
    {
        if (!await RuleTablesExistAsync(connection, transaction))
        {
            return new List<RecommendedGroup>();
        }

        var ruleSets = await LoadActiveRuleSetsAsync(connection, transaction);

        if (ruleSets.Count == 0)
        {
            return new List<RecommendedGroup>();
        }

        await LoadRuleConditionsAsync(connection, ruleSets, transaction);
        await LoadRuleTargetsAsync(connection, ruleSets, transaction);

        var included = new Dictionary<Guid, RecommendedGroup>();
        var excluded = new HashSet<Guid>();

        foreach (var ruleSet in ruleSets.OrderBy(r => r.Priority).ThenBy(r => r.RuleSetName))
        {
            if (!RuleMatches(ruleSet, context))
            {
                continue;
            }

            foreach (var target in ruleSet.Targets)
            {
                if (target.TargetAction.Equals("EXCLUDE", StringComparison.OrdinalIgnoreCase))
                {
                    excluded.Add(target.GroupObjectGuid);
                    included.Remove(target.GroupObjectGuid);
                    continue;
                }

                if (excluded.Contains(target.GroupObjectGuid))
                {
                    continue;
                }

                if (included.TryGetValue(target.GroupObjectGuid, out var existing))
                {
                    existing.Required = existing.Required || target.Required;
                    existing.ApprovalRequired = existing.ApprovalRequired || target.ApprovalRequired;

                    if (!existing.RuleSetName.Contains(ruleSet.RuleSetName, StringComparison.OrdinalIgnoreCase))
                    {
                        existing.RuleSetName = string.Join(", ", new[] { existing.RuleSetName, ruleSet.RuleSetName }.Where(v => !string.IsNullOrWhiteSpace(v)));
                    }

                    if (!string.IsNullOrWhiteSpace(target.Notes) && !existing.Reason.Contains(target.Notes, StringComparison.OrdinalIgnoreCase))
                    {
                        existing.Reason = string.IsNullOrWhiteSpace(existing.Reason)
                            ? target.Notes
                            : existing.Reason + " " + target.Notes;
                    }

                    continue;
                }

                included[target.GroupObjectGuid] = new RecommendedGroup
                {
                    GroupObjectGuid = target.GroupObjectGuid,
                    SamAccountName = target.SamAccountName,
                    Name = target.Name,
                    DistinguishedName = target.DistinguishedName,
                    RuleSetName = ruleSet.RuleSetName,
                    RuleSetId = ruleSet.RuleSetId,
                    Action = "ADD",
                    Required = target.Required,
                    Selected = true,
                    ApprovalRequired = target.ApprovalRequired,
                    Reason = !string.IsNullOrWhiteSpace(target.Notes)
                        ? target.Notes
                        : ruleSet.Description
                };
            }
        }

        return included.Values
            .OrderBy(g => g.SamAccountName)
            .ThenBy(g => g.Name)
            .ToList();
    }

    public async Task<List<RecommendedGroup>> LoadQueuedGroupsAsync(long requestId)
    {
        await using var connection = await _connectionFactory.OpenAsync();

        return await LoadQueuedGroupsAsync(connection, requestId);
    }

    public async Task<List<RecommendedGroup>> LoadQueuedGroupsAsync(
        SqlConnection connection,
        long requestId,
        SqlTransaction? transaction = null)
    {
        if (!await QueueGroupsTableExistsAsync(connection, transaction))
        {
            return new List<RecommendedGroup>();
        }

        await using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = @"
SELECT
    qg.GroupObjectGUID,
    ISNULL(qg.SnapshotGroupSamAccountName, ISNULL(g.SamAccountName, '')) AS SamAccountName,
    ISNULL(qg.SnapshotGroupName, ISNULL(g.Name, '')) AS GroupName,
    ISNULL(qg.SnapshotGroupDistinguishedName, ISNULL(g.DistinguishedName, '')) AS DistinguishedName,
    ISNULL(rs.RuleSetName, '') AS RuleSetName,
    qg.RuleSetId,
    qg.Action,
    qg.Required,
    qg.Selected,
    qg.ApprovalRequired,
    ISNULL(qg.Reason, '') AS Reason
FROM dbo.ADUserChangeQueueGroups qg
LEFT JOIN dbo.ADGroups g
    ON g.ObjectGUID = qg.GroupObjectGUID
LEFT JOIN dbo.ADGroupRuleSets rs
    ON rs.RuleSetId = qg.RuleSetId
WHERE qg.RequestId = @RequestId
ORDER BY
    ISNULL(qg.SnapshotGroupSamAccountName, ISNULL(g.SamAccountName, '')),
    qg.Id;
";
        cmd.Parameters.AddBigInt("@RequestId", requestId);

        var groups = new List<RecommendedGroup>();

        await using var reader = await cmd.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {
            groups.Add(new RecommendedGroup
            {
                GroupObjectGuid = reader.GetGuid(0),
                SamAccountName = reader.IsDBNull(1) ? "" : reader.GetString(1),
                Name = reader.IsDBNull(2) ? "" : reader.GetString(2),
                DistinguishedName = reader.IsDBNull(3) ? "" : reader.GetString(3),
                RuleSetName = reader.IsDBNull(4) ? "" : reader.GetString(4),
                RuleSetId = reader.IsDBNull(5) ? null : reader.GetInt32(5),
                Action = reader.IsDBNull(6) ? "ADD" : reader.GetString(6),
                Required = !reader.IsDBNull(7) && reader.GetBoolean(7),
                Selected = reader.IsDBNull(8) || reader.GetBoolean(8),
                ApprovalRequired = !reader.IsDBNull(9) && reader.GetBoolean(9),
                Reason = reader.IsDBNull(10) ? "" : reader.GetString(10)
            });
        }

        return groups;
    }

    public async Task ReplaceRuleGeneratedQueueGroupsAsync(
        SqlConnection connection,
        long requestId,
        IReadOnlyCollection<RecommendedGroup> recommendedGroups,
        string changedBy,
        SqlTransaction? transaction = null)
    {
        if (!await QueueGroupsTableExistsAsync(connection, transaction) ||
            !await RuleTablesExistAsync(connection, transaction))
        {
            return;
        }

        await using (var deleteCmd = connection.CreateCommand())
        {
            deleteCmd.Transaction = transaction;
            deleteCmd.CommandText = @"
DELETE FROM dbo.ADUserChangeQueueGroups
WHERE RequestId = @RequestId
  AND Source = 'Rule';
";
            deleteCmd.Parameters.AddBigInt("@RequestId", requestId);
            await deleteCmd.ExecuteNonQueryAsync();
        }

        foreach (var group in recommendedGroups.Where(g => g.Action.Equals("ADD", StringComparison.OrdinalIgnoreCase)))
        {
            await using var insertCmd = connection.CreateCommand();
            insertCmd.Transaction = transaction;
            insertCmd.CommandText = @"
INSERT INTO dbo.ADUserChangeQueueGroups
(
    RequestId,
    GroupObjectGUID,
    Action,
    Source,
    RuleSetId,
    Selected,
    Required,
    ApprovalRequired,
    Reason,
    SnapshotGroupSamAccountName,
    SnapshotGroupName,
    SnapshotGroupDistinguishedName,
    CreatedBy
)
VALUES
(
    @RequestId,
    @GroupObjectGUID,
    @Action,
    'Rule',
    @RuleSetId,
    @Selected,
    @Required,
    @ApprovalRequired,
    @Reason,
    @SnapshotGroupSamAccountName,
    @SnapshotGroupName,
    @SnapshotGroupDistinguishedName,
    @CreatedBy
);
";
            insertCmd.Parameters.AddBigInt("@RequestId", requestId);
            insertCmd.Parameters.AddUniqueIdentifier("@GroupObjectGUID", group.GroupObjectGuid);
            insertCmd.Parameters.AddRequiredNVarChar("@Action", group.Action, 20);
            insertCmd.Parameters.AddNullableInt("@RuleSetId", group.RuleSetId);
            insertCmd.Parameters.AddBit("@Selected", group.Selected);
            insertCmd.Parameters.AddBit("@Required", group.Required);
            insertCmd.Parameters.AddBit("@ApprovalRequired", group.ApprovalRequired);
            insertCmd.Parameters.AddNVarChar("@Reason", group.Reason, 1000);
            insertCmd.Parameters.AddNVarChar("@SnapshotGroupSamAccountName", group.SamAccountName, 300);
            insertCmd.Parameters.AddNVarChar("@SnapshotGroupName", group.Name, 300);
            insertCmd.Parameters.AddNVarChar("@SnapshotGroupDistinguishedName", group.DistinguishedName, 1000);
            insertCmd.Parameters.AddRequiredNVarChar("@CreatedBy", changedBy, 300);

            await insertCmd.ExecuteNonQueryAsync();
        }
    }

    private static async Task<List<RuleSet>> LoadActiveRuleSetsAsync(SqlConnection connection, SqlTransaction? transaction)
    {
        await using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = @"
SELECT
    RuleSetId,
    RuleSetName,
    ISNULL(Description, '') AS Description,
    Priority,
    MatchMode,
    AppliesToAllUsers
FROM dbo.ADGroupRuleSets
WHERE Active = 1
  AND (EffectiveFrom IS NULL OR EffectiveFrom <= CONVERT(date, SYSUTCDATETIME()))
  AND (EffectiveTo IS NULL OR EffectiveTo >= CONVERT(date, SYSUTCDATETIME()))
ORDER BY Priority, RuleSetName;
";

        var ruleSets = new List<RuleSet>();

        await using var reader = await cmd.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {
            ruleSets.Add(new RuleSet
            {
                RuleSetId = reader.GetInt32(0),
                RuleSetName = reader.GetString(1),
                Description = reader.GetString(2),
                Priority = reader.GetInt32(3),
                MatchMode = reader.GetString(4),
                AppliesToAllUsers = reader.GetBoolean(5)
            });
        }

        return ruleSets;
    }

    private static async Task LoadRuleConditionsAsync(
        SqlConnection connection,
        List<RuleSet> ruleSets,
        SqlTransaction? transaction)
    {
        await using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = @"
SELECT
    RuleSetId,
    FieldName,
    Operator,
    MatchValue,
    MatchValue2
FROM dbo.ADGroupRuleConditions
WHERE RuleSetId IN (SELECT RuleSetId FROM dbo.ADGroupRuleSets WHERE Active = 1);
";

        var byId = ruleSets.ToDictionary(r => r.RuleSetId);

        await using var reader = await cmd.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {
            var ruleSetId = reader.GetInt32(0);

            if (!byId.TryGetValue(ruleSetId, out var ruleSet))
            {
                continue;
            }

            ruleSet.Conditions.Add(new RuleCondition
            {
                FieldName = reader.GetString(1),
                Operator = reader.GetString(2),
                MatchValue = reader.IsDBNull(3) ? null : reader.GetString(3),
                MatchValue2 = reader.IsDBNull(4) ? null : reader.GetString(4)
            });
        }
    }

    private static async Task LoadRuleTargetsAsync(
        SqlConnection connection,
        List<RuleSet> ruleSets,
        SqlTransaction? transaction)
    {
        await using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = @"
SELECT
    t.RuleSetId,
    t.GroupObjectGUID,
    ISNULL(g.SamAccountName, '') AS SamAccountName,
    ISNULL(g.Name, '') AS GroupName,
    ISNULL(g.DistinguishedName, '') AS DistinguishedName,
    t.Action,
    t.Required,
    ISNULL(m.ApprovalRequired, 1) AS ApprovalRequired,
    ISNULL(t.Notes, '') AS Notes
FROM dbo.ADGroupRuleTargets t
INNER JOIN dbo.ADGroupRuleSets rs
    ON rs.RuleSetId = t.RuleSetId
INNER JOIN dbo.ADGroups g
    ON g.ObjectGUID = t.GroupObjectGUID
INNER JOIN dbo.ADGroupMetadata m
    ON m.GroupObjectGUID = g.ObjectGUID
WHERE rs.Active = 1
  AND ISNULL(g.IsDeleted, 0) = 0
  AND m.Active = 1
  AND m.AllowAutomaticRecommendation = 1;
";

        var byId = ruleSets.ToDictionary(r => r.RuleSetId);

        await using var reader = await cmd.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {
            var ruleSetId = reader.GetInt32(0);

            if (!byId.TryGetValue(ruleSetId, out var ruleSet))
            {
                continue;
            }

            ruleSet.Targets.Add(new RuleTarget
            {
                GroupObjectGuid = reader.GetGuid(1),
                SamAccountName = reader.GetString(2),
                Name = reader.GetString(3),
                DistinguishedName = reader.GetString(4),
                TargetAction = reader.GetString(5),
                Required = reader.GetBoolean(6),
                ApprovalRequired = reader.GetBoolean(7),
                Notes = reader.GetString(8)
            });
        }
    }

    private static bool RuleMatches(RuleSet ruleSet, GroupRuleContext context)
    {
        if (ruleSet.AppliesToAllUsers)
        {
            return true;
        }

        if (ruleSet.Conditions.Count == 0)
        {
            return false;
        }

        if (ruleSet.MatchMode.Equals("ANY", StringComparison.OrdinalIgnoreCase))
        {
            return ruleSet.Conditions.Any(c => ConditionMatches(c, context));
        }

        return ruleSet.Conditions.All(c => ConditionMatches(c, context));
    }

    private static bool ConditionMatches(RuleCondition condition, GroupRuleContext context)
    {
        var actualValue = GetContextValue(condition.FieldName, context);
        var op = (condition.Operator ?? "").Trim();
        var matchValue = condition.MatchValue;

        if (op.Equals("IsEmpty", StringComparison.OrdinalIgnoreCase))
        {
            return string.IsNullOrWhiteSpace(actualValue);
        }

        if (op.Equals("IsNotEmpty", StringComparison.OrdinalIgnoreCase))
        {
            return !string.IsNullOrWhiteSpace(actualValue);
        }

        if (actualValue == null)
        {
            actualValue = "";
        }

        var actual = actualValue.Trim();
        var expected = (matchValue ?? "").Trim();

        if (op.Equals("Equals", StringComparison.OrdinalIgnoreCase))
        {
            return string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase);
        }

        if (op.Equals("NotEquals", StringComparison.OrdinalIgnoreCase))
        {
            return !string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase);
        }

        if (op.Equals("Contains", StringComparison.OrdinalIgnoreCase))
        {
            return actual.Contains(expected, StringComparison.OrdinalIgnoreCase);
        }

        if (op.Equals("StartsWith", StringComparison.OrdinalIgnoreCase))
        {
            return actual.StartsWith(expected, StringComparison.OrdinalIgnoreCase);
        }

        if (op.Equals("EndsWith", StringComparison.OrdinalIgnoreCase))
        {
            return actual.EndsWith(expected, StringComparison.OrdinalIgnoreCase);
        }

        if (op.Equals("In", StringComparison.OrdinalIgnoreCase))
        {
            var allowedValues = expected
                .Split(new[] { ';', ',', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            return allowedValues.Any(value => string.Equals(value, actual, StringComparison.OrdinalIgnoreCase));
        }

        return false;
    }

    private static string? GetContextValue(string fieldName, GroupRuleContext context)
    {
        return fieldName.Trim().ToLowerInvariant() switch
        {
            "domain" => context.Domain,
            "company" => context.Company,
            "department" => context.Department,
            "title" => context.Title,
            "employeetype" => context.EmployeeType,
            "office" => context.Office,
            "country" => context.Country,
            "city" => context.City,
            "computertype" => context.ComputerType,
            "officelicense" => context.OfficeLicense,
            "managersamaccountname" => context.ManagerSamAccountName,
            "accesscard" => context.AccessCard ? "true" : "false",
            "enabled" => context.Enabled ? "true" : "false",
            _ => null
        };
    }

    private static async Task<bool> RuleTablesExistAsync(SqlConnection connection, SqlTransaction? transaction)
    {
        await using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = @"
SELECT CASE
    WHEN OBJECT_ID(N'dbo.ADGroupMetadata', N'U') IS NOT NULL
     AND OBJECT_ID(N'dbo.ADGroupRuleSets', N'U') IS NOT NULL
     AND OBJECT_ID(N'dbo.ADGroupRuleConditions', N'U') IS NOT NULL
     AND OBJECT_ID(N'dbo.ADGroupRuleTargets', N'U') IS NOT NULL
     AND OBJECT_ID(N'dbo.ADGroups', N'U') IS NOT NULL
     AND COL_LENGTH(N'dbo.ADGroupRuleSets', N'AppliesToAllUsers') IS NOT NULL
    THEN 1 ELSE 0 END;
";

        var result = await cmd.ExecuteScalarAsync();
        return Convert.ToInt32(result) == 1;
    }

    private static async Task<bool> QueueGroupsTableExistsAsync(SqlConnection connection, SqlTransaction? transaction)
    {
        await using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = "SELECT CASE WHEN OBJECT_ID(N'dbo.ADUserChangeQueueGroups', N'U') IS NULL THEN 0 ELSE 1 END;";

        var result = await cmd.ExecuteScalarAsync();
        return Convert.ToInt32(result) == 1;
    }
}

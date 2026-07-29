using Microsoft.Data.SqlClient;
using System.DirectoryServices.AccountManagement;
using System.Security.Claims;

namespace UserChangeQueueWeb.Services;

public sealed class AccessCardGroupService
{
    private readonly SqlConnectionFactory _connectionFactory;

    public AccessCardGroupService(SqlConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    public sealed class AccessCardGroupOption
    {
        public int Id { get; set; }
        public string Site { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public string ExternalGroupName { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public int SortOrder { get; set; }
        public bool Restricted { get; set; }
        internal List<string> AllowedAdGroups { get; } = new();
    }

    public async Task<List<AccessCardGroupOption>> GetAvailableGroupsAsync(
        ClaimsPrincipal user,
        string? primaryOffice = null,
        SqlConnection? connection = null,
        SqlTransaction? transaction = null)
    {
        var ownsConnection = connection is null;
        connection ??= await _connectionFactory.OpenAsync();

        try
        {
            var groups = new Dictionary<int, AccessCardGroupOption>();

            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = @"
SELECT
    g.Id,
    g.Site,
    g.DisplayName,
    g.ExternalGroupName,
    ISNULL(g.Description, N'') AS Description,
    g.SortOrder,
    r.AdGroupName
FROM dbo.AccessCardGroups AS g
LEFT JOIN dbo.AccessCardGroupAccessRules AS r
    ON r.AccessCardGroupId = g.Id
   AND r.Active = 1
WHERE g.Active = 1
ORDER BY g.Site, g.SortOrder, g.DisplayName, r.AdGroupName;";

            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var id = reader.GetInt32(0);
                if (!groups.TryGetValue(id, out var item))
                {
                    item = new AccessCardGroupOption
                    {
                        Id = id,
                        Site = reader.GetString(1),
                        DisplayName = reader.GetString(2),
                        ExternalGroupName = reader.GetString(3),
                        Description = reader.GetString(4),
                        SortOrder = reader.GetInt32(5)
                    };
                    groups.Add(id, item);
                }

                if (!reader.IsDBNull(6))
                {
                    item.AllowedAdGroups.Add(reader.GetString(6));
                }
            }

            var samAccountName = ExtractSamAccountName(user.Identity?.Name);
            var available = new List<AccessCardGroupOption>();

            foreach (var item in groups.Values)
            {
                item.Restricted = item.AllowedAdGroups.Count > 0;
                if (!item.Restricted || item.AllowedAdGroups.Any(group => IsUserInAdGroup(samAccountName, group)))
                {
                    available.Add(item);
                }
            }

            return available
                .OrderBy(item => string.Equals(item.Site, primaryOffice, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .ThenBy(item => item.Site, StringComparer.OrdinalIgnoreCase)
                .ThenBy(item => item.SortOrder)
                .ThenBy(item => item.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        finally
        {
            if (ownsConnection)
            {
                await connection.DisposeAsync();
            }
        }
    }

    public async Task<List<int>> LoadSelectedGroupIdsAsync(
        long requestId,
        SqlConnection? connection = null,
        SqlTransaction? transaction = null)
    {
        var ownsConnection = connection is null;
        connection ??= await _connectionFactory.OpenAsync();

        try
        {
            var ids = new List<int>();
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = @"
SELECT AccessCardGroupId
FROM dbo.ADUserChangeQueueAccessCardGroups
WHERE RequestId = @RequestId
ORDER BY AccessCardGroupId;";
            command.Parameters.AddBigInt("@RequestId", requestId);

            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                ids.Add(reader.GetInt32(0));
            }

            return ids;
        }
        finally
        {
            if (ownsConnection)
            {
                await connection.DisposeAsync();
            }
        }
    }

    public async Task ReplaceSelectionsAsync(
        SqlConnection connection,
        long requestId,
        bool accessCardRequested,
        IEnumerable<int>? selectedGroupIds,
        ClaimsPrincipal user,
        string? primaryOffice,
        string changedBy,
        SqlTransaction transaction)
    {
        var selected = accessCardRequested
            ? (selectedGroupIds ?? Array.Empty<int>()).Distinct().ToList()
            : new List<int>();

        var available = await GetAvailableGroupsAsync(user, primaryOffice, connection, transaction);
        var allowedIds = available.Select(item => item.Id).ToHashSet();
        var unauthorized = selected.Where(id => !allowedIds.Contains(id)).ToList();

        if (unauthorized.Count > 0)
        {
            throw new InvalidOperationException(
                "One or more selected access-card groups are not available to the current user.");
        }

        await using (var deleteCommand = connection.CreateCommand())
        {
            deleteCommand.Transaction = transaction;
            deleteCommand.CommandText = @"
DELETE FROM dbo.ADUserChangeQueueAccessCardGroups
WHERE RequestId = @RequestId;";
            deleteCommand.Parameters.AddBigInt("@RequestId", requestId);
            await deleteCommand.ExecuteNonQueryAsync();
        }

        foreach (var groupId in selected)
        {
            await using var insertCommand = connection.CreateCommand();
            insertCommand.Transaction = transaction;
            insertCommand.CommandText = @"
INSERT INTO dbo.ADUserChangeQueueAccessCardGroups
(
    RequestId,
    AccessCardGroupId,
    CreatedBy
)
VALUES
(
    @RequestId,
    @AccessCardGroupId,
    @CreatedBy
);";
            insertCommand.Parameters.AddBigInt("@RequestId", requestId);
            insertCommand.Parameters.AddInt("@AccessCardGroupId", groupId);
            insertCommand.Parameters.AddNVarChar("@CreatedBy", changedBy, 300);
            await insertCommand.ExecuteNonQueryAsync();
        }
    }

    private static string ExtractSamAccountName(string? identityName)
    {
        if (string.IsNullOrWhiteSpace(identityName)) return string.Empty;
        var value = identityName.Trim();
        var slash = value.LastIndexOf('\\');
        if (slash >= 0 && slash < value.Length - 1) value = value[(slash + 1)..];
        var at = value.IndexOf('@');
        if (at > 0) value = value[..at];
        return value;
    }

    private static bool IsUserInAdGroup(string samAccountName, string groupName)
    {
        if (string.IsNullOrWhiteSpace(samAccountName) || string.IsNullOrWhiteSpace(groupName)) return false;
        try
        {
            using var context = new PrincipalContext(ContextType.Domain);
            using var user = UserPrincipal.FindByIdentity(context, IdentityType.SamAccountName, samAccountName);
            if (user is null) return false;

            var lookupName = groupName.Contains('\\') ? groupName.Split('\\').Last() : groupName;
            using var group = GroupPrincipal.FindByIdentity(context, IdentityType.Name, lookupName);
            return group is not null && user.IsMemberOf(group);
        }
        catch
        {
            return false;
        }
    }
}

using Microsoft.Data.SqlClient;
using System.DirectoryServices.AccountManagement;

namespace UserChangeQueueWeb.Services;

public class PageAccessService
{
    private readonly SqlConnectionFactory _connectionFactory;

    public PageAccessService(SqlConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    public async Task<bool> UserHasAccessAsync(string userName, string pagePath)
    {
        if (string.IsNullOrWhiteSpace(userName) || string.IsNullOrWhiteSpace(pagePath))
        {
            return false;
        }

        if (IsPublicAuthenticatedPage(pagePath))
        {
            return true;
        }

        var allowedGroups = await GetAllowedGroupsForPageAsync(GetRuleLookupPaths(pagePath));

        // No active access rule, and no configured page fallback, means deny by default.
        if (allowedGroups.Count == 0)
        {
            return false;
        }

        var samAccountName = ExtractSamAccountName(userName);

        foreach (var groupName in allowedGroups)
        {
            if (groupName.Equals(
                    "*AUTHENTICATED*",
                    StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (IsUserInAdGroup(samAccountName, groupName))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsPublicAuthenticatedPage(string pagePath)
    {
        var normalizedPagePath = NormalizePagePath(pagePath);

        return normalizedPagePath.Equals("/", StringComparison.OrdinalIgnoreCase)
            || normalizedPagePath.Equals("/Index", StringComparison.OrdinalIgnoreCase)
            || normalizedPagePath.Equals("/MyProfile", StringComparison.OrdinalIgnoreCase)
            || normalizedPagePath.Equals("/MyProfile/Index", StringComparison.OrdinalIgnoreCase)
            || normalizedPagePath.Equals("/Privacy", StringComparison.OrdinalIgnoreCase)
            || normalizedPagePath.Equals("/Logout", StringComparison.OrdinalIgnoreCase)
            || normalizedPagePath.Equals("/Language", StringComparison.OrdinalIgnoreCase)
            || normalizedPagePath.Equals("/Error", StringComparison.OrdinalIgnoreCase)
            || normalizedPagePath.Equals("/TemporaryAccess", StringComparison.OrdinalIgnoreCase)
            || normalizedPagePath.Equals("/TemporaryAccess/Index", StringComparison.OrdinalIgnoreCase)
;
    }

    private static IReadOnlyList<string> GetRuleLookupPaths(string pagePath)
    {
        var normalizedPagePath = NormalizePagePath(pagePath);

        // During r0.9 cleanup, request pages were moved under /Requests. Keep looking up
        // old PageAccessRules paths as fallbacks so deployments stay compatible until
        // the migration SQL has been applied everywhere.
        return normalizedPagePath.ToUpperInvariant() switch
        {
            "/REQUESTS/NEWUSER" => new[] { normalizedPagePath, "/UserChangeQueue" },
            "/REQUESTS/UPDATEUSER" => new[] { normalizedPagePath, "/UpdateUser" },
            "/REQUESTS/APPROVALS" => new[] { normalizedPagePath, "/Approvals" },
            "/REQUESTS/UPCOMING" => new[] { normalizedPagePath, "/UpcomingChanges" },
            "/REQUESTS/SUPERVISOR" => new[] { normalizedPagePath, "/Supervisor" },
            "/LICENSEREQUESTS/INDEX" => new[] { normalizedPagePath, "/LicenseRequests" },

            // UserPendingChanges is a drill-down page from Supervisor and is still protected by
            // ObjectAccessService. Allow users with Supervisor page access to open the drill-down.
            "/REQUESTS/USERPENDINGCHANGES" => new[]
            {
                normalizedPagePath,
                "/UserPendingChanges",
                "/Requests/Supervisor",
                "/Supervisor"
            },

            _ => new[] { normalizedPagePath }
        };
    }

    private async Task<List<string>> GetAllowedGroupsForPageAsync(IReadOnlyList<string> pagePaths)
    {
        var groups = new List<string>();

        if (pagePaths.Count == 0)
        {
            return groups;
        }

        await using var cn = await _connectionFactory.OpenAsync();
        await using var cmd = cn.CreateCommand();

        var parameterNames = new List<string>();

        for (var i = 0; i < pagePaths.Count; i++)
        {
            var parameterName = "@PagePath" + i;
            parameterNames.Add(parameterName);
            cmd.Parameters.AddNVarChar(parameterName, pagePaths[i], 200);
        }

        cmd.CommandText = $@"
SELECT DISTINCT AdGroupName
FROM dbo.PageAccessRules
WHERE Active = 1
  AND PagePath IN ({string.Join(", ", parameterNames)})
ORDER BY AdGroupName;
";

        await using var reader = await cmd.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {
            groups.Add(reader.GetString(0));
        }

        return groups;
    }

    private static bool IsUserInAdGroup(string samAccountName, string groupName)
    {
        try
        {
            using var context = new PrincipalContext(ContextType.Domain);

            using var user = UserPrincipal.FindByIdentity(
                context,
                IdentityType.SamAccountName,
                samAccountName);

            if (user == null)
            {
                return false;
            }

            using var group = GroupPrincipal.FindByIdentity(
                context,
                IdentityType.Name,
                groupName);

            if (group == null)
            {
                return false;
            }

            return user.IsMemberOf(group);
        }
        catch
        {
            return false;
        }
    }

    private static string ExtractSamAccountName(string loginName)
    {
        if (string.IsNullOrWhiteSpace(loginName))
        {
            return "";
        }

        return loginName.Contains('\\')
            ? loginName.Split('\\').Last()
            : loginName;
    }

    private static string NormalizePagePath(string pagePath)
    {
        if (string.IsNullOrWhiteSpace(pagePath))
        {
            return "";
        }

        var normalized = pagePath.Trim();

        return normalized.StartsWith('/')
            ? normalized
            : "/" + normalized;
    }
}

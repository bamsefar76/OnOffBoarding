using Microsoft.Data.SqlClient;
using System.Data;
using System.Security.Claims;

namespace UserChangeQueueWeb.Services;

public class ObjectAccessService
{
    // This is not a real Razor Page. It reuses dbo.PageAccessRules as an
    // optional "may access all users/requests" rule.
    public const string AccessAllRulePath = "/ObjectAccessAll";

    private const string ManagedUsersCte = @"
WITH ManagedUsers AS
(
    SELECT
        ObjectGUID,
        SamAccountName,
        ManagerSamAccountName,
        CAST(LOWER(SamAccountName) AS nvarchar(max)) AS SamPath
    FROM dbo.ADObjects
    WHERE ManagerSamAccountName = @RootManagerSamAccountName
      AND SamAccountName IS NOT NULL
      AND ISNULL(IsDeleted, 0) = 0

    UNION ALL

    SELECT
        child.ObjectGUID,
        child.SamAccountName,
        child.ManagerSamAccountName,
        CAST(parent.SamPath + N'|' + LOWER(child.SamAccountName) AS nvarchar(max)) AS SamPath
    FROM dbo.ADObjects child
    INNER JOIN ManagedUsers parent
        ON child.ManagerSamAccountName = parent.SamAccountName
    WHERE child.SamAccountName IS NOT NULL
      AND ISNULL(child.IsDeleted, 0) = 0
      AND CHARINDEX(N'|' + LOWER(child.SamAccountName) + N'|', N'|' + parent.SamPath + N'|') = 0
)
";

    private readonly SqlConnectionFactory _connectionFactory;
    private readonly PageAccessService _pageAccessService;
    private readonly AccessScopeService _accessScopeService;

    public ObjectAccessService(
        SqlConnectionFactory connectionFactory,
        PageAccessService pageAccessService,
        AccessScopeService accessScopeService)
    {
        _connectionFactory = connectionFactory;
        _pageAccessService = pageAccessService;
        _accessScopeService = accessScopeService;
    }

    public async Task<bool> UserHasAccessAllAsync(ClaimsPrincipal user)
    {
        var loginName = GetLoginName(user);

        if (string.IsNullOrWhiteSpace(loginName))
        {
            return false;
        }

        var scope = await _accessScopeService.GetCurrentAsync(user);
        return scope.IsIT || await _pageAccessService.UserHasAccessAsync(loginName, AccessAllRulePath);
    }

    public async Task<bool> CanViewManagerAsync(ClaimsPrincipal user, string? managerSamAccountName)
    {
        var loginName = GetLoginName(user);
        var currentSamAccountName = ExtractSamAccountName(loginName);
        var requestedManagerSamAccountName = ExtractSamAccountName(managerSamAccountName ?? "");

        if (string.IsNullOrWhiteSpace(currentSamAccountName) || string.IsNullOrWhiteSpace(requestedManagerSamAccountName))
        {
            return false;
        }

        if (requestedManagerSamAccountName.Equals(currentSamAccountName, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (await UserHasAccessAllAsync(user))
        {
            return true;
        }

        return await IsSamAccountNameInManagedHierarchyAsync(currentSamAccountName, requestedManagerSamAccountName);
    }

    public async Task<bool> CanViewUserAsync(ClaimsPrincipal user, Guid targetObjectGuid)
    {
        if (targetObjectGuid == Guid.Empty)
        {
            return false;
        }

        if (await UserHasAccessAllAsync(user))
        {
            return true;
        }

        var scope = await _accessScopeService.GetCurrentAsync(user);
        if (scope.IsHR && await IsObjectGuidInOfficeAsync(targetObjectGuid, scope.Office))
        {
            return true;
        }

        var loginName = GetLoginName(user);
        var currentSamAccountName = ExtractSamAccountName(loginName);

        if (string.IsNullOrWhiteSpace(currentSamAccountName))
        {
            return false;
        }

        return await IsObjectGuidInManagedHierarchyAsync(currentSamAccountName, targetObjectGuid);
    }

    public async Task<bool> CanAccessRequestAsync(ClaimsPrincipal user, long requestId, string? requestType = null)
    {
        if (requestId <= 0)
        {
            return false;
        }

        if (await UserHasAccessAllAsync(user))
        {
            return true;
        }

        var scope = await _accessScopeService.GetCurrentAsync(user);
        if (scope.IsHR && await IsRequestInOfficeAsync(requestId, requestType, scope.Office))
        {
            return true;
        }

        var loginName = GetLoginName(user);
        var currentSamAccountName = ExtractSamAccountName(loginName);

        if (string.IsNullOrWhiteSpace(loginName) || string.IsNullOrWhiteSpace(currentSamAccountName))
        {
            return false;
        }

        await using var cn = await _connectionFactory.OpenAsync();

        await using var cmd = cn.CreateCommand();
        cmd.CommandText = ManagedUsersCte + @"
SELECT CASE WHEN EXISTS
(
    SELECT 1
    FROM dbo.ADUserChangeQueue q
    LEFT JOIN ManagedUsers managed
        ON managed.ObjectGUID = q.TargetObjectGUID
    WHERE q.RequestId = @RequestId
      AND (@RequestType IS NULL OR q.RequestType = @RequestType)
      AND
      (
          q.RequestedBy = @LoginName
          OR q.RequestedBy = @SamAccountName
          OR q.RequestedBy LIKE @DomainSlashSamAccountName
          OR managed.ObjectGUID IS NOT NULL
      )
)
THEN CAST(1 AS bit) ELSE CAST(0 AS bit) END
OPTION (MAXRECURSION 32767);
";

        AddRootManagerParameter(cmd, currentSamAccountName);
        cmd.Parameters.Add("@RequestId", SqlDbType.BigInt).Value = requestId;
        cmd.Parameters.Add("@RequestType", SqlDbType.NVarChar, 20).Value = (object?)requestType ?? DBNull.Value;
        cmd.Parameters.Add("@LoginName", SqlDbType.NVarChar, 300).Value = loginName;
        cmd.Parameters.Add("@SamAccountName", SqlDbType.NVarChar, 300).Value = currentSamAccountName;
        cmd.Parameters.Add("@DomainSlashSamAccountName", SqlDbType.NVarChar, 301).Value = @"%\" + currentSamAccountName;

        return await ExecuteBooleanScalarAsync(cmd);
    }

    private async Task<bool> IsObjectGuidInOfficeAsync(Guid targetObjectGuid, string office)
    {
        if (targetObjectGuid == Guid.Empty || string.IsNullOrWhiteSpace(office))
        {
            return false;
        }

        await using var cn = await _connectionFactory.OpenAsync();
        await using var cmd = cn.CreateCommand();
        cmd.CommandText = @"
SELECT CASE WHEN EXISTS
(
    SELECT 1
    FROM dbo.ADObjects
    WHERE ObjectGUID = @TargetObjectGuid
      AND ISNULL(IsDeleted, 0) = 0
      AND NULLIF(LTRIM(RTRIM(Office)), N'') = @Office
)
THEN CAST(1 AS bit) ELSE CAST(0 AS bit) END;";
        cmd.Parameters.Add("@TargetObjectGuid", SqlDbType.UniqueIdentifier).Value = targetObjectGuid;
        cmd.Parameters.Add("@Office", SqlDbType.NVarChar, 300).Value = office;
        return await ExecuteBooleanScalarAsync(cmd);
    }

    private async Task<bool> IsRequestInOfficeAsync(long requestId, string? requestType, string office)
    {
        if (requestId <= 0 || string.IsNullOrWhiteSpace(office))
        {
            return false;
        }

        await using var cn = await _connectionFactory.OpenAsync();
        await using var cmd = cn.CreateCommand();
        cmd.CommandText = @"
SELECT CASE WHEN EXISTS
(
    SELECT 1
    FROM dbo.ADUserChangeQueue
    WHERE RequestId = @RequestId
      AND (@RequestType IS NULL OR RequestType = @RequestType)
      AND NULLIF(LTRIM(RTRIM(Office)), N'') = @Office
)
THEN CAST(1 AS bit) ELSE CAST(0 AS bit) END;";
        cmd.Parameters.Add("@RequestId", SqlDbType.BigInt).Value = requestId;
        cmd.Parameters.Add("@RequestType", SqlDbType.NVarChar, 20).Value = (object?)requestType ?? DBNull.Value;
        cmd.Parameters.Add("@Office", SqlDbType.NVarChar, 300).Value = office;
        return await ExecuteBooleanScalarAsync(cmd);
    }

    private async Task<bool> IsSamAccountNameInManagedHierarchyAsync(string rootManagerSamAccountName, string targetSamAccountName)
    {
        await using var cn = await _connectionFactory.OpenAsync();

        await using var cmd = cn.CreateCommand();
        cmd.CommandText = ManagedUsersCte + @"
SELECT CASE WHEN EXISTS
(
    SELECT 1
    FROM ManagedUsers
    WHERE SamAccountName = @TargetSamAccountName
)
THEN CAST(1 AS bit) ELSE CAST(0 AS bit) END
OPTION (MAXRECURSION 32767);
";

        AddRootManagerParameter(cmd, rootManagerSamAccountName);
        cmd.Parameters.Add("@TargetSamAccountName", SqlDbType.NVarChar, 300).Value = targetSamAccountName;

        return await ExecuteBooleanScalarAsync(cmd);
    }

    private async Task<bool> IsObjectGuidInManagedHierarchyAsync(string rootManagerSamAccountName, Guid targetObjectGuid)
    {
        await using var cn = await _connectionFactory.OpenAsync();

        await using var cmd = cn.CreateCommand();
        cmd.CommandText = ManagedUsersCte + @"
SELECT CASE WHEN EXISTS
(
    SELECT 1
    FROM ManagedUsers
    WHERE ObjectGUID = @TargetObjectGuid
)
THEN CAST(1 AS bit) ELSE CAST(0 AS bit) END
OPTION (MAXRECURSION 32767);
";

        AddRootManagerParameter(cmd, rootManagerSamAccountName);
        cmd.Parameters.Add("@TargetObjectGuid", SqlDbType.UniqueIdentifier).Value = targetObjectGuid;

        return await ExecuteBooleanScalarAsync(cmd);
    }

    private static async Task<bool> ExecuteBooleanScalarAsync(SqlCommand cmd)
    {
        var result = await cmd.ExecuteScalarAsync();
        return result is bool boolResult && boolResult;
    }

    private static void AddRootManagerParameter(SqlCommand cmd, string rootManagerSamAccountName)
    {
        cmd.Parameters.Add("@RootManagerSamAccountName", SqlDbType.NVarChar, 300).Value = rootManagerSamAccountName;
    }

    private static string GetLoginName(ClaimsPrincipal user)
    {
        return user.Identity?.Name ?? "";
    }

    public static string ExtractSamAccountName(string loginName)
    {
        if (string.IsNullOrWhiteSpace(loginName))
        {
            return "";
        }

        var slashIndex = loginName.LastIndexOf('\\');

        return slashIndex >= 0 && slashIndex < loginName.Length - 1
            ? loginName[(slashIndex + 1)..]
            : loginName;
    }
}

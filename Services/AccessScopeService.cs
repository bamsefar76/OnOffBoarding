using System.Security.Claims;
using Microsoft.Data.SqlClient;

namespace UserChangeQueueWeb.Services;

public sealed class AccessScopeService
{
    private readonly SqlConnectionFactory _connectionFactory;
    private readonly PageAccessService _pageAccessService;
    private UserAccessScope? _cachedScope;

    public AccessScopeService(
        SqlConnectionFactory connectionFactory,
        PageAccessService pageAccessService)
    {
        _connectionFactory = connectionFactory;
        _pageAccessService = pageAccessService;
    }

    public async Task<UserAccessScope> GetCurrentAsync(
        ClaimsPrincipal user,
        CancellationToken cancellationToken = default)
    {
        if (_cachedScope is not null)
        {
            return _cachedScope;
        }

        var loginName = user.Identity?.Name ?? string.Empty;
        var samAccountName = ExtractSamAccountName(loginName);

        if (string.IsNullOrWhiteSpace(samAccountName))
        {
            _cachedScope = UserAccessScope.Empty;
            return _cachedScope;
        }

        // PageAccessRules remains the source of truth for the IT/HR capabilities.
        // Access to the page-access administration page identifies IT administrators.
        var isIt = await _pageAccessService.UserHasAccessAsync(
            loginName,
            "/Settings/PageAccessRules");

        var canCreateUsers = await _pageAccessService.UserHasAccessAsync(
            loginName,
            "/Requests/NewUser");

        var canUpdateUsers = await _pageAccessService.UserHasAccessAsync(
            loginName,
            "/Requests/UpdateUser");

        await using var connection = await _connectionFactory.OpenAsync(cancellationToken);

        var office = await LoadOfficeAsync(connection, samAccountName, cancellationToken);
        var isProjectManager = await IsProjectManagerAsync(connection, samAccountName, cancellationToken);

        _cachedScope = new UserAccessScope(
            loginName,
            samAccountName,
            office,
            isIt,
            !isIt && (canCreateUsers || canUpdateUsers) && !string.IsNullOrWhiteSpace(office),
            !isIt && isProjectManager);

        return _cachedScope;
    }

    public async Task<bool> CanOpenScopedPageAsync(
        ClaimsPrincipal user,
        string pagePath,
        CancellationToken cancellationToken = default)
    {
        var scope = await GetCurrentAsync(user, cancellationToken);
        var normalized = NormalizePagePath(pagePath);

        return normalized.ToUpperInvariant() switch
        {
            "/REQUESTS/UPCOMING" => scope.IsIT || scope.IsHR || scope.IsProjectManager,
            "/UPCOMINGCHANGES" => scope.IsIT || scope.IsHR || scope.IsProjectManager,
            "/PROJECTS/INDEX" => scope.IsIT || scope.IsProjectManager,
            "/PROJECTS" => scope.IsIT || scope.IsProjectManager,
            _ => false
        };
    }

    private static async Task<string> LoadOfficeAsync(
        SqlConnection connection,
        string samAccountName,
        CancellationToken cancellationToken)
    {
        var officeColumn = await FindFirstColumnAsync(
            connection,
            "ADObjects",
            new[] { "Office", "PhysicalDeliveryOfficeName", "physicalDeliveryOfficeName" },
            cancellationToken);

        if (officeColumn is null)
        {
            return string.Empty;
        }

        await using var command = connection.CreateCommand();
        command.CommandText = $@"
SELECT TOP (1)
    NULLIF(LTRIM(RTRIM([{officeColumn}])), N'')
FROM dbo.ADObjects
WHERE SamAccountName = @SamAccountName
  AND ISNULL(IsDeleted, 0) = 0;";
        command.Parameters.AddNVarChar("@SamAccountName", samAccountName, 256);

        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is null || value is DBNull ? string.Empty : Convert.ToString(value)?.Trim() ?? string.Empty;
    }

    private static async Task<bool> IsProjectManagerAsync(
        SqlConnection connection,
        string samAccountName,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = @"
SELECT CASE WHEN EXISTS
(
    SELECT 1
    FROM dbo.Projects
    WHERE Active = 1
      AND
      (
           ProductionManager = @SamAccountName
        OR ProductionManager LIKE @DomainSlashSamAccountName
      )
) THEN 1 ELSE 0 END;";
        command.Parameters.AddNVarChar("@SamAccountName", samAccountName, 256);
        command.Parameters.AddNVarChar("@DomainSlashSamAccountName", @"%\" + samAccountName, 300);

        return Convert.ToBoolean(await command.ExecuteScalarAsync(cancellationToken));
    }

    private static async Task<string?> FindFirstColumnAsync(
        SqlConnection connection,
        string tableName,
        IReadOnlyCollection<string> candidates,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = @"
SELECT COLUMN_NAME
FROM INFORMATION_SCHEMA.COLUMNS
WHERE TABLE_SCHEMA = N'dbo'
  AND TABLE_NAME = @TableName;";
        command.Parameters.AddNVarChar("@TableName", tableName, 128);

        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            columns.Add(reader.GetString(0));
        }

        return candidates.FirstOrDefault(columns.Contains);
    }

    public static string ExtractSamAccountName(string? loginName)
    {
        if (string.IsNullOrWhiteSpace(loginName))
        {
            return string.Empty;
        }

        var trimmed = loginName.Trim();
        var slash = trimmed.LastIndexOf('\\');
        return slash >= 0 ? trimmed[(slash + 1)..] : trimmed;
    }

    private static string NormalizePagePath(string pagePath)
    {
        if (string.IsNullOrWhiteSpace(pagePath))
        {
            return string.Empty;
        }

        var normalized = pagePath.Trim();
        return normalized.StartsWith('/') ? normalized : "/" + normalized;
    }
}

public sealed record UserAccessScope(
    string LoginName,
    string SamAccountName,
    string Office,
    bool IsIT,
    bool IsHR,
    bool IsProjectManager)
{
    public static UserAccessScope Empty { get; } = new("", "", "", false, false, false);

    public bool HasScopedUpcomingAccess => IsIT || IsHR || IsProjectManager;
}

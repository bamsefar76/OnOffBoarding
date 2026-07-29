using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Data.SqlClient;
using UserChangeQueueWeb.Services;

namespace UserChangeQueueWeb.Pages.Organisation;

[Authorize]
public sealed class TreeModel : PageModel
{
    private readonly SqlConnectionFactory _connectionFactory;
    private readonly ObjectAccessService _objectAccessService;

    public TreeModel(
        SqlConnectionFactory connectionFactory,
        ObjectAccessService objectAccessService)
    {
        _connectionFactory = connectionFactory;
        _objectAccessService = objectAccessService;
    }

    public bool HasFullTreeAccess { get; private set; }
    public string CurrentSamAccountName { get; private set; } = string.Empty;
    public List<OrganisationNode> Nodes { get; private set; } = new();
    public int EmployeeCount => Nodes.Count;
    public int RootCount => Nodes.Count(node => node.Depth == 0);
    public int IncludedOuCount { get; private set; }

    public async Task OnGetAsync()
    {
        CurrentSamAccountName = ObjectAccessService.ExtractSamAccountName(
            User.Identity?.Name ?? string.Empty);

        HasFullTreeAccess = await _objectAccessService.UserHasAccessAllAsync(User);

        await using var connection = await _connectionFactory.OpenAsync(HttpContext.RequestAborted);
        IncludedOuCount = await CountActiveIncludedOusAsync(connection);

        if (IncludedOuCount == 0)
        {
            Nodes = new List<OrganisationNode>();
            return;
        }

        var employees = HasFullTreeAccess
            ? await LoadFullTreeAsync(connection)
            : await LoadManagerTreeAsync(connection, CurrentSamAccountName);

        Nodes = BuildFlatTree(employees, HasFullTreeAccess ? null : CurrentSamAccountName);
    }

    private static async Task<int> CountActiveIncludedOusAsync(SqlConnection connection)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = @"
SELECT COUNT_BIG(1)
FROM dbo.OrganisationTreeOUs
WHERE Active = 1;
";

        var value = await command.ExecuteScalarAsync();
        return Convert.ToInt32(value ?? 0);
    }

    private static async Task<List<EmployeeRow>> LoadFullTreeAsync(SqlConnection connection)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = BaseSelect + @"
FROM dbo.ADObjects AS ad
WHERE ISNULL(ad.IsDeleted, 0) = 0
  AND ISNULL(ad.Enabled, 1) = 1
  AND ad.SamAccountName IS NOT NULL
  AND EXISTS
  (
      SELECT 1
      FROM dbo.OrganisationTreeOUs AS includedOu
      WHERE includedOu.Active = 1
        AND ad.DistinguishedName IS NOT NULL
        AND
        (
            ad.DistinguishedName = includedOu.DistinguishedName
            OR RIGHT(ad.DistinguishedName, LEN(includedOu.DistinguishedName) + 1)
                = N',' + includedOu.DistinguishedName
        )
  );
";

        return await ReadEmployeesAsync(command);
    }

    private static async Task<List<EmployeeRow>> LoadManagerTreeAsync(
        SqlConnection connection,
        string rootSamAccountName)
    {
        if (string.IsNullOrWhiteSpace(rootSamAccountName))
        {
            return new List<EmployeeRow>();
        }

        await using var command = connection.CreateCommand();
        command.Parameters.AddNVarChar("@RootSamAccountName", rootSamAccountName, 256);
        command.CommandText = @"
WITH OrganisationBranch AS
(
    SELECT
        ad.ObjectGUID,
        ad.SamAccountName,
        ad.ManagerSamAccountName,
        CAST(N'|' + LOWER(ad.SamAccountName) + N'|' AS nvarchar(max)) AS SamPath
    FROM dbo.ADObjects AS ad
    WHERE ad.SamAccountName = @RootSamAccountName
      AND ISNULL(ad.IsDeleted, 0) = 0
      AND ISNULL(ad.Enabled, 1) = 1

    UNION ALL

    SELECT
        child.ObjectGUID,
        child.SamAccountName,
        child.ManagerSamAccountName,
        CAST(parent.SamPath + LOWER(child.SamAccountName) + N'|' AS nvarchar(max)) AS SamPath
    FROM dbo.ADObjects AS child
    INNER JOIN OrganisationBranch AS parent
        ON child.ManagerSamAccountName = parent.SamAccountName
    WHERE child.SamAccountName IS NOT NULL
      AND ISNULL(child.IsDeleted, 0) = 0
      AND ISNULL(child.Enabled, 1) = 1
      AND CHARINDEX(N'|' + LOWER(child.SamAccountName) + N'|', parent.SamPath) = 0
)
" + BaseSelect + @"
FROM dbo.ADObjects AS ad
INNER JOIN OrganisationBranch AS branch
    ON branch.ObjectGUID = ad.ObjectGUID
WHERE EXISTS
(
    SELECT 1
    FROM dbo.OrganisationTreeOUs AS includedOu
    WHERE includedOu.Active = 1
      AND ad.DistinguishedName IS NOT NULL
      AND
      (
          ad.DistinguishedName = includedOu.DistinguishedName
          OR RIGHT(ad.DistinguishedName, LEN(includedOu.DistinguishedName) + 1)
              = N',' + includedOu.DistinguishedName
      )
)
OPTION (MAXRECURSION 32767);
";

        return await ReadEmployeesAsync(command);
    }

    private const string BaseSelect = @"
SELECT
    ad.ObjectGUID,
    ad.SamAccountName,
    ISNULL(ad.DisplayName, ad.SamAccountName) AS DisplayName,
    ISNULL(ad.Title, N'') AS Title,
    ISNULL(ad.Department, N'') AS Department,
    ISNULL(ad.Company, N'') AS Company,
    ISNULL(ad.Office, N'') AS Office,
    ISNULL(ad.Mail, N'') AS Mail,
    ISNULL(ad.ManagerSamAccountName, N'') AS ManagerSamAccountName,
    ISNULL(ad.EmployeeType, N'') AS EmployeeType
";

    private static async Task<List<EmployeeRow>> ReadEmployeesAsync(SqlCommand command)
    {
        var employees = new List<EmployeeRow>();

        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            employees.Add(new EmployeeRow
            {
                ObjectGuid = reader.GetGuid(0),
                SamAccountName = reader.GetString(1),
                DisplayName = reader.GetString(2),
                Title = reader.GetString(3),
                Department = reader.GetString(4),
                Company = reader.GetString(5),
                Office = reader.GetString(6),
                Mail = reader.GetString(7),
                ManagerSamAccountName = reader.GetString(8),
                EmployeeType = reader.GetString(9)
            });
        }

        return employees;
    }

    private static List<OrganisationNode> BuildFlatTree(
        IReadOnlyCollection<EmployeeRow> employees,
        string? preferredRootSamAccountName)
    {
        var bySam = employees
            .Where(employee => !string.IsNullOrWhiteSpace(employee.SamAccountName))
            .GroupBy(employee => employee.SamAccountName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

        var children = new Dictionary<string, List<EmployeeRow>>(StringComparer.OrdinalIgnoreCase);
        foreach (var employee in bySam.Values)
        {
            if (string.IsNullOrWhiteSpace(employee.ManagerSamAccountName) ||
                !bySam.ContainsKey(employee.ManagerSamAccountName) ||
                employee.ManagerSamAccountName.Equals(employee.SamAccountName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!children.TryGetValue(employee.ManagerSamAccountName, out var managerChildren))
            {
                managerChildren = new List<EmployeeRow>();
                children[employee.ManagerSamAccountName] = managerChildren;
            }

            managerChildren.Add(employee);
        }

        foreach (var managerChildren in children.Values)
        {
            managerChildren.Sort(EmployeeComparer.Instance);
        }

        var roots = bySam.Values
            .Where(employee =>
                string.IsNullOrWhiteSpace(employee.ManagerSamAccountName) ||
                !bySam.ContainsKey(employee.ManagerSamAccountName) ||
                employee.ManagerSamAccountName.Equals(employee.SamAccountName, StringComparison.OrdinalIgnoreCase))
            .OrderBy(employee => employee, EmployeeComparer.Instance)
            .ToList();

        if (!string.IsNullOrWhiteSpace(preferredRootSamAccountName) &&
            bySam.TryGetValue(preferredRootSamAccountName, out var preferredRoot))
        {
            roots.RemoveAll(root => root.SamAccountName.Equals(preferredRootSamAccountName, StringComparison.OrdinalIgnoreCase));
            roots.Insert(0, preferredRoot);
        }

        var flattened = new List<OrganisationNode>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var root in roots)
        {
            AddBranch(root, null, 0, children, visited, flattened);
        }

        // A corrupt manager loop must not make employees disappear.
        foreach (var remaining in bySam.Values.OrderBy(employee => employee, EmployeeComparer.Instance))
        {
            if (!visited.Contains(remaining.SamAccountName))
            {
                AddBranch(remaining, null, 0, children, visited, flattened);
            }
        }

        var descendants = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var node in flattened.OrderByDescending(node => node.Depth))
        {
            descendants.TryAdd(node.SamAccountName, 0);
            if (!string.IsNullOrWhiteSpace(node.ParentSamAccountName))
            {
                descendants.TryAdd(node.ParentSamAccountName, 0);
                descendants[node.ParentSamAccountName] += descendants[node.SamAccountName] + 1;
            }
        }

        foreach (var node in flattened)
        {
            node.TotalReports = descendants.GetValueOrDefault(node.SamAccountName);
        }

        return flattened;
    }

    private static void AddBranch(
        EmployeeRow employee,
        string? parentSamAccountName,
        int depth,
        IReadOnlyDictionary<string, List<EmployeeRow>> children,
        ISet<string> visited,
        ICollection<OrganisationNode> output)
    {
        if (!visited.Add(employee.SamAccountName))
        {
            return;
        }

        var directReports = children.GetValueOrDefault(employee.SamAccountName) ?? new List<EmployeeRow>();

        output.Add(new OrganisationNode
        {
            ObjectGuid = employee.ObjectGuid,
            SamAccountName = employee.SamAccountName,
            DisplayName = employee.DisplayName,
            Title = employee.Title,
            Department = employee.Department,
            Company = employee.Company,
            Office = employee.Office,
            Mail = employee.Mail,
            EmployeeType = employee.EmployeeType,
            ParentSamAccountName = parentSamAccountName,
            Depth = depth,
            DirectReports = directReports.Count
        });

        foreach (var child in directReports)
        {
            AddBranch(child, employee.SamAccountName, depth + 1, children, visited, output);
        }
    }

    private sealed class EmployeeRow
    {
        public Guid ObjectGuid { get; init; }
        public string SamAccountName { get; init; } = string.Empty;
        public string DisplayName { get; init; } = string.Empty;
        public string Title { get; init; } = string.Empty;
        public string Department { get; init; } = string.Empty;
        public string Company { get; init; } = string.Empty;
        public string Office { get; init; } = string.Empty;
        public string Mail { get; init; } = string.Empty;
        public string ManagerSamAccountName { get; init; } = string.Empty;
        public string EmployeeType { get; init; } = string.Empty;
    }

    public sealed class OrganisationNode
    {
        public Guid ObjectGuid { get; init; }
        public string SamAccountName { get; init; } = string.Empty;
        public string DisplayName { get; init; } = string.Empty;
        public string Title { get; init; } = string.Empty;
        public string Department { get; init; } = string.Empty;
        public string Company { get; init; } = string.Empty;
        public string Office { get; init; } = string.Empty;
        public string Mail { get; init; } = string.Empty;
        public string EmployeeType { get; init; } = string.Empty;
        public string? ParentSamAccountName { get; init; }
        public int Depth { get; init; }
        public int DirectReports { get; init; }
        public int TotalReports { get; set; }
        public bool HasChildren => DirectReports > 0;
    }

    private sealed class EmployeeComparer : IComparer<EmployeeRow>
    {
        public static EmployeeComparer Instance { get; } = new();

        public int Compare(EmployeeRow? x, EmployeeRow? y)
        {
            if (ReferenceEquals(x, y)) return 0;
            if (x is null) return -1;
            if (y is null) return 1;

            var displayNameComparison = string.Compare(
                x.DisplayName,
                y.DisplayName,
                StringComparison.CurrentCultureIgnoreCase);

            return displayNameComparison != 0
                ? displayNameComparison
                : string.Compare(x.SamAccountName, y.SamAccountName, StringComparison.OrdinalIgnoreCase);
        }
    }
}

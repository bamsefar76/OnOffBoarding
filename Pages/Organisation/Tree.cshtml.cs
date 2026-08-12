using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Data.SqlClient;
using UserChangeQueueWeb.Services;

namespace UserChangeQueueWeb.Pages.Organisation;

[Authorize]
public sealed class TreeModel : PageModel
{
    private readonly SqlConnectionFactory _connectionFactory;
    private readonly ObjectAccessService _objectAccessService;

    public TreeModel(SqlConnectionFactory connectionFactory, ObjectAccessService objectAccessService)
    {
        _connectionFactory = connectionFactory;
        _objectAccessService = objectAccessService;
    }

    [BindProperty(SupportsGet = true)]
    public string Mode { get; set; } = "ad";

    public bool HasFullTreeAccess { get; private set; }
    public string CurrentSamAccountName { get; private set; } = string.Empty;
    public List<OrganisationNode> Nodes { get; private set; } = new();
    public List<ProjectOrganisation> ProjectStructures { get; private set; } = new();
    public int EmployeeCount => Mode == "project"
        ? ProjectStructures.SelectMany(x => x.Members).Select(x => x.SamAccountName).Distinct(StringComparer.OrdinalIgnoreCase).Count()
        : Nodes.Count;
    public int RootCount => Nodes.Count(node => node.Depth == 0);
    public int IncludedOuCount { get; private set; }

    public async Task OnGetAsync()
    {
        Mode = string.Equals(Mode, "project", StringComparison.OrdinalIgnoreCase) ? "project" : "ad";
        CurrentSamAccountName = ObjectAccessService.ExtractSamAccountName(User.Identity?.Name ?? string.Empty);
        HasFullTreeAccess = await _objectAccessService.UserHasAccessAllAsync(User);

        await using var connection = await _connectionFactory.OpenAsync(HttpContext.RequestAborted);

        if (Mode == "project")
        {
            ProjectStructures = await LoadProjectStructureAsync(connection, CurrentSamAccountName, HasFullTreeAccess);
            return;
        }

        IncludedOuCount = await CountActiveIncludedOusAsync(connection);
        if (IncludedOuCount == 0)
        {
            Nodes = new List<OrganisationNode>();
            return;
        }

        var employees = HasFullTreeAccess
            ? await LoadFullTreeAsync(connection)
            : await LoadScopedTreeAsync(connection, CurrentSamAccountName);

        Nodes = BuildFlatTree(employees, HasFullTreeAccess ? null : CurrentSamAccountName);
    }

    private static async Task<int> CountActiveIncludedOusAsync(SqlConnection connection)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT_BIG(1) FROM dbo.OrganisationTreeOUs WHERE Active=1;";
        return Convert.ToInt32(await command.ExecuteScalarAsync() ?? 0);
    }

    private static async Task<List<EmployeeRow>> LoadFullTreeAsync(SqlConnection connection)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = BaseSelect + @"
FROM dbo.ADObjects ad
WHERE ISNULL(ad.IsDeleted,0)=0 AND ISNULL(ad.Enabled,1)=1 AND ad.SamAccountName IS NOT NULL
  AND EXISTS
  (
      SELECT 1 FROM dbo.OrganisationTreeOUs includedOu
      WHERE includedOu.Active=1 AND ad.DistinguishedName IS NOT NULL
        AND (ad.DistinguishedName=includedOu.DistinguishedName
             OR RIGHT(ad.DistinguishedName,LEN(includedOu.DistinguishedName)+1)=N','+includedOu.DistinguishedName)
  );";
        return await ReadEmployeesAsync(command);
    }

    private static async Task<List<EmployeeRow>> LoadScopedTreeAsync(SqlConnection connection, string viewerSam)
    {
        if (string.IsNullOrWhiteSpace(viewerSam)) return new List<EmployeeRow>();

        await using var command = connection.CreateCommand();
        command.Parameters.AddNVarChar("@ViewerSam", viewerSam, 256);
        command.CommandText = @"
DECLARE @ViewerManager nvarchar(256);
SELECT @ViewerManager = NULLIF(LTRIM(RTRIM(ManagerSamAccountName)),N'')
FROM dbo.ADObjects
WHERE SamAccountName=@ViewerSam AND ISNULL(IsDeleted,0)=0;

DECLARE @VisibleDepth int = 0;
;WITH ViewerReports AS
(
    SELECT child.SamAccountName, 1 AS Depth,
           CAST(N'|'+LOWER(child.SamAccountName)+N'|' AS nvarchar(max)) AS P
    FROM dbo.ADObjects child
    WHERE child.ManagerSamAccountName=@ViewerSam
      AND child.SamAccountName IS NOT NULL AND ISNULL(child.IsDeleted,0)=0 AND ISNULL(child.Enabled,1)=1
    UNION ALL
    SELECT child.SamAccountName, parent.Depth+1,
           CAST(parent.P+LOWER(child.SamAccountName)+N'|' AS nvarchar(max))
    FROM dbo.ADObjects child
    JOIN ViewerReports parent ON child.ManagerSamAccountName=parent.SamAccountName
    WHERE child.SamAccountName IS NOT NULL AND ISNULL(child.IsDeleted,0)=0 AND ISNULL(child.Enabled,1)=1
      AND CHARINDEX(N'|'+LOWER(child.SamAccountName)+N'|',parent.P)=0
)
SELECT @VisibleDepth = ISNULL(MAX(Depth),0) FROM ViewerReports OPTION (MAXRECURSION 32767);

;WITH PeerRoots AS
(
    SELECT ad.SamAccountName, 0 AS Depth,
           CAST(N'|'+LOWER(ad.SamAccountName)+N'|' AS nvarchar(max)) AS P
    FROM dbo.ADObjects ad
    WHERE ad.SamAccountName IS NOT NULL AND ISNULL(ad.IsDeleted,0)=0 AND ISNULL(ad.Enabled,1)=1
      AND ((@ViewerManager IS NULL AND NULLIF(LTRIM(RTRIM(ad.ManagerSamAccountName)),N'') IS NULL)
           OR ad.ManagerSamAccountName=@ViewerManager)
),
PeerBranch AS
(
    SELECT SamAccountName, Depth, P FROM PeerRoots
    UNION ALL
    SELECT child.SamAccountName, parent.Depth+1,
           CAST(parent.P+LOWER(child.SamAccountName)+N'|' AS nvarchar(max))
    FROM dbo.ADObjects child
    JOIN PeerBranch parent ON child.ManagerSamAccountName=parent.SamAccountName
    WHERE parent.Depth < @VisibleDepth
      AND child.SamAccountName IS NOT NULL AND ISNULL(child.IsDeleted,0)=0 AND ISNULL(child.Enabled,1)=1
      AND CHARINDEX(N'|'+LOWER(child.SamAccountName)+N'|',parent.P)=0
),
Ancestors AS
(
    SELECT manager.SamAccountName, manager.ManagerSamAccountName,
           CAST(N'|'+LOWER(manager.SamAccountName)+N'|' AS nvarchar(max)) AS P
    FROM dbo.ADObjects manager
    WHERE manager.SamAccountName=@ViewerManager AND ISNULL(manager.IsDeleted,0)=0
    UNION ALL
    SELECT manager.SamAccountName, manager.ManagerSamAccountName,
           CAST(parent.P+LOWER(manager.SamAccountName)+N'|' AS nvarchar(max))
    FROM dbo.ADObjects manager
    JOIN Ancestors parent ON manager.SamAccountName=parent.ManagerSamAccountName
    WHERE manager.SamAccountName IS NOT NULL AND ISNULL(manager.IsDeleted,0)=0
      AND CHARINDEX(N'|'+LOWER(manager.SamAccountName)+N'|',parent.P)=0
),
Visible AS
(
    SELECT SamAccountName FROM PeerBranch
    UNION
    SELECT SamAccountName FROM Ancestors
)
" + BaseSelect + @"
FROM dbo.ADObjects ad
JOIN Visible v ON v.SamAccountName=ad.SamAccountName
WHERE ISNULL(ad.IsDeleted,0)=0 AND ISNULL(ad.Enabled,1)=1
  AND EXISTS
  (
      SELECT 1 FROM dbo.OrganisationTreeOUs includedOu
      WHERE includedOu.Active=1 AND ad.DistinguishedName IS NOT NULL
        AND (ad.DistinguishedName=includedOu.DistinguishedName
             OR RIGHT(ad.DistinguishedName,LEN(includedOu.DistinguishedName)+1)=N','+includedOu.DistinguishedName)
  )
OPTION (MAXRECURSION 32767);";
        return await ReadEmployeesAsync(command);
    }

    private static async Task<List<ProjectOrganisation>> LoadProjectStructureAsync(SqlConnection connection, string viewerSam, bool full)
    {
        var result = new Dictionary<int, ProjectOrganisation>();
        await using var command = connection.CreateCommand();
        command.Parameters.AddNVarChar("@ViewerSam", viewerSam, 256);
        command.Parameters.AddBit("@Full", full);
        command.CommandText = @"
DECLARE @Today date=CAST(SYSDATETIME() AS date);
WITH VisibleProjects AS
(
    SELECT p.Id
    FROM dbo.Projects p
    WHERE p.Active=1 AND
    (
        @Full=1
        OR EXISTS (SELECT 1 FROM dbo.ProjectManagers pm WHERE pm.ProjectId=p.Id AND pm.SamAccountName=@ViewerSam)
        OR EXISTS
        (
            SELECT 1
            FROM dbo.Assignments a
            JOIN dbo.Employees e ON e.EmployeeId=a.EmployeeId AND e.Status<>N'Merged'
            WHERE a.ProjectId=p.Id
              AND e.CurrentSamAccountName=@ViewerSam
              AND a.StartDate<=@Today AND (a.EndDate IS NULL OR a.EndDate>=@Today)
        )
    )
)
SELECT p.Id, ISNULL(p.ProjectNumber,N''), ISNULL(p.ProjectName,N''), ISNULL(p.Company,N'')
FROM dbo.Projects p JOIN VisibleProjects v ON v.Id=p.Id
ORDER BY p.Company,p.ProjectName;

WITH VisibleProjects AS
(
    SELECT p.Id FROM dbo.Projects p WHERE p.Active=1 AND
    (@Full=1 OR EXISTS(SELECT 1 FROM dbo.ProjectManagers pm WHERE pm.ProjectId=p.Id AND pm.SamAccountName=@ViewerSam)
     OR EXISTS(SELECT 1 FROM dbo.Assignments a JOIN dbo.Employees e ON e.EmployeeId=a.EmployeeId AND e.Status<>N'Merged'
               WHERE a.ProjectId=p.Id AND e.CurrentSamAccountName=@ViewerSam
                 AND a.StartDate<=CAST(SYSDATETIME() AS date) AND (a.EndDate IS NULL OR a.EndDate>=CAST(SYSDATETIME() AS date))))
)
SELECT pm.ProjectId, pm.SamAccountName,
       COALESCE(
           NULLIF(ad.DisplayName,N''),
           NULLIF(LTRIM(RTRIM(CONCAT(emp.CanonicalGivenName,N' ',emp.CanonicalSurname))),N''),
           NULLIF(ad.Mail,N''),
           N'')
FROM dbo.ProjectManagers pm JOIN VisibleProjects v ON v.Id=pm.ProjectId
LEFT JOIN dbo.ADObjects ad ON ad.SamAccountName=pm.SamAccountName AND ISNULL(ad.IsDeleted,0)=0
LEFT JOIN dbo.Employees emp ON emp.CurrentSamAccountName=pm.SamAccountName AND emp.Status<>N'Merged'
ORDER BY pm.ProjectId,pm.SortOrder,
         COALESCE(NULLIF(ad.DisplayName,N''),NULLIF(LTRIM(RTRIM(CONCAT(emp.CanonicalGivenName,N' ',emp.CanonicalSurname))),N''),NULLIF(ad.Mail,N''),N'');

WITH VisibleProjects AS
(
    SELECT p.Id FROM dbo.Projects p WHERE p.Active=1 AND
    (@Full=1 OR EXISTS(SELECT 1 FROM dbo.ProjectManagers pm WHERE pm.ProjectId=p.Id AND pm.SamAccountName=@ViewerSam)
     OR EXISTS(SELECT 1 FROM dbo.Assignments a JOIN dbo.Employees e ON e.EmployeeId=a.EmployeeId AND e.Status<>N'Merged'
               WHERE a.ProjectId=p.Id AND e.CurrentSamAccountName=@ViewerSam
                 AND a.StartDate<=CAST(SYSDATETIME() AS date) AND (a.EndDate IS NULL OR a.EndDate>=CAST(SYSDATETIME() AS date))))
)
SELECT a.ProjectId, e.EmployeeId,
       COALESCE(NULLIF(LTRIM(RTRIM(CONCAT(e.CanonicalGivenName,N' ',e.CanonicalSurname))),N''),
                NULLIF(ad.DisplayName,N''), e.CurrentSamAccountName, N'#'+CONVERT(nvarchar(20),e.EmployeeId)),
       ISNULL(e.CurrentSamAccountName,N''), ISNULL(ad.Title,N''), a.StartDate, a.EndDate
FROM dbo.Assignments a
JOIN VisibleProjects v ON v.Id=a.ProjectId
JOIN dbo.Employees e ON e.EmployeeId=a.EmployeeId AND e.Status<>N'Merged'
LEFT JOIN dbo.ADObjects ad ON ad.ObjectGUID=e.CurrentADObjectGuid AND ISNULL(ad.IsDeleted,0)=0
WHERE a.StartDate<=CAST(SYSDATETIME() AS date) AND (a.EndDate IS NULL OR a.EndDate>=CAST(SYSDATETIME() AS date))
ORDER BY a.ProjectId,3;";

        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var row = new ProjectOrganisation
            {
                ProjectId = reader.GetInt32(0), ProjectNumber = reader.GetString(1),
                ProjectName = reader.GetString(2), Company = reader.GetString(3)
            };
            result[row.ProjectId] = row;
        }
        await reader.NextResultAsync();
        while (await reader.ReadAsync())
            if (result.TryGetValue(reader.GetInt32(0), out var p))
                p.Leaders.Add(new ProjectPerson { SamAccountName=reader.GetString(1), DisplayName=reader.GetString(2) });
        await reader.NextResultAsync();
        while (await reader.ReadAsync())
            if (result.TryGetValue(reader.GetInt32(0), out var p))
                p.Members.Add(new ProjectPerson
                {
                    EmployeeId=Convert.ToInt64(reader.GetValue(1)), DisplayName=reader.GetString(2), SamAccountName=reader.GetString(3),
                    Title=reader.GetString(4), StartDate=reader.GetDateTime(5), EndDate=reader.IsDBNull(6)?null:reader.GetDateTime(6)
                });
        return result.Values.ToList();
    }

    private const string BaseSelect = @"
SELECT ad.ObjectGUID, ad.SamAccountName, ISNULL(ad.DisplayName,ad.SamAccountName), ISNULL(ad.Title,N''),
       ISNULL(ad.Department,N''), ISNULL(ad.Company,N''), ISNULL(ad.Office,N''), ISNULL(ad.Mail,N''),
       ISNULL(ad.ManagerSamAccountName,N''), ISNULL(ad.EmployeeType,N'')
";

    private static async Task<List<EmployeeRow>> ReadEmployeesAsync(SqlCommand command)
    {
        var employees=new List<EmployeeRow>();
        await using var reader=await command.ExecuteReaderAsync();
        while(await reader.ReadAsync()) employees.Add(new EmployeeRow
        {
            ObjectGuid=reader.GetGuid(0), SamAccountName=reader.GetString(1), DisplayName=reader.GetString(2),
            Title=reader.GetString(3), Department=reader.GetString(4), Company=reader.GetString(5), Office=reader.GetString(6),
            Mail=reader.GetString(7), ManagerSamAccountName=reader.GetString(8), EmployeeType=reader.GetString(9)
        });
        return employees;
    }

    private static List<OrganisationNode> BuildFlatTree(IReadOnlyCollection<EmployeeRow> employees,string? preferredRootSamAccountName)
    {
        var bySam=employees.Where(e=>!string.IsNullOrWhiteSpace(e.SamAccountName)).GroupBy(e=>e.SamAccountName,StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g=>g.Key,g=>g.First(),StringComparer.OrdinalIgnoreCase);
        var children=new Dictionary<string,List<EmployeeRow>>(StringComparer.OrdinalIgnoreCase);
        foreach(var e in bySam.Values)
        {
            if(string.IsNullOrWhiteSpace(e.ManagerSamAccountName)||!bySam.ContainsKey(e.ManagerSamAccountName)||e.ManagerSamAccountName.Equals(e.SamAccountName,StringComparison.OrdinalIgnoreCase)) continue;
            if(!children.TryGetValue(e.ManagerSamAccountName,out var list)) children[e.ManagerSamAccountName]=list=new();
            list.Add(e);
        }
        foreach(var list in children.Values) list.Sort(EmployeeComparer.Instance);
        var roots=bySam.Values.Where(e=>string.IsNullOrWhiteSpace(e.ManagerSamAccountName)||!bySam.ContainsKey(e.ManagerSamAccountName)||e.ManagerSamAccountName.Equals(e.SamAccountName,StringComparison.OrdinalIgnoreCase))
            .OrderBy(e=>e,EmployeeComparer.Instance).ToList();
        var flat=new List<OrganisationNode>(); var visited=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach(var root in roots) AddBranch(root,null,0,children,visited,flat);
        foreach(var remaining in bySam.Values.OrderBy(e=>e,EmployeeComparer.Instance)) if(!visited.Contains(remaining.SamAccountName)) AddBranch(remaining,null,0,children,visited,flat);
        var descendants=new Dictionary<string,int>(StringComparer.OrdinalIgnoreCase);
        foreach(var node in flat.OrderByDescending(n=>n.Depth))
        {
            descendants.TryAdd(node.SamAccountName,0);
            if(!string.IsNullOrWhiteSpace(node.ParentSamAccountName)) { descendants.TryAdd(node.ParentSamAccountName,0); descendants[node.ParentSamAccountName]+=descendants[node.SamAccountName]+1; }
        }
        foreach(var node in flat) node.TotalReports=descendants.GetValueOrDefault(node.SamAccountName);
        return flat;
    }

    private static void AddBranch(EmployeeRow e,string? parent,int depth,IReadOnlyDictionary<string,List<EmployeeRow>> children,ISet<string> visited,ICollection<OrganisationNode> output)
    {
        if(!visited.Add(e.SamAccountName)) return;
        var reports=children.GetValueOrDefault(e.SamAccountName)??new();
        output.Add(new OrganisationNode { ObjectGuid=e.ObjectGuid,SamAccountName=e.SamAccountName,DisplayName=e.DisplayName,Title=e.Title,Department=e.Department,Company=e.Company,Office=e.Office,Mail=e.Mail,EmployeeType=e.EmployeeType,ParentSamAccountName=parent,Depth=depth,DirectReports=reports.Count });
        foreach(var child in reports) AddBranch(child,e.SamAccountName,depth+1,children,visited,output);
    }

    private sealed class EmployeeRow
    {
        public Guid ObjectGuid {get;init;} public string SamAccountName {get;init;}=""; public string DisplayName {get;init;}="";
        public string Title {get;init;}=""; public string Department {get;init;}=""; public string Company {get;init;}=""; public string Office {get;init;}="";
        public string Mail {get;init;}=""; public string ManagerSamAccountName {get;init;}=""; public string EmployeeType {get;init;}="";
    }
    public sealed class OrganisationNode
    {
        public Guid ObjectGuid {get;init;} public string SamAccountName {get;init;}=""; public string DisplayName {get;init;}=""; public string Title {get;init;}="";
        public string Department {get;init;}=""; public string Company {get;init;}=""; public string Office {get;init;}=""; public string Mail {get;init;}=""; public string EmployeeType {get;init;}="";
        public string? ParentSamAccountName {get;init;} public int Depth {get;init;} public int DirectReports {get;init;} public int TotalReports {get;set;} public bool HasChildren=>DirectReports>0;
    }
    public sealed class ProjectOrganisation
    {
        public int ProjectId {get;init;} public string ProjectNumber {get;init;}=""; public string ProjectName {get;init;}=""; public string Company {get;init;}="";
        public List<ProjectPerson> Leaders {get;}=new(); public List<ProjectPerson> Members {get;}=new();
    }
    public sealed class ProjectPerson
    {
        public long? EmployeeId {get;init;} public string SamAccountName {get;init;}=""; public string DisplayName {get;init;}=""; public string Title {get;init;}="";
        public DateTime? StartDate {get;init;} public DateTime? EndDate {get;init;}
    }
    private sealed class EmployeeComparer:IComparer<EmployeeRow>
    {
        public static EmployeeComparer Instance {get;}=new();
        public int Compare(EmployeeRow? x,EmployeeRow? y) => x is null?-1:y is null?1:string.Compare(x.DisplayName,y.DisplayName,StringComparison.CurrentCultureIgnoreCase);
    }
}

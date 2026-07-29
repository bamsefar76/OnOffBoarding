using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Data.SqlClient;
using UserChangeQueueWeb.Services;

namespace UserChangeQueueWeb.Pages.Settings;

[Authorize]
public sealed class TemporaryAccessGroupsModel : PageModel
{
    private readonly SqlConnectionFactory _connectionFactory;
    public TemporaryAccessGroupsModel(SqlConnectionFactory connectionFactory) => _connectionFactory = connectionFactory;

    [BindProperty(SupportsGet = true)] public int? Id { get; set; }
    [BindProperty] public EditModel Edit { get; set; } = new();
    [TempData] public string? StatusMessage { get; set; }
    public string? ErrorMessage { get; set; }
    public List<GroupListRow> Groups { get; } = new();
    public List<MemberRow> Members { get; } = new();

    public async Task OnGetAsync() => await LoadAsync();

    public async Task<IActionResult> OnPostSaveAsync()
    {
        Edit.DisplayName = Edit.DisplayName?.Trim() ?? "";
        Edit.AdGroupName = Edit.AdGroupName?.Trim() ?? "";
        Edit.Description = Edit.Description?.Trim();
        if (string.IsNullOrWhiteSpace(Edit.DisplayName) || string.IsNullOrWhiteSpace(Edit.AdGroupName) || Edit.DurationDays is < 1 or > 365)
        {
            ErrorMessage = "Display name, AD group name and a duration from 1 to 365 days are required.";
            Id = Edit.Id > 0 ? Edit.Id : null;
            await LoadAsync(loadEdit: false);
            return Page();
        }

        await using var cn = await _connectionFactory.OpenAsync(HttpContext.RequestAborted);
        try
        {
            await using var cmd = cn.CreateCommand();
            cmd.CommandText = Edit.Id > 0 ? @"
UPDATE dbo.TemporaryAccessGroups SET DisplayName=@DisplayName, AdGroupName=@AdGroupName, Description=@Description,
DurationDays=@DurationDays, AllowRenewal=@AllowRenewal, RequireReason=@RequireReason, Active=@Active, SortOrder=@SortOrder,
UpdatedAt=SYSDATETIME(), UpdatedBy=@User WHERE Id=@Id;" : @"
INSERT dbo.TemporaryAccessGroups(DisplayName,AdGroupName,Description,DurationDays,AllowRenewal,RequireReason,Active,SortOrder,CreatedBy)
OUTPUT INSERTED.Id VALUES(@DisplayName,@AdGroupName,@Description,@DurationDays,@AllowRenewal,@RequireReason,@Active,@SortOrder,@User);";
            AddParams(cmd);
            if (Edit.Id > 0) { await cmd.ExecuteNonQueryAsync(HttpContext.RequestAborted); Id = Edit.Id; }
            else Id = Convert.ToInt32(await cmd.ExecuteScalarAsync(HttpContext.RequestAborted));
            StatusMessage = "Temporary access group saved.";
            return RedirectToPage(new { id = Id });
        }
        catch (SqlException ex) when (ex.Number is 2601 or 2627)
        {
            ErrorMessage = "That AD group is already configured.";
            await LoadAsync(cn, loadEdit: false);
            return Page();
        }
    }

    public async Task<IActionResult> OnPostEndNowAsync(long membershipId)
    {
        await using var cn = await _connectionFactory.OpenAsync(HttpContext.RequestAborted);
        await using var cmd = cn.CreateCommand();
        cmd.CommandText = @"UPDATE dbo.TemporaryGroupMemberships SET Status=CASE WHEN Status=N'PendingAdd' THEN N'Cancelled' ELSE N'PendingRemove' END, CancelledAt=SYSDATETIME(), UpdatedAt=SYSDATETIME(), LastError=NULL
WHERE Id=@Id AND Status IN(N'Active',N'PendingAdd',N'ProcessingAdd');";
        cmd.Parameters.Add(new SqlParameter("@Id", System.Data.SqlDbType.BigInt){Value=membershipId});
        await cmd.ExecuteNonQueryAsync(HttpContext.RequestAborted);
        StatusMessage = "Removal has been queued.";
        return RedirectToPage(new { id = Id });
    }

    private void AddParams(SqlCommand cmd)
    {
        cmd.Parameters.Add(new SqlParameter("@Id", System.Data.SqlDbType.Int){Value=Edit.Id});
        cmd.Parameters.Add(new SqlParameter("@DisplayName", System.Data.SqlDbType.NVarChar,200){Value=Edit.DisplayName});
        cmd.Parameters.Add(new SqlParameter("@AdGroupName", System.Data.SqlDbType.NVarChar,300){Value=Edit.AdGroupName});
        cmd.Parameters.Add(new SqlParameter("@Description", System.Data.SqlDbType.NVarChar,1000){Value=(object?)Edit.Description??DBNull.Value});
        cmd.Parameters.Add(new SqlParameter("@DurationDays", System.Data.SqlDbType.Int){Value=Edit.DurationDays});
        cmd.Parameters.Add(new SqlParameter("@AllowRenewal", System.Data.SqlDbType.Bit){Value=Edit.AllowRenewal});
        cmd.Parameters.Add(new SqlParameter("@RequireReason", System.Data.SqlDbType.Bit){Value=Edit.RequireReason});
        cmd.Parameters.Add(new SqlParameter("@Active", System.Data.SqlDbType.Bit){Value=Edit.Active});
        cmd.Parameters.Add(new SqlParameter("@SortOrder", System.Data.SqlDbType.Int){Value=Edit.SortOrder});
        cmd.Parameters.Add(new SqlParameter("@User", System.Data.SqlDbType.NVarChar,300){Value=User.Identity?.Name??Environment.UserName});
    }

    private async Task LoadAsync(SqlConnection? existing=null, bool loadEdit=true)
    {
        var owns=existing is null; var cn=existing??await _connectionFactory.OpenAsync(HttpContext.RequestAborted);
        try
        {
            await using var cmd=cn.CreateCommand();
            cmd.CommandText=@"
SELECT g.Id,g.DisplayName,g.AdGroupName,g.DurationDays,g.Active,g.SortOrder,
COUNT(CASE WHEN m.Status IN(N'PendingAdd',N'ProcessingAdd',N'Active',N'PendingRemove',N'ProcessingRemove') THEN 1 END)
FROM dbo.TemporaryAccessGroups g LEFT JOIN dbo.TemporaryGroupMemberships m ON m.TemporaryAccessGroupId=g.Id
GROUP BY g.Id,g.DisplayName,g.AdGroupName,g.DurationDays,g.Active,g.SortOrder ORDER BY g.SortOrder,g.DisplayName;
SELECT TOP(100) m.Id,g.DisplayName,m.UserLoginName,m.Status,m.RequestedAt,m.ExpiresAt,m.LastError
FROM dbo.TemporaryGroupMemberships m JOIN dbo.TemporaryAccessGroups g ON g.Id=m.TemporaryAccessGroupId
WHERE (@GroupId IS NULL OR g.Id=@GroupId) ORDER BY m.Id DESC;";
            cmd.Parameters.Add(new SqlParameter("@GroupId", System.Data.SqlDbType.Int){Value=(object?)Id??DBNull.Value});
            await using var r=await cmd.ExecuteReaderAsync(HttpContext.RequestAborted);
            while(await r.ReadAsync(HttpContext.RequestAborted)) Groups.Add(new GroupListRow{Id=r.GetInt32(0),DisplayName=r.GetString(1),AdGroupName=r.GetString(2),DurationDays=r.GetInt32(3),Active=r.GetBoolean(4),SortOrder=r.GetInt32(5),CurrentCount=r.GetInt32(6)});
            await r.NextResultAsync(HttpContext.RequestAborted);
            while(await r.ReadAsync(HttpContext.RequestAborted)) Members.Add(new MemberRow{Id=r.GetInt64(0),GroupName=r.GetString(1),UserLoginName=r.GetString(2),Status=r.GetString(3),RequestedAt=r.GetDateTime(4),ExpiresAt=r.GetDateTime(5),LastError=r.IsDBNull(6)?null:r.GetString(6)});
            await r.CloseAsync();
            if(loadEdit && Id.HasValue)
            {
                await using var e=cn.CreateCommand(); e.CommandText="SELECT Id,DisplayName,AdGroupName,Description,DurationDays,AllowRenewal,RequireReason,Active,SortOrder FROM dbo.TemporaryAccessGroups WHERE Id=@Id;";
                e.Parameters.Add(new SqlParameter("@Id",System.Data.SqlDbType.Int){Value=Id.Value}); await using var er=await e.ExecuteReaderAsync(HttpContext.RequestAborted);
                if(await er.ReadAsync(HttpContext.RequestAborted)) Edit=new EditModel{Id=er.GetInt32(0),DisplayName=er.GetString(1),AdGroupName=er.GetString(2),Description=er.IsDBNull(3)?null:er.GetString(3),DurationDays=er.GetInt32(4),AllowRenewal=er.GetBoolean(5),RequireReason=er.GetBoolean(6),Active=er.GetBoolean(7),SortOrder=er.GetInt32(8)};
            }
        }
        finally{if(owns)await cn.DisposeAsync();}
    }

    public sealed class EditModel { public int Id{get;set;} public string? DisplayName{get;set;} public string? AdGroupName{get;set;} public string? Description{get;set;} public int DurationDays{get;set;}=7; public bool AllowRenewal{get;set;}=true; public bool RequireReason{get;set;} public bool Active{get;set;}=true; public int SortOrder{get;set;}=100; }
    public sealed class GroupListRow { public int Id{get;init;} public string DisplayName{get;init;}=""; public string AdGroupName{get;init;}=""; public int DurationDays{get;init;} public bool Active{get;init;} public int SortOrder{get;init;} public int CurrentCount{get;init;} }
    public sealed class MemberRow { public long Id{get;init;} public string GroupName{get;init;}=""; public string UserLoginName{get;init;}=""; public string Status{get;init;}=""; public DateTime RequestedAt{get;init;} public DateTime ExpiresAt{get;init;} public string? LastError{get;init;} }
}

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Data.SqlClient;
using UserChangeQueueWeb.Services;

namespace UserChangeQueueWeb.Pages.Settings;

[Authorize]
public sealed class PageAccessRulesModel : PageModel
{
    private const string ThisPagePath = "/Settings/PageAccessRules";
    private readonly SqlConnectionFactory _connectionFactory;

    public PageAccessRulesModel(SqlConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    [BindProperty(SupportsGet = true)]
    public string? Search { get; set; }

    [BindProperty(SupportsGet = true)]
    public bool ShowInactive { get; set; }

    [BindProperty]
    public RuleEditModel NewRule { get; set; } = new() { Active = true };

    [BindProperty]
    public RuleEditModel EditRule { get; set; } = new();

    [TempData]
    public string? StatusMessage { get; set; }

    public string? ErrorMessage { get; set; }

    public List<RuleRow> Rules { get; } = new();

    public async Task OnGetAsync()
    {
        await LoadRulesAsync();
    }

    public async Task<IActionResult> OnPostCreateAsync()
    {
        Normalize(NewRule);
        var validationError = Validate(NewRule);
        if (validationError is not null)
        {
            ErrorMessage = validationError;
            await LoadRulesAsync();
            return Page();
        }

        await using var cn = await _connectionFactory.OpenAsync(HttpContext.RequestAborted);
        await using var cmd = cn.CreateCommand();
        cmd.Parameters.AddNVarChar("@PagePath", NewRule.PagePath, 200);
        cmd.Parameters.AddNVarChar("@AdGroupName", NewRule.AdGroupName, 300);
        cmd.Parameters.AddBit("@Active", NewRule.Active);
        cmd.CommandText = @"
IF EXISTS
(
    SELECT 1
    FROM dbo.PageAccessRules
    WHERE PagePath = @PagePath
      AND AdGroupName = @AdGroupName
)
BEGIN
    UPDATE dbo.PageAccessRules
    SET Active = @Active
    WHERE PagePath = @PagePath
      AND AdGroupName = @AdGroupName;
END
ELSE
BEGIN
    INSERT INTO dbo.PageAccessRules (PagePath, AdGroupName, Active)
    VALUES (@PagePath, @AdGroupName, @Active);
END;
";
        await cmd.ExecuteNonQueryAsync(HttpContext.RequestAborted);

        StatusMessage = $"Access rule for {NewRule.PagePath} and {NewRule.AdGroupName} was saved.";
        return RedirectToPage(new { Search, ShowInactive });
    }

    public async Task<IActionResult> OnPostSaveAsync()
    {
        Normalize(EditRule);
        var validationError = Validate(EditRule, requireOriginal: true);
        if (validationError is not null)
        {
            ErrorMessage = validationError;
            await LoadRulesAsync();
            return Page();
        }

        await using var cn = await _connectionFactory.OpenAsync(HttpContext.RequestAborted);

        if (MovesOrDisablesLastSelfRule(EditRule) &&
            await IsLastActiveRuleForPageAsync(cn, ThisPagePath, EditRule.OriginalPagePath!, EditRule.OriginalAdGroupName!))
        {
            ErrorMessage = "This change would remove the final active access rule for the Page access rules page. Add the replacement rule first.";
            await LoadRulesAsync(cn);
            return Page();
        }

        await using var cmd = cn.CreateCommand();
        cmd.Parameters.AddNVarChar("@OriginalPagePath", EditRule.OriginalPagePath, 200);
        cmd.Parameters.AddNVarChar("@OriginalAdGroupName", EditRule.OriginalAdGroupName, 300);
        cmd.Parameters.AddNVarChar("@PagePath", EditRule.PagePath, 200);
        cmd.Parameters.AddNVarChar("@AdGroupName", EditRule.AdGroupName, 300);
        cmd.Parameters.AddBit("@Active", EditRule.Active);
        cmd.CommandText = @"
IF EXISTS
(
    SELECT 1
    FROM dbo.PageAccessRules
    WHERE PagePath = @PagePath
      AND AdGroupName = @AdGroupName
      AND NOT (PagePath = @OriginalPagePath AND AdGroupName = @OriginalAdGroupName)
)
BEGIN
    THROW 51001, 'An access rule with the same page path and AD group already exists.', 1;
END;

UPDATE dbo.PageAccessRules
SET
    PagePath = @PagePath,
    AdGroupName = @AdGroupName,
    Active = @Active
WHERE PagePath = @OriginalPagePath
  AND AdGroupName = @OriginalAdGroupName;
";

        try
        {
            var affected = await cmd.ExecuteNonQueryAsync(HttpContext.RequestAborted);
            if (affected == 0)
            {
                ErrorMessage = "The access rule was not found.";
                await LoadRulesAsync(cn);
                return Page();
            }
        }
        catch (SqlException ex) when (ex.Number is 51001 or 2601 or 2627)
        {
            ErrorMessage = "An access rule with the same page path and AD group already exists.";
            await LoadRulesAsync(cn);
            return Page();
        }

        StatusMessage = "Access rule was updated.";
        return RedirectToPage(new { Search, ShowInactive });
    }

    public async Task<IActionResult> OnPostSetActiveAsync(string pagePath, string adGroupName, bool active)
    {
        pagePath = NormalizePagePath(pagePath);
        adGroupName = (adGroupName ?? string.Empty).Trim();

        await using var cn = await _connectionFactory.OpenAsync(HttpContext.RequestAborted);

        if (!active && pagePath.Equals(ThisPagePath, StringComparison.OrdinalIgnoreCase) &&
            await IsLastActiveRuleForPageAsync(cn, ThisPagePath, pagePath, adGroupName))
        {
            ErrorMessage = "You cannot disable the final active access rule for this page. Add the replacement rule first.";
            await LoadRulesAsync(cn);
            return Page();
        }

        await using var cmd = cn.CreateCommand();
        cmd.Parameters.AddNVarChar("@PagePath", pagePath, 200);
        cmd.Parameters.AddNVarChar("@AdGroupName", adGroupName, 300);
        cmd.Parameters.AddBit("@Active", active);
        cmd.CommandText = @"
UPDATE dbo.PageAccessRules
SET Active = @Active
WHERE PagePath = @PagePath
  AND AdGroupName = @AdGroupName;
";
        await cmd.ExecuteNonQueryAsync(HttpContext.RequestAborted);

        StatusMessage = active ? "Access rule was activated." : "Access rule was disabled.";
        return RedirectToPage(new { Search, ShowInactive });
    }

    public async Task<IActionResult> OnPostDeleteAsync(string pagePath, string adGroupName)
    {
        pagePath = NormalizePagePath(pagePath);
        adGroupName = (adGroupName ?? string.Empty).Trim();

        await using var cn = await _connectionFactory.OpenAsync(HttpContext.RequestAborted);

        if (pagePath.Equals(ThisPagePath, StringComparison.OrdinalIgnoreCase) &&
            await IsLastActiveRuleForPageAsync(cn, ThisPagePath, pagePath, adGroupName))
        {
            ErrorMessage = "You cannot delete the final active access rule for this page. Add the replacement rule first.";
            await LoadRulesAsync(cn);
            return Page();
        }

        await using var cmd = cn.CreateCommand();
        cmd.Parameters.AddNVarChar("@PagePath", pagePath, 200);
        cmd.Parameters.AddNVarChar("@AdGroupName", adGroupName, 300);
        cmd.CommandText = @"
DELETE FROM dbo.PageAccessRules
WHERE PagePath = @PagePath
  AND AdGroupName = @AdGroupName;
";
        await cmd.ExecuteNonQueryAsync(HttpContext.RequestAborted);

        StatusMessage = "Access rule was deleted.";
        return RedirectToPage(new { Search, ShowInactive });
    }

    private async Task LoadRulesAsync(SqlConnection? existingConnection = null)
    {
        Rules.Clear();
        if (existingConnection is not null)
        {
            await ReadRulesAsync(existingConnection);
            return;
        }

        await using var cn = await _connectionFactory.OpenAsync(HttpContext.RequestAborted);
        await ReadRulesAsync(cn);
    }

    private async Task ReadRulesAsync(SqlConnection cn)
    {
        await using var cmd = cn.CreateCommand();
        cmd.Parameters.AddNVarChar("@Search", Search, 400);
        cmd.Parameters.AddBit("@ShowInactive", ShowInactive);
        cmd.CommandText = @"
SELECT
    PagePath,
    AdGroupName,
    Active
FROM dbo.PageAccessRules
WHERE (@ShowInactive = 1 OR Active = 1)
  AND
  (
      NULLIF(LTRIM(RTRIM(@Search)), N'') IS NULL
      OR PagePath LIKE N'%' + @Search + N'%'
      OR AdGroupName LIKE N'%' + @Search + N'%'
  )
ORDER BY PagePath, Active DESC, AdGroupName;
";

        await using var reader = await cmd.ExecuteReaderAsync(HttpContext.RequestAborted);
        while (await reader.ReadAsync(HttpContext.RequestAborted))
        {
            Rules.Add(new RuleRow
            {
                PagePath = reader.GetString(0),
                AdGroupName = reader.GetString(1),
                Active = reader.GetBoolean(2)
            });
        }
    }

    private static async Task<bool> IsLastActiveRuleForPageAsync(
        SqlConnection cn,
        string protectedPagePath,
        string currentPagePath,
        string currentAdGroupName)
    {
        if (!currentPagePath.Equals(protectedPagePath, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        await using var cmd = cn.CreateCommand();
        cmd.Parameters.AddNVarChar("@PagePath", protectedPagePath, 200);
        cmd.Parameters.AddNVarChar("@AdGroupName", currentAdGroupName, 300);
        cmd.CommandText = @"
SELECT CASE
    WHEN EXISTS
    (
        SELECT 1
        FROM dbo.PageAccessRules
        WHERE PagePath = @PagePath
          AND AdGroupName = @AdGroupName
          AND Active = 1
    )
    AND
    (
        SELECT COUNT_BIG(1)
        FROM dbo.PageAccessRules
        WHERE PagePath = @PagePath
          AND Active = 1
    ) <= 1
    THEN CAST(1 AS bit)
    ELSE CAST(0 AS bit)
END;
";
        return Convert.ToBoolean(await cmd.ExecuteScalarAsync());
    }

    private static bool MovesOrDisablesLastSelfRule(RuleEditModel rule)
    {
        if (!string.Equals(rule.OriginalPagePath, ThisPagePath, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return !rule.Active ||
               !string.Equals(rule.PagePath, ThisPagePath, StringComparison.OrdinalIgnoreCase);
    }

    private static string? Validate(RuleEditModel rule, bool requireOriginal = false)
    {
        if (requireOriginal &&
            (string.IsNullOrWhiteSpace(rule.OriginalPagePath) || string.IsNullOrWhiteSpace(rule.OriginalAdGroupName)))
        {
            return "The original access rule key is missing.";
        }

        if (string.IsNullOrWhiteSpace(rule.PagePath))
        {
            return "Page path is required.";
        }

        if (!rule.PagePath.StartsWith("/", StringComparison.Ordinal))
        {
            return "Page path must begin with /.";
        }

        if (string.IsNullOrWhiteSpace(rule.AdGroupName))
        {
            return "AD group name is required.";
        }

        return null;
    }

    private static void Normalize(RuleEditModel rule)
    {
        rule.PagePath = NormalizePagePath(rule.PagePath);
        rule.AdGroupName = rule.AdGroupName?.Trim();
        rule.OriginalPagePath = string.IsNullOrWhiteSpace(rule.OriginalPagePath)
            ? rule.OriginalPagePath
            : NormalizePagePath(rule.OriginalPagePath);
        rule.OriginalAdGroupName = rule.OriginalAdGroupName?.Trim();
    }

    private static string NormalizePagePath(string? value)
    {
        var pagePath = (value ?? string.Empty).Trim();
        if (pagePath.Length == 0)
        {
            return string.Empty;
        }

        return pagePath.StartsWith("/", StringComparison.Ordinal)
            ? pagePath
            : "/" + pagePath;
    }

    public sealed class RuleRow
    {
        public string PagePath { get; set; } = string.Empty;
        public string AdGroupName { get; set; } = string.Empty;
        public bool Active { get; set; }
    }

    public sealed class RuleEditModel
    {
        public string? OriginalPagePath { get; set; }
        public string? OriginalAdGroupName { get; set; }
        public string? PagePath { get; set; }
        public string? AdGroupName { get; set; }
        public bool Active { get; set; } = true;
    }
}

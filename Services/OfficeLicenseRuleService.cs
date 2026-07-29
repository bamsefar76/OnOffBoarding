using Microsoft.Data.SqlClient;

namespace UserChangeQueueWeb.Services;

public class OfficeLicenseRuleService
{
    private readonly SqlConnectionFactory _connectionFactory;

    public OfficeLicenseRuleService(SqlConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    public class TitleOfficeLicenseRule
    {
        public string Title { get; set; } = "";
        public string? LicenseName { get; set; }
        public int Priority { get; set; }
    }

    public class OfficeLicenseRuleResult
    {
        public string? Title { get; set; }
        public bool RuleTableExists { get; set; }
        public bool HasRule { get; set; }
        public string? LicenseName { get; set; }

        public bool HasOfficeLicense => !string.IsNullOrWhiteSpace(LicenseName);
    }

    public async Task<OfficeLicenseRuleResult> ResolveLicenseForTitleAsync(string? title)
    {
        await using var cn = await _connectionFactory.OpenAsync();

        return await ResolveLicenseForTitleAsync(cn, title);
    }

    public async Task<OfficeLicenseRuleResult> ResolveLicenseForTitleAsync(
        SqlConnection cn,
        string? title,
        SqlTransaction? transaction = null)
    {
        var normalizedTitle = Normalize(title);

        var result = new OfficeLicenseRuleResult
        {
            Title = normalizedTitle
        };

        if (string.IsNullOrWhiteSpace(normalizedTitle))
        {
            result.RuleTableExists = await RuleTableExistsAsync(cn, transaction);
            return result;
        }

        result.RuleTableExists = await RuleTableExistsAsync(cn, transaction);

        if (!result.RuleTableExists)
        {
            return result;
        }

        await using var cmd = cn.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = @"
SELECT TOP (1)
    LicenseName
FROM dbo.TitleOfficeLicenseRules
WHERE Active = 1
  AND LOWER(LTRIM(RTRIM(Title))) = LOWER(LTRIM(RTRIM(@Title)))
ORDER BY Priority, Id;
";
        cmd.Parameters.AddNVarChar("@Title", normalizedTitle, 256);

        var value = await cmd.ExecuteScalarAsync();

        if (value is null)
        {
            return result;
        }

        result.HasRule = true;
        result.LicenseName = value == DBNull.Value ? null : Convert.ToString(value)?.Trim();

        return result;
    }

    public async Task<List<TitleOfficeLicenseRule>> LoadActiveTitleRulesAsync()
    {
        await using var cn = await _connectionFactory.OpenAsync();

        return await LoadActiveTitleRulesAsync(cn);
    }

    public async Task<List<TitleOfficeLicenseRule>> LoadActiveTitleRulesAsync(
        SqlConnection cn,
        SqlTransaction? transaction = null)
    {
        if (!await RuleTableExistsAsync(cn, transaction))
        {
            return new List<TitleOfficeLicenseRule>();
        }

        await using var cmd = cn.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = @"
SELECT
    Title,
    LicenseName,
    Priority
FROM dbo.TitleOfficeLicenseRules
WHERE Active = 1
ORDER BY Title, Priority, Id;
";

        var rules = new List<TitleOfficeLicenseRule>();

        await using var reader = await cmd.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {
            rules.Add(new TitleOfficeLicenseRule
            {
                Title = reader.IsDBNull(0) ? "" : reader.GetString(0),
                LicenseName = reader.IsDBNull(1) ? null : reader.GetString(1),
                Priority = reader.IsDBNull(2) ? 100 : reader.GetInt32(2)
            });
        }

        return rules;
    }

    private static async Task<bool> RuleTableExistsAsync(SqlConnection cn, SqlTransaction? transaction = null)
    {
        await using var cmd = cn.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = @"
SELECT CASE
    WHEN OBJECT_ID(N'dbo.TitleOfficeLicenseRules', N'U') IS NULL THEN 0
    ELSE 1
END;
";

        var value = await cmd.ExecuteScalarAsync();
        return Convert.ToInt32(value) == 1;
    }

    private static string? Normalize(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}

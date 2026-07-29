using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Data.SqlClient;
using UserChangeQueueWeb.Services;

namespace UserChangeQueueWeb.Pages.SettingsAdmin;

public sealed class DomainsModel : PageModel
{
    private readonly SqlConnectionFactory _connectionFactory;
    private readonly UiTextService _uiTextService;

    public DomainsModel(SqlConnectionFactory connectionFactory, UiTextService uiTextService)
    {
        _connectionFactory = connectionFactory;
        _uiTextService = uiTextService;
    }

    [BindProperty(SupportsGet = true)]
    public string? Search { get; set; }

    [BindProperty]
    public DomainEditModel Edit { get; set; } = new();

    [BindProperty]
    public DomainEditModel NewDomain { get; set; } = new();

    public string? Message { get; set; }

    public string? ErrorMessage { get; set; }

    public List<DomainRow> Rows { get; } = new();

    public Dictionary<string, string> Ui { get; private set; } = new(StringComparer.OrdinalIgnoreCase);

    public string T(string key, string fallback)
    {
        return Ui.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : fallback;
    }

    public async Task OnGetAsync()
    {
        await LoadPageAsync();
    }

    public async Task<IActionResult> OnPostCreateAsync()
    {
        var validationError = ValidateDomain(NewDomain, isNew: true);
        if (validationError is not null)
        {
            ErrorMessage = validationError;
            await LoadPageAsync();
            return Page();
        }

        await using var cn = await OpenConnectionAsync();

        if (await DomainExistsAsync(cn, NewDomain.Domain!))
        {
            ErrorMessage = $"Domain '{NewDomain.Domain}' already exists.";
            await LoadPageAsync(cn);
            return Page();
        }

        await using var cmd = cn.CreateCommand();
        cmd.Parameters.AddNVarChar("@Domain", NewDomain.Domain, 200);
        cmd.Parameters.AddNVarChar("@OU", NewDomain.OU, 1000);
        cmd.Parameters.AddNVarChar("@Company", NewDomain.Company, 300);
        cmd.Parameters.AddNVarChar("@Street", NewDomain.Street, 300);
        cmd.Parameters.AddNVarChar("@Zipcode", NewDomain.Zipcode, 50);
        cmd.Parameters.AddNVarChar("@City", NewDomain.City, 200);
        cmd.Parameters.AddNVarChar("@Country", NewDomain.Country, 100);
        cmd.Parameters.AddNVarChar("@Office", NewDomain.Office, 300);
        cmd.CommandText = @"
INSERT INTO dbo.domains
(
    [domain],
    [OU],
    [company],
    [Street],
    [Zipcode],
    [City],
    [Country],
    [Office]
)
VALUES
(
    @Domain,
    @OU,
    @Company,
    @Street,
    @Zipcode,
    @City,
    @Country,
    @Office
);
";
        await cmd.ExecuteNonQueryAsync();

        return RedirectToPage(new { Search = NewDomain.Domain });
    }

    public async Task<IActionResult> OnPostSaveAsync()
    {
        var validationError = ValidateDomain(Edit, isNew: false);
        if (validationError is not null)
        {
            ErrorMessage = validationError;
            await LoadPageAsync();
            return Page();
        }

        if (string.IsNullOrWhiteSpace(Edit.OriginalDomain))
        {
            ErrorMessage = "Original domain is missing.";
            await LoadPageAsync();
            return Page();
        }

        await using var cn = await OpenConnectionAsync();

        if (!string.Equals(Edit.OriginalDomain.Trim(), Edit.Domain!.Trim(), StringComparison.OrdinalIgnoreCase)
            && await DomainExistsAsync(cn, Edit.Domain!))
        {
            ErrorMessage = $"Domain '{Edit.Domain}' already exists.";
            await LoadPageAsync(cn);
            return Page();
        }

        await using var cmd = cn.CreateCommand();
        cmd.Parameters.AddNVarChar("@OriginalDomain", Edit.OriginalDomain, 200);
        cmd.Parameters.AddNVarChar("@Domain", Edit.Domain, 200);
        cmd.Parameters.AddNVarChar("@OU", Edit.OU, 1000);
        cmd.Parameters.AddNVarChar("@Company", Edit.Company, 300);
        cmd.Parameters.AddNVarChar("@Street", Edit.Street, 300);
        cmd.Parameters.AddNVarChar("@Zipcode", Edit.Zipcode, 50);
        cmd.Parameters.AddNVarChar("@City", Edit.City, 200);
        cmd.Parameters.AddNVarChar("@Country", Edit.Country, 100);
        cmd.Parameters.AddNVarChar("@Office", Edit.Office, 300);
        cmd.CommandText = @"
UPDATE dbo.domains
SET
    [domain] = @Domain,
    [OU] = @OU,
    [company] = @Company,
    [Street] = @Street,
    [Zipcode] = @Zipcode,
    [City] = @City,
    [Country] = @Country,
    [Office] = @Office
WHERE [domain] = @OriginalDomain;
";
        var affected = await cmd.ExecuteNonQueryAsync();
        if (affected == 0)
        {
            ErrorMessage = $"Domain '{Edit.OriginalDomain}' was not found.";
            await LoadPageAsync(cn);
            return Page();
        }

        return RedirectToPage(new { Search = Edit.Domain });
    }

    public async Task<IActionResult> OnPostDeleteAsync(string originalDomain)
    {
        if (string.IsNullOrWhiteSpace(originalDomain))
        {
            ErrorMessage = "Domain is missing.";
            await LoadPageAsync();
            return Page();
        }

        await using var cn = await OpenConnectionAsync();
        await using var cmd = cn.CreateCommand();
        cmd.Parameters.AddNVarChar("@Domain", originalDomain, 200);
        cmd.CommandText = @"
DELETE FROM dbo.domains
WHERE [domain] = @Domain;
";
        await cmd.ExecuteNonQueryAsync();

        return RedirectToPage(new { Search });
    }

    private static string? ValidateDomain(DomainEditModel model, bool isNew)
    {
        if (!isNew && string.IsNullOrWhiteSpace(model.OriginalDomain))
        {
            return "Original domain is required.";
        }

        if (string.IsNullOrWhiteSpace(model.Domain))
        {
            return "Domain is required.";
        }

        if (string.IsNullOrWhiteSpace(model.OU))
        {
            return "OU is required.";
        }

        if (!model.Domain.Contains('.', StringComparison.Ordinal))
        {
            return "Domain should be a DNS domain such as example.com.";
        }

        if (!model.OU.Contains("DC=", StringComparison.OrdinalIgnoreCase))
        {
            return "OU must be a distinguished name such as OU=Users,DC=contoso,DC=local";
        }

        return null;
    }

    private async Task LoadPageAsync(SqlConnection? existingConnection = null)
    {
        Ui = (await _uiTextService.GetTextsAsync(HttpContext, FallbackTexts)).Texts.ToDictionary(x => x.Key, x => x.Value, StringComparer.OrdinalIgnoreCase);

        if (existingConnection is not null)
        {
            Rows.AddRange(await LoadRowsAsync(existingConnection));
            return;
        }

        await using var cn = await OpenConnectionAsync();
        Rows.AddRange(await LoadRowsAsync(cn));
    }

    private async Task<List<DomainRow>> LoadRowsAsync(SqlConnection cn)
    {
        var rows = new List<DomainRow>();
        await using var cmd = cn.CreateCommand();
        cmd.Parameters.AddNVarChar("@Search", Search, 400);
        cmd.CommandText = @"
SELECT
    [domain],
    [OU],
    [company],
    [Street],
    [Zipcode],
    [City],
    [Country],
    [Office]
FROM dbo.domains
WHERE
    @Search IS NULL
    OR [domain] LIKE '%' + @Search + '%'
    OR [OU] LIKE '%' + @Search + '%'
    OR [company] LIKE '%' + @Search + '%'
    OR [Office] LIKE '%' + @Search + '%'
ORDER BY [domain];
";

        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add(new DomainRow
            {
                Domain = GetString(reader, 0),
                OU = GetString(reader, 1),
                Company = GetString(reader, 2),
                Street = GetString(reader, 3),
                Zipcode = GetString(reader, 4),
                City = GetString(reader, 5),
                Country = GetString(reader, 6),
                Office = GetString(reader, 7)
            });
        }

        return rows;
    }

    private static async Task<bool> DomainExistsAsync(SqlConnection cn, string domain)
    {
        await using var cmd = cn.CreateCommand();
        cmd.Parameters.AddNVarChar("@Domain", domain, 200);
        cmd.CommandText = @"
SELECT COUNT(1)
FROM dbo.domains
WHERE LOWER(LTRIM(RTRIM([domain]))) = LOWER(LTRIM(RTRIM(@Domain)));
";
        var count = Convert.ToInt32(await cmd.ExecuteScalarAsync());
        return count > 0;
    }

    private async Task<SqlConnection> OpenConnectionAsync()
    {
        var cn = await _connectionFactory.OpenAsync();
        return cn;
    }

    private static string? GetString(SqlDataReader reader, int ordinal)
    {
        return reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    }

    private static readonly Dictionary<string, string> FallbackTexts = new(StringComparer.OrdinalIgnoreCase)
    {
        ["domains.title"] = "Domains and OUs",
        ["domains.description"] = "Maintain the domain-to-OU/company defaults used by new user requests.",
        ["domains.search"] = "Search",
        ["domains.applyFilters"] = "Apply filters",
        ["domains.addDomain"] = "Add domain",
        ["domains.domain"] = "Domain",
        ["domains.ou"] = "OU",
        ["domains.company"] = "Company",
        ["domains.street"] = "Street",
        ["domains.zipcode"] = "Zip code",
        ["domains.city"] = "City",
        ["domains.country"] = "Country",
        ["domains.office"] = "Office",
        ["domains.save"] = "Save",
        ["domains.delete"] = "Delete",
        ["domains.warning"] = "Changing OU affects new requests only. Existing queue rows keep their stored NewOU until edited or updated manually.",
        ["domains.noRows"] = "No domains found."
    };

    public class DomainEditModel
    {
        public string? OriginalDomain { get; set; }
        public string? Domain { get; set; }
        public string? OU { get; set; }
        public string? Company { get; set; }
        public string? Street { get; set; }
        public string? Zipcode { get; set; }
        public string? City { get; set; }
        public string? Country { get; set; }
        public string? Office { get; set; }
    }

    public sealed class DomainRow : DomainEditModel
    {
    }
}

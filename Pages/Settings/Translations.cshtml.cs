using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Data.SqlClient;
using UserChangeQueueWeb.Services;

namespace UserChangeQueueWeb.Pages.SettingsAdmin;

public sealed class TranslationsModel : PageModel
{
    private readonly SqlConnectionFactory _connectionFactory;
    private readonly UiTextService _uiTextService;

    public TranslationsModel(SqlConnectionFactory connectionFactory, UiTextService uiTextService)
    {
        _connectionFactory = connectionFactory;
        _uiTextService = uiTextService;
    }

    [BindProperty(SupportsGet = true)]
    public string? Search { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? Category { get; set; }

    [BindProperty(SupportsGet = true)]
    public bool ShowInactive { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? Language { get; set; }

    [BindProperty(SupportsGet = true)]
    public bool OnlyMissingForLanguage { get; set; }

    [BindProperty]
    public string? NewKey { get; set; }

    [BindProperty]
    public string? NewCategory { get; set; }

    [BindProperty]
    public string? NewEnglishText { get; set; }

    [BindProperty]
    public string? NewLanguageCode { get; set; }

    [BindProperty]
    public string? NewLanguageDisplayName { get; set; }

    [BindProperty]
    public string? NewLanguageNativeName { get; set; }

    [BindProperty]
    public int NewLanguageSortOrder { get; set; } = 100;

    public string? Message { get; set; }

    public string? ErrorMessage { get; set; }

    public List<LanguageRow> Languages { get; } = new();

    // The view renders one column per entry here (not per entry in Languages directly),
    // so that selecting a Language filter restricts which column(s) are shown.
    public List<LanguageRow> DisplayLanguages => string.IsNullOrWhiteSpace(Language)
        ? Languages
        : Languages.Where(l => string.Equals(l.LanguageCode, Language, StringComparison.OrdinalIgnoreCase)).ToList();

    public List<string> Categories { get; } = new();

    public List<TranslationRow> Rows { get; } = new();

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

    public async Task<IActionResult> OnPostCreateKeyAsync()
    {
        if (string.IsNullOrWhiteSpace(NewKey))
        {
            ErrorMessage = "Text key is required.";
            await LoadPageAsync();
            return Page();
        }

        var key = NewKey.Trim();
        var category = string.IsNullOrWhiteSpace(NewCategory) ? null : NewCategory.Trim();
        var englishText = string.IsNullOrWhiteSpace(NewEnglishText) ? key : NewEnglishText.Trim();

        await using var cn = await OpenConnectionAsync();
        await EnsureCategoryColumnAsync(cn);

        await using var tx = (SqlTransaction)await cn.BeginTransactionAsync();
        try
        {
            foreach (var language in await LoadLanguagesAsync(cn))
            {
                var textValue = string.Equals(language.LanguageCode, "en", StringComparison.OrdinalIgnoreCase)
                    ? englishText
                    : "";

                await UpsertTextAsync(cn, key, language.LanguageCode, textValue, category, true, "Translations page", tx);
            }

            await tx.CommitAsync();
        }
        catch
        {
            try
            {
                await tx.RollbackAsync();
            }
            catch
            {
                // Ignore rollback errors and rethrow the original exception.
            }

            throw;
        }

        return RedirectToPage(new { Search = key, Category = category, ShowInactive = true });
    }

    public async Task<IActionResult> OnPostAddLanguageAsync()
    {
        if (string.IsNullOrWhiteSpace(NewLanguageCode))
        {
            ErrorMessage = "Language code is required.";
            await LoadPageAsync();
            return Page();
        }

        var code = UiTextService.NormalizeLanguageCode(NewLanguageCode);
        var displayName = string.IsNullOrWhiteSpace(NewLanguageDisplayName) ? code : NewLanguageDisplayName.Trim();
        var nativeName = string.IsNullOrWhiteSpace(NewLanguageNativeName) ? displayName : NewLanguageNativeName.Trim();

        await using var cn = await OpenConnectionAsync();
        await using var cmd = cn.CreateCommand();
        cmd.Parameters.AddNVarChar("@LanguageCode", code, 10);
        cmd.Parameters.AddNVarChar("@DisplayName", displayName, 100);
        cmd.Parameters.AddNVarChar("@NativeName", nativeName, 100);
        cmd.Parameters.AddInt("@SortOrder", NewLanguageSortOrder);
        cmd.CommandText = @"
MERGE dbo.UiLanguages AS target
USING
(
    SELECT @LanguageCode AS LanguageCode,
           @DisplayName AS DisplayName,
           @NativeName AS NativeName,
           @SortOrder AS SortOrder
) AS source
ON target.LanguageCode = source.LanguageCode
WHEN MATCHED THEN
    UPDATE SET
        DisplayName = source.DisplayName,
        NativeName = source.NativeName,
        SortOrder = source.SortOrder,
        Active = 1
WHEN NOT MATCHED THEN
    INSERT (LanguageCode, DisplayName, NativeName, SortOrder, Active)
    VALUES (source.LanguageCode, source.DisplayName, source.NativeName, source.SortOrder, 1);
";
        await cmd.ExecuteNonQueryAsync();

        return RedirectToPage(new { Search, Category, ShowInactive = true });
    }

    [Microsoft.AspNetCore.Mvc.RequestFormLimits(ValueCountLimit = 20000)]
    public async Task<IActionResult> OnPostSaveVisibleAsync()
    {
        var form = Request.Form;
        var rowCount = ParseInt(form["RowCount"].ToString());
        var languages = await GetActiveLanguagesForSaveAsync();

        await using var cn = await OpenConnectionAsync();
        await EnsureCategoryColumnAsync(cn);

        await using var tx = (SqlTransaction)await cn.BeginTransactionAsync();
        try
        {
            for (var i = 0; i < rowCount; i++)
            {
                var key = form[$"RowKey_{i}"].ToString();
                if (string.IsNullOrWhiteSpace(key))
                {
                    continue;
                }

                var category = form[$"Category_{i}"].ToString();
                var active = string.Equals(form[$"Active_{i}"].ToString(), "true", StringComparison.OrdinalIgnoreCase);

                foreach (var language in languages)
                {
                    var formKey = $"Value_{i}_{language.LanguageCode}";
                    if (!form.ContainsKey(formKey))
                    {
                        // This language's column wasn't rendered (e.g. the Language filter
                        // restricted the view to a different language) -- leave its existing
                        // value untouched rather than overwriting it with a blank.
                        continue;
                    }

                    var value = form[formKey].ToString();
                    await UpsertTextAsync(
                        cn,
                        key.Trim(),
                        language.LanguageCode,
                        value,
                        string.IsNullOrWhiteSpace(category) ? null : category.Trim(),
                        active,
                        User.Identity?.Name ?? "Translations page",
                        tx);
                }
            }

            await tx.CommitAsync();
        }
        catch
        {
            try
            {
                await tx.RollbackAsync();
            }
            catch
            {
                // Ignore rollback errors and rethrow the original exception.
            }

            throw;
        }

        return RedirectToPage(new
        {
            Search = form["Search"].ToString(),
            Category = form["Category"].ToString(),
            ShowInactive = string.Equals(form["ShowInactive"].ToString(), "True", StringComparison.OrdinalIgnoreCase),
            Language = form["Language"].ToString(),
            OnlyMissingForLanguage = string.Equals(form["OnlyMissingForLanguage"].ToString(), "True", StringComparison.OrdinalIgnoreCase)
        });
    }

    private async Task LoadPageAsync()
    {
        Ui = (await _uiTextService.GetTextsAsync(HttpContext, FallbackTexts)).Texts.ToDictionary(x => x.Key, x => x.Value, StringComparer.OrdinalIgnoreCase);

        await using var cn = await OpenConnectionAsync();
        await EnsureCategoryColumnAsync(cn);

        Languages.AddRange(await LoadLanguagesAsync(cn));
        Categories.AddRange(await LoadCategoriesAsync(cn));

        var rows = await LoadRowsAsync(cn);

        if (OnlyMissingForLanguage && !string.IsNullOrWhiteSpace(Language))
        {
            rows = rows
                .Where(row => !row.Values.TryGetValue(Language, out var value) || string.IsNullOrWhiteSpace(value))
                .ToList();
        }

        Rows.AddRange(rows);
    }

    private async Task<SqlConnection> OpenConnectionAsync()
    {
        var cn = await _connectionFactory.OpenAsync();
        return cn;
    }

    private static async Task EnsureCategoryColumnAsync(SqlConnection cn)
    {
        await using var cmd = cn.CreateCommand();
        cmd.CommandText = @"
IF COL_LENGTH('dbo.UiTexts', 'Category') IS NULL
BEGIN
    ALTER TABLE dbo.UiTexts ADD Category nvarchar(100) NULL;
END;
";
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<List<LanguageRow>> GetActiveLanguagesForSaveAsync()
    {
        await using var cn = await OpenConnectionAsync();
        return await LoadLanguagesAsync(cn);
    }

    private static async Task<List<LanguageRow>> LoadLanguagesAsync(SqlConnection cn)
    {
        var result = new List<LanguageRow>();
        await using var cmd = cn.CreateCommand();
        cmd.CommandText = @"
SELECT LanguageCode, DisplayName, NativeName
FROM dbo.UiLanguages
WHERE Active = 1
ORDER BY SortOrder, LanguageCode;
";
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            result.Add(new LanguageRow(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2)));
        }
        return result;
    }

    private static async Task<List<string>> LoadCategoriesAsync(SqlConnection cn)
    {
        var result = new List<string>();
        await using var cmd = cn.CreateCommand();
        cmd.CommandText = @"
SELECT DISTINCT Category
FROM dbo.UiTexts
WHERE NULLIF(LTRIM(RTRIM(Category)), N'') IS NOT NULL
ORDER BY Category;
";
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            result.Add(reader.GetString(0));
        }
        return result;
    }

    private async Task<List<TranslationRow>> LoadRowsAsync(SqlConnection cn)
    {
        var result = new Dictionary<string, TranslationRow>(StringComparer.OrdinalIgnoreCase);

        await using var cmd = cn.CreateCommand();
        cmd.Parameters.AddNVarChar("@Search", Search, 200);
        cmd.Parameters.AddNVarChar("@Category", Category, 100);
        cmd.Parameters.AddBit("@ShowInactive", ShowInactive);
        cmd.CommandText = @"
SELECT UiTextKey,
       LanguageCode,
       TextValue,
       Active,
       Category
FROM dbo.UiTexts
WHERE (@ShowInactive = 1 OR Active = 1)
  AND (@Search IS NULL OR UiTextKey LIKE N'%' + @Search + N'%' OR TextValue LIKE N'%' + @Search + N'%')
  AND (@Category IS NULL OR Category = @Category)
ORDER BY UiTextKey, LanguageCode;
";

        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var key = reader.GetString(0);
            var languageCode = reader.GetString(1);
            var textValue = reader.IsDBNull(2) ? "" : reader.GetString(2);
            var active = !reader.IsDBNull(3) && reader.GetBoolean(3);
            var category = reader.IsDBNull(4) ? "" : reader.GetString(4);

            if (!result.TryGetValue(key, out var row))
            {
                row = new TranslationRow { UiTextKey = key, Category = category, Active = active };
                result[key] = row;
            }

            row.Values[languageCode] = textValue;
            row.Active = row.Active || active;
            if (string.IsNullOrWhiteSpace(row.Category) && !string.IsNullOrWhiteSpace(category))
            {
                row.Category = category;
            }
        }

        return result.Values.OrderBy(x => x.UiTextKey, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static async Task UpsertTextAsync(
        SqlConnection cn,
        string key,
        string languageCode,
        string textValue,
        string? category,
        bool active,
        string updatedBy,
        SqlTransaction? transaction = null)
    {
        await using var cmd = cn.CreateCommand();
        cmd.Transaction = transaction;
        cmd.Parameters.AddNVarChar("@UiTextKey", key, 200);
        cmd.Parameters.AddNVarChar("@LanguageCode", languageCode, 10);
        // UiTexts.TextValue is NOT NULL -- unlike Category below, a blank value here must be
        // stored as an empty string, never DBNull (AddNVarCharMax converts blank -> DBNull,
        // which is correct for optional columns but wrong for this one).
        var textValueParameter = cmd.Parameters.Add("@TextValue", System.Data.SqlDbType.NVarChar, -1);
        textValueParameter.Value = textValue ?? string.Empty;
        cmd.Parameters.AddNVarChar("@Category", category, 100);
        cmd.Parameters.AddBit("@Active", active);
        cmd.Parameters.AddNVarChar("@UpdatedBy", updatedBy, 300);
        cmd.CommandText = @"
MERGE dbo.UiTexts AS target
USING
(
    SELECT @UiTextKey AS UiTextKey,
           @LanguageCode AS LanguageCode,
           @TextValue AS TextValue,
           @Category AS Category,
           @Active AS Active,
           @UpdatedBy AS UpdatedBy
) AS source
ON target.UiTextKey = source.UiTextKey
AND target.LanguageCode = source.LanguageCode
WHEN MATCHED THEN
    UPDATE SET
        TextValue = source.TextValue,
        Category = source.Category,
        Active = source.Active,
        UpdatedAt = SYSDATETIME(),
        UpdatedBy = source.UpdatedBy
WHEN NOT MATCHED THEN
    INSERT (UiTextKey, LanguageCode, TextValue, Category, Active, CreatedAt, UpdatedBy)
    VALUES (source.UiTextKey, source.LanguageCode, source.TextValue, source.Category, source.Active, SYSDATETIME(), source.UpdatedBy);
";
        await cmd.ExecuteNonQueryAsync();
    }

    private static int ParseInt(string? value)
    {
        return int.TryParse(value, out var result) ? result : 0;
    }

    private static readonly Dictionary<string, string> FallbackTexts = new(StringComparer.OrdinalIgnoreCase)
    {
        ["translations.title"] = "Translations",
        ["translations.description"] = "Maintain UI text for all enabled languages.",
        ["translations.search"] = "Search",
        ["translations.category"] = "Category",
        ["translations.key"] = "Key",
        ["translations.active"] = "Active",
        ["translations.saveVisible"] = "Save visible texts",
        ["translations.addKey"] = "Add text key",
        ["translations.addLanguage"] = "Add language",
        ["translations.showInactive"] = "Show inactive",
        ["translations.applyFilters"] = "Apply filters",
        ["translations.noRows"] = "No translation rows matched the filter."
    };

    public sealed record LanguageRow(string LanguageCode, string DisplayName, string NativeName);

    public sealed class TranslationRow
    {
        public string UiTextKey { get; set; } = "";
        public string Category { get; set; } = "";
        public bool Active { get; set; }
        public Dictionary<string, string> Values { get; } = new(StringComparer.OrdinalIgnoreCase);
    }
}

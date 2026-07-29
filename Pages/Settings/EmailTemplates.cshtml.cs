using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Data.SqlClient;
using UserChangeQueueWeb.Services;

namespace UserChangeQueueWeb.Pages.Settings;

public sealed class EmailTemplatesModel : PageModel
{
    private readonly SqlConnectionFactory _connectionFactory;

    public EmailTemplatesModel(SqlConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    [BindProperty(SupportsGet = true, Name = "id")]
    public int? SelectedTemplateId { get; set; }

    [BindProperty(SupportsGet = true, Name = "language")]
    public string? LanguageFilter { get; set; }

    [BindProperty(SupportsGet = true, Name = "domain")]
    public string? DomainFilter { get; set; }

    [BindProperty(SupportsGet = true, Name = "type")]
    public string? TemplateTypeFilter { get; set; }

    [BindProperty]
    public TemplateEditModel EditTemplate { get; set; } = new();

    [BindProperty]
    public TemplateEditModel NewTemplate { get; set; } = new() { Domain = "*", LanguageCode = "en", Active = true };

    [BindProperty]
    public CopyTemplateModel CopyTemplate { get; set; } = new() { Domain = "*", LanguageCode = "en", Active = true, CopyImages = true };

    [BindProperty]
    public ImageEditModel NewImage { get; set; } = new() { Domain = "*", ContentId = "header-logo", MimeType = "image/png", Active = true };

    public List<TemplateRow> Templates { get; } = new();
    public List<string> AvailableLanguages { get; } = new();
    public List<string> AvailableDomains { get; } = new();
    public List<string> AvailableTemplateTypes { get; } = new();
    public List<ImageRow> Images { get; } = new();

    public string? Message { get; set; }
    public string? ErrorMessage { get; set; }

    public async Task OnGetAsync()
    {
        await using var cn = await _connectionFactory.OpenAsync();
        await LoadTemplatesAsync(cn);
        if (SelectedTemplateId.HasValue)
        {
            await LoadEditTemplateAsync(cn, SelectedTemplateId.Value);
            if (EditTemplate.Id > 0)
            {
                CopyTemplate = new CopyTemplateModel
                {
                    SourceTemplateId = EditTemplate.Id,
                    TemplateName = EditTemplate.TemplateName,
                    Domain = EditTemplate.Domain,
                    LanguageCode = EditTemplate.LanguageCode,
                    Active = EditTemplate.Active,
                    CopyImages = true
                };
            }
        }
    }

    public async Task<IActionResult> OnPostCreateTemplateAsync()
    {
        var validation = ValidateTemplate(NewTemplate, requireBody: false);
        if (validation is not null)
        {
            ErrorMessage = validation;
            await ReloadAsync();
            return Page();
        }

        await using var cn = await _connectionFactory.OpenAsync();
        await using var cmd = cn.CreateCommand();
        cmd.Parameters.AddNVarChar("@TemplateName", NewTemplate.TemplateName, 100);
        cmd.Parameters.AddNVarChar("@Domain", NormalizeDomain(NewTemplate.Domain), 200);
        cmd.Parameters.AddNVarChar("@LanguageCode", NormalizeLanguageCode(NewTemplate.LanguageCode), 10);
        cmd.Parameters.AddNVarChar("@Subject", NewTemplate.Subject, 500);
        cmd.Parameters.AddNVarCharMax("@HtmlBody", string.IsNullOrWhiteSpace(NewTemplate.HtmlBody) ? "<p>Edit this template.</p>" : NewTemplate.HtmlBody);
        cmd.Parameters.AddNVarCharMax("@PlainTextBody", NewTemplate.PlainTextBody);
        cmd.Parameters.AddBit("@Active", true);
        cmd.Parameters.AddNVarChar("@UpdatedBy", User.Identity?.Name, 300);
        cmd.CommandText = @"
INSERT INTO dbo.EmailTemplates
(
    TemplateName,
    Domain,
    LanguageCode,
    Subject,
    HtmlBody,
    PlainTextBody,
    Active,
    UpdatedBy
)
OUTPUT inserted.Id
VALUES
(
    @TemplateName,
    @Domain,
    @LanguageCode,
    @Subject,
    @HtmlBody,
    @PlainTextBody,
    @Active,
    @UpdatedBy
);
";

        try
        {
            var newId = Convert.ToInt32(await cmd.ExecuteScalarAsync());
            return RedirectToPage("/Settings/EmailTemplates", new { id = newId });
        }
        catch (SqlException ex) when (ex.Number is 2601 or 2627)
        {
            ErrorMessage = "A template with the same template name, domain and language already exists.";
            await LoadTemplatesAsync(cn);
            return Page();
        }
    }

    public async Task<IActionResult> OnPostSaveTemplateAsync()
    {
        var validation = ValidateTemplate(EditTemplate, requireBody: true);
        if (validation is not null)
        {
            ErrorMessage = validation;
            await ReloadAsync(EditTemplate.Id);
            return Page();
        }

        await using var cn = await _connectionFactory.OpenAsync();
        await using var cmd = cn.CreateCommand();
        cmd.Parameters.AddInt("@Id", EditTemplate.Id);
        cmd.Parameters.AddNVarChar("@TemplateName", EditTemplate.TemplateName, 100);
        cmd.Parameters.AddNVarChar("@Domain", NormalizeDomain(EditTemplate.Domain), 200);
        cmd.Parameters.AddNVarChar("@LanguageCode", NormalizeLanguageCode(EditTemplate.LanguageCode), 10);
        cmd.Parameters.AddNVarChar("@Subject", EditTemplate.Subject, 500);
        cmd.Parameters.AddNVarCharMax("@HtmlBody", EditTemplate.HtmlBody);
        cmd.Parameters.AddNVarCharMax("@PlainTextBody", EditTemplate.PlainTextBody);
        cmd.Parameters.AddBit("@Active", EditTemplate.Active);
        cmd.Parameters.AddNVarChar("@UpdatedBy", User.Identity?.Name, 300);
        cmd.CommandText = @"
UPDATE dbo.EmailTemplates
SET
    TemplateName = @TemplateName,
    Domain = @Domain,
    LanguageCode = @LanguageCode,
    Subject = @Subject,
    HtmlBody = @HtmlBody,
    PlainTextBody = @PlainTextBody,
    Active = @Active,
    UpdatedAt = sysdatetime(),
    UpdatedBy = @UpdatedBy
WHERE Id = @Id;
";

        try
        {
            var affected = await cmd.ExecuteNonQueryAsync();
            if (affected == 0)
            {
                ErrorMessage = "Template was not found.";
                await ReloadAsync();
                return Page();
            }

            return RedirectToPage("/Settings/EmailTemplates", new { id = EditTemplate.Id });
        }
        catch (SqlException ex) when (ex.Number is 2601 or 2627)
        {
            ErrorMessage = "A template with the same template name, domain and language already exists.";
            await ReloadAsync(EditTemplate.Id);
            return Page();
        }
    }

    public async Task<IActionResult> OnPostCopyTemplateAsync()
    {
        if (CopyTemplate.SourceTemplateId <= 0)
        {
            ErrorMessage = "Source template id is missing.";
            await ReloadAsync();
            return Page();
        }

        if (string.IsNullOrWhiteSpace(CopyTemplate.TemplateName))
        {
            ErrorMessage = "Template name is required.";
            await ReloadAsync(CopyTemplate.SourceTemplateId);
            return Page();
        }

        if (string.IsNullOrWhiteSpace(CopyTemplate.Domain))
        {
            ErrorMessage = "Domain is required. Use * as fallback.";
            await ReloadAsync(CopyTemplate.SourceTemplateId);
            return Page();
        }

        if (string.IsNullOrWhiteSpace(CopyTemplate.LanguageCode))
        {
            ErrorMessage = "Language code is required.";
            await ReloadAsync(CopyTemplate.SourceTemplateId);
            return Page();
        }

        await using var cn = await _connectionFactory.OpenAsync();
        await using var tx = (SqlTransaction)await cn.BeginTransactionAsync();

        try
        {
            int newId;
            string sourceTemplateName;
            string sourceDomain;

            await using (var cmd = cn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.Parameters.AddInt("@SourceId", CopyTemplate.SourceTemplateId);
                cmd.Parameters.AddNVarChar("@TemplateName", CopyTemplate.TemplateName.Trim(), 100);
                cmd.Parameters.AddNVarChar("@Domain", NormalizeDomain(CopyTemplate.Domain), 200);
                cmd.Parameters.AddNVarChar("@LanguageCode", NormalizeLanguageCode(CopyTemplate.LanguageCode), 10);
                cmd.Parameters.AddBit("@Active", CopyTemplate.Active);
                cmd.Parameters.AddNVarChar("@UpdatedBy", User.Identity?.Name, 300);
                cmd.CommandText = @"
DECLARE @NewTemplate table (Id int);

INSERT INTO dbo.EmailTemplates
(
    TemplateName,
    Domain,
    LanguageCode,
    Subject,
    HtmlBody,
    PlainTextBody,
    Active,
    UpdatedBy
)
OUTPUT inserted.Id INTO @NewTemplate(Id)
SELECT
    @TemplateName,
    @Domain,
    @LanguageCode,
    Subject,
    HtmlBody,
    PlainTextBody,
    @Active,
    @UpdatedBy
FROM dbo.EmailTemplates
WHERE Id = @SourceId;

SELECT
    n.Id,
    s.TemplateName,
    s.Domain
FROM @NewTemplate AS n
CROSS JOIN dbo.EmailTemplates AS s
WHERE s.Id = @SourceId;
";

                await using var reader = await cmd.ExecuteReaderAsync();
                if (!await reader.ReadAsync())
                {
                    await tx.RollbackAsync();
                    ErrorMessage = "Source template was not found.";
                    await ReloadAsync();
                    return Page();
                }

                newId = reader.GetInt32(0);
                sourceTemplateName = reader.GetString(1);
                sourceDomain = reader.GetString(2);
            }

            if (CopyTemplate.CopyImages)
            {
                await using var cmd = cn.CreateCommand();
                cmd.Transaction = tx;
                cmd.Parameters.AddNVarChar("@SourceTemplateName", sourceTemplateName, 100);
                cmd.Parameters.AddNVarChar("@SourceDomain", sourceDomain, 200);
                cmd.Parameters.AddNVarChar("@TargetTemplateName", CopyTemplate.TemplateName.Trim(), 100);
                cmd.Parameters.AddNVarChar("@TargetDomain", NormalizeDomain(CopyTemplate.Domain), 200);
                cmd.Parameters.AddNVarChar("@UpdatedBy", User.Identity?.Name, 300);
                cmd.CommandText = @"
INSERT INTO dbo.EmailTemplateImages
(
    TemplateName,
    Domain,
    ContentId,
    ImagePath,
    MimeType,
    Active,
    UpdatedBy
)
SELECT
    @TargetTemplateName,
    @TargetDomain,
    src.ContentId,
    src.ImagePath,
    src.MimeType,
    src.Active,
    @UpdatedBy
FROM dbo.EmailTemplateImages AS src
WHERE src.TemplateName = @SourceTemplateName
  AND src.Domain = @SourceDomain
  AND NOT EXISTS
  (
      SELECT 1
      FROM dbo.EmailTemplateImages AS existing
      WHERE existing.TemplateName = @TargetTemplateName
        AND existing.Domain = @TargetDomain
        AND existing.ContentId = src.ContentId
        AND existing.Active = 1
  );
";
                await cmd.ExecuteNonQueryAsync();
            }

            await tx.CommitAsync();
            return RedirectToPage("/Settings/EmailTemplates", new { id = newId });
        }
        catch (SqlException ex) when (ex.Number is 2601 or 2627)
        {
            await tx.RollbackAsync();
            ErrorMessage = "A template with the same template name, domain and language already exists.";
            await ReloadAsync(CopyTemplate.SourceTemplateId);
            return Page();
        }
    }

    public async Task<IActionResult> OnPostAddImageAsync(int id)
    {
        if (id <= 0)
        {
            ErrorMessage = "Template id is missing.";
            await ReloadAsync();
            return Page();
        }

        var validation = ValidateImage(NewImage);
        if (validation is not null)
        {
            ErrorMessage = validation;
            await ReloadAsync(id);
            return Page();
        }

        await using var cn = await _connectionFactory.OpenAsync();
        var templateName = await GetTemplateNameAsync(cn, id);
        if (templateName is null)
        {
            ErrorMessage = "Template was not found.";
            await ReloadAsync();
            return Page();
        }

        await using var cmd = cn.CreateCommand();
        cmd.Parameters.AddNVarChar("@TemplateName", templateName, 100);
        cmd.Parameters.AddNVarChar("@Domain", NormalizeDomain(NewImage.Domain), 200);
        cmd.Parameters.AddNVarChar("@ContentId", NormalizeContentId(NewImage.ContentId), 100);
        cmd.Parameters.AddNVarChar("@ImagePath", NewImage.ImagePath, 1000);
        cmd.Parameters.AddNVarChar("@MimeType", NewImage.MimeType, 100);
        cmd.Parameters.AddBit("@Active", true);
        cmd.Parameters.AddNVarChar("@UpdatedBy", User.Identity?.Name, 300);
        cmd.CommandText = @"
INSERT INTO dbo.EmailTemplateImages
(
    TemplateName,
    Domain,
    ContentId,
    ImagePath,
    MimeType,
    Active,
    UpdatedBy
)
VALUES
(
    @TemplateName,
    @Domain,
    @ContentId,
    @ImagePath,
    @MimeType,
    @Active,
    @UpdatedBy
);
";
        await cmd.ExecuteNonQueryAsync();
        return RedirectToPage("/Settings/EmailTemplates", new { id });
    }

    public async Task<IActionResult> OnPostDeleteImageAsync(int id, int imageId)
    {
        await using var cn = await _connectionFactory.OpenAsync();
        await using var cmd = cn.CreateCommand();
        cmd.Parameters.AddInt("@ImageId", imageId);
        cmd.Parameters.AddNVarChar("@UpdatedBy", User.Identity?.Name, 300);
        cmd.CommandText = @"
UPDATE dbo.EmailTemplateImages
SET
    Active = 0,
    LastUpdated = sysdatetime(),
    UpdatedBy = @UpdatedBy
WHERE Id = @ImageId;
";
        await cmd.ExecuteNonQueryAsync();
        return RedirectToPage("/Settings/EmailTemplates", new { id });
    }

    private async Task ReloadAsync(int? id = null)
    {
        await using var cn = await _connectionFactory.OpenAsync();
        await LoadTemplatesAsync(cn);
        if (id.HasValue)
        {
            SelectedTemplateId = id;
            await LoadEditTemplateAsync(cn, id.Value);
        }
    }

    private async Task LoadTemplatesAsync(SqlConnection cn)
    {
        Templates.Clear();
        AvailableLanguages.Clear();
        AvailableDomains.Clear();
        AvailableTemplateTypes.Clear();

        await using (var optionsCmd = cn.CreateCommand())
        {
            optionsCmd.CommandText = @"
IF OBJECT_ID(N'dbo.EmailTemplates', N'U') IS NOT NULL
BEGIN
    SELECT DISTINCT LanguageCode FROM dbo.EmailTemplates ORDER BY LanguageCode;
    SELECT DISTINCT Domain FROM dbo.EmailTemplates ORDER BY Domain;
    SELECT DISTINCT TemplateName FROM dbo.EmailTemplates ORDER BY TemplateName;
END
";
            await using var optionsReader = await optionsCmd.ExecuteReaderAsync();
            while (await optionsReader.ReadAsync())
            {
                if (!optionsReader.IsDBNull(0)) AvailableLanguages.Add(optionsReader.GetString(0));
            }
            if (await optionsReader.NextResultAsync())
            {
                while (await optionsReader.ReadAsync())
                {
                    if (!optionsReader.IsDBNull(0)) AvailableDomains.Add(optionsReader.GetString(0));
                }
            }
            if (await optionsReader.NextResultAsync())
            {
                while (await optionsReader.ReadAsync())
                {
                    if (!optionsReader.IsDBNull(0)) AvailableTemplateTypes.Add(optionsReader.GetString(0));
                }
            }
        }

        await using var cmd = cn.CreateCommand();
        cmd.Parameters.AddNVarChar("@LanguageCode", string.IsNullOrWhiteSpace(LanguageFilter) ? null : NormalizeLanguageCode(LanguageFilter), 10);
        cmd.Parameters.AddNVarChar("@Domain", string.IsNullOrWhiteSpace(DomainFilter) ? null : NormalizeDomain(DomainFilter), 200);
        cmd.Parameters.AddNVarChar("@TemplateName", string.IsNullOrWhiteSpace(TemplateTypeFilter) ? null : TemplateTypeFilter.Trim(), 100);
        cmd.CommandText = @"
IF OBJECT_ID(N'dbo.EmailTemplates', N'U') IS NULL
BEGIN
    SELECT TOP (0)
        CAST(0 AS int) AS Id,
        CAST(N'' AS nvarchar(100)) AS TemplateName,
        CAST(N'*' AS nvarchar(200)) AS Domain,
        CAST(N'' AS nvarchar(10)) AS LanguageCode,
        CAST(N'' AS nvarchar(500)) AS Subject,
        CAST(1 AS bit) AS Active;
END
ELSE
BEGIN
    SELECT
        Id,
        TemplateName,
        Domain,
        LanguageCode,
        Subject,
        Active
    FROM dbo.EmailTemplates
    WHERE (@LanguageCode IS NULL OR LanguageCode = @LanguageCode)
      AND (@Domain IS NULL OR Domain = @Domain)
      AND (@TemplateName IS NULL OR TemplateName = @TemplateName)
    ORDER BY TemplateName, Domain, LanguageCode;
END
";
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            Templates.Add(new TemplateRow
            {
                Id = reader.GetInt32(0),
                TemplateName = reader.GetString(1),
                Domain = reader.GetString(2),
                LanguageCode = reader.GetString(3),
                Subject = reader.GetString(4),
                Active = reader.GetBoolean(5)
            });
        }
    }

    private async Task LoadEditTemplateAsync(SqlConnection cn, int id)
    {
        Images.Clear();
        await using (var cmd = cn.CreateCommand())
        {
            cmd.Parameters.AddInt("@Id", id);
            cmd.CommandText = @"
SELECT
    Id,
    TemplateName,
    Domain,
    LanguageCode,
    Subject,
    HtmlBody,
    PlainTextBody,
    Active
FROM dbo.EmailTemplates
WHERE Id = @Id;
";
            await using var reader = await cmd.ExecuteReaderAsync();
            if (await reader.ReadAsync())
            {
                EditTemplate = new TemplateEditModel
                {
                    Id = reader.GetInt32(0),
                    TemplateName = reader.GetString(1),
                    Domain = reader.GetString(2),
                    LanguageCode = reader.GetString(3),
                    Subject = reader.GetString(4),
                    HtmlBody = reader.GetString(5),
                    PlainTextBody = reader.IsDBNull(6) ? null : reader.GetString(6),
                    Active = reader.GetBoolean(7)
                };
                SelectedTemplateId = EditTemplate.Id;
            }
        }

        if (EditTemplate.Id == 0)
        {
            return;
        }

        await using (var cmd = cn.CreateCommand())
        {
            cmd.Parameters.AddNVarChar("@TemplateName", EditTemplate.TemplateName, 100);
            cmd.CommandText = @"
SELECT
    Id,
    TemplateName,
    Domain,
    ContentId,
    ImagePath,
    MimeType,
    Active
FROM dbo.EmailTemplateImages
WHERE TemplateName = @TemplateName
ORDER BY Active DESC, Domain, ContentId, Id;
";
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                Images.Add(new ImageRow
                {
                    Id = reader.GetInt32(0),
                    TemplateName = reader.GetString(1),
                    Domain = reader.GetString(2),
                    ContentId = reader.GetString(3),
                    ImagePath = reader.GetString(4),
                    MimeType = reader.GetString(5),
                    Active = reader.GetBoolean(6)
                });
            }
        }
    }

    private static async Task<string?> GetTemplateNameAsync(SqlConnection cn, int id)
    {
        await using var cmd = cn.CreateCommand();
        cmd.Parameters.AddInt("@Id", id);
        cmd.CommandText = "SELECT TemplateName FROM dbo.EmailTemplates WHERE Id = @Id;";
        var value = await cmd.ExecuteScalarAsync();
        return value is null or DBNull ? null : Convert.ToString(value);
    }

    private static string? ValidateTemplate(TemplateEditModel template, bool requireBody)
    {
        if (string.IsNullOrWhiteSpace(template.TemplateName)) return "Template name is required.";
        if (string.IsNullOrWhiteSpace(template.Domain)) return "Domain is required. Use * as fallback.";
        if (string.IsNullOrWhiteSpace(template.LanguageCode)) return "Language code is required.";
        if (string.IsNullOrWhiteSpace(template.Subject)) return "Subject is required.";
        if (requireBody && string.IsNullOrWhiteSpace(template.HtmlBody)) return "HTML body is required.";
        return null;
    }

    private static string? ValidateImage(ImageEditModel image)
    {
        if (string.IsNullOrWhiteSpace(image.Domain)) return "Domain is required. Use * as fallback.";
        if (string.IsNullOrWhiteSpace(image.ContentId)) return "ContentId is required.";
        if (string.IsNullOrWhiteSpace(image.ImagePath)) return "Image path is required.";
        if (string.IsNullOrWhiteSpace(image.MimeType)) return "Mime type is required.";
        if (!image.MimeType.Trim().StartsWith("image/", StringComparison.OrdinalIgnoreCase)) return "Mime type should start with image/.";
        return null;
    }

    private static string NormalizeLanguageCode(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? "en" : value.Trim().ToLowerInvariant();
    }

    private static string NormalizeDomain(string? value)
    {
        var domain = string.IsNullOrWhiteSpace(value) ? "*" : value.Trim().TrimStart('@').ToLowerInvariant();
        return domain == "default" ? "*" : domain;
    }

    private static string NormalizeContentId(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? "header-logo" : value.Trim().TrimStart('<').TrimEnd('>');
    }

    public sealed class TemplateRow
    {
        public int Id { get; set; }
        public string TemplateName { get; set; } = string.Empty;
        public string Domain { get; set; } = "*";
        public string LanguageCode { get; set; } = string.Empty;
        public string Subject { get; set; } = string.Empty;
        public bool Active { get; set; }
    }

    public sealed class TemplateEditModel
    {
        public int Id { get; set; }
        public string? TemplateName { get; set; }
        public string? Domain { get; set; } = "*";
        public string? LanguageCode { get; set; }
        public string? Subject { get; set; }
        public string? HtmlBody { get; set; }
        public string? PlainTextBody { get; set; }
        public bool Active { get; set; } = true;
    }

    public sealed class CopyTemplateModel
    {
        public int SourceTemplateId { get; set; }
        public string? TemplateName { get; set; }
        public string? Domain { get; set; } = "*";
        public string? LanguageCode { get; set; } = "en";
        public bool Active { get; set; } = true;
        public bool CopyImages { get; set; } = true;
    }

    public sealed class ImageRow
    {
        public int Id { get; set; }
        public string TemplateName { get; set; } = string.Empty;
        public string Domain { get; set; } = string.Empty;
        public string ContentId { get; set; } = string.Empty;
        public string ImagePath { get; set; } = string.Empty;
        public string MimeType { get; set; } = string.Empty;
        public bool Active { get; set; }
    }

    public sealed class ImageEditModel
    {
        public string? TemplateName { get; set; }
        public string? Domain { get; set; }
        public string? ContentId { get; set; }
        public string? ImagePath { get; set; }
        public string? MimeType { get; set; }
        public bool Active { get; set; } = true;
    }
}

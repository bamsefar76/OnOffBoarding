using Microsoft.Data.SqlClient;
using System.Net;

namespace UserChangeQueueWeb.Services;

public sealed class LicenseEmailService
{
    public async Task QueueTemplateAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        string templateName,
        string toEmail,
        string? toName,
        IReadOnlyDictionary<string, string?> tokens,
        IReadOnlyDictionary<string, string?>? htmlTokenOverrides,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(templateName))
            throw new ArgumentException("Template name is required.", nameof(templateName));

        if (string.IsNullOrWhiteSpace(toEmail))
            throw new ArgumentException("Recipient email is required.", nameof(toEmail));

        var domain = GetEmailDomain(toEmail) ?? "*";
        var languageCode = await GetDefaultLanguageCodeAsync(
            connection,
            transaction,
            cancellationToken);

        var template = await LoadTemplateAsync(
            connection,
            transaction,
            templateName,
            domain,
            languageCode,
            cancellationToken);

        if (template is null)
        {
            throw new InvalidOperationException(
                $"No active email template '{templateName}' matched domain '{domain}' and language '{languageCode}'.");
        }

        var rawTokens = NormalizeTokens(tokens);
        var htmlTokens = rawTokens.ToDictionary(
            pair => pair.Key,
            pair => WebUtility.HtmlEncode(pair.Value) ?? string.Empty,
            StringComparer.OrdinalIgnoreCase);

        if (htmlTokenOverrides is not null)
        {
            foreach (var pair in htmlTokenOverrides)
            {
                htmlTokens[pair.Key] = pair.Value ?? string.Empty;
            }
        }

        var subject = ExpandTokens(template.Subject, rawTokens);
        var bodyHtml = ExpandTokens(template.HtmlBody, htmlTokens);
        var bodyText = string.IsNullOrWhiteSpace(template.PlainTextBody)
            ? null
            : ExpandTokens(template.PlainTextBody, rawTokens);

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = @"
INSERT INTO dbo.ADUserChangeQueueEmails
(
    RequestId,
    EmailType,
    ToEmail,
    ToName,
    Subject,
    BodyHtml,
    BodyText,
    Status,
    EarliestSendAt,
    Attempts,
    Domain,
    TemplateName,
    LanguageCode
)
VALUES
(
    NULL,
    @EmailType,
    @ToEmail,
    @ToName,
    @Subject,
    @BodyHtml,
    @BodyText,
    N'Pending',
    SYSDATETIME(),
    0,
    @Domain,
    @TemplateName,
    @LanguageCode
);";
        command.Parameters.AddRequiredNVarChar("@EmailType", templateName, 100);
        command.Parameters.AddRequiredNVarChar("@ToEmail", toEmail.Trim(), 320);
        command.Parameters.AddNVarChar("@ToName", toName, 200);
        command.Parameters.AddRequiredNVarChar("@Subject", subject, 500);
        command.Parameters.AddNVarCharMax("@BodyHtml", bodyHtml);
        command.Parameters.AddNVarCharMax("@BodyText", bodyText);
        command.Parameters.AddRequiredNVarChar("@Domain", domain, 200);
        command.Parameters.AddRequiredNVarChar("@TemplateName", template.TemplateName, 100);
        command.Parameters.AddRequiredNVarChar("@LanguageCode", template.LanguageCode, 10);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<EmailTemplate?> LoadTemplateAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        string templateName,
        string domain,
        string languageCode,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = @"
SELECT TOP (1)
    TemplateName,
    LanguageCode,
    Subject,
    HtmlBody,
    PlainTextBody
FROM dbo.EmailTemplates
WHERE LOWER(LTRIM(RTRIM(TemplateName))) = LOWER(LTRIM(RTRIM(@TemplateName)))
  AND Active = 1
  AND LOWER(LTRIM(RTRIM(Domain))) IN (LOWER(@Domain), N'*')
  AND LOWER(LTRIM(RTRIM(LanguageCode))) IN (LOWER(@LanguageCode), N'en')
ORDER BY
    CASE WHEN LOWER(LTRIM(RTRIM(Domain))) = LOWER(@Domain) THEN 0 ELSE 1 END,
    CASE WHEN LOWER(LTRIM(RTRIM(LanguageCode))) = LOWER(@LanguageCode) THEN 0 ELSE 1 END,
    COALESCE(UpdatedAt, CreatedAt) DESC,
    Id DESC;";
        command.Parameters.AddRequiredNVarChar("@TemplateName", templateName.Trim(), 100);
        command.Parameters.AddRequiredNVarChar("@Domain", domain, 200);
        command.Parameters.AddRequiredNVarChar("@LanguageCode", languageCode, 10);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            return null;

        return new EmailTemplate(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetString(4));
    }

    private static async Task<string> GetDefaultLanguageCodeAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = @"
IF OBJECT_ID(N'dbo.UserChangeQueueSettings', N'U') IS NULL
BEGIN
    SELECT CAST(NULL AS nvarchar(10));
END
ELSE
BEGIN
    SELECT TOP (1) SettingValue
    FROM dbo.UserChangeQueueSettings
    WHERE SettingName = N'EmailTemplateLanguage'
      AND Active = 1;
END;";

        var value = await command.ExecuteScalarAsync(cancellationToken);
        return UiTextService.NormalizeLanguageCode(
            value is null or DBNull ? null : Convert.ToString(value));
    }

    private static Dictionary<string, string> NormalizeTokens(
        IReadOnlyDictionary<string, string?> tokens)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in tokens)
        {
            result[pair.Key] = pair.Value ?? string.Empty;
        }

        return result;
    }

    private static string ExpandTokens(
        string template,
        IReadOnlyDictionary<string, string> tokens)
    {
        var result = template;
        foreach (var pair in tokens)
        {
            result = result.Replace(
                "{" + pair.Key + "}",
                pair.Value,
                StringComparison.OrdinalIgnoreCase);
        }

        return result;
    }

    private static string? GetEmailDomain(string email)
    {
        var trimmed = email.Trim();
        var at = trimmed.LastIndexOf('@');
        if (at < 0 || at >= trimmed.Length - 1)
            return null;

        return trimmed[(at + 1)..].Trim().ToLowerInvariant();
    }

    private sealed record EmailTemplate(
        string TemplateName,
        string LanguageCode,
        string Subject,
        string HtmlBody,
        string? PlainTextBody);
}

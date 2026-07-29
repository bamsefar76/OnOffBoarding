using Microsoft.AspNetCore.Http;
using Microsoft.Data.SqlClient;

namespace UserChangeQueueWeb.Services;

public sealed class UiTextService
{
    public const string LanguageCookieName = "UserChangeQueueLanguage";

    private readonly SqlConnectionFactory _connectionFactory;

    public UiTextService(SqlConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    public async Task<UiTextSet> GetTextsAsync(
        HttpContext httpContext,
        IReadOnlyDictionary<string, string> fallbackTexts)
    {
        var requestedLanguageCode = GetRequestedLanguageCode(httpContext);
        var languageCode = await ResolveActiveLanguageCodeAsync(requestedLanguageCode);

        if (fallbackTexts.Count == 0)
        {
            return new UiTextSet(languageCode, new Dictionary<string, string>());
        }

        try
        {
            var texts = await LoadTextsAsync(languageCode, fallbackTexts.Keys.ToList());

            foreach (var fallback in fallbackTexts)
            {
                if (!texts.ContainsKey(fallback.Key) || string.IsNullOrWhiteSpace(texts[fallback.Key]))
                {
                    texts[fallback.Key] = fallback.Value;
                }
            }

            return new UiTextSet(languageCode, texts);
        }
        catch
        {
            return new UiTextSet(languageCode, new Dictionary<string, string>(fallbackTexts));
        }
    }

    public async Task<IReadOnlyList<UiLanguageOption>> GetActiveLanguagesAsync()
    {
        try
        {
            await using var cn = await _connectionFactory.OpenAsync();

            await using var cmd = cn.CreateCommand();
            cmd.CommandText = @"
SELECT LanguageCode,
       DisplayName,
       NativeName
FROM dbo.UiLanguages
WHERE Active = 1
ORDER BY SortOrder, LanguageCode;
";

            var result = new List<UiLanguageOption>();

            await using var reader = await cmd.ExecuteReaderAsync();

            while (await reader.ReadAsync())
            {
                result.Add(new UiLanguageOption(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetString(2)));
            }

            if (result.Count > 0)
            {
                return result;
            }
        }
        catch
        {
            // Fall through to safe built-in language list.
        }

        return new List<UiLanguageOption>
        {
            new("en", "English", "English"),
            new("nb", "Norwegian Bokmål", "Norsk"),
            new("fi", "Finnish", "Suomi"),
            new("sv", "Swedish", "Svenska"),
            new("da", "Danish", "Dansk")
        };
    }

    public async Task<string> ResolveActiveLanguageCodeAsync(string? requestedLanguageCode)
    {
        var normalizedLanguageCode = NormalizeLanguageCode(requestedLanguageCode);

        try
        {
            await using var cn = await _connectionFactory.OpenAsync();

            await using var cmd = cn.CreateCommand();
            cmd.Parameters.AddNVarChar("@LanguageCode", normalizedLanguageCode, 10);
            cmd.CommandText = @"
IF EXISTS
(
    SELECT 1
    FROM dbo.UiLanguages
    WHERE LanguageCode = @LanguageCode
      AND Active = 1
)
BEGIN
    SELECT @LanguageCode;
END
ELSE IF EXISTS
(
    SELECT 1
    FROM dbo.UiLanguages
    WHERE LanguageCode = N'en'
      AND Active = 1
)
BEGIN
    SELECT N'en';
END
ELSE
BEGIN
    SELECT TOP (1) LanguageCode
    FROM dbo.UiLanguages
    WHERE Active = 1
    ORDER BY SortOrder, LanguageCode;
END;
";

            var result = await cmd.ExecuteScalarAsync();
            return Convert.ToString(result) ?? "en";
        }
        catch
        {
            return "en";
        }
    }

    public static string NormalizeLanguageCode(string? languageCode)
    {
        if (string.IsNullOrWhiteSpace(languageCode))
        {
            return "en";
        }

        var normalized = languageCode.Trim().ToLowerInvariant().Replace('_', '-');

        if (normalized.StartsWith("nb")
            || normalized.StartsWith("nn")
            || normalized.StartsWith("no"))
        {
            return "nb";
        }

        if (normalized.StartsWith("fi"))
        {
            return "fi";
        }

        if (normalized.StartsWith("sv")
            || normalized.StartsWith("se"))
        {
            return "sv";
        }

        if (normalized.StartsWith("da")
            || normalized.StartsWith("dk"))
        {
            return "da";
        }

        if (normalized.StartsWith("fr"))
        {
            return "fr";
        }

        if (normalized.StartsWith("nl"))
        {
            return "nl";
        }

        return "en";
    }

    private string GetRequestedLanguageCode(HttpContext httpContext)
    {
        if (httpContext.Request.Cookies.TryGetValue(LanguageCookieName, out var cookieLanguage)
            && !string.IsNullOrWhiteSpace(cookieLanguage))
        {
            return NormalizeLanguageCode(cookieLanguage);
        }

        var acceptedLanguages = httpContext.Request.Headers.AcceptLanguage.ToString();

        if (!string.IsNullOrWhiteSpace(acceptedLanguages))
        {
            var firstLanguage = acceptedLanguages
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .FirstOrDefault();

            if (!string.IsNullOrWhiteSpace(firstLanguage))
            {
                var languageWithoutQuality = firstLanguage
                    .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .FirstOrDefault();

                return NormalizeLanguageCode(languageWithoutQuality);
            }
        }

        return "en";
    }

    private async Task<Dictionary<string, string>> LoadTextsAsync(
        string languageCode,
        IReadOnlyList<string> textKeys)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (textKeys.Count == 0)
        {
            return result;
        }

        await using var cn = await _connectionFactory.OpenAsync();

        await using var cmd = cn.CreateCommand();
        cmd.Parameters.AddNVarChar("@LanguageCode", languageCode, 10);

        var keyParameterNames = new List<string>();

        for (var i = 0; i < textKeys.Count; i++)
        {
            var parameterName = "@Key" + i;
            keyParameterNames.Add(parameterName);
            cmd.Parameters.AddNVarChar(parameterName, textKeys[i], 200);
        }

        cmd.CommandText = $@"
SELECT requested.UiTextKey,
       COALESCE(currentLanguage.TextValue, english.TextValue) AS TextValue
FROM
(
    SELECT UiTextKey
    FROM dbo.UiTexts
    WHERE UiTextKey IN ({string.Join(", ", keyParameterNames)})
    GROUP BY UiTextKey
) AS requested
LEFT JOIN dbo.UiTexts AS currentLanguage
    ON currentLanguage.UiTextKey = requested.UiTextKey
   AND currentLanguage.LanguageCode = @LanguageCode
   AND currentLanguage.Active = 1
LEFT JOIN dbo.UiTexts AS english
    ON english.UiTextKey = requested.UiTextKey
   AND english.LanguageCode = N'en'
   AND english.Active = 1;
";

        await using var reader = await cmd.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {
            var key = reader.GetString(0);
            var value = reader.IsDBNull(1) ? "" : reader.GetString(1);
            result[key] = value;
        }

        return result;
    }
}

public sealed class UiLanguageOption
{
    public UiLanguageOption(string languageCode, string displayName, string nativeName)
    {
        LanguageCode = languageCode;
        DisplayName = displayName;
        NativeName = nativeName;
    }

    public string LanguageCode { get; }

    public string DisplayName { get; }

    public string NativeName { get; }
}

public sealed class UiTextSet
{
    public UiTextSet(string languageCode, IReadOnlyDictionary<string, string> texts)
    {
        LanguageCode = languageCode;
        Texts = texts;
    }

    public string LanguageCode { get; }

    public IReadOnlyDictionary<string, string> Texts { get; }

    public string T(string key, string fallback)
    {
        return Texts.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : fallback;
    }
}

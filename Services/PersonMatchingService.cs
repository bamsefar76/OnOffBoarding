using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;

namespace UserChangeQueueWeb.Services;

public sealed class PersonMatchingService
{
    private readonly SqlConnectionFactory _connectionFactory;

    public PersonMatchingService(SqlConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    public static string NormalizeEmail(string? value) =>
        string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim().ToLowerInvariant();

    public static string NormalizePhone(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var digits = new string(value.Where(char.IsDigit).ToArray());
        if (digits.StartsWith("00", StringComparison.Ordinal)) digits = digits[2..];
        return digits;
    }

    public static string NormalizeName(string? givenName, string? surname)
    {
        var combined = $"{givenName} {surname}".Trim().ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(combined.Length);
        foreach (var ch in combined)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark) continue;
            sb.Append(char.IsLetterOrDigit(ch) ? ch : ' ');
        }
        return Regex.Replace(sb.ToString(), @"\s+", " ").Trim();
    }

    public async Task<IReadOnlyList<PersonMatchCandidate>> FindCandidatesAsync(
        string? givenName,
        string? surname,
        string? privateEmail,
        string? mobilePhone,
        CancellationToken cancellationToken)
    {
        var normalizedEmail = NormalizeEmail(privateEmail);
        var normalizedPhone = NormalizePhone(mobilePhone);
        var normalizedName = NormalizeName(givenName, surname);
        var candidates = new Dictionary<string, PersonMatchCandidate>(StringComparer.OrdinalIgnoreCase);

        await using var cn = await _connectionFactory.OpenAsync(cancellationToken);
        await using var cmd = cn.CreateCommand();
        cmd.CommandText = @"
SELECT TOP (30)
    N'Employee' AS SourceType,
    p.EmployeeId,
    CAST(NULL AS bigint) AS ArchiveRequestId,
    p.CanonicalGivenName,
    p.CanonicalSurname,
    p.PrivateEmail,
    p.MobilePhone,
    p.CurrentSamAccountName,
    p.CurrentUPN,
    p.Status,
    MAX(CASE WHEN p.NormalizedPrivateEmail = @Email AND @Email <> N'' THEN 1 ELSE 0 END) AS EmailMatch,
    MAX(CASE WHEN p.NormalizedMobilePhone = @Phone AND @Phone <> N'' THEN 1 ELSE 0 END) AS PhoneMatch,
    MAX(CASE WHEN @Name <> N'' AND
        (
            pn.NormalizedName = @Name
            OR LOWER(LTRIM(RTRIM(CONCAT(ISNULL(p.CanonicalGivenName,N''),N' ',ISNULL(p.CanonicalSurname,N''))))) = @SimpleName
        ) THEN 1 ELSE 0 END) AS ExactNameMatch
FROM dbo.Employees p
LEFT JOIN dbo.EmployeeNames pn ON pn.EmployeeId = p.EmployeeId
WHERE
       (@Email <> N'' AND p.NormalizedPrivateEmail = @Email)
    OR (@Phone <> N'' AND p.NormalizedMobilePhone = @Phone)
    OR (@Name <> N'' AND
        (
            pn.NormalizedName = @Name
            OR pn.NormalizedName LIKE N'%' + @Name + N'%'
            OR @Name LIKE N'%' + pn.NormalizedName + N'%'
            OR LOWER(LTRIM(RTRIM(CONCAT(ISNULL(p.CanonicalGivenName,N''),N' ',ISNULL(p.CanonicalSurname,N''))))) LIKE N'%' + @SimpleName + N'%'
        ))
GROUP BY p.EmployeeId, p.CanonicalGivenName, p.CanonicalSurname, p.PrivateEmail, p.MobilePhone,
         p.CurrentSamAccountName, p.CurrentUPN, p.Status

UNION ALL

SELECT TOP (30)
    N'Archive' AS SourceType,
    CAST(NULL AS bigint) AS PersonId,
    q.RequestId AS ArchiveRequestId,
    q.NewGivenName,
    q.NewSurname,
    q.PrivateEmail,
    q.MobilePhone,
    COALESCE(q.TargetSamAccountName, q.NewSamAccountName),
    q.NewUserPrincipalName,
    q.Status,
    CASE WHEN LOWER(LTRIM(RTRIM(ISNULL(q.PrivateEmail, N'')))) = @Email AND @Email <> N'' THEN 1 ELSE 0 END,
    CASE WHEN REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(ISNULL(q.MobilePhone, N''), N' ', N''), N'+', N''), N'-', N''), N'(', N''), N')', N'') = @Phone AND @Phone <> N'' THEN 1 ELSE 0 END,
    CASE WHEN LOWER(LTRIM(RTRIM(CONCAT(ISNULL(q.NewGivenName,N''),N' ',ISNULL(q.NewSurname,N''))))) = @SimpleName AND @SimpleName <> N'' THEN 1 ELSE 0 END
FROM dbo.ADUserChangeQueue q
WHERE q.RequestType IN (N'CREATE', N'UPDATE')
  AND
  (
       (@Email <> N'' AND LOWER(LTRIM(RTRIM(ISNULL(q.PrivateEmail, N'')))) = @Email)
    OR (@Phone <> N'' AND REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(ISNULL(q.MobilePhone, N''), N' ', N''), N'+', N''), N'-', N''), N'(', N''), N')', N'') = @Phone)
    OR (@SimpleName <> N'' AND LOWER(LTRIM(RTRIM(CONCAT(ISNULL(q.NewGivenName,N''),N' ',ISNULL(q.NewSurname,N''))))) LIKE N'%' + @SimpleName + N'%')
  )
ORDER BY SourceType, 2 DESC, 3 DESC;";
        cmd.Parameters.AddNVarChar("@Email", normalizedEmail, 320);
        cmd.Parameters.AddNVarChar("@Phone", normalizedPhone, 50);
        cmd.Parameters.AddNVarChar("@Name", normalizedName, 500);
        cmd.Parameters.AddNVarChar("@SimpleName", $"{givenName} {surname}".Trim().ToLowerInvariant(), 500);

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
while (await reader.ReadAsync(cancellationToken))
{
    var sourceType = reader.GetString(0);

    long? personId = reader.IsDBNull(1)
        ? null
        : reader.GetInt64(1);

    long? archiveRequestId = reader.IsDBNull(2)
        ? null
        : reader.GetInt64(2);

    var key = personId.HasValue
        ? $"P:{personId.Value}"
        : $"A:{archiveRequestId?.ToString() ?? "unknown"}";

    var candidate = new PersonMatchCandidate
    {
        SourceType = sourceType,
        PersonId = personId,
        ArchiveRequestId = archiveRequestId,
        GivenName = reader.IsDBNull(3) ? "" : reader.GetString(3),
        Surname = reader.IsDBNull(4) ? "" : reader.GetString(4),
        MaskedPrivateEmail = reader.IsDBNull(5) ? "" : reader.GetString(5),
        MaskedMobilePhone = reader.IsDBNull(6) ? "" : reader.GetString(6),
        SamAccountName = reader.IsDBNull(7) ? null : reader.GetString(7),
        UserPrincipalName = reader.IsDBNull(8) ? null : reader.GetString(8),
        Status = reader.IsDBNull(9) ? null : reader.GetString(9),
        EmailMatch = reader.GetInt32(10) == 1,
        PhoneMatch = reader.GetInt32(11) == 1,
        ExactNameMatch = reader.GetInt32(12) == 1
    };

    candidate.Score =
        (candidate.EmailMatch ? 100 : 0)
        + (candidate.PhoneMatch ? 100 : 0)
        + (candidate.ExactNameMatch ? 35 : 0);

    if (candidate.Score >= 35 && !candidates.ContainsKey(key))
    {
        candidates[key] = candidate;
    }
}

        return candidates.Values.OrderByDescending(x => x.Score).ThenBy(x => x.DisplayName).Take(12).ToList();
    }

    private static string MaskEmail(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || !value.Contains('@')) return "";
        var parts = value.Split('@', 2);
        return parts[0].Length <= 1 ? $"*@{parts[1]}" : $"{parts[0][0]}***@{parts[1]}";
    }

    private static string MaskPhone(string? value)
    {
        var digits = NormalizePhone(value);
        return digits.Length <= 4 ? digits : new string('*', Math.Max(0, digits.Length - 4)) + digits[^4..];
    }

    public sealed class PersonMatchCandidate
    {
        public string SourceType { get; init; } = "";
        public long? PersonId { get; init; }
        public long? ArchiveRequestId { get; init; }
        public string GivenName { get; init; } = "";
        public string Surname { get; init; } = "";
        public string DisplayName => $"{GivenName} {Surname}".Trim();
        public string MaskedPrivateEmail { get; init; } = "";
        public string MaskedMobilePhone { get; init; } = "";
        public string? SamAccountName { get; init; }
        public string? UserPrincipalName { get; init; }
        public string? Status { get; init; }
        public bool EmailMatch { get; init; }
        public bool PhoneMatch { get; init; }
        public bool ExactNameMatch { get; init; }
        public int Score { get; set; }
    }
}

using Microsoft.Data.SqlClient;

namespace UserChangeQueueWeb.Services;

public sealed class LicenseCapacityService
{
    public async Task<IReadOnlyList<CapacityViolation>> CheckCapacityAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        IReadOnlyCollection<int> licenseProductIds,
        DateTime startDate,
        DateTime? endDate,
        CancellationToken cancellationToken = default)
    {
        var ids = licenseProductIds
            .Where(id => id > 0)
            .Distinct()
            .ToArray();

        if (ids.Length == 0)
            return Array.Empty<CapacityViolation>();

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;

        var parameterNames = new List<string>(ids.Length);
        for (var i = 0; i < ids.Length; i++)
        {
            var parameterName = "@ProductId" + i;
            parameterNames.Add(parameterName);
            command.Parameters.AddInt(parameterName, ids[i]);
        }

        command.Parameters.AddDate("@RequestedStart", startDate);
        command.Parameters.AddNullableDate("@RequestedEnd", endDate);

        command.CommandText = $@"
SELECT
    product.LicenseProductId,
    product.Name,
    product.LicenseCount,
    reservation.ReservedCount
FROM dbo.LicenseProducts AS product WITH (UPDLOCK, HOLDLOCK)
CROSS APPLY
(
    SELECT
        CAST
        (
            (
                SELECT COUNT_BIG(*)
                FROM dbo.LicenseApplicationItems AS item
                INNER JOIN dbo.LicenseApplications AS application
                    ON application.LicenseApplicationId = item.LicenseApplicationId
                WHERE item.LicenseProductId = product.LicenseProductId
                  AND item.Status <> N'Rejected'
                  AND application.Status NOT IN (N'ManagerRejected', N'ITRejected')
                  AND item.StartDate <= COALESCE(@RequestedEnd, CONVERT(date, '99991231', 112))
                  AND
                  (
                      item.IsPermanent = 1
                      OR item.EndDate IS NULL
                      OR item.EndDate >= @RequestedStart
                  )
            )
            +
            (
                SELECT COUNT_BIG(*)
                FROM dbo.AssignmentLicenseSelections AS selection
                INNER JOIN dbo.ADUserChangeQueue AS queueItem
                    ON queueItem.RequestId = selection.RequestId
                WHERE selection.LicenseProductId = product.LicenseProductId
                  AND selection.LicenseApplicationId IS NULL
                  AND UPPER(LTRIM(RTRIM(ISNULL(queueItem.Status, N'')))) NOT IN (N'REJECTED', N'DENIED', N'CANCELLED')
                  AND selection.StartDate <= COALESCE(@RequestedEnd, CONVERT(date, '99991231', 112))
                  AND
                  (
                      selection.IsPermanent = 1
                      OR selection.EndDate IS NULL
                      OR selection.EndDate >= @RequestedStart
                  )
            )
            AS int
        ) AS ReservedCount
) AS reservation
WHERE product.LicenseProductId IN ({string.Join(",", parameterNames)})
ORDER BY product.Name;";

        var violations = new List<CapacityViolation>();

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var licenseCount = reader.IsDBNull(2) ? (int?)null : reader.GetInt32(2);
            if (!licenseCount.HasValue || licenseCount.Value <= 0)
                continue;

            var reservedCount = reader.GetInt32(3);
            if (reservedCount + 1 <= licenseCount.Value)
                continue;

            violations.Add(new CapacityViolation(
                reader.GetInt32(0),
                reader.IsDBNull(1) ? "" : reader.GetString(1),
                licenseCount.Value,
                reservedCount));
        }

        return violations;
    }

    public sealed record CapacityViolation(
        int LicenseProductId,
        string LicenseName,
        int LicenseCount,
        int ReservedCount);
}

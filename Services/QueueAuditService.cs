using Microsoft.Data.SqlClient;

namespace UserChangeQueueWeb.Services;

public sealed class QueueAuditService
{
    public async Task<string?> ReadQueueRowJsonAsync(
        SqlConnection connection,
        long requestId,
        SqlTransaction? transaction = null)
    {
        await using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = @"
SELECT
(
    SELECT q.*
    FROM dbo.ADUserChangeQueue AS q
    WHERE q.RequestId = @RequestId
    FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES
);
";
        cmd.Parameters.AddBigInt("@RequestId", requestId);

        var result = await cmd.ExecuteScalarAsync();

        return result == null || result == DBNull.Value
            ? null
            : Convert.ToString(result);
    }

    public async Task WriteHistoryAsync(
        SqlConnection connection,
        long requestId,
        string changeType,
        string changedBy,
        string? oldJson,
        string? newJson,
        SqlTransaction? transaction = null)
    {
        if (!await HistoryTableExistsAsync(connection, transaction))
        {
            return;
        }

        await using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = @"
INSERT INTO dbo.ADUserChangeQueueHistory
(
    RequestId,
    ChangeType,
    ChangedBy,
    OldJson,
    NewJson
)
VALUES
(
    @RequestId,
    @ChangeType,
    @ChangedBy,
    @OldJson,
    @NewJson
);
";
        cmd.Parameters.AddBigInt("@RequestId", requestId);
        cmd.Parameters.AddRequiredNVarChar("@ChangeType", changeType, 50);
        cmd.Parameters.AddRequiredNVarChar("@ChangedBy", changedBy, 300);
        cmd.Parameters.AddNVarCharMax("@OldJson", oldJson);
        cmd.Parameters.AddNVarCharMax("@NewJson", newJson);

        await cmd.ExecuteNonQueryAsync();
    }

    public async Task MarkRequestUpdatedAsync(
        SqlConnection connection,
        long requestId,
        string updatedBy,
        SqlTransaction? transaction = null)
    {
        await using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = @"
IF COL_LENGTH(N'dbo.ADUserChangeQueue', N'UpdatedBy') IS NOT NULL
   AND COL_LENGTH(N'dbo.ADUserChangeQueue', N'UpdatedAt') IS NOT NULL
BEGIN
    EXEC sys.sp_executesql
        N'UPDATE dbo.ADUserChangeQueue SET UpdatedBy = @UpdatedBy, UpdatedAt = SYSUTCDATETIME() WHERE RequestId = @RequestId;',
        N'@UpdatedBy nvarchar(300), @RequestId bigint',
        @UpdatedBy = @UpdatedBy,
        @RequestId = @RequestId;
END;
";
        cmd.Parameters.AddRequiredNVarChar("@UpdatedBy", updatedBy, 300);
        cmd.Parameters.AddBigInt("@RequestId", requestId);

        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<bool> HistoryTableExistsAsync(
        SqlConnection connection,
        SqlTransaction? transaction)
    {
        await using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = "SELECT CASE WHEN OBJECT_ID(N'dbo.ADUserChangeQueueHistory', N'U') IS NULL THEN 0 ELSE 1 END;";

        var result = await cmd.ExecuteScalarAsync();
        return Convert.ToInt32(result) == 1;
    }
}

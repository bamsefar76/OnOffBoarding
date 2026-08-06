using Microsoft.Data.SqlClient;

namespace UserChangeQueueWeb.Services;

public sealed class LicenseEmailService
{
    public async Task QueueAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        string emailType,
        string toEmail,
        string? toName,
        string subject,
        string bodyHtml,
        CancellationToken cancellationToken)
    {
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
    Status,
    EarliestSendAt,
    Attempts,
    Domain,
    TemplateName
)
VALUES
(
    NULL,
    @EmailType,
    @ToEmail,
    @ToName,
    @Subject,
    @BodyHtml,
    N'Pending',
    SYSDATETIME(),
    0,
    N'*',
    @EmailType
);";
        command.Parameters.AddRequiredNVarChar("@EmailType", emailType, 100);
        command.Parameters.AddRequiredNVarChar("@ToEmail", toEmail, 320);
        command.Parameters.AddNVarChar("@ToName", toName, 200);
        command.Parameters.AddRequiredNVarChar("@Subject", subject, 500);
        command.Parameters.AddNVarCharMax("@BodyHtml", bodyHtml);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}

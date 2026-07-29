using Microsoft.Data.SqlClient;

namespace UserChangeQueueWeb.Services;

public sealed class SqlConnectionFactory
{
    public const string ConnectionStringName = "UserDatabase";

    private readonly IConfiguration _configuration;

    public SqlConnectionFactory(IConfiguration configuration)
    {
        _configuration = configuration;
    }

    public SqlConnection Create()
    {
        var connectionString = _configuration.GetConnectionString(ConnectionStringName);

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                $"Connection string '{ConnectionStringName}' is missing or empty. " +
                $"Add ConnectionStrings:{ConnectionStringName} to appsettings.json or appsettings.Production.json.");
        }

        return new SqlConnection(connectionString);
    }

    public async Task<SqlConnection> OpenAsync(CancellationToken cancellationToken = default)
    {
        var connection = Create();
        try
        {
            await connection.OpenAsync(cancellationToken);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }
}

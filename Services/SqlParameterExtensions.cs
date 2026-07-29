using Microsoft.Data.SqlClient;
using System.Data;

namespace UserChangeQueueWeb.Services;

public static class SqlParameterExtensions
{
    public static SqlParameter AddBigInt(this SqlParameterCollection parameters, string name, long value)
    {
        var parameter = parameters.Add(name, SqlDbType.BigInt);
        parameter.Value = value;
        return parameter;
    }

    public static SqlParameter AddNullableBigInt(this SqlParameterCollection parameters, string name, long? value)
    {
        var parameter = parameters.Add(name, SqlDbType.BigInt);
        parameter.Value = value.HasValue ? value.Value : DBNull.Value;
        return parameter;
    }

    public static SqlParameter AddInt(this SqlParameterCollection parameters, string name, int value)
    {
        var parameter = parameters.Add(name, SqlDbType.Int);
        parameter.Value = value;
        return parameter;
    }

    public static SqlParameter AddNullableInt(this SqlParameterCollection parameters, string name, int? value)
    {
        var parameter = parameters.Add(name, SqlDbType.Int);
        parameter.Value = value.HasValue ? value.Value : DBNull.Value;
        return parameter;
    }

    public static SqlParameter AddUniqueIdentifier(this SqlParameterCollection parameters, string name, Guid value)
    {
        var parameter = parameters.Add(name, SqlDbType.UniqueIdentifier);
        parameter.Value = value;
        return parameter;
    }

    public static SqlParameter AddNullableUniqueIdentifier(this SqlParameterCollection parameters, string name, Guid? value)
    {
        var parameter = parameters.Add(name, SqlDbType.UniqueIdentifier);
        parameter.Value = value.HasValue ? value.Value : DBNull.Value;
        return parameter;
    }

    public static SqlParameter AddDate(this SqlParameterCollection parameters, string name, DateTime value)
    {
        var parameter = parameters.Add(name, SqlDbType.Date);
        parameter.Value = value.Date;
        return parameter;
    }

    public static SqlParameter AddNullableDate(this SqlParameterCollection parameters, string name, DateTime? value)
    {
        var parameter = parameters.Add(name, SqlDbType.Date);
        parameter.Value = value.HasValue ? value.Value.Date : DBNull.Value;
        return parameter;
    }

    public static SqlParameter AddBit(this SqlParameterCollection parameters, string name, bool value)
    {
        var parameter = parameters.Add(name, SqlDbType.Bit);
        parameter.Value = value;
        return parameter;
    }

    public static SqlParameter AddNVarChar(this SqlParameterCollection parameters, string name, string? value, int size = 400)
    {
        var parameter = parameters.Add(name, SqlDbType.NVarChar, size);
        parameter.Value = string.IsNullOrWhiteSpace(value) ? DBNull.Value : value.Trim();
        return parameter;
    }

    public static SqlParameter AddRequiredNVarChar(this SqlParameterCollection parameters, string name, string value, int size = 400)
    {
        var parameter = parameters.Add(name, SqlDbType.NVarChar, size);
        parameter.Value = value;
        return parameter;
    }

    public static SqlParameter AddNVarCharMax(this SqlParameterCollection parameters, string name, string? value)
    {
        var parameter = parameters.Add(name, SqlDbType.NVarChar, -1);
        parameter.Value = string.IsNullOrWhiteSpace(value) ? DBNull.Value : value;
        return parameter;
    }
}

using System.Collections;
using System.Data;
using System.Data.Common;
using System.Data.OleDb;
using MdbToSql.Core.Data;
using MdbToSql.Core.Mapping;
using MdbToSql.Core.Models;
using MdbToSql.Core.Utilities;

namespace MdbToSql.Access;

public sealed class AccessTableDataReader
{
    private readonly AccessToSqlTypeMapper _typeMapper = new();

    public DbDataReader Open(OleDbConnection connection, AccessTableSchema schema)
    {
        var columns = string.Join(
            ", ",
            schema.Columns
                .OrderBy(column => column.Ordinal)
                .Select(column => AccessIdentifier.Quote(column.Name)));
        var command = connection.CreateCommand();
        command.CommandText = $"SELECT {columns} FROM {AccessIdentifier.Quote(schema.Name)}";
        try
        {
            var reader = command.ExecuteReader();
            return new AccessTypedDataReader(command, reader, schema, _typeMapper);
        }
        catch
        {
            command.Dispose();
            throw;
        }
    }
}

internal sealed class AccessTypedDataReader : DbDataReader
{
    private readonly OleDbCommand _command;
    private readonly DbDataReader _inner;
    private readonly IReadOnlyDictionary<int, AccessColumnSchema> _columnsByOrdinal;
    private readonly AccessToSqlTypeMapper _typeMapper;
    private bool _disposed;

    public AccessTypedDataReader(
        OleDbCommand command,
        DbDataReader inner,
        AccessTableSchema schema,
        AccessToSqlTypeMapper typeMapper)
    {
        _command = command;
        _inner = inner;
        _typeMapper = typeMapper;
        _columnsByOrdinal = schema.Columns
            .OrderBy(column => column.Ordinal)
            .Select((column, ordinal) => (ordinal, column))
            .ToDictionary(item => item.ordinal, item => item.column);
    }

    public override int FieldCount => _inner.FieldCount;

    public override bool HasRows => _inner.HasRows;

    public override bool IsClosed => _inner.IsClosed;

    public override int RecordsAffected => _inner.RecordsAffected;

    public override int Depth => _inner.Depth;

    public override object this[int ordinal] => GetValue(ordinal);

    public override object this[string name] => GetValue(GetOrdinal(name));

    public override bool Read() => _inner.Read();

    public override string GetName(int ordinal) => _inner.GetName(ordinal);

    public override int GetOrdinal(string name) => _inner.GetOrdinal(name);

    public override bool IsDBNull(int ordinal) => _inner.IsDBNull(ordinal);

    public override object GetValue(int ordinal)
    {
        if (_inner.IsDBNull(ordinal))
        {
            return DBNull.Value;
        }

        var raw = _inner.GetValue(ordinal);
        if (_columnsByOrdinal.TryGetValue(ordinal, out var column) &&
            column.TypeKind == AccessTypeKind.Boolean)
        {
            return BooleanValueNormalizer.Normalize(raw);
        }

        return raw is null ? DBNull.Value : raw;
    }

    public override Type GetFieldType(int ordinal)
    {
        if (_columnsByOrdinal.TryGetValue(ordinal, out var column))
        {
            return _typeMapper.GetClrType(column);
        }

        return _inner.GetFieldType(ordinal);
    }

    public override string GetDataTypeName(int ordinal) => _inner.GetDataTypeName(ordinal);

    public override int GetValues(object[] values)
    {
        var count = Math.Min(values.Length, FieldCount);
        for (var index = 0; index < count; index++)
        {
            values[index] = GetValue(index);
        }

        return count;
    }

    public override bool GetBoolean(int ordinal)
    {
        var value = GetValue(ordinal);
        if (value is bool flag)
        {
            return flag;
        }

        return (bool)BooleanValueNormalizer.Normalize(value);
    }

    public override byte GetByte(int ordinal) => _inner.GetByte(ordinal);

    public override char GetChar(int ordinal) => _inner.GetChar(ordinal);

    public override DateTime GetDateTime(int ordinal) => _inner.GetDateTime(ordinal);

    public override decimal GetDecimal(int ordinal) => _inner.GetDecimal(ordinal);

    public override double GetDouble(int ordinal) => _inner.GetDouble(ordinal);

    public override float GetFloat(int ordinal) => _inner.GetFloat(ordinal);

    public override Guid GetGuid(int ordinal) => _inner.GetGuid(ordinal);

    public override short GetInt16(int ordinal) => _inner.GetInt16(ordinal);

    public override int GetInt32(int ordinal) => _inner.GetInt32(ordinal);

    public override long GetInt64(int ordinal) => _inner.GetInt64(ordinal);

    public override string GetString(int ordinal) => _inner.GetString(ordinal);

    public override long GetBytes(
        int ordinal,
        long dataOffset,
        byte[]? buffer,
        int bufferOffset,
        int length)
    {
        return _inner.GetBytes(ordinal, dataOffset, buffer, bufferOffset, length);
    }

    public override long GetChars(
        int ordinal,
        long dataOffset,
        char[]? buffer,
        int bufferOffset,
        int length)
    {
        return _inner.GetChars(ordinal, dataOffset, buffer, bufferOffset, length);
    }

    public override bool NextResult() => false;

    public override IEnumerator GetEnumerator() => new DbEnumerator(this);

    public override DataTable? GetSchemaTable() => _inner.GetSchemaTable();

    public override void Close()
    {
        if (!_inner.IsClosed)
        {
            _inner.Close();
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (_disposed)
        {
            return;
        }

        if (disposing)
        {
            _inner.Dispose();
            _command.Dispose();
        }

        _disposed = true;
        base.Dispose(disposing);
    }
}

using System.Collections;
using System.Data;
using System.Data.Common;

namespace MdbToSql.Core.Data;

public sealed class BoundedDbDataReader : DbDataReader
{
    private readonly DbDataReader _inner;
    private readonly int _maxRows;
    private int _read;

    public BoundedDbDataReader(DbDataReader inner, int maxRows)
    {
        ArgumentNullException.ThrowIfNull(inner);
        if (maxRows <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxRows));
        }

        _inner = inner;
        _maxRows = maxRows;
    }

    public bool SourceExhausted { get; private set; }

    public int RowsReturned { get; private set; }

    public override int FieldCount => _inner.FieldCount;

    public override bool HasRows => _inner.HasRows;

    public override bool IsClosed => _inner.IsClosed;

    public override int RecordsAffected => _inner.RecordsAffected;

    public override int Depth => _inner.Depth;

    public override object this[int ordinal] => GetValue(ordinal);

    public override object this[string name] => GetValue(GetOrdinal(name));

    public override bool Read()
    {
        if (_read >= _maxRows)
        {
            return false;
        }

        if (!_inner.Read())
        {
            SourceExhausted = true;
            return false;
        }

        _read++;
        RowsReturned++;
        return true;
    }

    public override string GetName(int ordinal) => _inner.GetName(ordinal);

    public override int GetOrdinal(string name) => _inner.GetOrdinal(name);

    public override object GetValue(int ordinal) => _inner.GetValue(ordinal);

    public override bool IsDBNull(int ordinal) => _inner.IsDBNull(ordinal);

    public override Type GetFieldType(int ordinal) => _inner.GetFieldType(ordinal);

    public override string GetDataTypeName(int ordinal) => _inner.GetDataTypeName(ordinal);

    public override int GetValues(object[] values) => _inner.GetValues(values);

    public override bool GetBoolean(int ordinal) => _inner.GetBoolean(ordinal);

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
    }

    protected override void Dispose(bool disposing)
    {
    }
}

public static class BooleanValueNormalizer
{
    public static object Normalize(object? value)
    {
        if (value is null or DBNull)
        {
            return DBNull.Value;
        }

        return value switch
        {
            bool flag => flag,
            byte number => number != 0,
            sbyte number => number != 0,
            short number => number != 0,
            ushort number => number != 0,
            int number => number != 0,
            uint number => number != 0,
            long number => number != 0,
            ulong number => number != 0,
            string text when text.Equals("true", StringComparison.OrdinalIgnoreCase) => true,
            string text when text.Equals("false", StringComparison.OrdinalIgnoreCase) => false,
            string text when text == "-1" || text == "1" => true,
            string text when text == "0" => false,
            _ => throw new FormatException(
                $"No se puede convertir '{value}' ({value.GetType().Name}) a bit.")
        };
    }
}

using System.Collections;
using System.Data.Common;
using MdbToSql.Core.Import;
using MdbToSql.Core.Mapping;
using MdbToSql.Core.Models;

namespace MdbToSql.Tests;

internal sealed class SequenceReader : DbDataReader
{
    private readonly int _total;
    private int _current = -1;
    private bool _closed;

    public SequenceReader(int total)
    {
        _total = total;
    }

    public override int FieldCount => 1;

    public override bool HasRows => true;

    public override bool IsClosed => _closed;

    public override int RecordsAffected => -1;

    public override int Depth => 0;

    public override object this[int ordinal] => _current;

    public override object this[string name] => _current;

    public override bool Read()
    {
        if (_current + 1 >= _total)
        {
            return false;
        }

        _current++;
        return true;
    }

    public override string GetName(int ordinal) => "id";

    public override int GetOrdinal(string name) => 0;

    public override object GetValue(int ordinal) => _current;

    public override bool IsDBNull(int ordinal) => false;

    public override Type GetFieldType(int ordinal) => typeof(int);

    public override string GetDataTypeName(int ordinal) => "int";

    public override int GetValues(object[] values)
    {
        values[0] = _current;
        return 1;
    }

    public override bool GetBoolean(int ordinal) => false;

    public override byte GetByte(int ordinal) => (byte)_current;

    public override char GetChar(int ordinal) => (char)_current;

    public override DateTime GetDateTime(int ordinal) => DateTime.MinValue;

    public override decimal GetDecimal(int ordinal) => _current;

    public override double GetDouble(int ordinal) => _current;

    public override float GetFloat(int ordinal) => _current;

    public override Guid GetGuid(int ordinal) => Guid.Empty;

    public override short GetInt16(int ordinal) => (short)_current;

    public override int GetInt32(int ordinal) => _current;

    public override long GetInt64(int ordinal) => _current;

    public override string GetString(int ordinal) => _current.ToString();

    public override long GetBytes(int ordinal, long dataOffset, byte[]? buffer, int bufferOffset, int length)
        => 0;

    public override long GetChars(int ordinal, long dataOffset, char[]? buffer, int bufferOffset, int length)
        => 0;

    public override bool NextResult() => false;

    public override IEnumerator GetEnumerator() => throw new NotSupportedException();

    public override void Close() => _closed = true;
}

internal sealed class FakeMdbScanner : IMdbScanner
{
    private readonly IReadOnlyList<MdbFileInfo> _files;

    public FakeMdbScanner(IReadOnlyList<MdbFileInfo> files)
    {
        _files = files;
    }

    public IReadOnlyList<MdbFileInfo> Discover(string sourceDirectory)
    {
        return _files;
    }
}

internal sealed class FakeAccessDatabaseFactory : IAccessDatabaseFactory
{
    private readonly Dictionary<string, FakeAccessDatabase> _databases =
        new(StringComparer.OrdinalIgnoreCase);

    public void Add(string path, FakeAccessDatabase database)
    {
        _databases[path] = database;
    }

    public IAccessDatabase Open(string mdbPath)
    {
        return _databases[mdbPath];
    }
}

internal sealed class FakeAccessDatabase : IAccessDatabase
{
    private readonly Dictionary<string, AccessTableSchema> _schemas =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, IReadOnlyList<object?[]>> _rows =
        new(StringComparer.OrdinalIgnoreCase);

    public long? CountOverride { get; set; }

    public Exception? ReadException { get; set; }

    public void AddTable(AccessTableSchema schema, params object?[][] rows)
    {
        _schemas[schema.Name] = schema with { RowCount = rows.Length };
        _rows[schema.Name] = rows;
    }

    public IReadOnlyList<string> ListUserTables()
    {
        return _schemas.Keys.OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public AccessTableSchema ReadTable(string tableName)
    {
        if (ReadException is not null)
        {
            throw ReadException;
        }

        return _schemas[tableName];
    }

    public long CountRows(string tableName)
    {
        return CountOverride ?? _rows[tableName].Count;
    }

    public DbDataReader OpenReader(AccessTableSchema schema)
    {
        return new ArrayDataReader(schema, _rows[schema.Name]);
    }

    public void Dispose()
    {
    }
}

internal sealed class ArrayDataReader : DbDataReader
{
    private readonly AccessTableSchema _schema;
    private readonly IReadOnlyList<object?[]> _rows;
    private int _index = -1;

    public ArrayDataReader(AccessTableSchema schema, IReadOnlyList<object?[]> rows)
    {
        _schema = schema;
        _rows = rows;
    }

    private object?[] Current => _rows[_index];

    public override int FieldCount => _schema.Columns.Count;

    public override bool HasRows => _rows.Count > 0;

    public override bool IsClosed => false;

    public override int RecordsAffected => -1;

    public override int Depth => 0;

    public override object this[int ordinal] => GetValue(ordinal);

    public override object this[string name] => GetValue(GetOrdinal(name));

    public override bool Read()
    {
        if (_index + 1 >= _rows.Count)
        {
            return false;
        }

        _index++;
        return true;
    }

    public override string GetName(int ordinal) =>
        _schema.Columns.OrderBy(column => column.Ordinal).ElementAt(ordinal).Name;

    public override int GetOrdinal(string name)
    {
        return _schema.Columns
            .OrderBy(column => column.Ordinal)
            .Select((column, index) => (column.Name, index))
            .First(item => string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase))
            .index;
    }

    public override object GetValue(int ordinal) => Current[ordinal] ?? DBNull.Value;

    public override bool IsDBNull(int ordinal) => Current[ordinal] is null or DBNull;

    public override Type GetFieldType(int ordinal)
    {
        var column = _schema.Columns.OrderBy(item => item.Ordinal).ElementAt(ordinal);
        return new AccessToSqlTypeMapper().GetClrType(column);
    }

    public override string GetDataTypeName(int ordinal) => GetFieldType(ordinal).Name;

    public override int GetValues(object[] values)
    {
        var count = Math.Min(values.Length, FieldCount);
        for (var index = 0; index < count; index++)
        {
            values[index] = GetValue(index);
        }

        return count;
    }

    public override bool GetBoolean(int ordinal) => (bool)GetValue(ordinal);

    public override byte GetByte(int ordinal) => Convert.ToByte(GetValue(ordinal));

    public override char GetChar(int ordinal) => Convert.ToChar(GetValue(ordinal));

    public override DateTime GetDateTime(int ordinal) => (DateTime)GetValue(ordinal);

    public override decimal GetDecimal(int ordinal) => Convert.ToDecimal(GetValue(ordinal));

    public override double GetDouble(int ordinal) => Convert.ToDouble(GetValue(ordinal));

    public override float GetFloat(int ordinal) => Convert.ToSingle(GetValue(ordinal));

    public override Guid GetGuid(int ordinal) => (Guid)GetValue(ordinal);

    public override short GetInt16(int ordinal) => Convert.ToInt16(GetValue(ordinal));

    public override int GetInt32(int ordinal) => Convert.ToInt32(GetValue(ordinal));

    public override long GetInt64(int ordinal) => Convert.ToInt64(GetValue(ordinal));

    public override string GetString(int ordinal) => (string)GetValue(ordinal);

    public override long GetBytes(int ordinal, long dataOffset, byte[]? buffer, int bufferOffset, int length)
    {
        if (GetValue(ordinal) is not byte[] data || buffer is null)
        {
            return 0;
        }

        var copy = Math.Min(length, data.Length - (int)dataOffset);
        Array.Copy(data, dataOffset, buffer, bufferOffset, copy);
        return copy;
    }

    public override long GetChars(int ordinal, long dataOffset, char[]? buffer, int bufferOffset, int length)
        => 0;

    public override bool NextResult() => false;

    public override IEnumerator GetEnumerator() => throw new NotSupportedException();
}

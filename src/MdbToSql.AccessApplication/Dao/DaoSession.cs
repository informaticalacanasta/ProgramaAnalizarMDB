using System.Globalization;
using MdbToSql.AccessApplication.Com;
using MdbToSql.AccessApplication.Workspace;
using MdbToSql.Core.Analysis;
using MdbToSql.Core.Models;

namespace MdbToSql.AccessApplication.Dao;

internal sealed class DaoSession : IDisposable
{
    public const string ProgId = "DAO.DBEngine.36";
    public const int DbAttachedTable = 1073741824;
    public const int DbAttachedOdbc = 536870912;
    public const int DbHiddenObject = 1;
    public const int DbBoolean = 1;
    public const int RelationDontEnforce = 2;
    public const int RelationUpdateCascade = 256;
    public const int RelationDeleteCascade = 4096;

    private readonly ComLifetime _lifetime;
    private readonly object _engine;
    private readonly object _database;
    private bool _disposed;

    private DaoSession(ComLifetime lifetime, object engine, object database)
    {
        _lifetime = lifetime;
        _engine = engine;
        _database = database;
    }

    public string? JetVersion => ComInterop.GetString(_database, "Version");

    public static DaoSession OpenReadOnly(string path) => Open(path, exclusive: false, readOnly: true);

    public static DaoSession OpenExclusive(string path) => Open(path, exclusive: true, readOnly: false);

    private static DaoSession Open(string path, bool exclusive, bool readOnly)
    {
        AnalysisWorkspace.EnsureLocal(path, "DAO");
        var lifetime = new ComLifetime();
        try
        {
            var engine = lifetime.Track(ComInterop.Create(ProgId));
            var database = lifetime.Track(
                ComInterop.Call(engine, "OpenDatabase", path, exclusive, readOnly)!);
            return new DaoSession(lifetime, engine, database);
        }
        catch
        {
            lifetime.Dispose();
            throw;
        }
    }

    public IReadOnlyList<AccessTableReference> ReadTables(List<string> warnings)
    {
        var tables = new List<AccessTableReference>();
        object tableDefs;
        int count;
        try
        {
            tableDefs = _lifetime.Track(ComInterop.Get(_database, "TableDefs"));
            count = ComInterop.Count(tableDefs);
        }
        catch (Exception exception)
        {
            warnings.Add("TableDefs no enumerables: " + exception.Message);
            return tables;
        }

        for (var index = 0; index < count; index++)
        {
            try
            {
                var table = _lifetime.Track(ComInterop.Item(tableDefs, index));
                var name = ComInterop.GetString(table, "Name") ?? string.Empty;
                if (AccessSystemObject.IsSystemName(name))
                {
                    continue;
                }

                string? connect = null;
                string? sourceTable = null;
                try
                {
                    connect = ComInterop.GetString(table, "Connect");
                    sourceTable = ComInterop.GetString(table, "SourceTableName");
                }
                catch (Exception exception)
                {
                    warnings.Add($"Tabla '{name}': no se leyó Connect ({exception.Message}).");
                }

                var attributesObject = ComInterop.TryGet(table, "Attributes");
                var attributes = attributesObject is null
                    ? 0
                    : Convert.ToInt32(attributesObject, CultureInfo.InvariantCulture);
                var attachedByAttribute =
                    (attributes & DbAttachedTable) != 0
                    || (attributes & DbAttachedOdbc) != 0;
                var isLinked = attachedByAttribute || !string.IsNullOrWhiteSpace(connect);
                var classification = AccessLinkClassifier.Classify(connect, sourceTable, isLinked);
                tables.Add(new AccessTableReference(
                    name,
                    isLinked,
                    sourceTable,
                    connect,
                    classification.Kind,
                    classification.SourcePath,
                    classification.IsUnc,
                    classification.IsLocal));
            }
            catch (Exception exception)
            {
                warnings.Add($"TableDef[{index}]: {exception.Message}");
            }
        }

        return tables;
    }

    public IReadOnlyList<AccessLocalTableSchema> ReadLocalTableSchemas(
        IReadOnlyList<AccessTableReference> tables,
        List<string> warnings)
    {
        var schemas = new List<AccessLocalTableSchema>();
        object tableDefs;
        try
        {
            tableDefs = _lifetime.Track(ComInterop.Get(_database, "TableDefs"));
        }
        catch (Exception exception)
        {
            warnings.Add("TableDefs no enumerables para esquema local: " + exception.Message);
            return schemas;
        }

        foreach (var table in tables)
        {
            if (table.IsLinked)
            {
                continue;
            }

            try
            {
                var tableDef = _lifetime.Track(ComInterop.Item(tableDefs, table.Name));
                schemas.Add(new AccessLocalTableSchema(
                    table.Name,
                    ReadTableFields(tableDef, table.Name, warnings),
                    ReadTableIndexes(tableDef, table.Name, warnings)));
            }
            catch (Exception exception)
            {
                warnings.Add($"Tabla local '{table.Name}': no se leyeron Fields/Indexes ({exception.Message}).");
            }
        }

        return schemas;
    }

    private IReadOnlyList<AccessLocalFieldSchema> ReadTableFields(object tableDef, string tableName, List<string> warnings)
    {
        var fields = new List<AccessLocalFieldSchema>();
        object collection;
        int count;
        try
        {
            collection = _lifetime.Track(ComInterop.Get(tableDef, "Fields"));
            count = ComInterop.Count(collection);
        }
        catch (Exception exception)
        {
            warnings.Add($"Tabla '{tableName}' Fields: {exception.Message}");
            return fields;
        }

        for (var index = 0; index < count; index++)
        {
            try
            {
                var field = _lifetime.Track(ComInterop.Item(collection, index));
                var typeObject = ComInterop.TryGet(field, "Type");
                int? type = typeObject is null
                    ? null
                    : Convert.ToInt32(typeObject, CultureInfo.InvariantCulture);
                var sizeObject = ComInterop.TryGet(field, "Size");
                int? size = sizeObject is null
                    ? null
                    : Convert.ToInt32(sizeObject, CultureInfo.InvariantCulture);
                var requiredObject = ComInterop.TryGet(field, "Required");
                var required = requiredObject is null
                    ? (bool?)null
                    : Convert.ToBoolean(requiredObject, CultureInfo.InvariantCulture);
                var attributesObject = ComInterop.TryGet(field, "Attributes");
                var attributes = attributesObject is null
                    ? 0
                    : Convert.ToInt32(attributesObject, CultureInfo.InvariantCulture);
                fields.Add(new AccessLocalFieldSchema(
                    ComInterop.GetString(field, "Name") ?? $"[{index}]",
                    index,
                    type,
                    AccessDaoTypeNames.Name(type),
                    size,
                    required,
                    (attributes & 16) != 0));
            }
            catch (Exception exception)
            {
                warnings.Add($"Tabla '{tableName}' Field[{index}]: {exception.Message}");
            }
        }

        return fields;
    }

    private IReadOnlyList<AccessLocalIndexSchema> ReadTableIndexes(object tableDef, string tableName, List<string> warnings)
    {
        var indexes = new List<AccessLocalIndexSchema>();
        object collection;
        int count;
        try
        {
            collection = _lifetime.Track(ComInterop.Get(tableDef, "Indexes"));
            count = ComInterop.Count(collection);
        }
        catch (Exception exception)
        {
            warnings.Add($"Tabla '{tableName}' Indexes: {exception.Message}");
            return indexes;
        }

        for (var index = 0; index < count; index++)
        {
            try
            {
                var item = _lifetime.Track(ComInterop.Item(collection, index));
                var name = ComInterop.GetString(item, "Name") ?? $"[{index}]";
                var uniqueObject = ComInterop.TryGet(item, "Unique");
                var unique = uniqueObject is not null
                    && Convert.ToBoolean(uniqueObject, CultureInfo.InvariantCulture);
                var primaryObject = ComInterop.TryGet(item, "Primary");
                var primary = primaryObject is not null
                    && Convert.ToBoolean(primaryObject, CultureInfo.InvariantCulture);
                indexes.Add(new AccessLocalIndexSchema(
                    name,
                    unique,
                    primary,
                    ReadIndexFieldNames(item)));
            }
            catch (Exception exception)
            {
                warnings.Add($"Tabla '{tableName}' Index[{index}]: {exception.Message}");
            }
        }

        return indexes;
    }

    private IReadOnlyList<string> ReadIndexFieldNames(object index)
    {
        var names = new List<string>();
        try
        {
            var fields = _lifetime.Track(ComInterop.Get(index, "Fields"));
            var count = ComInterop.Count(fields);
            for (var fieldIndex = 0; fieldIndex < count; fieldIndex++)
            {
                var field = _lifetime.Track(ComInterop.Item(fields, fieldIndex));
                names.Add(ComInterop.GetString(field, "Name") ?? $"[{fieldIndex}]");
            }
        }
        catch
        {
            return names;
        }

        return names;
    }

    public IReadOnlyList<AccessQueryAnalysis> ReadQueries(List<string> warnings)
    {
        var queries = new List<AccessQueryAnalysis>();
        object queryDefs;
        int count;
        try
        {
            queryDefs = _lifetime.Track(ComInterop.Get(_database, "QueryDefs"));
            count = ComInterop.Count(queryDefs);
        }
        catch (Exception exception)
        {
            warnings.Add("QueryDefs no enumerables: " + exception.Message);
            return queries;
        }

        for (var index = 0; index < count; index++)
        {
            try
            {
                var query = _lifetime.Track(ComInterop.Item(queryDefs, index));
                var name = ComInterop.GetString(query, "Name") ?? string.Empty;
                var isSystem = AccessSystemObject.IsSystemName(name);
                if (isSystem)
                {
                    continue;
                }

                var queryWarnings = new List<string>();
                string? sql;
                try
                {
                    sql = ComInterop.GetString(query, "SQL");
                }
                catch (Exception exception)
                {
                    sql = null;
                    queryWarnings.Add("SQL no disponible: " + exception.Message);
                }

                int? type = null;
                var typeObject = ComInterop.TryGet(query, "Type");
                if (typeObject is not null)
                {
                    type = Convert.ToInt32(typeObject, CultureInfo.InvariantCulture);
                }

                bool? returnsRecords = null;
                var returnsObject = ComInterop.TryGet(query, "ReturnsRecords");
                if (returnsObject is not null)
                {
                    returnsRecords = Convert.ToBoolean(returnsObject, CultureInfo.InvariantCulture);
                }

                bool? isHidden = null;
                var queryAttributesObject = ComInterop.TryGet(query, "Attributes");
                if (queryAttributesObject is not null)
                {
                    var queryAttributes = Convert.ToInt32(queryAttributesObject, CultureInfo.InvariantCulture);
                    isHidden = (queryAttributes & DbHiddenObject) != 0;
                }

                queries.Add(new AccessQueryAnalysis(
                    name,
                    sql,
                    type,
                    ReadParameters(query, queryWarnings),
                    ComInterop.GetString(query, "Connect"),
                    returnsRecords,
                    isSystem,
                    isHidden,
                    queryWarnings));
            }
            catch (Exception exception)
            {
                warnings.Add($"QueryDef[{index}]: {exception.Message}");
            }
        }

        return queries;
    }

    public IReadOnlyList<AccessRelationAnalysis> ReadRelations(List<string> warnings)
    {
        var relations = new List<AccessRelationAnalysis>();
        object collection;
        int count;
        try
        {
            collection = _lifetime.Track(ComInterop.Get(_database, "Relations"));
            count = ComInterop.Count(collection);
        }
        catch (Exception exception)
        {
            warnings.Add("Relations no enumerables: " + exception.Message);
            return relations;
        }

        for (var index = 0; index < count; index++)
        {
            try
            {
                var relation = _lifetime.Track(ComInterop.Item(collection, index));
                var name = ComInterop.GetString(relation, "Name") ?? string.Empty;
                if (AccessSystemObject.IsSystemName(name))
                {
                    continue;
                }

                var attributesObject = ComInterop.TryGet(relation, "Attributes");
                var attributes = attributesObject is null
                    ? 0
                    : Convert.ToInt32(attributesObject, CultureInfo.InvariantCulture);
                relations.Add(new AccessRelationAnalysis(
                    name,
                    ComInterop.GetString(relation, "Table"),
                    ComInterop.GetString(relation, "ForeignTable"),
                    ReadRelationFields(relation),
                    attributes,
                    (attributes & RelationDontEnforce) == 0,
                    (attributes & RelationUpdateCascade) != 0,
                    (attributes & RelationDeleteCascade) != 0));
            }
            catch (Exception exception)
            {
                warnings.Add($"Relation[{index}]: {exception.Message}");
            }
        }

        return relations;
    }

    public AccessStartupAnalysis ReadStartup()
    {
        return new AccessStartupAnalysis(
            DocumentExists("Scripts", "AutoExec"),
            ReadStringProperty("StartupForm"),
            AccessBooleanParser.Parse(ReadStringProperty("StartupShowDBWindow")),
            AccessBooleanParser.Parse(ReadStringProperty("StartupShowStatusBar")),
            AccessBooleanParser.Parse(ReadStringProperty("AllowFullMenus")),
            AccessBooleanParser.Parse(ReadStringProperty("AllowBuiltinToolbars")),
            AccessBooleanParser.Parse(ReadStringProperty("AllowBreakIntoCode")),
            AccessBooleanParser.Parse(ReadStringProperty("AllowSpecialKeys")),
            AccessBooleanParser.Parse(ReadStringProperty("AllowBypassKey")));
    }

    public IReadOnlyList<string> ReadDocumentNames(string containerName)
    {
        var names = new List<string>();
        try
        {
            var containers = _lifetime.Track(ComInterop.Get(_database, "Containers"));
            var container = _lifetime.Track(ComInterop.Item(containers, containerName));
            var documents = _lifetime.Track(ComInterop.Get(container, "Documents"));
            var count = ComInterop.Count(documents);
            for (var index = 0; index < count; index++)
            {
                var document = _lifetime.Track(ComInterop.Item(documents, index));
                var name = ComInterop.GetString(document, "Name") ?? string.Empty;
                if (!AccessSystemObject.IsSystemName(name))
                {
                    names.Add(name);
                }
            }
        }
        catch
        {
            return names;
        }

        return names;
    }

    public void EnsureAllowBypassKey()
    {
        var current = ReadStringProperty("AllowBypassKey");
        var parsed = AccessBooleanParser.Parse(current);
        if (parsed == true)
        {
            return;
        }

        SetOrCreateProperty("AllowBypassKey", DbBoolean, true);
    }

    private IReadOnlyList<AccessQueryParameter> ReadParameters(object query, List<string> warnings)
    {
        var parameters = new List<AccessQueryParameter>();
        try
        {
            var collection = _lifetime.Track(ComInterop.Get(query, "Parameters"));
            var count = ComInterop.Count(collection);
            for (var index = 0; index < count; index++)
            {
                var parameter = _lifetime.Track(ComInterop.Item(collection, index));
                var name = ComInterop.GetString(parameter, "Name") ?? $"[{index}]";
                int? type = null;
                var typeObject = ComInterop.TryGet(parameter, "Type");
                if (typeObject is not null)
                {
                    type = Convert.ToInt32(typeObject, CultureInfo.InvariantCulture);
                }

                parameters.Add(new AccessQueryParameter(name, type));
            }
        }
        catch (Exception exception)
        {
            warnings.Add("Parameters: " + exception.Message);
        }

        return parameters;
    }

    private IReadOnlyList<AccessRelationField> ReadRelationFields(object relation)
    {
        var fields = new List<AccessRelationField>();
        try
        {
            var collection = _lifetime.Track(ComInterop.Get(relation, "Fields"));
            var count = ComInterop.Count(collection);
            for (var index = 0; index < count; index++)
            {
                var field = _lifetime.Track(ComInterop.Item(collection, index));
                fields.Add(new AccessRelationField(
                    ComInterop.GetString(field, "Name") ?? $"[{index}]",
                    ComInterop.GetString(field, "ForeignName")));
            }
        }
        catch
        {
            return fields;
        }

        return fields;
    }

    private bool DocumentExists(string containerName, string documentName)
    {
        return ReadDocumentNames(containerName)
            .Any(name => string.Equals(name, documentName, StringComparison.OrdinalIgnoreCase));
    }

    private string? ReadStringProperty(string name)
    {
        try
        {
            var properties = _lifetime.Track(ComInterop.Get(_database, "Properties"));
            var property = _lifetime.Track(ComInterop.Item(properties, name));
            return ComInterop.GetString(property, "Value");
        }
        catch
        {
            return null;
        }
    }

    private void SetOrCreateProperty(string name, int type, object value)
    {
        var properties = _lifetime.Track(ComInterop.Get(_database, "Properties"));
        try
        {
            var property = _lifetime.Track(ComInterop.Item(properties, name));
            ComInterop.Set(property, "Value", value);
        }
        catch
        {
            var created = _lifetime.Track(ComInterop.Call(_database, "CreateProperty", name, type, value)!);
            ComInterop.Call(properties, "Append", created);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        ComInterop.TryCall(_database, "Close");
        ComInterop.TryCall(_engine, "Idle", 8);
        _lifetime.Dispose();
        _disposed = true;
    }
}

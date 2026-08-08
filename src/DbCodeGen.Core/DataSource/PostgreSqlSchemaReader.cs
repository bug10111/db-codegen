using DbCodeGen.Core.Model;
using Npgsql;

namespace DbCodeGen.Core.DataSource;

/// <summary>
/// PostgreSQL 元数据读取方言实现，通过 information_schema 只读查询表清单与列元数据。
/// 连接由 01 连接服务打开后传入本实现持有，Dispose 时释放连接。
/// </summary>
public sealed class PostgreSqlSchemaReader : ISchemaReader
{
    /// <summary>
    /// 表清单查询语句，只读当前 schema 基础表，默认按表名排序，首屏不含列。
    /// 表注释经 pg_class 描述对象读取，schema 取当前 search_path 首段并以 public 兜底。
    /// </summary>
    private const string TableListSql =
        "SELECT t.table_name, t.table_schema, " +
        "obj_description((quote_ident(t.table_schema) || '.' || quote_ident(t.table_name))::regclass, 'pg_class') AS table_comment " +
        "FROM information_schema.tables t " +
        "WHERE t.table_type = 'BASE TABLE' AND t.table_schema = COALESCE(current_schema(), 'public') " +
        "ORDER BY t.table_name";

    /// <summary>
    /// 列元数据查询语句，按表名参数只读当前 schema 列，含主键标记（约束关联）、默认值与长度精度。
    /// 列注释经 pg_class 描述对象按列序号读取。
    /// </summary>
    private const string ColumnListSql =
        "SELECT c.column_name, c.table_schema, c.data_type, c.is_nullable, c.column_default, " +
        "c.character_maximum_length, c.numeric_precision, c.numeric_scale, c.ordinal_position, " +
        "col_description((quote_ident(c.table_schema) || '.' || quote_ident(c.table_name))::regclass, c.ordinal_position) AS column_comment, " +
        "EXISTS (" +
        "SELECT 1 FROM information_schema.table_constraints tc " +
        "JOIN information_schema.key_column_usage kcu " +
        "ON tc.constraint_name = kcu.constraint_name AND tc.constraint_schema = kcu.constraint_schema " +
        "WHERE tc.constraint_type = 'PRIMARY KEY' AND tc.table_schema = c.table_schema " +
        "AND tc.table_name = c.table_name AND kcu.column_name = c.column_name" +
        ") AS is_primary_key " +
        "FROM information_schema.columns c " +
        "WHERE c.table_schema = COALESCE(current_schema(), 'public') AND c.table_name = @tableName " +
        "ORDER BY c.ordinal_position";

    private readonly NpgsqlConnection _connection;

    /// <summary>
    /// 以已打开的 Npgsql 连接构造元数据读取器，连接生命周期随本实例 Dispose 释放。
    /// </summary>
    /// <param name="connection">由 01 连接服务打开的 Npgsql 连接。</param>
    public PostgreSqlSchemaReader(NpgsqlConnection connection)
    {
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<TableInfo>> GetTablesAsync(CancellationToken ct)
    {
        var tables = new List<TableInfo>();
        await using NpgsqlCommand command = _connection.CreateCommand();
        command.CommandText = TableListSql;

        // 逐行读取表清单，仅组装表名/库名/注释，不触碰列元数据
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            string rawName = reader.GetString(reader.GetOrdinal("table_name"));
            string schemaName = reader.GetString(reader.GetOrdinal("table_schema"));
            string? comment = ReadNullableString(reader, "table_comment");
            tables.Add(CreateTableSummary(rawName, schemaName, comment));
        }

        return tables;
    }

    /// <inheritdoc />
    public async Task<TableInfo> GetTableAsync(string tableName, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tableName);

        var columns = new List<ColumnInfo>();
        string schemaName = string.Empty;
        await using NpgsqlCommand command = _connection.CreateCommand();
        command.CommandText = ColumnListSql;
        command.Parameters.AddWithValue("@tableName", tableName);

        // 逐行读取列元数据，库名取自首行数据行的 schema 列
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            if (schemaName.Length == 0)
            {
                schemaName = reader.GetString(reader.GetOrdinal("table_schema"));
            }
            columns.Add(ReadColumnInfo(reader));
        }

        TableInfo table = CreateTableSummary(tableName, schemaName, null);
        table.SetColumns(columns);
        return table;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _connection.Dispose();
    }

    /// <summary>
    /// 从当前列数据行读取一列元数据，主键经约束关联标记，自增按默认值序列前缀判定。
    /// </summary>
    /// <param name="reader">已定位到当前列行的 Npgsql 数据读取器。</param>
    /// <returns>列元数据实体。</returns>
    private static ColumnInfo ReadColumnInfo(NpgsqlDataReader reader)
    {
        string rawName = reader.GetString(reader.GetOrdinal("column_name"));
        bool isPrimaryKey = reader.GetBoolean(reader.GetOrdinal("is_primary_key"));
        string? defaultValue = ReadNullableString(reader, "column_default");
        bool autoIncrement = defaultValue?.StartsWith("nextval(", StringComparison.OrdinalIgnoreCase) == true;
        bool isNullable = string.Equals(
            reader.GetString(reader.GetOrdinal("is_nullable")), "YES", StringComparison.OrdinalIgnoreCase);

        return new ColumnInfo
        {
            RawName = rawName,
            PropertyName = TableInfo.ToCamelCase(rawName),
            Comment = ReadNullableString(reader, "column_comment"),
            RawDbType = reader.GetString(reader.GetOrdinal("data_type")),
            IsPrimaryKey = isPrimaryKey,
            AutoIncrement = autoIncrement,
            IsNullable = isNullable,
            DefaultValue = defaultValue,
            Length = ReadNullableInt(reader, "character_maximum_length"),
            Precision = ReadNullableInt(reader, "numeric_precision"),
            Scale = ReadNullableInt(reader, "numeric_scale")
        };
    }

    /// <summary>
    /// 构造表清单摘要实体，只含表名/库名/注释，类名与变量名按表名实时转换。
    /// </summary>
    /// <param name="rawName">原始表名。</param>
    /// <param name="schemaName">所属 schema 名。</param>
    /// <param name="comment">表注释，可为空。</param>
    /// <returns>不含列的表摘要实体。</returns>
    private static TableInfo CreateTableSummary(string rawName, string schemaName, string? comment)
    {
        return new TableInfo
        {
            RawName = rawName,
            SchemaName = schemaName,
            ClassName = TableInfo.ToPascalCase(rawName),
            VariableName = TableInfo.ToCamelCase(rawName),
            Comment = comment
        };
    }

    /// <summary>
    /// 读取可空字符串列，数据库 NULL 返回空引用。
    /// </summary>
    /// <param name="reader">数据读取器。</param>
    /// <param name="columnName">目标列名。</param>
    /// <returns>列值，数据库 NULL 时为 null。</returns>
    private static string? ReadNullableString(NpgsqlDataReader reader, string columnName)
    {
        int ordinal = reader.GetOrdinal(columnName);
        return reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    }

    /// <summary>
    /// 读取可空整数列，数据库 NULL 返回空值。
    /// </summary>
    /// <param name="reader">数据读取器。</param>
    /// <param name="columnName">目标列名。</param>
    /// <returns>列值，数据库 NULL 时为 null。</returns>
    private static int? ReadNullableInt(NpgsqlDataReader reader, string columnName)
    {
        int ordinal = reader.GetOrdinal(columnName);
        if (reader.IsDBNull(ordinal))
        {
            return null;
        }

        return reader.GetInt32(ordinal);
    }
}

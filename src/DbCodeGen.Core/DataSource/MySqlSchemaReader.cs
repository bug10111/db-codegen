using DbCodeGen.Core.Model;
using MySqlConnector;

namespace DbCodeGen.Core.DataSource;

/// <summary>
/// MySQL 元数据读取方言实现，通过 information_schema 只读查询表清单与列元数据。
/// 连接由 01 连接服务打开后传入本实现持有，Dispose 时释放连接。
/// </summary>
public sealed class MySqlSchemaReader : ISchemaReader
{
    /// <summary>
    /// 表清单查询语句，只读当前库基础表，默认按表名排序，首屏不含列。
    /// </summary>
    private const string TableListSql =
        "SELECT TABLE_NAME, TABLE_SCHEMA, TABLE_COMMENT " +
        "FROM information_schema.TABLES " +
        "WHERE TABLE_SCHEMA = DATABASE() AND TABLE_TYPE = 'BASE TABLE' " +
        "ORDER BY TABLE_NAME";

    /// <summary>
    /// 列元数据查询语句，按表名参数只读当前库列，含主键标记、自增标记、默认值与长度精度。
    /// </summary>
    private const string ColumnListSql =
        "SELECT COLUMN_NAME, TABLE_SCHEMA, DATA_TYPE, IS_NULLABLE, COLUMN_DEFAULT, " +
        "CHARACTER_MAXIMUM_LENGTH, NUMERIC_PRECISION, NUMERIC_SCALE, " +
        "COLUMN_KEY, EXTRA, COLUMN_COMMENT " +
        "FROM information_schema.COLUMNS " +
        "WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = @tableName " +
        "ORDER BY ORDINAL_POSITION";

    private readonly MySqlConnection _connection;

    /// <summary>
    /// 以已打开的 MySql 连接构造元数据读取器，连接生命周期随本实例 Dispose 释放。
    /// </summary>
    /// <param name="connection">由 01 连接服务打开的 MySql 连接。</param>
    public MySqlSchemaReader(MySqlConnection connection)
    {
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<TableInfo>> GetTablesAsync(CancellationToken ct)
    {
        var tables = new List<TableInfo>();
        await using MySqlCommand command = _connection.CreateCommand();
        command.CommandText = TableListSql;

        // 逐行读取表清单，仅组装表名/库名/注释，不触碰列元数据
        await using MySqlDataReader reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            string rawName = reader.GetString(reader.GetOrdinal("TABLE_NAME"));
            string schemaName = reader.GetString(reader.GetOrdinal("TABLE_SCHEMA"));
            string? comment = ReadNullableString(reader, "TABLE_COMMENT");
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
        await using MySqlCommand command = _connection.CreateCommand();
        command.CommandText = ColumnListSql;
        command.Parameters.AddWithValue("@tableName", tableName);

        // 逐行读取列元数据，库名取自首行数据行的库列
        await using MySqlDataReader reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            if (schemaName.Length == 0)
            {
                schemaName = reader.GetString(reader.GetOrdinal("TABLE_SCHEMA"));
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
    /// 从当前列数据行读取一列元数据，含主键/自增/可空/默认值/长度/精度等原始信息。
    /// </summary>
    /// <param name="reader">已定位到当前列行的 MySql 数据读取器。</param>
    /// <returns>列元数据实体。</returns>
    private static ColumnInfo ReadColumnInfo(MySqlDataReader reader)
    {
        string rawName = reader.GetString(reader.GetOrdinal("COLUMN_NAME"));
        bool isPrimaryKey = string.Equals(
            reader.GetString(reader.GetOrdinal("COLUMN_KEY")), "PRI", StringComparison.OrdinalIgnoreCase);
        bool autoIncrement = reader.GetString(reader.GetOrdinal("EXTRA"))
            .Contains("auto_increment", StringComparison.OrdinalIgnoreCase);
        bool isNullable = string.Equals(
            reader.GetString(reader.GetOrdinal("IS_NULLABLE")), "YES", StringComparison.OrdinalIgnoreCase);

        return new ColumnInfo
        {
            RawName = rawName,
            PropertyName = TableInfo.ToCamelCase(rawName),
            Comment = ReadNullableString(reader, "COLUMN_COMMENT"),
            RawDbType = reader.GetString(reader.GetOrdinal("DATA_TYPE")),
            IsPrimaryKey = isPrimaryKey,
            AutoIncrement = autoIncrement,
            IsNullable = isNullable,
            DefaultValue = ReadNullableString(reader, "COLUMN_DEFAULT"),
            Length = ReadNullableInt(reader, "CHARACTER_MAXIMUM_LENGTH"),
            Precision = ReadNullableInt(reader, "NUMERIC_PRECISION"),
            Scale = ReadNullableInt(reader, "NUMERIC_SCALE")
        };
    }

    /// <summary>
    /// 构造表清单摘要实体，只含表名/库名/注释，类名与变量名按表名实时转换。
    /// </summary>
    /// <param name="rawName">原始表名。</param>
    /// <param name="schemaName">所属库名。</param>
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
    private static string? ReadNullableString(MySqlDataReader reader, string columnName)
    {
        int ordinal = reader.GetOrdinal(columnName);
        return reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    }

    /// <summary>
    /// 读取可空整数列，数据库 NULL 返回空值，长整型数值收敛为整数。
    /// </summary>
    /// <param name="reader">数据读取器。</param>
    /// <param name="columnName">目标列名。</param>
    /// <returns>列值，数据库 NULL 时为 null。</returns>
    private static int? ReadNullableInt(MySqlDataReader reader, string columnName)
    {
        int ordinal = reader.GetOrdinal(columnName);
        if (reader.IsDBNull(ordinal))
        {
            return null;
        }

        return (int)reader.GetInt64(ordinal);
    }
}

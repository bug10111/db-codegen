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
    /// 创建时间随清单一并读取，供①区列表展示与默认排序。
    /// </summary>
    private const string TableListSql =
        "SELECT TABLE_NAME, TABLE_SCHEMA, TABLE_COMMENT, CREATE_TIME " +
        "FROM information_schema.TABLES " +
        "WHERE TABLE_SCHEMA = DATABASE() AND TABLE_TYPE = 'BASE TABLE' " +
        "ORDER BY TABLE_NAME";

    /// <summary>
    /// 表元信息查询语句，按表名参数只读当前库表注释与创建时间，供表详情阶段补齐 comment 与 createdTime。
    /// </summary>
    private const string TableMetaSql =
        "SELECT TABLE_SCHEMA, TABLE_COMMENT, CREATE_TIME " +
        "FROM information_schema.TABLES " +
        "WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = @tableName";

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

        // 逐行读取表清单，仅组装表名/库名/注释/创建时间，不触碰列元数据
        await using MySqlDataReader reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            string rawName = reader.GetString(reader.GetOrdinal("TABLE_NAME"));
            string schemaName = reader.GetString(reader.GetOrdinal("TABLE_SCHEMA"));
            string? comment = ReadNullableString(reader, "TABLE_COMMENT");
            DateTime? createdTime = ReadNullableDateTime(reader, "CREATE_TIME");
            tables.Add(CreateTableSummary(rawName, schemaName, comment, createdTime));
        }

        return tables;
    }

    /// <inheritdoc />
    public async Task<TableInfo> GetTableAsync(string tableName, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tableName);

        var columns = new List<ColumnInfo>();
        string schemaName = string.Empty;

        // 逐行读取列元数据，库名取自首行数据行的库列；读取器与命令限定在块内，读完即释放连接，
        // 避免连接上残留未关闭读取器导致后续表元信息查询报"连接正忙"
        await using (MySqlCommand command = _connection.CreateCommand())
        {
            command.CommandText = ColumnListSql;
            command.Parameters.AddWithValue("@tableName", tableName);

            await using (MySqlDataReader reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false))
            {
                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    if (schemaName.Length == 0)
                    {
                        schemaName = reader.GetString(reader.GetOrdinal("TABLE_SCHEMA"));
                    }
                    columns.Add(ReadColumnInfo(reader));
                }
            }
        }

        // 首个读取器已释放、连接空闲，再查表注释与创建时间，补齐 comment/createdTime（空列表时库名同样取自表元信息）
        (string? tableComment, DateTime? createdTime, string? metaSchema) = await ReadTableMetaAsync(tableName, ct);
        if (schemaName.Length == 0 && !string.IsNullOrEmpty(metaSchema))
        {
            schemaName = metaSchema;
        }

        TableInfo table = CreateTableSummary(tableName, schemaName, tableComment, createdTime);
        table.SetColumns(columns);
        return table;
    }

    /// <summary>
    /// 读取单张表的注释与创建时间元信息，供表详情阶段补齐；表不存在时返回空值。
    /// </summary>
    /// <param name="tableName">目标表名。</param>
    /// <param name="ct">取消标记。</param>
    /// <returns>表注释、创建时间与库名三元组，任一缺失时对应值为 null。</returns>
    private async Task<(string? Comment, DateTime? CreatedTime, string? SchemaName)> ReadTableMetaAsync(string tableName, CancellationToken ct)
    {
        await using MySqlCommand command = _connection.CreateCommand();
        command.CommandText = TableMetaSql;
        command.Parameters.AddWithValue("@tableName", tableName);

        await using MySqlDataReader reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            return (null, null, null);
        }

        string? schemaName = ReadNullableString(reader, "TABLE_SCHEMA");
        string? comment = ReadNullableString(reader, "TABLE_COMMENT");
        DateTime? tableCreatedTime = ReadNullableDateTime(reader, "CREATE_TIME");
        return (comment, tableCreatedTime, schemaName);
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
    /// 构造表清单摘要实体，只含表名/库名/注释/创建时间，类名与变量名按表名实时转换。
    /// </summary>
    /// <param name="rawName">原始表名。</param>
    /// <param name="schemaName">所属库名。</param>
    /// <param name="comment">表注释，可为空。</param>
    /// <param name="createdTime">表创建时间，可为空。</param>
    /// <returns>不含列的表摘要实体。</returns>
    private static TableInfo CreateTableSummary(string rawName, string schemaName, string? comment, DateTime? createdTime = null)
    {
        return new TableInfo
        {
            RawName = rawName,
            SchemaName = schemaName,
            ClassName = TableInfo.ToPascalCase(rawName),
            VariableName = TableInfo.ToCamelCase(rawName),
            Comment = comment,
            CreatedTime = createdTime
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
    /// 读取可空日期时间列，数据库 NULL 返回空值。
    /// </summary>
    /// <param name="reader">数据读取器。</param>
    /// <param name="columnName">目标列名。</param>
    /// <returns>列值，数据库 NULL 时为 null。</returns>
    private static DateTime? ReadNullableDateTime(MySqlDataReader reader, string columnName)
    {
        int ordinal = reader.GetOrdinal(columnName);
        return reader.IsDBNull(ordinal) ? null : reader.GetDateTime(ordinal);
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

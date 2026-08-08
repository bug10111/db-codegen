using System.Data.Common;
using DbCodeGen.Core.Model;
using MySqlConnector;
using Npgsql;

namespace DbCodeGen.Core.DataSource;

/// <summary>
/// 元数据读取器工厂实现，按数据库类型创建 MySql 或 PostgreSql 方言读取器，
/// 并校验传入连接与数据库类型匹配，避免错误驱动组合导致运行时异常。
/// </summary>
public sealed class SchemaReaderFactory : ISchemaReaderFactory
{
    /// <inheritdoc />
    public ISchemaReader Create(DataSourceType type, DbConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);

        return type switch
        {
            DataSourceType.MySql when connection is MySqlConnection mysql => new MySqlSchemaReader(mysql),
            DataSourceType.PostgreSql when connection is NpgsqlConnection npgsql => new PostgreSqlSchemaReader(npgsql),
            DataSourceType.MySql => throw new ArgumentException("数据库类型为 MySql 但传入连接不是 MySqlConnection。", nameof(connection)),
            DataSourceType.PostgreSql => throw new ArgumentException("数据库类型为 PostgreSql 但传入连接不是 NpgsqlConnection。", nameof(connection)),
            _ => throw new NotSupportedException($"不支持的数据库类型：{type}。")
        };
    }
}

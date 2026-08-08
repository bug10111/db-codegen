using DbCodeGen.Core.Model;

namespace DbCodeGen.Core.DataSource;

/// <summary>
/// 数据库元数据读取契约，按方言实现（MySql/PostgreSql）读取表清单与列元数据。
/// 实现构造时持有已打开的数据库连接，连接生命周期随 Dispose 释放。
/// </summary>
public interface ISchemaReader : IDisposable
{
    /// <summary>
    /// 读取表清单，只返回表名/库名/注释，首屏不含列元数据，默认按表名排序。
    /// </summary>
    /// <param name="ct">取消令牌，贯穿查询全程。</param>
    /// <returns>表清单，每项为不含列的表摘要实体。</returns>
    Task<IReadOnlyList<TableInfo>> GetTablesAsync(CancellationToken ct);

    /// <summary>
    /// 读取单张表的完整列元数据，含主键/自增/可空/默认值/长度等，并派生主键与非主键子集。
    /// </summary>
    /// <param name="tableName">目标表名。</param>
    /// <param name="ct">取消令牌，贯穿查询全程。</param>
    /// <returns>含完整列元数据的表实体。</returns>
    Task<TableInfo> GetTableAsync(string tableName, CancellationToken ct);
}

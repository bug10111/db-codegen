using System.Data.Common;
using DbCodeGen.Core.Model;

namespace DbCodeGen.Core.DataSource;

/// <summary>
/// 元数据读取器工厂契约，按数据库类型创建对应方言实现并绑定已打开的连接。
/// 工厂不负责建立连接，连接由调用方经 01 连接服务打开后传入。
/// </summary>
public interface ISchemaReaderFactory
{
    /// <summary>
    /// 按数据库类型创建对应的元数据读取器实例。
    /// </summary>
    /// <param name="type">数据库类型，MySql 或 PostgreSql。</param>
    /// <param name="connection">已打开的数据库连接，生命周期归读取器释放。</param>
    /// <returns>对应方言的元数据读取器。</returns>
    ISchemaReader Create(DataSourceType type, DbConnection connection);
}

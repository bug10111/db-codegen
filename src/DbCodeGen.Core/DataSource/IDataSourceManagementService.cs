using System.Runtime.Versioning;
using DbCodeGen.Core.Model;

namespace DbCodeGen.Core.DataSource;

/// <summary>
/// 数据源连接管理服务：连接配置增删改查的唯一 Core 通道，字段校验、唯一名校验、
/// 密码 DPAPI 加密与经配置服务持久化在此收敛；供桌面端数据源管理窗口与 MCP 数据源管理工具共用，
/// 桌面端表单校验与 MCP 入参校验共用同一套服务端规则。
/// 密码明文只在服务内的加密瞬态存在，列表与持久化只持有 DPAPI 密文。
/// </summary>
[SupportedOSPlatform("windows")]
public interface IDataSourceManagementService
{
    /// <summary>
    /// 获取全部已保存的连接配置，首次访问触发配置加载。
    /// </summary>
    /// <returns>连接配置列表，顺序与配置存储一致。</returns>
    IReadOnlyList<DataSourceConfig> List();

    /// <summary>
    /// 按连接名称获取单个连接配置。
    /// </summary>
    /// <param name="name">连接名称。</param>
    /// <returns>命中的连接配置；不存在时返回 null。</returns>
    DataSourceConfig? GetByName(string name);

    /// <summary>
    /// 保存连接：OriginalName 为空按新增处理，非空按原名定位更新。
    /// 字段校验、唯一名校验（忽略大小写、排除自身）通过后写入内存快照并落盘；
    /// 密码明文非空白时加密覆盖，留空保持原密文；落盘失败自动回滚内存快照并返回 SaveFailed。
    /// </summary>
    /// <param name="draft">连接保存草稿。</param>
    /// <returns>保存结果：成功携带落盘后的配置；失败携带状态与可读消息且不改变配置。</returns>
    /// <exception cref="ArgumentNullException">draft 为 null 时抛出。</exception>
    DataSourceOperationResult Save(DataSourceConnectionDraft draft);

    /// <summary>
    /// 按连接名称删除连接并落盘；落盘失败自动回滚内存快照并返回 SaveFailed。
    /// </summary>
    /// <param name="name">待删除连接的名称。</param>
    /// <returns>删除结果：成功携带被删除的配置；目标不存在返回 NotFound。</returns>
    DataSourceOperationResult Delete(string name);
}

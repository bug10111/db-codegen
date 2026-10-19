using System.ComponentModel;
using DbCodeGen.Core.DataSource;
using DbCodeGen.Core.Model;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace DbCodeGen.McpServer.Tools;

/// <summary>
/// 数据源只读枚举工具：列出已保存的数据源连接。
/// 安全语义：密码明文与 DPAPI 密文一律不进入任何应答字段（投影白名单式输出）。
/// </summary>
[McpServerToolType]
public sealed class DataSourceQueryTools
{
    private readonly IDataSourceManagementService _dataSourceManagement;
    private readonly ILogger<DataSourceQueryTools> _logger;

    /// <summary>
    /// 以数据源连接管理服务与日志器构造数据源枚举工具。
    /// </summary>
    /// <param name="dataSourceManagement">数据源连接管理服务，读取已保存连接清单。</param>
    /// <param name="logger">工具日志器。</param>
    public DataSourceQueryTools(
        IDataSourceManagementService dataSourceManagement,
        ILogger<DataSourceQueryTools> logger)
    {
        _dataSourceManagement = dataSourceManagement;
        _logger = logger;
    }

    /// <summary>
    /// 列出全部已保存的数据源连接摘要（名称、类型、主机、端口、库、用户、时间戳）。
    /// 出于安全考虑密码不回显：既不返回明文也不返回密文。
    /// </summary>
    /// <param name="cancellationToken">取消标记。</param>
    /// <returns>数据源连接摘要列表。</returns>
    [McpServerTool(Name = "list_datasources", Title = "列出数据源", ReadOnly = true, Idempotent = true, Destructive = false, UseStructuredContent = true)]
    [Description("用途：列出全部已保存的数据源连接摘要，为连库类工具提供 datasource 名称。\n"
        + "何时用：list_tables/get_table_detail/check_type_mapping/按名 test_connection/add_datasource/update_datasource/delete_datasource 之前。\n"
        + "参数：无。\n"
        + "返回：datasources[]——name（唯一连接名）、type（MySql/PostgreSql）、host、port、database、userId、createdAt、updatedAt。密码明文与 DPAPI 密文结构性不回显。\n"
        + "常见错误：InternalError（配置读取异常，罕见）。")]
    public Task<ListDataSourcesResult> ListDataSourcesAsync(CancellationToken cancellationToken)
    {
        try
        {
            // 白名单投影：仅输出连接摘要字段，PasswordCipher 字段被结构性排除
            IReadOnlyList<DataSourceConfig> configs = _dataSourceManagement.List();
            List<DataSourceSummary> items = new();
            foreach (DataSourceConfig config in configs)
            {
                items.Add(new DataSourceSummary(
                    config.Name,
                    config.Type.ToString(),
                    config.Host,
                    config.Port,
                    config.Database,
                    config.UserId,
                    config.CreatedAt,
                    config.UpdatedAt));
            }

            _logger.LogInformation("list_datasources 完成，连接数 {DataSourceCount}。", items.Count);
            return Task.FromResult(new ListDataSourcesResult(items));
        }
        catch (Exception exception)
        {
            // 错误走 MCP 标准执行错误通道（isError=true）：结构化输出工具不再以成功载体混装错误对象
            McpException mapped = QueryToolExceptionMapper.Map(exception, cancellationToken);
            _logger.LogError(mapped, "list_datasources 执行失败。");
            throw mapped;
        }
    }
}

/// <summary>
/// 数据源连接摘要 DTO（安全白名单：不含密码明文与密文字段）。
/// </summary>
/// <param name="Name">连接名称，全局唯一。</param>
/// <param name="Type">数据库类型（MySql / PostgreSql）。</param>
/// <param name="Host">主机名或 IP。</param>
/// <param name="Port">端口。</param>
/// <param name="Database">数据库名。</param>
/// <param name="UserId">用户名。</param>
/// <param name="CreatedAt">创建时间。</param>
/// <param name="UpdatedAt">最近更新时间。</param>
public sealed record DataSourceSummary(
    string Name,
    string Type,
    string Host,
    int Port,
    string Database,
    string UserId,
    DateTime CreatedAt,
    DateTime UpdatedAt);

/// <summary>
/// list_datasources 应答 DTO。
/// </summary>
/// <param name="Datasources">数据源连接摘要列表。</param>
public sealed record ListDataSourcesResult(IReadOnlyList<DataSourceSummary> Datasources);

/// <summary>
/// 结构化查询工具（list_datasources / list_tables / get_table_detail / get_type_map）共享的错误通道映射器：
/// 将工具执行异常映射为 MCP 标准执行错误异常（SDK 转 CallToolResult(isError=true) 应答），
/// 消息文本沿用既有结构化错误的脱敏语义，保证错误可诊断且不携带敏感字段。
/// </summary>
internal static class QueryToolExceptionMapper
{
    /// <summary>
    /// 将工具执行异常映射为 MCP 执行错误异常；requestToken 为本次请求关联的取消令牌，
    /// 用于区分「调用方主动取消」与「连接超时兜底触发的链式取消」两类同形态异常。
    /// </summary>
    /// <param name="exception">工具执行中抛出的异常。</param>
    /// <param name="requestToken">请求关联取消令牌；未置位时收到取消异常即判内部超时。</param>
    /// <returns>携带脱敏消息的 MCP 执行错误异常。</returns>
    public static McpException Map(Exception exception, CancellationToken? requestToken = null) => exception switch
    {
        // 调用方未取消却收到取消异常：来源为连接的超时兜底（内部链式取消令牌触发），
        // 固定文案给出可诊断指引，与桌面端测试连接的超时反馈语义对齐
        OperationCanceledException when requestToken is null || !requestToken.Value.IsCancellationRequested =>
            new McpException("连接超时（10 秒兜底触发），请检查主机地址、端口与网络后重试。", exception),
        // 调用方主动取消：请求关联取消令牌已置位，保留取消语义文案
        OperationCanceledException => new McpException("操作已取消。", exception),
        // 其余异常消息经 Core 层脱敏后透出：参数校验与领域错误消息自解释，供调用方自纠
        _ => new McpException(exception.Message, exception)
    };
}

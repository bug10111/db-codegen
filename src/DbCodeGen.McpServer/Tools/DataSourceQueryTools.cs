using System.ComponentModel;
using DbCodeGen.Core.DataSource;
using DbCodeGen.Core.Model;
using Microsoft.Extensions.Logging;
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
    [McpServerTool(Name = "list_datasources", ReadOnly = true, Idempotent = true)]
    [Description("列出全部已保存的数据源连接（名称、类型、主机、端口、库、用户、时间戳）；密码与密文永不回显。")]
    public Task<object> ListDataSourcesAsync(CancellationToken cancellationToken)
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
            return Task.FromResult<object>(new ListDataSourcesResult(items));
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "list_datasources 执行失败。");
            return Task.FromResult<object>(ToolError.From(exception, cancellationToken));
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
internal sealed record DataSourceSummary(
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
internal sealed record ListDataSourcesResult(IReadOnlyList<DataSourceSummary> Datasources);

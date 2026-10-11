using System.ComponentModel;
using DbCodeGen.Core.DataSource;
using DbCodeGen.Core.Model;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace DbCodeGen.McpServer.Tools;

/// <summary>
/// 数据源连接管理工具组：新增、更新与删除已保存的数据源连接，
/// 全部经 Core 连接管理服务完成（字段校验、唯一名校验、DPAPI 加密与持久化在该服务收敛）。
/// 安全语义：密码明文仅作为入参瞬态传递给服务加密入库，任何应答与日志都不回显明文与密文。
/// </summary>
[McpServerToolType]
public sealed class DataSourceManageTools
{
    private readonly IDataSourceManagementService _dataSourceManagement;
    private readonly ILogger<DataSourceManageTools> _logger;

    /// <summary>
    /// 以数据源连接管理服务与日志器构造数据源管理工具组。
    /// </summary>
    /// <param name="dataSourceManagement">连接管理服务，连接增删改的唯一 Core 通道。</param>
    /// <param name="logger">工具组日志器，日志不得输出密码明文。</param>
    public DataSourceManageTools(
        IDataSourceManagementService dataSourceManagement,
        ILogger<DataSourceManageTools> logger)
    {
        _dataSourceManagement = dataSourceManagement;
        _logger = logger;
    }

    /// <summary>
    /// 新增数据源连接并落盘。密码明文经 DPAPI（CurrentUser 作用域）加密后入库，不传密码按空密码保存。
    /// </summary>
    /// <param name="name">连接名称，在连接列表内唯一（忽略大小写）。</param>
    /// <param name="type">数据库类型：mysql / postgresql。</param>
    /// <param name="host">主机名或 IP 地址。</param>
    /// <param name="port">端口号，取值 1-65535。</param>
    /// <param name="database">数据库名。</param>
    /// <param name="userId">用户名。</param>
    /// <param name="password">密码明文，仅瞬态用于加密入库，不回显；缺省表示空密码。</param>
    /// <param name="cancellationToken">取消标记。</param>
    /// <returns>成功携带连接摘要；失败携带结构化错误（InvalidArgument / NameConflict / SaveFailed）。</returns>
    [McpServerTool(Name = "add_datasource", Title = "新增数据源", Destructive = false)]
    [Description("新增数据源连接并保存（名称需唯一）。密码经 DPAPI 加密入库且不回显；先用 test_connection 直测参数可用，再以此工具保存。")]
    public Task<object> AddDataSourceAsync(
        string name,
        [Description("数据库类型：mysql / postgresql。")]
        string type,
        string host,
        int port,
        string database,
        string userId,
        string? password = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            // 新增场景不携带 OriginalName，由服务按新增语义处理唯一名校验与追加落盘
            DataSourceConnectionDraft draft = new(
                name,
                DataSourceTypeParser.Parse(type),
                host,
                port,
                database,
                userId,
                password);
            DataSourceOperationResult result = _dataSourceManagement.Save(draft);
            return Task.FromResult(MapOperationResult("add", result, name));
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "add_datasource 执行失败，连接名 {ConnectionName}。", name);
            return Task.FromResult<object>(ToolError.From(exception, cancellationToken));
        }
    }

    /// <summary>
    /// 按原名称定位并更新已保存的数据源连接：仅传需要修改的字段，未传字段保持原值；
    /// password 留空表示保持已保存密码（原密文不动）。
    /// </summary>
    /// <param name="originalName">被更新连接的原始名称（区分大小写精确匹配）。</param>
    /// <param name="name">新连接名称；缺省保持原名。</param>
    /// <param name="type">数据库类型（mysql / postgresql）；缺省保持原值。</param>
    /// <param name="host">主机名或 IP；缺省保持原值。</param>
    /// <param name="port">端口（1-65535）；缺省保持原值。</param>
    /// <param name="database">数据库名；缺省保持原值。</param>
    /// <param name="userId">用户名；缺省保持原值。</param>
    /// <param name="password">新密码明文；缺省或空白保持已保存密码。</param>
    /// <param name="cancellationToken">取消标记。</param>
    /// <returns>成功携带更新后的连接摘要；失败携带结构化错误（InvalidArgument / NameConflict / OriginalMissing / SaveFailed）。</returns>
    [McpServerTool(Name = "update_datasource", Title = "更新数据源", Destructive = false, Idempotent = true)]
    [Description("按 originalName 定位并更新已保存连接：只传需要修改的字段，未传字段保持原值；password 留空表示保持已保存密码。")]
    public Task<object> UpdateDataSourceAsync(
        string originalName,
        string? name = null,
        [Description("数据库类型（mysql / postgresql）；缺省保持原值。")]
        string? type = null,
        string? host = null,
        int? port = null,
        string? database = null,
        string? userId = null,
        string? password = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            // 编辑目标先按原名称定位，用既有连接的值补全未传字段，组装为完整保存草稿
            DataSourceConfig? existing = _dataSourceManagement.GetByName(originalName);
            if (existing is null)
            {
                throw BuildOriginalMissingError(originalName);
            }

            string targetName = string.IsNullOrWhiteSpace(name) ? existing.Name : name.Trim();
            DataSourceType targetType = type is null ? existing.Type : DataSourceTypeParser.Parse(type);
            string targetHost = string.IsNullOrWhiteSpace(host) ? existing.Host : host.Trim();
            int targetPort = port ?? existing.Port;
            string targetDatabase = string.IsNullOrWhiteSpace(database) ? existing.Database : database.Trim();
            string targetUserId = string.IsNullOrWhiteSpace(userId) ? existing.UserId : userId.Trim();

            // 密码留空（含空白）传 null，服务按“保持原密文”语义处理；输入了新值才重新加密覆盖
            string? targetPassword = string.IsNullOrWhiteSpace(password) ? null : password;

            DataSourceConnectionDraft draft = new(
                targetName,
                targetType,
                targetHost,
                targetPort,
                targetDatabase,
                targetUserId,
                targetPassword,
                originalName);
            DataSourceOperationResult result = _dataSourceManagement.Save(draft);
            return Task.FromResult(MapOperationResult("update", result, originalName));
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "update_datasource 执行失败，原连接名 {OriginalName}。", originalName);
            return Task.FromResult<object>(ToolError.From(exception, cancellationToken));
        }
    }

    /// <summary>
    /// 按名称删除已保存的数据源连接并落盘；目标不存在返回 NotFound 结构化错误。
    /// </summary>
    /// <param name="name">待删除连接的名称（区分大小写精确匹配）。</param>
    /// <param name="cancellationToken">取消标记。</param>
    /// <returns>成功携带被删除连接的摘要；失败携带结构化错误（NotFound / SaveFailed）。</returns>
    [McpServerTool(Name = "delete_datasource", Title = "删除数据源", Destructive = true)]
    [Description("按名称删除已保存的数据源连接（区分大小写精确匹配，不存在时返回 NotFound）。")]
    public Task<object> DeleteDataSourceAsync(string name, CancellationToken cancellationToken = default)
    {
        try
        {
            DataSourceOperationResult result = _dataSourceManagement.Delete(name);
            return Task.FromResult(MapOperationResult("delete", result, name));
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "delete_datasource 执行失败，连接名 {ConnectionName}。", name);
            return Task.FromResult<object>(ToolError.From(exception, cancellationToken));
        }
    }

    /// <summary>
    /// 将连接管理服务的操作结果映射为工具应答：成功投影为安全摘要 DTO，
    /// 预期内业务失败（校验/唯一名/目标缺失/落盘）映射为携带状态标识的结构化错误。
    /// </summary>
    /// <param name="operation">操作类型标识（add / update / delete），用于日志与应答 DTO。</param>
    /// <param name="result">连接管理服务返回的操作结果。</param>
    /// <param name="requestedName">调用方请求的连接名，用于失败日志定位。</param>
    /// <returns>成功应答 DTO 或结构化错误结果。</returns>
    private object MapOperationResult(string operation, DataSourceOperationResult result, string requestedName)
    {
        if (result.IsSuccess)
        {
            DataSourceConfig config = result.Config!;
            _logger.LogInformation(
                "{Operation}_datasource 完成，连接 {ConnectionName}。", operation, config.Name);
            return new DataSourceMutationResult(operation, ToSummary(config));
        }

        // 消息来自服务层可读文案且不含敏感字段；按状态归类供调用方程序化识别失败原因
        _logger.LogWarning(
            "{Operation}_datasource 未成功，连接 {ConnectionName}，状态 {Status}，原因：{Message}。",
            operation, requestedName, result.Status, result.Message);
        return new ToolErrorResult(ToolErrorResult.ErrorMarker, MapFailureType(result.Status), result.Message);
    }

    /// <summary>
    /// 将连接配置投影为安全摘要（白名单字段，不含密码明文与密文）。
    /// </summary>
    /// <param name="config">落盘后的连接配置。</param>
    /// <returns>连接摘要 DTO。</returns>
    private static DataSourceSummary ToSummary(DataSourceConfig config)
    {
        return new DataSourceSummary(
            config.Name,
            config.Type.ToString(),
            config.Host,
            config.Port,
            config.Database,
            config.UserId,
            config.CreatedAt,
            config.UpdatedAt);
    }

    /// <summary>
    /// 将连接管理失败状态映射为错误分类标识：字段校验失败对齐既有 InvalidArgument 档，
    /// 其余状态沿用服务枚举名，保持调用方可按标识区分失败原因。
    /// </summary>
    /// <param name="status">连接管理服务的失败状态。</param>
    /// <returns>错误分类标识文本。</returns>
    private static string MapFailureType(DataSourceOperationStatus status)
    {
        return status switch
        {
            DataSourceOperationStatus.Invalid => "InvalidArgument",
            DataSourceOperationStatus.NameConflict => "NameConflict",
            DataSourceOperationStatus.OriginalMissing => "OriginalMissing",
            DataSourceOperationStatus.NotFound => "NotFound",
            DataSourceOperationStatus.SaveFailed => "SaveFailed",
            _ => "InternalError"
        };
    }

    /// <summary>
    /// 构造“编辑目标连接不存在”的参数异常，附现有连接名清单辅助调用方自纠。
    /// </summary>
    /// <param name="originalName">调用方传入的原连接名。</param>
    /// <returns>携带可读消息的参数异常。</returns>
    private ArgumentException BuildOriginalMissingError(string originalName)
    {
        string known = string.Join(", ", _dataSourceManagement.List().Select(item => item.Name));
        string knownList = string.IsNullOrWhiteSpace(known) ? "（无）" : known;
        return new ArgumentException($"原连接“{originalName}”不存在。可用数据源：{knownList}。");
    }
}

/// <summary>
/// add / update / delete_datasource 成功应答 DTO。
/// </summary>
/// <param name="Operation">操作类型：add / update / delete。</param>
/// <param name="Datasource">落盘后的连接摘要（安全白名单：不含密码明文与密文）。</param>
internal sealed record DataSourceMutationResult(string Operation, DataSourceSummary Datasource);

/// <summary>
/// 数据库类型入参解析器：将工具入参的数据库类型文本解析为 Core 枚举，
/// 大小写不敏感，非法取值抛参数异常并列出合法值。
/// </summary>
internal static class DataSourceTypeParser
{
    /// <summary>
    /// 解析数据库类型文本。
    /// </summary>
    /// <param name="type">数据库类型文本，支持 mysql / postgresql / postgres。</param>
    /// <returns>解析后的数据库类型枚举。</returns>
    /// <exception cref="ArgumentException">取值不属于受支持数据库类型时抛出。</exception>
    public static DataSourceType Parse(string type)
    {
        return type.Trim().ToLowerInvariant() switch
        {
            "mysql" => DataSourceType.MySql,
            "postgresql" or "postgres" => DataSourceType.PostgreSql,
            _ => throw new ArgumentException($"无效的数据库类型：{type}；合法取值为 mysql / postgresql。")
        };
    }
}

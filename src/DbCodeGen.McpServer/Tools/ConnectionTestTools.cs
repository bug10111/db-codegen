using System.ComponentModel;
using DbCodeGen.Core.DataSource;
using DbCodeGen.Core.Model;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace DbCodeGen.McpServer.Tools;

/// <summary>
/// 连接测试工具：按已保存连接名测试连通性，或以完整明文参数直测（不入库）。
/// 复用 Core 连接服务（明文/密文二选一契约、连接串防注入、10 秒超时兜底、异常脱敏）；
/// 明文密码仅瞬态参与连接组装，不进入任何持久化、应答与日志。
/// </summary>
[McpServerToolType]
public sealed class ConnectionTestTools
{
    private readonly IDataSourceManagementService _dataSourceManagement;
    private readonly IDataSourceService _dataSourceService;
    private readonly ILogger<ConnectionTestTools> _logger;

    /// <summary>
    /// 以数据源连接管理服务、连接服务与日志器构造连接测试工具。
    /// </summary>
    /// <param name="dataSourceManagement">连接管理服务，按名称定位已保存连接。</param>
    /// <param name="dataSourceService">连接服务，承载连接串组装与超时兜底的连通性测试。</param>
    /// <param name="logger">工具日志器，日志不得输出密码明文。</param>
    public ConnectionTestTools(
        IDataSourceManagementService dataSourceManagement,
        IDataSourceService dataSourceService,
        ILogger<ConnectionTestTools> logger)
    {
        _dataSourceManagement = dataSourceManagement;
        _dataSourceService = dataSourceService;
        _logger = logger;
    }

    /// <summary>
    /// 测试数据库连接可用性（10 秒超时兜底，失败信息经 Core 层脱敏）。
    /// 两种模式二选一：传 datasource 按已保存连接测试（使用其 DPAPI 密文凭据）；
    /// 或传完整明文参数直测（type/host/port/database/userId 必填，password 可选），直测不落库不留痕。
    /// </summary>
    /// <param name="datasource">已保存的数据源连接名（可用 list_datasources 查询）；传入即按名测试模式。</param>
    /// <param name="type">直测模式数据库类型：mysql / postgresql。</param>
    /// <param name="host">直测模式主机名或 IP。</param>
    /// <param name="port">直测模式端口，取值 1-65535。</param>
    /// <param name="database">直测模式数据库名。</param>
    /// <param name="userId">直测模式用户名。</param>
    /// <param name="password">直测模式密码明文，仅瞬态用于本次连接，不入库不回显；缺省按空密码尝试。</param>
    /// <param name="cancellationToken">取消标记。</param>
    /// <returns>连接测试结果（成功与否、可读信息、服务端版本、耗时毫秒）。</returns>
    [McpServerTool(Name = "test_connection", Title = "测试连接", ReadOnly = true, Idempotent = true, Destructive = false)]
    [Description("测试数据库连接可用性（10 秒超时）。两模式二选一：传 datasource 按已保存连接测试；或传 type/host/port/database/userId（password 可选）完整明文直测——不入库、不保存任何信息。")]
    public async Task<object> TestConnectionAsync(
        string? datasource = null,
        [Description("直测模式数据库类型：mysql / postgresql；传 datasource 按已保存连接测试时忽略本参数。")]
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
            TestConnectionInput input = BuildTestInput(datasource, type, host, port, database, userId, password);

            // 直测模式与按名模式在应答中显式区分，便于调用方确认明文参数未被保存
            bool byNameMode = !string.IsNullOrWhiteSpace(datasource);
            string mode = byNameMode ? "saved" : "direct";
            string? connectionName = byNameMode ? datasource!.Trim() : null;

            TestConnectionResult result = await _dataSourceService.TestConnectionAsync(input, cancellationToken);

            long elapsedMilliseconds = (long)result.Elapsed.TotalMilliseconds;
            _logger.LogInformation(
                "test_connection 完成，模式 {Mode}，连接 {ConnectionName}，成功 {IsSuccess}，耗时 {ElapsedMilliseconds} 毫秒。",
                mode, connectionName, result.IsSuccess, elapsedMilliseconds);
            return new ConnectionTestOutcome(
                mode,
                connectionName,
                result.IsSuccess,
                result.Message,
                result.ServerVersion,
                elapsedMilliseconds);
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "test_connection 执行失败。");
            return ToolError.From(exception, cancellationToken);
        }
    }

    /// <summary>
    /// 按模式分发组装测试连接输入：datasource 非空走已保存连接（密文契约），
    /// 否则走完整明文参数直测（不入库）。
    /// </summary>
    /// <param name="datasource">已保存连接名；空白表示直测模式。</param>
    /// <param name="type">直测数据库类型文本。</param>
    /// <param name="host">直测主机。</param>
    /// <param name="port">直测端口。</param>
    /// <param name="database">直测数据库名。</param>
    /// <param name="userId">直测用户名。</param>
    /// <param name="password">直测密码明文。</param>
    /// <returns>组装完成的测试连接输入。</returns>
    private TestConnectionInput BuildTestInput(
        string? datasource,
        string? type,
        string? host,
        int? port,
        string? database,
        string? userId,
        string? password)
    {
        if (!string.IsNullOrWhiteSpace(datasource))
        {
            return BuildSavedConnectionInput(datasource, type, host, port, database, userId, password);
        }

        return BuildDirectConnectionInput(type, host, port, database, userId, password);
    }

    /// <summary>
    /// 组装按名测试输入：定位已保存连接并以密文契约传递凭据（解密在 Core 连接服务内部完成）。
    /// </summary>
    /// <param name="datasource">已保存连接名。</param>
    /// <param name="type">直测参数，按名模式下必须缺省。</param>
    /// <param name="host">直测参数，按名模式下必须缺省。</param>
    /// <param name="port">直测参数，按名模式下必须缺省。</param>
    /// <param name="database">直测参数，按名模式下必须缺省。</param>
    /// <param name="userId">直测参数，按名模式下必须缺省。</param>
    /// <param name="password">直测参数，按名模式下必须缺省。</param>
    /// <returns>按名模式的测试连接输入。</returns>
    private TestConnectionInput BuildSavedConnectionInput(
        string datasource,
        string? type,
        string? host,
        int? port,
        string? database,
        string? userId,
        string? password)
    {
        // 两模式互斥：按名测试只使用已保存凭据，混入明文参数直接拒绝，避免调用方误以为明文参数会生效
        bool hasDirectParam = type is not null || host is not null || port is not null
            || database is not null || userId is not null || password is not null;
        if (hasDirectParam)
        {
            throw new ArgumentException(
                "datasource 与明文直测参数（type/host/port/database/userId/password）互斥，二选一。");
        }

        DataSourceConfig? config = _dataSourceManagement.GetByName(datasource);
        if (config is null)
        {
            throw BuildUnknownDatasourceError(datasource);
        }

        // 密文契约：空密文传 null（Core 按“无已存密码→空密码尝试”处理），非空传密文由服务内部解密
        return new TestConnectionInput
        {
            Type = config.Type,
            Host = config.Host,
            Port = config.Port,
            Database = config.Database,
            UserId = config.UserId,
            SavedPasswordCipher = string.IsNullOrEmpty(config.PasswordCipher) ? null : config.PasswordCipher
        };
    }

    /// <summary>
    /// 组装明文直测输入：完整连接参数必填校验后以明文契约传递（不入库）。
    /// </summary>
    /// <param name="type">数据库类型文本。</param>
    /// <param name="host">主机名或 IP。</param>
    /// <param name="port">端口。</param>
    /// <param name="database">数据库名。</param>
    /// <param name="userId">用户名。</param>
    /// <param name="password">密码明文，缺省按空密码尝试。</param>
    /// <returns>直测模式的测试连接输入。</returns>
    private static TestConnectionInput BuildDirectConnectionInput(
        string? type,
        string? host,
        int? port,
        string? database,
        string? userId,
        string? password)
    {
        // 完整参数必填：任一缺失即结构化拒绝并列出缺失项，提示可改用按名模式
        List<string> missing = new();
        if (string.IsNullOrWhiteSpace(type))
        {
            missing.Add("type");
        }

        if (string.IsNullOrWhiteSpace(host))
        {
            missing.Add("host");
        }

        if (string.IsNullOrWhiteSpace(database))
        {
            missing.Add("database");
        }

        if (string.IsNullOrWhiteSpace(userId))
        {
            missing.Add("userId");
        }

        if (missing.Count > 0)
        {
            throw new ArgumentException(
                $"明文直测模式缺少必填参数：{string.Join(" / ", missing)}；或改传 datasource 按已保存连接测试。");
        }

        // 端口必填经模式匹配收口可空性，越界提前拒绝，不进入连接串组装
        if (port is not int portValue)
        {
            throw new ArgumentException("明文直测模式缺少必填参数：port；或改传 datasource 按已保存连接测试。");
        }

        if (portValue is < 1 or > 65535)
        {
            throw new ArgumentException("端口必须在 1-65535 范围内。");
        }

        return new TestConnectionInput
        {
            Type = DataSourceTypeParser.Parse(type!),
            Host = host!.Trim(),
            Port = portValue,
            Database = database!.Trim(),
            UserId = userId!.Trim(),
            PlainPassword = password
        };
    }

    /// <summary>
    /// 构造“数据源不存在”的参数异常，附现有连接名清单辅助调用方自纠。
    /// </summary>
    /// <param name="datasource">调用方传入的连接名。</param>
    /// <returns>携带可读消息的参数异常。</returns>
    private ArgumentException BuildUnknownDatasourceError(string datasource)
    {
        string known = string.Join(", ", _dataSourceManagement.List().Select(item => item.Name));
        string knownList = string.IsNullOrWhiteSpace(known) ? "（无）" : known;
        return new ArgumentException($"数据源不存在：{datasource}。可用数据源：{knownList}。");
    }
}

/// <summary>
/// test_connection 应答 DTO。
/// </summary>
/// <param name="Mode">测试模式：saved=按已保存连接名测试；direct=完整明文参数直测（不入库）。</param>
/// <param name="Datasource">按名测试的连接名；直测模式为 null。</param>
/// <param name="Success">连接是否成功。</param>
/// <param name="Message">成功或失败的可读信息，失败原因经 Core 层脱敏，不含密码。</param>
/// <param name="ServerVersion">服务端版本，连接成功时可能有值。</param>
/// <param name="ElapsedMilliseconds">连接耗时（毫秒）。</param>
internal sealed record ConnectionTestOutcome(
    string Mode,
    string? Datasource,
    bool Success,
    string Message,
    string? ServerVersion,
    long ElapsedMilliseconds);

using System.Data.Common;
using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using DbCodeGen.Core.Model;
using DbCodeGen.Core.Security;
using Microsoft.Extensions.Logging;
using MySqlConnector;
using Npgsql;

namespace DbCodeGen.Core.DataSource;

/// <summary>
/// 数据源连接服务实现，承载连接串组装、密码解析、测试连接与建立连接。
/// 明文密码只在解析与连接串构建的瞬态存在，任何日志与错误信息均在书写点脱敏。
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class DataSourceService : IDataSourceService
{
    /// <summary>
    /// 连接建立超时秒数，测试连接与建立连接统一按 10 秒兜底。
    /// </summary>
    private const int ConnectionTimeoutSeconds = 10;

    /// <summary>
    /// 掩码后的密码占位，用于连接串与日志的脱敏展示。
    /// </summary>
    private const string MaskedPasswordValue = "*****";

    /// <summary>
    /// 匹配连接串中 Password 键值段的表达式，兼容引号包裹的值且只认段首关键字，用于脱敏输出。
    /// </summary>
    private static readonly Regex PasswordSegmentRegex = new(
        @"(^|;)\s*Password\s*=\s*(?:""(?:[^""]|"""")*""|'[^']*'|[^;]*)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly CredentialProtector _credentialProtector;
    private readonly ILogger<DataSourceService> _logger;

    /// <summary>
    /// 以密码保护器与日志器构造连接服务。
    /// </summary>
    /// <param name="credentialProtector">DPAPI 密码保护器，用于解密已保存的密码密文。</param>
    /// <param name="logger">连接服务日志器，脱敏在日志书写点执行。</param>
    public DataSourceService(CredentialProtector credentialProtector, ILogger<DataSourceService> logger)
    {
        _credentialProtector = credentialProtector ?? throw new ArgumentNullException(nameof(credentialProtector));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public string BuildConnectionString(DataSourceConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        // 端口非法直接抛出，不进入连接串组装，保证调用方在源头收到明确错误
        ValidatePort(config.Port);

        string password = DecryptStoredPassword(config.PasswordCipher);
        return BuildConnectionStringCore(config.Type, config.Host, config.Port, config.Database, config.UserId, password);
    }

    /// <inheritdoc />
    public async Task<TestConnectionResult> TestConnectionAsync(TestConnectionInput input, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);

        // 端口校验前置，非法端口直接返回可读错误，不进入连接串组装
        if (!IsValidPort(input.Port))
        {
            return new TestConnectionResult { IsSuccess = false, Message = "端口必须在 1-65535 范围内。" };
        }

        Stopwatch stopwatch = Stopwatch.StartNew();
        string password = string.Empty;
        try
        {
            // 按明文/密文二选一契约解析本次测试使用的密码
            password = ResolvePassword(input);
            string connectionString = BuildConnectionStringCore(
                input.Type, input.Host, input.Port, input.Database, input.UserId, password);
            _logger.LogInformation(
                "开始测试连接，数据库类型 {DatabaseType}，主机 {Host}，端口 {Port}，数据库名 {Database}。",
                input.Type, input.Host, input.Port, input.Database);

            DbConnection connection = await OpenWithTimeoutAsync(input.Type, connectionString, ct).ConfigureAwait(false);
            await using (connection)
            {
                string serverVersion = connection.ServerVersion;
                stopwatch.Stop();
                _logger.LogInformation(
                    "测试连接成功，数据库类型 {DatabaseType}，主机 {Host}，服务端版本 {ServerVersion}，耗时 {ElapsedMilliseconds} 毫秒。",
                    input.Type, input.Host, serverVersion, stopwatch.ElapsedMilliseconds);

                return new TestConnectionResult
                {
                    IsSuccess = true,
                    Message = "连接成功。",
                    ServerVersion = serverVersion,
                    Elapsed = stopwatch.Elapsed
                };
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // 取消来源为本服务的 10 秒超时兜底，向用户反馈连接超时
            stopwatch.Stop();
            _logger.LogWarning(
                "测试连接超时，数据库类型 {DatabaseType}，主机 {Host}，端口 {Port}。",
                input.Type, input.Host, input.Port);
            return CreateFailureResult("连接超时，请检查主机地址、端口与网络后重试。", stopwatch.Elapsed);
        }
        catch (OperationCanceledException)
        {
            stopwatch.Stop();
            return CreateFailureResult("操作已取消。", stopwatch.Elapsed);
        }
        catch (TimeoutException)
        {
            stopwatch.Stop();
            _logger.LogWarning(
                "测试连接超时，数据库类型 {DatabaseType}，主机 {Host}，端口 {Port}。",
                input.Type, input.Host, input.Port);
            return CreateFailureResult("连接超时，请检查主机地址、端口与网络后重试。", stopwatch.Elapsed);
        }
        catch (Exception exception)
        {
            stopwatch.Stop();
            string sanitizedMessage = SanitizeExceptionMessage(exception, password);
            _logger.LogWarning(
                "测试连接失败：{Message}，数据库类型 {DatabaseType}，主机 {Host}，端口 {Port}。",
                sanitizedMessage, input.Type, input.Host, input.Port);
            return CreateFailureResult($"连接失败：{sanitizedMessage}", stopwatch.Elapsed);
        }
    }

    /// <inheritdoc />
    public async Task<DbConnection> OpenConnectionAsync(DataSourceConfig config, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(config);

        // 端口非法直接抛出，已保存配置不应出现越界端口
        ValidatePort(config.Port);

        string password = DecryptStoredPassword(config.PasswordCipher);
        string connectionString = BuildConnectionStringCore(
            config.Type, config.Host, config.Port, config.Database, config.UserId, password);
        _logger.LogDebug(
            "建立数据库连接，数据库类型 {DatabaseType}，主机 {Host}，端口 {Port}，数据库名 {Database}。",
            config.Type, config.Host, config.Port, config.Database);

        return await OpenWithTimeoutAsync(config.Type, connectionString, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 按明文/密文二选一契约解析测试连接的密码：明文优先，其次解密已保存密文，二者皆空按空密码处理。
    /// </summary>
    /// <param name="input">测试连接输入。</param>
    /// <returns>本次测试使用的明文字符串，调用方用完即弃。</returns>
    internal string ResolvePassword(TestConnectionInput input)
    {
        // 表单输入了新密码时优先使用明文，未输入则尝试解密已保存的密文
        if (input.PlainPassword is not null)
        {
            return input.PlainPassword;
        }

        return input.SavedPasswordCipher is null
            ? string.Empty
            : _credentialProtector.Decrypt(input.SavedPasswordCipher);
    }

    /// <summary>
    /// 将连接串中的密码键值段替换为掩码，用于日志与错误信息脱敏。
    /// </summary>
    /// <param name="connectionString">原始连接串，可能含明文密码段。</param>
    /// <returns>密码段已掩码的连接串，不包含明文密码。</returns>
    internal static string MaskPassword(string connectionString)
    {
        // 每个 Password 键值段整体替换为掩码占位，保留段前分隔符与键名
        return PasswordSegmentRegex.Replace(
            connectionString, match => match.Groups[1].Value + "Password=" + MaskedPasswordValue);
    }

    /// <summary>
    /// 解密数据源配置中的密码密文，空密文按空密码处理。
    /// </summary>
    /// <param name="passwordCipher">DPAPI 加密后的 Base64 密文，可为空串。</param>
    /// <returns>解密后的明文密码，用完即弃。</returns>
    private string DecryptStoredPassword(string passwordCipher)
    {
        return string.IsNullOrEmpty(passwordCipher) ? string.Empty : _credentialProtector.Decrypt(passwordCipher);
    }

    /// <summary>
    /// 创建连接并打开，叠加 10 秒超时兜底；打开失败时释放连接再抛出，防止连接泄漏。
    /// </summary>
    /// <param name="type">数据库类型。</param>
    /// <param name="connectionString">已组装好的连接串。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>已打开的数据库连接。</returns>
    private async Task<DbConnection> OpenWithTimeoutAsync(DataSourceType type, string connectionString, CancellationToken ct)
    {
        DbConnection connection = CreateConnection(type, connectionString);
        try
        {
            using CancellationTokenSource timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(ConnectionTimeoutSeconds));
            await connection.OpenAsync(timeoutCts.Token).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// 按数据库类型创建对应驱动的数据库连接实例。
    /// </summary>
    /// <param name="type">数据库类型。</param>
    /// <param name="connectionString">连接串。</param>
    /// <returns>对应驱动的数据库连接实例。</returns>
    private static DbConnection CreateConnection(DataSourceType type, string connectionString)
    {
        return type switch
        {
            DataSourceType.MySql => new MySqlConnection(connectionString),
            DataSourceType.PostgreSql => new NpgsqlConnection(connectionString),
            _ => throw new NotSupportedException($"不支持的数据库类型：{type}。")
        };
    }

    /// <summary>
    /// 依据数据库类型与各连接要素组装连接串，密码经驱动连接串构建器写入，规避连接串注入。
    /// </summary>
    /// <param name="type">数据库类型。</param>
    /// <param name="host">主机名或 IP 地址。</param>
    /// <param name="port">端口号。</param>
    /// <param name="database">数据库名。</param>
    /// <param name="userId">用户名。</param>
    /// <param name="password">明文密码，仅本方法内存周期使用。</param>
    /// <returns>组装完成的连接串。</returns>
    private static string BuildConnectionStringCore(
        DataSourceType type, string host, int port, string database, string userId, string password)
    {
        switch (type)
        {
            case DataSourceType.MySql:
                // MySql 连接串构建器统一键名与转义，密码含特殊字符也按值原样承载
                return new MySqlConnectionStringBuilder
                {
                    Server = host,
                    Port = (uint)port,
                    Database = database,
                    UserID = userId,
                    Password = password,
                    ConnectionTimeout = ConnectionTimeoutSeconds
                }.ConnectionString;

            case DataSourceType.PostgreSql:
                // Npgsql 连接串构建器与 MySql 一致，密码段交由构建器转义
                return new NpgsqlConnectionStringBuilder
                {
                    Host = host,
                    Port = port,
                    Database = database,
                    Username = userId,
                    Password = password,
                    Timeout = ConnectionTimeoutSeconds
                }.ConnectionString;

            default:
                throw new NotSupportedException($"不支持的数据库类型：{type}。");
        }
    }

    /// <summary>
    /// 校验端口是否在合法范围内。
    /// </summary>
    /// <param name="port">端口号。</param>
    /// <returns>合法返回 true，否则返回 false。</returns>
    private static bool IsValidPort(int port) => port is >= 1 and <= 65535;

    /// <summary>
    /// 端口非法时抛出参数越界异常，用于建立连接的前置校验。
    /// </summary>
    /// <param name="port">端口号。</param>
    /// <exception cref="ArgumentOutOfRangeException">端口不在 1-65535 范围时抛出。</exception>
    private static void ValidatePort(int port)
    {
        if (!IsValidPort(port))
        {
            throw new ArgumentOutOfRangeException(nameof(port), port, "端口必须在 1-65535 范围内。");
        }
    }

    /// <summary>
    /// 构造失败结果并携带耗时，供测试连接的各类失败分支复用。
    /// </summary>
    /// <param name="message">可读的失败信息。</param>
    /// <param name="elapsed">已耗时。</param>
    /// <returns>失败态测试连接结果。</returns>
    private static TestConnectionResult CreateFailureResult(string message, TimeSpan elapsed)
    {
        return new TestConnectionResult { IsSuccess = false, Message = message, Elapsed = elapsed };
    }

    /// <summary>
    /// 对异常信息脱敏：抹去明文密码与连接串密码段，防止凭据随错误信息泄漏。
    /// </summary>
    /// <param name="exception">原始异常。</param>
    /// <param name="password">本次解析出的明文密码，用于从信息中抹除。</param>
    /// <returns>脱敏后的异常信息。</returns>
    private static string SanitizeExceptionMessage(Exception exception, string password)
    {
        string message = exception.Message;
        if (!string.IsNullOrEmpty(password))
        {
            message = message.Replace(password, MaskedPasswordValue, StringComparison.Ordinal);
        }

        return MaskPassword(message);
    }
}

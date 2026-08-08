using System.Data.Common;
using System.Diagnostics;
using System.Runtime.Versioning;
using DbCodeGen.Core.Model;
using Microsoft.Extensions.Logging;
using MySqlConnector;
using Npgsql;

namespace DbCodeGen.Core.DataSource;

/// <summary>
/// SQL 执行服务，复用数据源连接能力对已配置数据源执行单条 SQL，统一承载查询结果集读取与影响行数获取。
/// 日志与错误信息在书写点脱敏：不输出密码、连接串明文与被执行的 SQL 文本。
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class SqlExecutor
{
    private readonly IDataSourceService _dataSourceService;
    private readonly ILogger<SqlExecutor> _logger;

    /// <summary>
    /// 以连接服务与日志器构造 SQL 执行服务。
    /// </summary>
    /// <param name="dataSourceService">数据源连接服务，提供 OpenConnectionAsync 复用连接能力。</param>
    /// <param name="logger">执行服务日志器，脱敏在日志书写点执行。</param>
    public SqlExecutor(IDataSourceService dataSourceService, ILogger<SqlExecutor> logger)
    {
        _dataSourceService = dataSourceService ?? throw new ArgumentNullException(nameof(dataSourceService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// 对指定数据源执行单条 SQL，按结果集字段数分流查询分支与执行分支。
    /// 查询分支读取列定义与结果行并受 MaxResultRows 截断；执行分支先关闭 reader 再取 RecordsAffected。
    /// </summary>
    /// <param name="config">数据源连接配置，密码密文由连接服务内部解密。</param>
    /// <param name="sql">单条 SQL 语句文本。</param>
    /// <param name="options">执行参数，为空时采用默认值。</param>
    /// <param name="ct">取消令牌，贯穿连接建立与结果读取全程。</param>
    /// <returns>SQL 执行结果，含查询结果集或影响行数以及执行耗时。</returns>
    /// <exception cref="ArgumentNullException">config 为 null 时抛出。</exception>
    /// <exception cref="ArgumentException">sql 为空或纯空白时抛出。</exception>
    public async Task<SqlExecutionResult> ExecuteAsync(
        DataSourceConfig config, string sql, SqlExecutionOptions? options, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentException.ThrowIfNullOrWhiteSpace(sql);

        // 空参数按默认执行参数处理，超时与行上限做防御性钳制防止越界
        SqlExecutionOptions effectiveOptions = options ?? new SqlExecutionOptions();
        int timeoutSeconds = Math.Max(0, effectiveOptions.TimeoutSeconds);
        int maxResultRows = Math.Max(1, effectiveOptions.MaxResultRows);

        Stopwatch stopwatch = Stopwatch.StartNew();
        try
        {
            _logger.LogInformation(
                "开始执行 SQL，数据库类型 {DatabaseType}，主机 {Host}，端口 {Port}，数据库名 {Database}。",
                config.Type, config.Host, config.Port, config.Database);

            // 复用数据源连接能力建立连接，生命周期由 await using 保证释放
            await using DbConnection connection =
                await _dataSourceService.OpenConnectionAsync(config, ct).ConfigureAwait(false);

            DbCommand command = connection.CreateCommand();
            await using (command)
            {
                command.CommandText = sql;
                command.CommandTimeout = timeoutSeconds;

                await using DbDataReader reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);

                // 查询分支：有结果列定义则按查询结果集读取
                if (reader.FieldCount > 0)
                {
                    SqlExecutionResult queryResult =
                        await ReadQueryResultAsync(reader, maxResultRows, ct).ConfigureAwait(false);
                    stopwatch.Stop();
                    queryResult.Duration = stopwatch.Elapsed;
                    _logger.LogInformation(
                        "SQL 执行成功（查询），返回 {RowCount} 行，耗时 {ElapsedMilliseconds} 毫秒，是否截断 {Truncated}。",
                        queryResult.Rows.Count, stopwatch.ElapsedMilliseconds, queryResult.Truncated);
                    return queryResult;
                }

                // 执行分支：先关闭 reader 使受影响行数完整落定，再读取 RecordsAffected
                await reader.CloseAsync().ConfigureAwait(false);
                long affectedRows = reader.RecordsAffected;

                stopwatch.Stop();
                _logger.LogInformation(
                    "SQL 执行成功（执行），影响行数 {AffectedRows}，耗时 {ElapsedMilliseconds} 毫秒。",
                    affectedRows, stopwatch.ElapsedMilliseconds);

                return new SqlExecutionResult
                {
                    Success = true,
                    AffectedRows = affectedRows,
                    Duration = stopwatch.Elapsed
                };
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // 取消令牌主动取消，向调用方反馈已取消
            stopwatch.Stop();
            _logger.LogWarning("SQL 执行已取消，耗时 {ElapsedMilliseconds} 毫秒。", stopwatch.ElapsedMilliseconds);
            return CreateFailureResult("操作已取消。", null, stopwatch.Elapsed);
        }
        catch (OperationCanceledException)
        {
            // 非调用方取消的取消异常，多为命令超时内部触发，向用户反馈执行超时
            stopwatch.Stop();
            _logger.LogWarning("SQL 执行超时，耗时 {ElapsedMilliseconds} 毫秒。", stopwatch.ElapsedMilliseconds);
            return CreateFailureResult("执行超时，请检查 SQL 复杂度或适当增大超时时间。", null, stopwatch.Elapsed);
        }
        catch (TimeoutException)
        {
            stopwatch.Stop();
            _logger.LogWarning("SQL 执行超时，耗时 {ElapsedMilliseconds} 毫秒。", stopwatch.ElapsedMilliseconds);
            return CreateFailureResult("执行超时，请检查 SQL 复杂度或适当增大超时时间。", null, stopwatch.Elapsed);
        }
        catch (MySqlException exception)
        {
            // MySql 驱动异常按错误号映射错误码，消息经脱敏后返回
            stopwatch.Stop();
            string sanitized = DataSourceService.MaskPassword(exception.Message);
            _logger.LogWarning(
                "SQL 执行失败，数据库类型 {DatabaseType}，错误码 {ErrorCode}，错误信息 {Message}。",
                config.Type, exception.Number, sanitized);
            return CreateFailureResult(sanitized, exception.Number.ToString(), stopwatch.Elapsed);
        }
        catch (PostgresException exception)
        {
            // PostgreSQL 服务端错误按 SqlState 映射错误码，消息经脱敏后返回
            stopwatch.Stop();
            string sanitized = DataSourceService.MaskPassword(exception.Message);
            _logger.LogWarning(
                "SQL 执行失败，数据库类型 {DatabaseType}，错误码 {SqlState}，错误信息 {Message}。",
                config.Type, exception.SqlState, sanitized);
            return CreateFailureResult(sanitized, exception.SqlState, stopwatch.Elapsed);
        }
        catch (NpgsqlException exception)
        {
            // Npgsql 10 的 NpgsqlException 不继承 DbException，需单独捕获以覆盖连接与协议层错误
            stopwatch.Stop();
            string sanitized = DataSourceService.MaskPassword(exception.Message);
            _logger.LogWarning(
                "SQL 执行失败，数据库类型 {DatabaseType}，错误信息 {Message}。",
                config.Type, sanitized);
            return CreateFailureResult(sanitized, null, stopwatch.Elapsed);
        }
        catch (Exception exception)
        {
            // 其它未知异常同样脱敏后返回，保证调用方拿到结构化失败结果
            stopwatch.Stop();
            string sanitized = DataSourceService.MaskPassword(exception.Message);
            _logger.LogWarning(
                "SQL 执行失败，数据库类型 {DatabaseType}，错误信息 {Message}。",
                config.Type, sanitized);
            return CreateFailureResult(sanitized, null, stopwatch.Elapsed);
        }
    }

    /// <summary>
    /// 读取查询结果集：先读取列定义，再循环读取结果行直至读尽或达到行数上限。
    /// </summary>
    /// <param name="reader">已执行查询的结果集读取器。</param>
    /// <param name="maxResultRows">结果行读取上限，超出即截断。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>含列定义、结果行与截断标记的查询结果。</returns>
    private static async Task<SqlExecutionResult> ReadQueryResultAsync(
        DbDataReader reader, int maxResultRows, CancellationToken ct)
    {
        // 读取列名与显示类型，构建结果表格表头
        List<SqlColumnInfo> columns = new(reader.FieldCount);
        for (int index = 0; index < reader.FieldCount; index++)
        {
            columns.Add(new SqlColumnInfo
            {
                Name = reader.GetName(index),
                DisplayType = SimplifyTypeName(reader.GetFieldType(index))
            });
        }

        // 循环读取结果行，行数达到上限后探测一次溢出置截断标记并停止
        List<IReadOnlyList<object?>> rows = new();
        bool truncated = false;
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            if (rows.Count >= maxResultRows)
            {
                truncated = true;
                break;
            }

            object?[] row = new object?[reader.FieldCount];
            for (int index = 0; index < reader.FieldCount; index++)
            {
                row[index] = await reader.IsDBNullAsync(index, ct).ConfigureAwait(false)
                    ? null
                    : reader.GetValue(index);
            }
            rows.Add(row);
        }

        return new SqlExecutionResult
        {
            Success = true,
            Columns = columns,
            Rows = rows,
            Truncated = truncated
        };
    }

    /// <summary>
    /// 将数据库列类型简化为可读名称，一维数组类型统一为小写元素名加方括号。
    /// </summary>
    /// <param name="type">列值类型。</param>
    /// <returns>简化的类型名称。</returns>
    private static string SimplifyTypeName(Type type)
    {
        // 一维数组取元素类型名小写加方括号，其余直接用类型短名称
        if (type.IsArray && type.GetArrayRank() == 1)
        {
            Type? elementType = type.GetElementType();
            return elementType is null ? type.Name : $"{elementType.Name.ToLowerInvariant()}[]";
        }

        return type.Name;
    }

    /// <summary>
    /// 构造失败结果并携带耗时，供各类异常分支复用。
    /// </summary>
    /// <param name="errorMessage">脱敏后的错误描述。</param>
    /// <param name="errorCode">数据库错误码，无则传 null。</param>
    /// <param name="duration">已耗时。</param>
    /// <returns>失败态 SQL 执行结果。</returns>
    private static SqlExecutionResult CreateFailureResult(string errorMessage, string? errorCode, TimeSpan duration)
    {
        return new SqlExecutionResult
        {
            Success = false,
            ErrorCode = errorCode,
            ErrorMessage = errorMessage,
            Duration = duration
        };
    }
}

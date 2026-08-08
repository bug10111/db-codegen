using System.Collections.Concurrent;
using System.Data.Common;
using DbCodeGen.Core.Model;
using Microsoft.Extensions.Logging;

namespace DbCodeGen.Core.DataSource;

/// <summary>
/// 表元数据读取编排服务，复用 01 连接服务打开连接，经方言读取器读取表清单与表详情。
/// 表详情按（连接名, 表名）内存缓存，刷新表或当前连接变更时调用 ClearCache 失效缓存。
/// 服务为单例注入，缓存使用并发字典保证线程安全，不持有其它共享可变状态。
/// </summary>
public sealed class TableCatalogService
{
    /// <summary>
    /// 缓存键中连接名与表名的分隔符，规避两段拼接歧义。
    /// </summary>
    private const string CacheKeySeparator = ":";

    private readonly IDataSourceService _dataSourceService;
    private readonly ISchemaReaderFactory _schemaReaderFactory;
    private readonly ILogger<TableCatalogService> _logger;

    /// <summary>
    /// 表详情缓存，键为（连接名:表名），值为含完整列元数据的表实体。
    /// </summary>
    private readonly ConcurrentDictionary<string, TableInfo> _detailCache = new();

    /// <summary>
    /// 以连接服务、读取器工厂与日志器构造表元数据编排服务。
    /// </summary>
    /// <param name="dataSourceService">01 连接服务，负责打开数据库连接。</param>
    /// <param name="schemaReaderFactory">方言读取器工厂，按数据库类型创建读取器。</param>
    /// <param name="logger">编排服务日志器，日志不输出密码与连接串。</param>
    public TableCatalogService(
        IDataSourceService dataSourceService,
        ISchemaReaderFactory schemaReaderFactory,
        ILogger<TableCatalogService> logger)
    {
        _dataSourceService = dataSourceService ?? throw new ArgumentNullException(nameof(dataSourceService));
        _schemaReaderFactory = schemaReaderFactory ?? throw new ArgumentNullException(nameof(schemaReaderFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// 读取表清单，经 01 连接服务打开连接后按方言查询 information_schema，返回的表不含列元数据。
    /// 连接生命周期随读取器 Dispose 释放，全程贯穿取消令牌。
    /// </summary>
    /// <param name="config">数据源连接配置。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>表清单，默认按表名排序。</returns>
    /// <exception cref="ArgumentNullException">config 为 null 时抛出。</exception>
    public async Task<IReadOnlyList<TableInfo>> GetTablesAsync(DataSourceConfig config, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(config);

        await using DbConnection connection = await _dataSourceService.OpenConnectionAsync(config, ct).ConfigureAwait(false);
        using ISchemaReader reader = _schemaReaderFactory.Create(config.Type, connection);
        IReadOnlyList<TableInfo> tables = await reader.GetTablesAsync(ct).ConfigureAwait(false);
        _logger.LogInformation(
            "读取表清单完成，连接名 {ConnectionName}，表数量 {TableCount}。", config.Name, tables.Count);
        return tables;
    }

    /// <summary>
    /// 读取单张表完整列元数据，按（连接名, 表名）命中缓存时直接返回，未命中时打开连接惰性读取并写入缓存。
    /// </summary>
    /// <param name="config">数据源连接配置。</param>
    /// <param name="tableName">目标表名。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>含完整列元数据的表实体。</returns>
    /// <exception cref="ArgumentNullException">config 为 null 时抛出。</exception>
    /// <exception cref="ArgumentException">tableName 为空或空白时抛出。</exception>
    public async Task<TableInfo> GetTableDetailAsync(DataSourceConfig config, string tableName, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentException.ThrowIfNullOrWhiteSpace(tableName);

        string cacheKey = BuildCacheKey(config.Name, tableName);
        if (_detailCache.TryGetValue(cacheKey, out TableInfo? cached))
        {
            return cached;
        }

        await using DbConnection connection = await _dataSourceService.OpenConnectionAsync(config, ct).ConfigureAwait(false);
        using ISchemaReader reader = _schemaReaderFactory.Create(config.Type, connection);
        TableInfo detail = await reader.GetTableAsync(tableName, ct).ConfigureAwait(false);
        _detailCache[cacheKey] = detail;
        _logger.LogInformation(
            "读取表详情完成，连接名 {ConnectionName}，表名 {TableName}，列数量 {ColumnCount}。",
            config.Name, tableName, detail.Columns.Count);
        return detail;
    }

    /// <summary>
    /// 清空表详情缓存，供刷新表与当前连接变更时调用，防陈旧列元数据。
    /// </summary>
    public void ClearCache()
    {
        _detailCache.Clear();
        _logger.LogDebug("表详情缓存已清空。");
    }

    /// <summary>
    /// 组装表详情缓存键，以连接名与表名拼接，保证不同连接或不同表互不串用。
    /// </summary>
    /// <param name="connectionName">连接名。</param>
    /// <param name="tableName">表名。</param>
    /// <returns>缓存键文本。</returns>
    private static string BuildCacheKey(string connectionName, string tableName)
    {
        return string.Concat(connectionName, CacheKeySeparator, tableName);
    }
}

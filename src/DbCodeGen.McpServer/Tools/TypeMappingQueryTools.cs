using System.ComponentModel;
using DbCodeGen.Core.Config;
using DbCodeGen.Core.DataSource;
using DbCodeGen.Core.Model;
using DbCodeGen.Core.Templates;
using DbCodeGen.Core.Templates.Packages;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace DbCodeGen.McpServer.Tools;

/// <summary>
/// 类型映射只读查询工具组：查看生效映射表与生成前未映射类型预检，与桌面端共享同一 Core 解析链。
/// </summary>
[McpServerToolType]
public sealed class TypeMappingQueryTools
{
    private readonly IConfigService _configService;
    private readonly ITemplatePackageService _packageService;
    private readonly IDataSourceManagementService _dataSourceManagement;
    private readonly TableCatalogService _tableCatalogService;
    private readonly ITypeMappingService _typeMappingService;
    private readonly ILogger<TypeMappingQueryTools> _logger;

    /// <summary>
    /// 以配置服务、模板包服务、数据源连接管理服务、表元数据编排服务、类型映射服务与日志器构造类型映射查询工具组。
    /// </summary>
    /// <param name="configService">配置服务，提供全局类型映射表。</param>
    /// <param name="packageService">模板包服务，加载包级 typeMap。</param>
    /// <param name="dataSourceManagement">数据源连接管理服务，预检时按名称定位连接。</param>
    /// <param name="tableCatalogService">表元数据编排服务，预检时读取表列元数据。</param>
    /// <param name="typeMappingService">类型映射解析服务，承载未映射类型预检。</param>
    /// <param name="logger">工具组日志器。</param>
    public TypeMappingQueryTools(
        IConfigService configService,
        ITemplatePackageService packageService,
        IDataSourceManagementService dataSourceManagement,
        TableCatalogService tableCatalogService,
        ITypeMappingService typeMappingService,
        ILogger<TypeMappingQueryTools> logger)
    {
        _configService = configService;
        _packageService = packageService;
        _dataSourceManagement = dataSourceManagement;
        _tableCatalogService = tableCatalogService;
        _typeMappingService = typeMappingService;
        _logger = logger;
    }

    /// <summary>
    /// 查看生效的类型映射表：全局映射（配置存储）与可选的包级 typeMap（模板包清单），
    /// 生成前了解当前映射口径，未命中项生成时回落默认类型 String。
    /// </summary>
    /// <param name="package">可选模板包包名；提供时一并返回该包清单声明的 typeMap。</param>
    /// <param name="cancellationToken">取消标记。</param>
    /// <returns>全局与包级映射条目及未命中兜底类型。</returns>
    [McpServerTool(Name = "get_type_map", Title = "查看类型映射", ReadOnly = true, Idempotent = true, Destructive = false, UseStructuredContent = true)]
    [Description("查看生效的类型映射表：全局映射条目与可选包级 typeMap；未映射类型生成时回落 defaultType。")]
    public async Task<GetTypeMapResult> GetTypeMapAsync(string? package = null, CancellationToken cancellationToken = default)
    {
        try
        {
            // 全局映射来自配置快照，实时反映桌面端或 MCP 侧的最新维护结果
            List<TypeMappingEntryDto> globalEntries = _configService.Current.TypeMappings
                .Select(entry => new TypeMappingEntryDto(
                    entry.DbType,
                    entry.TargetType,
                    entry.Import,
                    entry.Remark,
                    entry.DatabaseType?.ToString()))
                .ToList();

            // 包级 typeMap 仅在显式指定包名时加载，包不存在抛领域异常由调用方感知
            List<PackageTypeMapEntryDto>? packageEntries = null;
            string packageName = string.Empty;
            if (!string.IsNullOrWhiteSpace(package))
            {
                TemplatePackageInfo packageInfo = await _packageService.LoadPackageAsync(package, cancellationToken);
                packageName = packageInfo.Name;
                packageEntries = packageInfo.TypeMap
                    .Select(pair => new PackageTypeMapEntryDto(pair.Key, pair.Value))
                    .ToList();
            }

            _logger.LogInformation(
                "get_type_map 完成，全局条目 {GlobalCount} 条，包 {PackageName} 条目 {PackageCount} 条。",
                globalEntries.Count, packageName, packageEntries?.Count ?? 0);
            return new GetTypeMapResult(globalEntries, packageName, packageEntries, "String");
        }
        catch (Exception exception)
        {
            // 错误走 MCP 标准执行错误通道（isError=true）：结构化输出工具不再以成功载体混装错误对象
            McpException mapped = QueryToolExceptionMapper.Map(exception, cancellationToken);
            _logger.LogError(mapped, "get_type_map 执行失败，包名 {PackageName}。", package);
            throw mapped;
        }
    }

    /// <summary>
    /// 生成前未映射类型预检：读取指定表集合的完整列元数据，按与生成一致的解析链
    /// （包 typeMap > 数据库专属条目 > 通用条目）汇总未命中类型及首次出现位置。
    /// </summary>
    /// <param name="datasource">已保存的数据源连接名称。</param>
    /// <param name="tables">待生成的表名清单（与 list_tables 返回的 name 一致）。</param>
    /// <param name="package">目标模板包包名，预检与生成须使用同一包的 typeMap 口径。</param>
    /// <param name="cancellationToken">取消标记。</param>
    /// <returns>未映射类型清单与是否全部命中。</returns>
    [McpServerTool(Name = "check_type_mapping", Title = "预检类型映射", ReadOnly = true, Idempotent = true, Destructive = false)]
    [Description("生成前预检：检查指定表集合在当前映射链（包 typeMap>全局>兜底）下的未映射数据库类型，避免生成出回落 String 的列。")]
    public async Task<object> CheckTypeMappingAsync(
        string datasource,
        string[] tables,
        string package,
        CancellationToken cancellationToken)
    {
        try
        {
            // 表名清单非空校验：空集合预检无意义
            if (tables is null || tables.Length == 0)
            {
                throw new ArgumentException("表名清单不能为空，至少提供一张待生成的表名。");
            }

            // 连接与包口径与生成流水线保持一致
            DataSourceConfig? config = _dataSourceManagement.GetByName(datasource);
            if (config is null)
            {
                throw new ArgumentException($"数据源不存在：{datasource}。可用 list_datasources 查询已保存连接。");
            }

            TemplatePackageInfo packageInfo = await _packageService.LoadPackageAsync(package, cancellationToken);

            // 逐表读取完整列元数据后聚合，与桌面端生成前预检同一入口
            List<TableInfo> tableDetails = new();
            foreach (string tableName in tables)
            {
                TableInfo detail = await _tableCatalogService.GetTableDetailAsync(config, tableName, cancellationToken);
                tableDetails.Add(detail);
            }

            IReadOnlyList<UnmappedTypeInfo> unmapped =
                _typeMappingService.FindUnmappedTypes(tableDetails, packageInfo.TypeMap);

            List<UnmappedTypeDto> items = unmapped
                .Select(info => new UnmappedTypeDto(info.DbType, info.TableName, info.ColumnName, info.Occurrences))
                .ToList();

            _logger.LogInformation(
                "check_type_mapping 完成，连接 {ConnectionName}，包 {PackageName}，表 {TableCount} 张，未映射类型 {UnmappedCount} 种。",
                datasource, package, tableDetails.Count, items.Count);
            return new CheckTypeMappingResult(datasource, package, tableDetails.Count, items.Count == 0, items);
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "check_type_mapping 执行失败，连接 {ConnectionName}，包 {PackageName}。", datasource, package);
            return ToolError.From(exception, cancellationToken);
        }
    }
}

/// <summary>
/// 全局类型映射条目 DTO。
/// </summary>
/// <param name="DbType">数据库原始类型。</param>
/// <param name="TargetType">目标语言类型。</param>
/// <param name="Import">可选导包，无导包需求时为 null。</param>
/// <param name="Remark">可选备注。</param>
/// <param name="DatabaseType">适用数据库类型；null 表示通用条目（对所有数据库生效）。</param>
public sealed record TypeMappingEntryDto(
    string DbType,
    string TargetType,
    string? Import,
    string? Remark,
    string? DatabaseType);

/// <summary>
/// 包级 typeMap 条目 DTO。
/// </summary>
/// <param name="DbType">数据库原始类型。</param>
/// <param name="TargetType">目标语言类型。</param>
public sealed record PackageTypeMapEntryDto(string DbType, string TargetType);

/// <summary>
/// get_type_map 应答 DTO。
/// </summary>
/// <param name="Global">全局映射条目列表。</param>
/// <param name="Package">包名；未指定包时为空串。</param>
/// <param name="PackageTypeMap">包级 typeMap 条目；未指定包时为 null。</param>
/// <param name="DefaultType">全部未命中时的兜底目标类型。</param>
public sealed record GetTypeMapResult(
    IReadOnlyList<TypeMappingEntryDto> Global,
    string Package,
    IReadOnlyList<PackageTypeMapEntryDto>? PackageTypeMap,
    string DefaultType);

/// <summary>
/// 未映射类型条目 DTO。
/// </summary>
/// <param name="DbType">未映射的数据库原始类型。</param>
/// <param name="TableName">首次出现的表名。</param>
/// <param name="ColumnName">首次出现的列名。</param>
/// <param name="Occurrences">跨表跨列总出现次数。</param>
internal sealed record UnmappedTypeDto(string DbType, string TableName, string ColumnName, int Occurrences);

/// <summary>
/// check_type_mapping 应答 DTO。
/// </summary>
/// <param name="Datasource">数据源连接名。</param>
/// <param name="Package">模板包包名。</param>
/// <param name="TableCount">参与预检的表数量。</param>
/// <param name="AllMapped">是否全部列类型均命中映射链。</param>
/// <param name="Unmapped">未映射类型清单（按类型名排序）。</param>
internal sealed record CheckTypeMappingResult(
    string Datasource,
    string Package,
    int TableCount,
    bool AllMapped,
    IReadOnlyList<UnmappedTypeDto> Unmapped);

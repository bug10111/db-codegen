using System.ComponentModel;
using DbCodeGen.Core.DataSource;
using DbCodeGen.Core.Model;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace DbCodeGen.McpServer.Tools;

/// <summary>
/// 数据库表元数据只读查询工具组：按数据源名列出表清单与读取单表完整列元数据。
/// 连接与方言读取全部复用 Core 编排服务；异常消息经 Core 连接服务脱敏，不携带连接串敏感段。
/// </summary>
[McpServerToolType]
public sealed class TableQueryTools
{
    private readonly IDataSourceManagementService _dataSourceManagement;
    private readonly TableCatalogService _tableCatalogService;
    private readonly ILogger<TableQueryTools> _logger;

    /// <summary>
    /// 以数据源连接管理服务、表元数据编排服务与日志器构造表元数据查询工具组。
    /// </summary>
    /// <param name="dataSourceManagement">数据源连接管理服务，按名称定位已保存连接。</param>
    /// <param name="tableCatalogService">表元数据编排服务，承载表清单与表详情读取。</param>
    /// <param name="logger">工具组日志器。</param>
    public TableQueryTools(
        IDataSourceManagementService dataSourceManagement,
        TableCatalogService tableCatalogService,
        ILogger<TableQueryTools> logger)
    {
        _dataSourceManagement = dataSourceManagement;
        _tableCatalogService = tableCatalogService;
        _logger = logger;
    }

    /// <summary>
    /// 列出指定数据源下的全部表（不含列元数据），返回原始表名、类名、变量名、注释等摘要。
    /// </summary>
    /// <param name="datasource">已保存的数据源连接名称（可用 list_datasources 查询）。</param>
    /// <param name="cancellationToken">取消标记。</param>
    /// <returns>表摘要列表。</returns>
    [McpServerTool(Name = "list_tables", ReadOnly = true)]
    [Description("列出指定数据源下的全部表（原始表名、类名、变量名、注释；不含列元数据）。datasource 为已保存连接名。")]
    public async Task<object> ListTablesAsync(string datasource, CancellationToken cancellationToken)
    {
        try
        {
            DataSourceConfig config = ResolveDataSource(datasource);
            IReadOnlyList<TableInfo> tables =
                await _tableCatalogService.GetTablesAsync(config, cancellationToken);

            List<TableSummary> items = new();
            foreach (TableInfo table in tables)
            {
                items.Add(new TableSummary(
                    table.RawName,
                    table.SchemaName,
                    table.ClassName,
                    table.VariableName,
                    table.Comment));
            }

            _logger.LogInformation("list_tables 完成，连接 {ConnectionName}，表数量 {TableCount}。", datasource, items.Count);
            return new ListTablesResult(datasource, items);
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "list_tables 执行失败，连接 {ConnectionName}。", datasource);
            return ToolError.From(exception, cancellationToken);
        }
    }

    /// <summary>
    /// 读取指定表的完整列元数据（列名、类型、主键、可空、长度精度等），供生成前核对表结构。
    /// </summary>
    /// <param name="datasource">已保存的数据源连接名称。</param>
    /// <param name="table">目标表名（与 list_tables 返回的 name 一致）。</param>
    /// <param name="cancellationToken">取消标记。</param>
    /// <returns>表详情（含完整列元数据与主键列名清单）。</returns>
    [McpServerTool(Name = "get_table_detail", ReadOnly = true)]
    [Description("读取指定表的完整列元数据（列名、数据库类型、主键、自增、可空、长度/精度/小数位、注释）。")]
    public async Task<object> GetTableDetailAsync(string datasource, string table, CancellationToken cancellationToken)
    {
        try
        {
            DataSourceConfig config = ResolveDataSource(datasource);
            TableInfo detail = await _tableCatalogService.GetTableDetailAsync(config, table, cancellationToken);

            // 列元数据投影：保留模板渲染决策所需的全部字段
            List<ColumnDetail> columns = new();
            foreach (ColumnInfo column in detail.Columns)
            {
                columns.Add(new ColumnDetail(
                    column.RawName,
                    column.PropertyName,
                    column.RawDbType,
                    column.Comment,
                    column.IsPrimaryKey,
                    column.AutoIncrement,
                    column.IsNullable,
                    column.Length,
                    column.Precision,
                    column.Scale));
            }

            // 主键列名单独成清单，便于调用方快速核对
            List<string> primaryKeyColumns = detail.PrimaryKeys.Select(column => column.RawName).ToList();

            _logger.LogInformation(
                "get_table_detail 完成，连接 {ConnectionName}，表 {TableName}，列数 {ColumnCount}。",
                datasource, table, columns.Count);
            return new GetTableDetailResult(
                datasource,
                detail.RawName,
                detail.SchemaName,
                detail.ClassName,
                detail.VariableName,
                detail.Comment,
                columns,
                primaryKeyColumns);
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "get_table_detail 执行失败，连接 {ConnectionName}，表 {TableName}。", datasource, table);
            return ToolError.From(exception, cancellationToken);
        }
    }

    /// <summary>
    /// 按名称定位已保存数据源连接；未命中时抛参数异常并附现有连接名清单辅助调用方自纠。
    /// </summary>
    /// <param name="datasource">数据源连接名称。</param>
    /// <returns>命中的连接配置。</returns>
    private DataSourceConfig ResolveDataSource(string datasource)
    {
        DataSourceConfig? config = _dataSourceManagement.GetByName(datasource);
        if (config is null)
        {
            // 附现有连接名清单，减少调用方盲试
            string known = string.Join(", ", _dataSourceManagement.List().Select(item => item.Name));
            string knownList = string.IsNullOrWhiteSpace(known) ? "（无）" : known;
            throw new ArgumentException($"数据源不存在：{datasource}。可用数据源：{knownList}。");
        }

        return config;
    }
}

/// <summary>
/// 表摘要 DTO。
/// </summary>
/// <param name="Name">原始表名，与数据库一致。</param>
/// <param name="Schema">所属 schema/库名，数据库未提供时为 null。</param>
/// <param name="ClassName">按表名推导的 PascalCase 类名。</param>
/// <param name="VariableName">按表名推导的 camelCase 变量名。</param>
/// <param name="Comment">表注释，无注释时为 null。</param>
internal sealed record TableSummary(
    string Name,
    string? Schema,
    string ClassName,
    string VariableName,
    string? Comment);

/// <summary>
/// list_tables 应答 DTO。
/// </summary>
/// <param name="Datasource">数据源连接名。</param>
/// <param name="Tables">表摘要列表。</param>
internal sealed record ListTablesResult(string Datasource, IReadOnlyList<TableSummary> Tables);

/// <summary>
/// 列元数据 DTO。
/// </summary>
/// <param name="Name">原始列名。</param>
/// <param name="PropertyName">驼峰属性名。</param>
/// <param name="DbType">原始数据库类型（不含长度修饰）。</param>
/// <param name="Comment">列注释，无注释时为 null。</param>
/// <param name="IsPrimaryKey">是否主键列（联合主键每列均为 true）。</param>
/// <param name="AutoIncrement">是否自增列。</param>
/// <param name="IsNullable">是否可空。</param>
/// <param name="Length">列长度，无长度语义时为 null。</param>
/// <param name="Precision">精度，无精度语义时为 null。</param>
/// <param name="Scale">小数位，无精度语义时为 null。</param>
internal sealed record ColumnDetail(
    string Name,
    string PropertyName,
    string DbType,
    string? Comment,
    bool IsPrimaryKey,
    bool AutoIncrement,
    bool IsNullable,
    int? Length,
    int? Precision,
    int? Scale);

/// <summary>
/// get_table_detail 应答 DTO。
/// </summary>
/// <param name="Datasource">数据源连接名。</param>
/// <param name="Name">原始表名。</param>
/// <param name="Schema">所属 schema/库名。</param>
/// <param name="ClassName">类名。</param>
/// <param name="VariableName">变量名。</param>
/// <param name="Comment">表注释。</param>
/// <param name="Columns">完整列元数据。</param>
/// <param name="PrimaryKeyColumns">主键列名清单。</param>
internal sealed record GetTableDetailResult(
    string Datasource,
    string Name,
    string? Schema,
    string ClassName,
    string VariableName,
    string? Comment,
    IReadOnlyList<ColumnDetail> Columns,
    IReadOnlyList<string> PrimaryKeyColumns);

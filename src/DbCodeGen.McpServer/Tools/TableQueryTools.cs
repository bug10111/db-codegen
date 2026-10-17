using System.ComponentModel;
using System.Text.Json.Serialization;
using DbCodeGen.Core.DataSource;
using DbCodeGen.Core.Model;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
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
    [McpServerTool(Name = "list_tables", Title = "列出数据表", ReadOnly = true, Idempotent = true, Destructive = false, UseStructuredContent = true)]
    [Description("列出指定数据源下的全部表（原始表名、类名、变量名、注释；不含列元数据）。datasource 为已保存连接名。")]
    public async Task<ListTablesResult> ListTablesAsync(string datasource, CancellationToken cancellationToken)
    {
        try
        {
            DataSourceConfig config = ResolveDataSource(datasource);
            IReadOnlyList<TableInfo> tables =
                await _tableCatalogService.GetTablesAsync(config, cancellationToken);

            List<TableSummary> items = new();
            foreach (TableInfo table in tables)
            {
                // 可空字符串成员归一为空串（结构化应答 schema 必填属性恒在场，调用方免 null 分支）
                items.Add(new TableSummary(
                    table.RawName,
                    table.SchemaName ?? string.Empty,
                    table.ClassName,
                    table.VariableName,
                    table.Comment ?? string.Empty));
            }

            _logger.LogInformation("list_tables 完成，连接 {ConnectionName}，表数量 {TableCount}。", datasource, items.Count);
            return new ListTablesResult(datasource, items);
        }
        catch (Exception exception)
        {
            // 错误走 MCP 标准执行错误通道（isError=true）：结构化输出工具不再以成功载体混装错误对象
            McpException mapped = QueryToolExceptionMapper.Map(exception, cancellationToken);
            _logger.LogError(mapped, "list_tables 执行失败，连接 {ConnectionName}。", datasource);
            throw mapped;
        }
    }

    /// <summary>
    /// 读取指定表的完整列元数据（列名、类型、主键、可空、长度精度等），供生成前核对表结构。
    /// </summary>
    /// <param name="datasource">已保存的数据源连接名称。</param>
    /// <param name="table">目标表名（与 list_tables 返回的 name 一致）。</param>
    /// <param name="cancellationToken">取消标记。</param>
    /// <returns>表详情（含完整列元数据与主键列名清单）。</returns>
    [McpServerTool(Name = "get_table_detail", Title = "查询表结构", ReadOnly = true, Idempotent = true, Destructive = false, UseStructuredContent = true)]
    [Description("读取指定表的完整列元数据（列名、数据库类型、主键、自增、可空、长度/精度/小数位、注释）。列字段 name/rawDbType 与生成工具入参 tables[].columns[] 同名，可原样透传。")]
    public async Task<GetTableDetailResult> GetTableDetailAsync(string datasource, string table, CancellationToken cancellationToken)
    {
        try
        {
            DataSourceConfig config = ResolveDataSource(datasource);
            TableInfo detail = await _tableCatalogService.GetTableDetailAsync(config, table, cancellationToken);

            // 列元数据投影：保留模板渲染决策所需的全部字段；
            // 可空字符串成员归一为空串（schema 必填属性与序列化实况一致），
            // 数值维度成员保留 null 语义（不适用与真实 0 值必须可区分），经显式 null 序列化保证属性恒在场
            List<ColumnDetail> columns = new();
            foreach (ColumnInfo column in detail.Columns)
            {
                columns.Add(new ColumnDetail(
                    column.RawName,
                    column.PropertyName,
                    column.RawDbType,
                    column.Comment ?? string.Empty,
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
                detail.SchemaName ?? string.Empty,
                detail.ClassName,
                detail.VariableName,
                detail.Comment ?? string.Empty,
                columns,
                primaryKeyColumns);
        }
        catch (Exception exception)
        {
            // 错误走 MCP 标准执行错误通道（isError=true）：结构化输出工具不再以成功载体混装错误对象
            McpException mapped = QueryToolExceptionMapper.Map(exception, cancellationToken);
            _logger.LogError(mapped, "get_table_detail 执行失败，连接 {ConnectionName}，表 {TableName}。", datasource, table);
            throw mapped;
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
/// <param name="Schema">所属 schema/库名，数据库未提供时为空串。</param>
/// <param name="ClassName">按表名推导的 PascalCase 类名。</param>
/// <param name="VariableName">按表名推导的 camelCase 变量名。</param>
/// <param name="Comment">表注释，无注释时为空串。</param>
public sealed record TableSummary(
    string Name,
    string Schema,
    string ClassName,
    string VariableName,
    string Comment);

/// <summary>
/// list_tables 应答 DTO。
/// </summary>
/// <param name="Datasource">数据源连接名。</param>
/// <param name="Tables">表摘要列表。</param>
public sealed record ListTablesResult(string Datasource, IReadOnlyList<TableSummary> Tables);

/// <summary>
/// 列元数据 DTO，字段名与生成入参 tables[].columns[]（name/rawDbType）一致，调用方可将本应答列条目原样透传。
/// </summary>
/// <param name="Name">原始列名。</param>
/// <param name="PropertyName">驼峰属性名。</param>
/// <param name="RawDbType">原始数据库类型（不含长度修饰），与生成入参及渲染模型 rawDbType 同口径。</param>
/// <param name="Comment">列注释，无注释时为空串。</param>
/// <param name="IsPrimaryKey">是否主键列（联合主键每列均为 true）。</param>
/// <param name="AutoIncrement">是否自增列。</param>
/// <param name="IsNullable">是否可空。</param>
/// <param name="Length">列长度，仅字符等类型有值；无长度语义时为 null（应答中序列化为显式 null，属性恒在场）。</param>
/// <param name="Precision">精度，numeric/decimal 等类型有值；无精度语义时为 null（应答中序列化为显式 null，属性恒在场）。</param>
/// <param name="Scale">小数位，numeric/decimal 等类型有值；无小数位语义时为 null（应答中序列化为显式 null，属性恒在场；
/// 数值维度不适用与真实 0 值（如 numeric(x,0)）必须可区分，故保留 null 语义不以 0 归一）。</param>
public sealed record ColumnDetail(
    string Name,
    string PropertyName,
    string RawDbType,
    string Comment,
    bool IsPrimaryKey,
    bool AutoIncrement,
    bool IsNullable,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    int? Length,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    int? Precision,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    int? Scale);

/// <summary>
/// get_table_detail 应答 DTO。
/// </summary>
/// <param name="Datasource">数据源连接名。</param>
/// <param name="Name">原始表名。</param>
/// <param name="Schema">所属 schema/库名，数据库未提供时为空串。</param>
/// <param name="ClassName">类名。</param>
/// <param name="VariableName">变量名。</param>
/// <param name="Comment">表注释，无注释时为空串。</param>
/// <param name="Columns">完整列元数据。</param>
/// <param name="PrimaryKeyColumns">主键列名清单。</param>
public sealed record GetTableDetailResult(
    string Datasource,
    string Name,
    string Schema,
    string ClassName,
    string VariableName,
    string Comment,
    IReadOnlyList<ColumnDetail> Columns,
    IReadOnlyList<string> PrimaryKeyColumns);

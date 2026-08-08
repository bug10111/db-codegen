namespace DbCodeGen.Core.Model;

/// <summary>
/// 一次 SQL 执行的统一结果模型，同时承载查询结果集、影响行数与错误信息。
/// </summary>
public class SqlExecutionResult
{
    /// <summary>
    /// 是否执行成功。
    /// </summary>
    public bool Success { get; set; }

    /// <summary>
    /// 结果列定义，查询分支非空，DDL/DML 执行分支为空列表。
    /// </summary>
    public IReadOnlyList<SqlColumnInfo> Columns { get; set; } = Array.Empty<SqlColumnInfo>();

    /// <summary>
    /// 结果行集合，每行为一组单元格值；行数超上限截断时仅含前 MaxResultRows 行。
    /// </summary>
    public IReadOnlyList<IReadOnlyList<object?>> Rows { get; set; } = Array.Empty<IReadOnlyList<object?>>();

    /// <summary>
    /// 结果行数是否因超过 MaxResultRows 而被截断。
    /// </summary>
    public bool Truncated { get; set; }

    /// <summary>
    /// 影响行数，INSERT/UPDATE/DELETE 为实际行数，DDL 通常为 -1/0，查询分支为 null。
    /// </summary>
    public long? AffectedRows { get; set; }

    /// <summary>
    /// 数据库错误码，如 MySql 错误号或 PostgreSQL SqlState，成功时为空。
    /// </summary>
    public string? ErrorCode { get; set; }

    /// <summary>
    /// 错误描述，只承载数据库错误文本，不含密码、连接串明文与被执行的 SQL 文本，成功时为空。
    /// </summary>
    public string? ErrorMessage { get; set; }

    /// <summary>
    /// 执行耗时，供状态栏展示。
    /// </summary>
    public TimeSpan Duration { get; set; }
}

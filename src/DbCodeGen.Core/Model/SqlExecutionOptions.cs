namespace DbCodeGen.Core.Model;

/// <summary>
/// SQL 执行参数，控制命令超时、结果行上限与危险语句确认开关。
/// </summary>
public class SqlExecutionOptions
{
    /// <summary>
    /// 命令超时秒数，默认 30，0 表示不超时，需谨慎使用。
    /// </summary>
    public int TimeoutSeconds { get; set; } = 30;

    /// <summary>
    /// 结果行读取上限，默认 1000，超限截断并置 Truncated。
    /// </summary>
    public int MaxResultRows { get; set; } = 1000;

    /// <summary>
    /// 危险语句（DROP/TRUNCATE/无顶层 WHERE 的 DELETE·UPDATE）执行前是否弹确认，默认 true。
    /// </summary>
    public bool ConfirmDangerousSql { get; set; } = true;
}

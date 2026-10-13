namespace DbCodeGen.Core.Templates;

/// <summary>
/// 模板内容渲染结果，承载渲染成功后的真实代码或失败后的结构化错误与定位信息。
/// 实时预览与批量代码生成共用同一渲染结果契约。
/// </summary>
public sealed class PreviewResult
{
    /// <summary>
    /// 渲染是否成功，成功时 Output 有效，失败时 ErrorMessage 与行列有效。
    /// </summary>
    public bool IsSuccess { get; }

    /// <summary>
    /// 渲染后的真实代码，仅渲染成功时有值。
    /// </summary>
    public string Output { get; }

    /// <summary>
    /// 结构化错误描述，渲染失败时包含模板名与行列信息。
    /// </summary>
    public string ErrorMessage { get; }

    /// <summary>
    /// 错误所在模板行号（从 1 开始），定位到编辑器行；成功或未知时为空。
    /// </summary>
    public int? ErrorLine { get; }

    /// <summary>
    /// 错误所在模板列号（从 1 开始），定位到编辑器列；成功或未知时为空。
    /// </summary>
    public int? ErrorColumn { get; }

    /// <summary>
    /// 本次渲染耗时（毫秒），供预览状态展示。
    /// </summary>
    public long RenderDurationMs { get; }

    /// <summary>
    /// 渲染前静态分析产出的非阻断告警文案清单（当前为空迭代告警：for 循环迭代源为空集合/null 的零迭代信号）；
    /// 无告警时为空列表。告警不改变渲染结果，仅提示调用方核对产物关键段是否缺失。
    /// </summary>
    public IReadOnlyList<string> Warnings { get; }

    /// <summary>
    /// 使用完整字段构造渲染结果，仅由静态工厂方法调用。
    /// </summary>
    private PreviewResult(bool isSuccess, string output, string errorMessage, int? errorLine, int? errorColumn, long renderDurationMs, IReadOnlyList<string> warnings)
    {
        IsSuccess = isSuccess;
        Output = output;
        ErrorMessage = errorMessage;
        ErrorLine = errorLine;
        ErrorColumn = errorColumn;
        RenderDurationMs = renderDurationMs;
        Warnings = warnings;
    }

    /// <summary>
    /// 创建渲染成功结果。
    /// </summary>
    /// <param name="output">渲染后的真实代码。</param>
    /// <param name="renderDurationMs">渲染耗时（毫秒）。</param>
    /// <returns>成功渲染结果。</returns>
    public static PreviewResult Success(string output, long renderDurationMs)
    {
        return new PreviewResult(true, output, string.Empty, null, null, renderDurationMs, Array.Empty<string>());
    }

    /// <summary>
    /// 创建渲染成功结果并附带渲染前静态分析告警。
    /// </summary>
    /// <param name="output">渲染后的真实代码。</param>
    /// <param name="renderDurationMs">渲染耗时（毫秒）。</param>
    /// <param name="warnings">非阻断告警文案清单，可为空。</param>
    /// <returns>成功渲染结果。</returns>
    public static PreviewResult Success(string output, long renderDurationMs, IReadOnlyList<string> warnings)
    {
        ArgumentNullException.ThrowIfNull(warnings);
        return new PreviewResult(true, output, string.Empty, null, null, renderDurationMs, warnings);
    }

    /// <summary>
    /// 创建渲染失败结果，错误信息包含模板名与行列。
    /// </summary>
    /// <param name="errorMessage">面向用户的结构化错误描述。</param>
    /// <param name="errorLine">错误所在模板行号（从 1 开始）。</param>
    /// <param name="errorColumn">错误所在模板列号（从 1 开始）。</param>
    /// <param name="renderDurationMs">渲染耗时（毫秒）。</param>
    /// <returns>失败渲染结果。</returns>
    public static PreviewResult Error(string errorMessage, int? errorLine, int? errorColumn, long renderDurationMs)
    {
        return new PreviewResult(false, string.Empty, errorMessage, errorLine, errorColumn, renderDurationMs, Array.Empty<string>());
    }
}

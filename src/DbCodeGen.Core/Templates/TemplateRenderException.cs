namespace DbCodeGen.Core.Templates;

/// <summary>
/// 模板渲染异常，承载输出路径模板等渲染失败的结构化定位信息。
/// 路径占位渲染失败时抛出，供批量生成按模板名与行列整单定位失败原因。
/// </summary>
public sealed class TemplateRenderException : Exception
{
    /// <summary>
    /// 错误所在模板行号（从 1 开始），未知时为空。
    /// </summary>
    public int? Line { get; }

    /// <summary>
    /// 错误所在模板列号（从 1 开始），未知时为空。
    /// </summary>
    public int? Column { get; }

    /// <summary>
    /// 使用错误描述与行列创建模板渲染异常。
    /// </summary>
    /// <param name="message">面向用户的结构化错误描述。</param>
    /// <param name="line">错误所在模板行号（从 1 开始）。</param>
    /// <param name="column">错误所在模板列号（从 1 开始）。</param>
    public TemplateRenderException(string message, int? line = null, int? column = null)
        : base(message)
    {
        Line = line;
        Column = column;
    }

    /// <summary>
    /// 使用错误描述、内部异常与行列创建模板渲染异常。
    /// </summary>
    /// <param name="message">面向用户的结构化错误描述。</param>
    /// <param name="innerException">导致渲染失败的底层异常。</param>
    /// <param name="line">错误所在模板行号（从 1 开始）。</param>
    /// <param name="column">错误所在模板列号（从 1 开始）。</param>
    public TemplateRenderException(string message, Exception innerException, int? line = null, int? column = null)
        : base(message, innerException)
    {
        Line = line;
        Column = column;
    }
}

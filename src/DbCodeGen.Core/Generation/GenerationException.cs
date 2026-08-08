namespace DbCodeGen.Core.Generation;

/// <summary>
/// 批量生成领域异常，表达渲染失败、输出路径越界等"整单失败"的结构化业务错误。
/// 渲染失败携带模板名与行列定位；路径穿越携带被拒绝的相对路径，供界面展示失败原因。
/// </summary>
public sealed class GenerationException : Exception
{
    /// <summary>
    /// 使用错误描述创建批量生成异常。
    /// </summary>
    /// <param name="message">面向用户的错误描述。</param>
    public GenerationException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// 使用错误描述与内部异常创建批量生成异常。
    /// </summary>
    /// <param name="message">面向用户的错误描述。</param>
    /// <param name="innerException">导致本次异常的底层异常。</param>
    public GenerationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

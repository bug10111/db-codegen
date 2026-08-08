namespace DbCodeGen.Core.Templates.Packages;

/// <summary>
/// 模板包领域异常，用于表达模板包加载校验、路径防穿越、内置包只读等结构化业务错误。
/// 单包加载与删除接口以抛出本异常表达错误，导入/复制接口则以操作结果对象承载。
/// </summary>
public sealed class TemplatePackageException : Exception
{
    /// <summary>
    /// 使用错误描述创建模板包异常。
    /// </summary>
    /// <param name="message">面向用户的错误描述。</param>
    public TemplatePackageException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// 使用错误描述与内部异常创建模板包异常。
    /// </summary>
    /// <param name="message">面向用户的错误描述。</param>
    /// <param name="innerException">导致本次异常的底层异常。</param>
    public TemplatePackageException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

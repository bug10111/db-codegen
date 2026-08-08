namespace DbCodeGen.Core.Templates;

/// <summary>
/// 模板文件保存写回结果，承载写盘成功、内置包只读拒绝、路径越界拒绝与 IO 失败四类结果态。
/// </summary>
public sealed class TemplateSaveResult
{
    /// <summary>
    /// 是否写盘成功。
    /// </summary>
    public bool IsSuccess { get; }

    /// <summary>
    /// 是否因内置包只读被拒绝；为 true 时 IsSuccess 为 false，UI 应引导先复制到用户库。
    /// </summary>
    public bool IsReadOnlyBuiltin { get; }

    /// <summary>
    /// 是否因相对路径越界（目录穿越）被拒绝。
    /// </summary>
    public bool IsPathTraversal { get; }

    /// <summary>
    /// 结果或错误描述，成功时为空。
    /// </summary>
    public string? Message { get; }

    /// <summary>
    /// 使用完整字段构造保存结果，仅由静态工厂方法调用。
    /// </summary>
    private TemplateSaveResult(bool isSuccess, bool isReadOnlyBuiltin, bool isPathTraversal, string? message)
    {
        IsSuccess = isSuccess;
        IsReadOnlyBuiltin = isReadOnlyBuiltin;
        IsPathTraversal = isPathTraversal;
        Message = message;
    }

    /// <summary>
    /// 创建保存成功结果。
    /// </summary>
    /// <returns>保存成功结果。</returns>
    public static TemplateSaveResult Success()
    {
        return new TemplateSaveResult(true, false, false, null);
    }

    /// <summary>
    /// 创建内置包只读拒绝结果。
    /// </summary>
    /// <param name="message">面向用户的只读说明。</param>
    /// <returns>只读拒绝结果。</returns>
    public static TemplateSaveResult ReadOnlyBuiltin(string message)
    {
        return new TemplateSaveResult(false, true, false, message);
    }

    /// <summary>
    /// 创建相对路径越界拒绝结果。
    /// </summary>
    /// <param name="message">面向用户的路径越界说明。</param>
    /// <returns>路径越界结果。</returns>
    public static TemplateSaveResult PathTraversal(string message)
    {
        return new TemplateSaveResult(false, false, true, message);
    }

    /// <summary>
    /// 创建其它写盘失败结果（IO/权限等）。
    /// </summary>
    /// <param name="message">面向用户的失败说明。</param>
    /// <returns>失败结果。</returns>
    public static TemplateSaveResult Failure(string message)
    {
        return new TemplateSaveResult(false, false, false, message);
    }
}

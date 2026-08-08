namespace DbCodeGen.Core.Templates.Packages;

/// <summary>
/// 模板包导入/复制操作结果，携带状态、面向用户的消息与成功后新包信息。
/// </summary>
public sealed class TemplatePackageOperationResult
{
    /// <summary>
    /// 操作结果状态。
    /// </summary>
    public TemplatePackageOperationStatus Status { get; init; }

    /// <summary>
    /// 结果或错误描述，如同名冲突、内置包只读、目录穿越已拒绝等。
    /// </summary>
    public string Message { get; init; } = string.Empty;

    /// <summary>
    /// 成功后新包信息；冲突、失败等状态时为 null。
    /// </summary>
    public TemplatePackageInfo? Package { get; init; }

    /// <summary>
    /// 构造操作成功结果，携带新包信息。
    /// </summary>
    /// <param name="package">安装或复制后的新包信息。</param>
    /// <returns>Succeeded 状态的操作结果。</returns>
    public static TemplatePackageOperationResult Success(TemplatePackageInfo package)
    {
        return new TemplatePackageOperationResult
        {
            Status = TemplatePackageOperationStatus.Succeeded,
            Message = "操作成功",
            Package = package
        };
    }

    /// <summary>
    /// 构造同名用户包冲突结果，需覆盖确认。
    /// </summary>
    /// <param name="message">冲突描述。</param>
    /// <returns>NameConflict 状态的操作结果。</returns>
    public static TemplatePackageOperationResult NameConflict(string message)
    {
        return new TemplatePackageOperationResult
        {
            Status = TemplatePackageOperationStatus.NameConflict,
            Message = message
        };
    }

    /// <summary>
    /// 构造内置包只读冲突结果，拒绝覆盖或删除。
    /// </summary>
    /// <param name="message">只读拒绝描述。</param>
    /// <returns>BuiltinConflict 状态的操作结果。</returns>
    public static TemplatePackageOperationResult BuiltinReadonly(string message)
    {
        return new TemplatePackageOperationResult
        {
            Status = TemplatePackageOperationStatus.BuiltinConflict,
            Message = message
        };
    }

    /// <summary>
    /// 构造校验失败结果，对应 manifest、路径、文件或新包名不合法。
    /// </summary>
    /// <param name="message">校验失败描述。</param>
    /// <returns>Invalid 状态的操作结果。</returns>
    public static TemplatePackageOperationResult Invalid(string message)
    {
        return new TemplatePackageOperationResult
        {
            Status = TemplatePackageOperationStatus.Invalid,
            Message = message
        };
    }

    /// <summary>
    /// 构造 IO 或其它异常结果。
    /// </summary>
    /// <param name="message">失败描述。</param>
    /// <returns>Failed 状态的操作结果。</returns>
    public static TemplatePackageOperationResult Failure(string message)
    {
        return new TemplatePackageOperationResult
        {
            Status = TemplatePackageOperationStatus.Failed,
            Message = message
        };
    }
}

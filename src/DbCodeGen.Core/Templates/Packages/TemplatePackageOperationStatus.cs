namespace DbCodeGen.Core.Templates.Packages;

/// <summary>
/// 模板包导入/复制操作的结果状态枚举。
/// </summary>
public enum TemplatePackageOperationStatus
{
    /// <summary>
    /// 操作成功，Package 携带新包信息。
    /// </summary>
    Succeeded,

    /// <summary>
    /// 与现有用户包同名，需 overwrite=true 覆盖确认，属结果态而非失败。
    /// </summary>
    NameConflict,

    /// <summary>
    /// 与内置包同名，内置包只读，拒绝覆盖或删除，overwrite 不生效。
    /// </summary>
    BuiltinConflict,

    /// <summary>
    /// manifest、路径、文件或新包名校验失败。
    /// </summary>
    Invalid,

    /// <summary>
    /// IO 或其它异常导致操作失败。
    /// </summary>
    Failed
}

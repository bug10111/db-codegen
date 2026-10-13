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
    /// 目标内容不存在：模板编辑场景下 oldText 在模板正文中未命中（0 次命中）。
    /// </summary>
    NotFound,

    /// <summary>
    /// 内容冲突：模板编辑场景下条件写期望哈希与当前正文哈希不一致（正文已被其它写入方更新）。
    /// </summary>
    ContentConflict,

    /// <summary>
    /// IO 或其它异常导致操作失败。
    /// </summary>
    Failed
}

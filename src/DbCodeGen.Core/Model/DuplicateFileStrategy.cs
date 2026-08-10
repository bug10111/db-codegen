namespace DbCodeGen.Core.Model;

/// <summary>
/// 同名目标文件处理策略，决定批量生成时目标文件已存在（同名）的处置方式。
/// 由④生成栏操作行下拉选择并记忆到配置，dry-run 分类与写盘均按此策略执行，无确认弹窗。
/// </summary>
public enum DuplicateFileStrategy
{
    /// <summary>
    /// 覆盖：同名目标存在且内容与渲染结果不同时直接替换，内容相同仍跳过；非 UTF-8 遗留文件按覆盖处理。
    /// </summary>
    Overwrite,

    /// <summary>
    /// 跳过：同名目标存在时一律不写盘（无论内容是否相同），只写本次新增文件。
    /// </summary>
    Skip
}

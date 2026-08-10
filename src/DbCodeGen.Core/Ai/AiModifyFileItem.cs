namespace DbCodeGen.Core.Ai;

/// <summary>
/// AI 改模板批量请求中单个待修改文件项：携带文件相对包根路径与内容快照。
/// 内容快照为不可信外部输入，仅注入本次对话提示词，不写盘、不进日志。
/// </summary>
public sealed class AiModifyFileItem
{
    /// <summary>
    /// 文件相对包根路径（正斜杠规范化），供日志脱敏记录与结果回写定位。
    /// </summary>
    public string RelativePath { get; set; } = string.Empty;

    /// <summary>
    /// 文件内容快照（②区当前文件取编辑器最新内容，其余取磁盘原文），必填非空。
    /// </summary>
    public string Content { get; set; } = string.Empty;
}

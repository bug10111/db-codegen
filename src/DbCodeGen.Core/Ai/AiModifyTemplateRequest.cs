namespace DbCodeGen.Core.Ai;

/// <summary>
/// AI 改模板请求，承载②区当前模板文件快照、修改指令、参考文件内容快照清单与多轮对话历史。
/// 内容快照为不可信外部输入，仅注入本次对话提示词，不写盘、不进日志。
/// </summary>
public sealed class AiModifyTemplateRequest
{
    /// <summary>
    /// ②区当前模板文件相对包根路径（发送时快照，供日志脱敏记录与目标守卫展示用）。
    /// </summary>
    public string CurrentTemplateFilePath { get; set; } = string.Empty;

    /// <summary>
    /// ②区编辑器当前最新内容快照（含未保存编辑，发送时读取 EditorText），必填非空。
    /// </summary>
    public string CurrentTemplateContent { get; set; } = string.Empty;

    /// <summary>
    /// 修改指令，必填非空，注入 user 提示词。
    /// </summary>
    public string ModificationInstruction { get; set; } = string.Empty;

    /// <summary>
    /// 参考文件内容快照清单，来自窗口级共享参考文件上下文，发送时取快照；默认空集合。
    /// 内容仅注入本次对话提示词，不写盘、不进日志。
    /// </summary>
    public IReadOnlyList<AiReferenceFileItem> ReferenceFiles { get; set; } = Array.Empty<AiReferenceFileItem>();

    /// <summary>
    /// 历史对话轮次（不含 system 与本轮指令，仅成功轮追加的 user+assistant），多轮回环全量回放；默认空集合。
    /// </summary>
    public IReadOnlyList<LlmChatMessage> HistoryMessages { get; set; } = Array.Empty<LlmChatMessage>();
}

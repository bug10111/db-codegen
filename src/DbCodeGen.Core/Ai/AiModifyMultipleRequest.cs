namespace DbCodeGen.Core.Ai;

/// <summary>
/// AI 改模板批量修改请求，承载目标模板包名、待修改文件清单（每文件独立内容快照）、
/// 共享的修改指令、参考文件内容快照清单与多轮对话历史。
/// 所有文件组装进同一条 user 提示词并单次调用 LLM，共享同一条修改指令与参考文件；
/// 内容快照为不可信外部输入，仅注入本次对话提示词，不写盘、不进日志。
/// </summary>
public sealed class AiModifyMultipleRequest
{
    /// <summary>
    /// 目标模板包名，供日志脱敏记录与结果回写定位。
    /// </summary>
    public string PackageName { get; set; } = string.Empty;

    /// <summary>
    /// 待修改文件清单，必填非空；每文件内容非空才参与 LLM 调用，内容为空视为该文件失败。
    /// </summary>
    public IReadOnlyList<AiModifyFileItem> Files { get; set; } = Array.Empty<AiModifyFileItem>();

    /// <summary>
    /// 修改指令，必填非空，注入每个文件的 user 提示词。
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

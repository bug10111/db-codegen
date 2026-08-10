namespace DbCodeGen.Core.Ai;

/// <summary>
/// AI 改模板对话服务接口：读取 LLM 配置、组装提示词（TEMPLATE_SPEC + 当前文件内容 + 修改指令 +
/// 参考文件 + 多轮历史）、调用 LLM、剥离代码围栏并做内容非空校验，是 AI 改模板功能的核心编排入口。
/// 只返回修改后的完整新文件内容并交由调用方应用，不直接写盘、不绕过既有保存安全线。
/// 批量修改把全部选中文件组装进同一条 user 提示词，单次 LLM 调用按 #FILE# 相对路径 标记一次返回全部文件修改结果，
/// 未返回的文件单独记失败不中断其它文件。
/// </summary>
public interface ITemplateAiModifier
{
    /// <summary>
    /// 依据修改指令与参考上下文，让 LLM 返回修改后的完整模板文件内容。
    /// </summary>
    /// <param name="request">改模板请求，含当前文件快照、修改指令、参考文件内容快照与多轮对话历史。</param>
    /// <param name="cancellationToken">取消标记。</param>
    /// <returns>改模板结果，成功携带剥离代码围栏且非空的完整新文件，失败携带错误清单与原始输出。</returns>
    Task<AiModifyTemplateResult> ModifyAsync(
        AiModifyTemplateRequest request,
        CancellationToken cancellationToken);

    /// <summary>
    /// 批量修改多个模板文件：所有待修改文件组装进同一条 user 提示词并单次调用 LLM，
    /// AI 按 #FILE# 相对路径 标记一次返回全部文件修改结果，未返回的文件单独记失败不中断其它文件。
    /// 单次调用取消时抛 OperationCanceledException 由调用方捕获处理。
    /// </summary>
    /// <param name="request">批量修改请求，含目标包名、待修改文件清单、共享指令、参考文件快照与多轮历史。</param>
    /// <param name="cancellationToken">取消标记。</param>
    /// <returns>批量修改结果，成功携带逐文件剥离分隔标记后的完整新文件，失败按文件隔离并附批量级错误。</returns>
    Task<AiModifyMultipleResult> ModifyMultipleAsync(
        AiModifyMultipleRequest request,
        CancellationToken cancellationToken);
}

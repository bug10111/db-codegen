namespace DbCodeGen.Core.Ai;

/// <summary>
/// AI 改模板对话服务接口：读取 LLM 配置、组装提示词（TEMPLATE_SPEC + 当前文件内容 + 修改指令 +
/// 参考文件 + 多轮历史）、调用 LLM、剥离代码围栏并做内容非空校验，是 AI 改模板功能的核心编排入口。
/// 只返回修改后的完整新文件内容并交由调用方应用，不直接写盘、不绕过既有保存安全线。
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
}

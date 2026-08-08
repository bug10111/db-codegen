namespace DbCodeGen.Core.Ai;

/// <summary>
/// AI 模板生成服务接口：读取 LLM 配置、组装提示词、调用 LLM、解析模板包、
/// 写临时目录校验并提交落库，是 AI 模板生成功能的核心编排入口。
/// </summary>
public interface ITemplateAiGenerator
{
    /// <summary>
    /// 生成一套模板包并提交落库：解析 LLM 输出后先写临时目录，
    /// 经 TemplatePackageLoader 完整校验通过后再提交到用户模板库，失败或取消清理临时目录。
    /// </summary>
    /// <param name="request">生成请求，含技术栈描述与样例表元数据。</param>
    /// <param name="overwrite">与用户包同名时是否允许覆盖；内置包同名一律只读拒绝。</param>
    /// <param name="cancellationToken">取消标记。</param>
    /// <returns>生成结果，成功携带包名与落库目录，失败携带错误清单与原始输出。</returns>
    Task<AiTemplateGenerationResult> GenerateAsync(
        AiTemplateGenerationRequest request,
        bool overwrite,
        CancellationToken cancellationToken);
}

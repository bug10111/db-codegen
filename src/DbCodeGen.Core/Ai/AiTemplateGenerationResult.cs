namespace DbCodeGen.Core.Ai;

/// <summary>
/// AI 模板生成结果，成功携带包名与落库目录，失败携带错误清单与原始 LLM 输出。
/// 原始输出仅结果页展示供人工修复，不落日志。
/// </summary>
public sealed class AiTemplateGenerationResult
{
    /// <summary>
    /// 是否生成并落库成功。
    /// </summary>
    public bool IsSuccess { get; set; }

    /// <summary>
    /// 成功时模板包名。
    /// </summary>
    public string? PackageName { get; set; }

    /// <summary>
    /// 成功时模板包落库目录绝对路径。
    /// </summary>
    public string? TemplateDir { get; set; }

    /// <summary>
    /// 失败原因清单，覆盖 LLM 错误、解析错误、校验错误、提交错误与包名冲突。
    /// </summary>
    public List<string> Errors { get; set; } = new();

    /// <summary>
    /// 原始 LLM 回复文本，失败时保留供人工修复，仅结果页展示，不落日志。
    /// </summary>
    public string? RawLlmOutput { get; set; }

    /// <summary>
    /// 构造成功结果，携带包名与落库目录。
    /// </summary>
    /// <param name="packageName">模板包名。</param>
    /// <param name="templateDir">模板包落库目录绝对路径。</param>
    /// <param name="rawLlmOutput">原始 LLM 回复文本。</param>
    /// <returns>成功结果实例。</returns>
    public static AiTemplateGenerationResult Success(string packageName, string? templateDir, string? rawLlmOutput)
    {
        return new AiTemplateGenerationResult
        {
            IsSuccess = true,
            PackageName = packageName,
            TemplateDir = templateDir,
            RawLlmOutput = rawLlmOutput
        };
    }

    /// <summary>
    /// 构造失败结果，携带错误清单与原始输出。
    /// </summary>
    /// <param name="errors">失败原因清单。</param>
    /// <param name="rawLlmOutput">原始 LLM 回复文本，可空。</param>
    /// <returns>失败结果实例。</returns>
    public static AiTemplateGenerationResult Failed(List<string> errors, string? rawLlmOutput = null)
    {
        return new AiTemplateGenerationResult
        {
            IsSuccess = false,
            Errors = errors,
            RawLlmOutput = rawLlmOutput
        };
    }
}

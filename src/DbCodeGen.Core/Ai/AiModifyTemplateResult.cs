namespace DbCodeGen.Core.Ai;

/// <summary>
/// AI 改模板结果，成功携带剥离代码围栏且内容非空校验通过的完整新文件，
/// 失败携带错误清单与原始 LLM 输出。原始输出仅会话页展示供人工查看/复制，不落日志。
/// </summary>
public sealed class AiModifyTemplateResult
{
    /// <summary>
    /// 是否成功返回完整新文件。
    /// </summary>
    public bool IsSuccess { get; set; }

    /// <summary>
    /// 成功时剥离代码围栏并做内容非空校验后的完整新文件。
    /// </summary>
    public string? NewContent { get; set; }

    /// <summary>
    /// 失败原因清单，覆盖请求校验、LLM 调用错误与返回内容为空。
    /// </summary>
    public List<string> Errors { get; set; } = new();

    /// <summary>
    /// 原始 LLM 回复文本，失败时保留供人工查看/复制，仅会话页展示，不落日志。
    /// </summary>
    public string? RawLlmOutput { get; set; }

    /// <summary>
    /// 构造成功结果，携带完整新文件与原始 LLM 输出。
    /// </summary>
    /// <param name="newContent">完整新文件内容。</param>
    /// <param name="rawLlmOutput">原始 LLM 回复文本。</param>
    /// <returns>成功结果实例。</returns>
    public static AiModifyTemplateResult Success(string newContent, string? rawLlmOutput)
    {
        return new AiModifyTemplateResult
        {
            IsSuccess = true,
            NewContent = newContent,
            RawLlmOutput = rawLlmOutput
        };
    }

    /// <summary>
    /// 构造失败结果，携带错误清单与原始输出。
    /// </summary>
    /// <param name="errors">失败原因清单。</param>
    /// <param name="rawLlmOutput">原始 LLM 回复文本，可空。</param>
    /// <returns>失败结果实例。</returns>
    public static AiModifyTemplateResult Failed(List<string> errors, string? rawLlmOutput = null)
    {
        return new AiModifyTemplateResult
        {
            IsSuccess = false,
            Errors = errors,
            RawLlmOutput = rawLlmOutput
        };
    }
}

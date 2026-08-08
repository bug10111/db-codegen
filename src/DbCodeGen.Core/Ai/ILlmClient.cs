namespace DbCodeGen.Core.Ai;

/// <summary>
/// OpenAI 兼容 LLM 对话客户端接口，向 {baseUrl}/chat/completions 发起非流式对话请求。
/// 成功返回响应内容，失败返回结构化错误码与可读信息，不抛出业务异常。
/// </summary>
public interface ILlmClient
{
    /// <summary>
    /// 发起一次对话补全请求，成功返回 choices[0].message.content，失败返回结构化错误码与可读信息。
    /// </summary>
    /// <param name="request">对话请求，含模型、消息与生成参数。</param>
    /// <param name="options">瞬态调用配置，含端点、模型与明文 apiKey。</param>
    /// <param name="cancellationToken">取消标记。</param>
    /// <returns>对话响应，含内容或错误信息。</returns>
    Task<LlmChatResponse> ChatCompletionAsync(
        LlmChatRequest request,
        LlmClientOptions options,
        CancellationToken cancellationToken);
}

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

    /// <summary>
    /// 测试 LLM 连接：以最小对话请求验证端点、鉴权与模型可用性，成功返回成功响应，失败返回结构化错误。
    /// </summary>
    /// <param name="options">瞬态调用配置，含端点、模型与明文 apiKey。</param>
    /// <param name="cancellationToken">取消标记。</param>
    /// <returns>对话响应，成功含内容，失败含错误信息。</returns>
    Task<LlmChatResponse> TestConnectionAsync(
        LlmClientOptions options,
        CancellationToken cancellationToken);

    /// <summary>
    /// 读取端点支持的模型列表，经 GET {baseUrl}/models 拉取 data[].id；端点不支持或调用失败时返回空集合。
    /// </summary>
    /// <param name="options">瞬态调用配置，含端点与明文 apiKey。</param>
    /// <param name="cancellationToken">取消标记。</param>
    /// <returns>模型名清单，读取失败时为空集合。</returns>
    Task<IReadOnlyList<string>> ListModelsAsync(
        LlmClientOptions options,
        CancellationToken cancellationToken);
}

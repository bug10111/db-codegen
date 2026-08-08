namespace DbCodeGen.Core.Ai;

/// <summary>
/// OpenAI 兼容对话响应，成功携带 choices[0].message.content，失败携带结构化错误码与可读错误信息。
/// 错误信息不得包含 apiKey 与连接串等敏感信息。
/// </summary>
public sealed class LlmChatResponse
{
    /// <summary>
    /// 响应内容文本，即 choices[0].message.content。
    /// </summary>
    public string Content { get; set; } = string.Empty;

    /// <summary>
    /// 调用是否成功。
    /// </summary>
    public bool IsSuccess { get; set; }

    /// <summary>
    /// 错误码，如 auth / invalid_request / rate_limit / upstream / timeout / network。
    /// </summary>
    public string? ErrorCode { get; set; }

    /// <summary>
    /// 可读错误信息，不含 apiKey 与连接串等敏感信息。
    /// </summary>
    public string? ErrorMessage { get; set; }

    /// <summary>
    /// 构造成功响应，携带内容文本。
    /// </summary>
    /// <param name="content">响应内容文本。</param>
    /// <returns>成功响应实例。</returns>
    public static LlmChatResponse Success(string content)
    {
        return new LlmChatResponse
        {
            IsSuccess = true,
            Content = content
        };
    }

    /// <summary>
    /// 构造失败响应，携带结构化错误码与可读错误信息。
    /// </summary>
    /// <param name="errorCode">错误码。</param>
    /// <param name="errorMessage">可读错误信息。</param>
    /// <returns>失败响应实例。</returns>
    public static LlmChatResponse Failure(string errorCode, string errorMessage)
    {
        return new LlmChatResponse
        {
            IsSuccess = false,
            ErrorCode = errorCode,
            ErrorMessage = errorMessage
        };
    }
}

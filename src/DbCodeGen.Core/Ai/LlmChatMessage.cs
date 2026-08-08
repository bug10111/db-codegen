namespace DbCodeGen.Core.Ai;

/// <summary>
/// OpenAI 兼容对话消息，角色为 system / user / assistant。
/// </summary>
public sealed class LlmChatMessage
{
    /// <summary>
    /// 消息角色：system / user / assistant。
    /// </summary>
    public string Role { get; set; } = string.Empty;

    /// <summary>
    /// 消息内容。
    /// </summary>
    public string Content { get; set; } = string.Empty;
}

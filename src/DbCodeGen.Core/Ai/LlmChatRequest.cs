namespace DbCodeGen.Core.Ai;

/// <summary>
/// OpenAI 兼容对话请求，承载模型、消息列表、采样温度与可选的最大输出 token 数。
/// MaxTokens 不设置时请求体不含 max_tokens 字段，由服务端按其默认输出上限处理，避免模型输出被截断或超限被拒。
/// </summary>
public sealed class LlmChatRequest
{
    /// <summary>
    /// 默认采样温度，模板生成建议值。
    /// </summary>
    public const double DefaultTemperature = 0.2;

    /// <summary>
    /// 模型名，为空时回退使用 LlmClientOptions.Model。
    /// </summary>
    public string Model { get; set; } = string.Empty;

    /// <summary>
    /// 对话消息列表，首条通常为 system 提示词。
    /// </summary>
    public List<LlmChatMessage> Messages { get; set; } = new();

    /// <summary>
    /// 采样温度，为空时使用默认值 0.2。
    /// </summary>
    public double? Temperature { get; set; }

    /// <summary>
    /// 最大输出 token 数，可空；不设置时请求体不含 max_tokens 字段，由服务端按默认输出上限处理。
    /// </summary>
    public int? MaxTokens { get; set; }
}

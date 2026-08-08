namespace DbCodeGen.Core.Ai;

/// <summary>
/// OpenAI 兼容对话请求，承载模型、消息列表、采样温度与最大输出 token 数。
/// 整套模板包 JSON 体量大，MaxTokens 默认 16000 防止输出截断产生非法 JSON。
/// </summary>
public sealed class LlmChatRequest
{
    /// <summary>
    /// 默认最大输出 token 数。
    /// </summary>
    public const int DefaultMaxTokens = 16000;

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
    /// 最大输出 token 数，为空时使用默认值 16000。
    /// </summary>
    public int? MaxTokens { get; set; }
}

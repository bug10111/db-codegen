namespace DbCodeGen.Core.Config;

/// <summary>
/// LLM 配置子模型，持久化字段只含 apiKey 的 DPAPI 密文，明文 apiKey 不进入任何长周期模型。
/// </summary>
public class LlmConfig
{
    /// <summary>
    /// 默认 OpenAI 兼容协议端点，DashScope 兼容模式地址。
    /// </summary>
    public const string DefaultBaseUrl = "https://dashscope.aliyuncs.com/compatible-mode/v1";

    /// <summary>
    /// 默认模型名。
    /// </summary>
    public const string DefaultModel = "qwen-plus";

    /// <summary>
    /// OpenAI 兼容协议端点，默认 DashScope 兼容端点。
    /// </summary>
    public string BaseUrl { get; set; } = DefaultBaseUrl;

    /// <summary>
    /// Windows DPAPI 加密后的 apiKey 密文；空串表示未配置，使用方经 IConfigService.GetLlmApiKey 解密取得瞬态明文。
    /// </summary>
    public string ApiKeyEncrypted { get; set; } = string.Empty;

    /// <summary>
    /// 模型名，默认 qwen-plus。
    /// </summary>
    public string Model { get; set; } = DefaultModel;
}

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
    /// 常用 OpenAI 兼容模型名清单，设置窗口模型下拉的初始候选项，测试连接成功后按端点实际支持刷新。
    /// </summary>
    public static readonly IReadOnlyList<string> CommonModels = new[]
    {
        "qwen-plus",
        "qwen-max",
        "qwen-turbo",
        "qwen-long",
        "qwen3-plus",
        "qwen3-max",
        "qwen3-turbo",
        "deepseek-chat",
        "deepseek-reasoner",
        "glm-4",
        "glm-4-plus",
        "kimi-latest",
        "gpt-4o",
        "gpt-4o-mini"
    };

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

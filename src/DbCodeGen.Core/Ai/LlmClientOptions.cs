namespace DbCodeGen.Core.Ai;

/// <summary>
/// LLM 调用瞬态配置，承载端点、模型与解密后的明文 apiKey，仅内存短周期，用后即弃。
/// 不持久化、不进入向导长生命周期状态，明文 apiKey 绝不落盘或进入日志。
/// </summary>
public sealed class LlmClientOptions
{
    /// <summary>
    /// OpenAI 兼容端点，取自 LlmConfig.BaseUrl。
    /// </summary>
    public string BaseUrl { get; set; } = string.Empty;

    /// <summary>
    /// 模型名，取自 LlmConfig.Model，请求未显式指定模型时回退使用。
    /// </summary>
    public string Model { get; set; } = string.Empty;

    /// <summary>
    /// 明文 apiKey，仅本次 HTTP 调用的内存短周期，经 IConfigService.GetLlmApiKey 解密所得。
    /// </summary>
    public string ApiKey { get; set; } = string.Empty;
}

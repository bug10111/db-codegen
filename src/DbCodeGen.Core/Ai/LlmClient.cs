using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace DbCodeGen.Core.Ai;

/// <summary>
/// OpenAI 兼容 LLM 对话客户端，手写 HttpClient 直连 {baseUrl}/chat/completions，Bearer 鉴权。
/// 承载请求超时、HTTP 错误映射与响应内容抽取；apiKey 仅作为请求头存在于内存短周期，不落日志。
/// </summary>
public sealed class LlmClient : ILlmClient, IDisposable
{
    /// <summary>
    /// 默认请求超时秒数。
    /// </summary>
    public const int DefaultTimeoutSeconds = 120;

    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly ILogger<LlmClient> _logger;

    private static readonly JsonSerializerOptions PayloadJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    /// <summary>
    /// 使用日志器与可选 HttpClient 创建客户端；未传入时自建并设默认超时。
    /// </summary>
    /// <param name="logger">客户端日志器。</param>
    /// <param name="httpClient">外部传入的 HttpClient，为空时自建。</param>
    /// <exception cref="ArgumentNullException">logger 为 null 时抛出。</exception>
    public LlmClient(ILogger<LlmClient> logger, HttpClient? httpClient = null)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
        _ownsHttpClient = httpClient is null;
        _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(DefaultTimeoutSeconds) };
    }

    /// <inheritdoc />
    public async Task<LlmChatResponse> ChatCompletionAsync(
        LlmChatRequest request,
        LlmClientOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(options);

        // 端点与鉴权配置缺失时直接返回结构化错误，不发起网络请求
        if (string.IsNullOrWhiteSpace(options.BaseUrl))
        {
            return LlmChatResponse.Failure("invalid_request", "LLM 端点未配置，请检查设置。");
        }

        if (string.IsNullOrWhiteSpace(options.ApiKey))
        {
            return LlmChatResponse.Failure("auth", "LLM apiKey 未配置，请检查设置。");
        }

        string model = string.IsNullOrWhiteSpace(request.Model) ? options.Model : request.Model;
        if (string.IsNullOrWhiteSpace(model))
        {
            return LlmChatResponse.Failure("invalid_request", "LLM 模型未配置，请检查设置。");
        }

        string endpoint = BuildEndpoint(options.BaseUrl);

        // 端点须为合法绝对 URL，否则 HttpRequestMessage 构造会抛 UriFormatException，此处前置校验并结构化拒绝
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out _))
        {
            return LlmChatResponse.Failure("invalid_request", "LLM 端点 URL 不合法，请检查 BaseUrl 设置。");
        }

        int maxTokens = request.MaxTokens ?? LlmChatRequest.DefaultMaxTokens;

        // 消息列表兜底空集合，防止外部误置 null 导致装配请求体时空引用
        List<LlmChatMessage> messages = request.Messages ?? new List<LlmChatMessage>();

        // 组装 OpenAI 兼容请求体：model + messages + 可选 temperature + max_tokens
        var payload = new Dictionary<string, object?>
        {
            ["model"] = model,
            ["messages"] = messages.Select(message => new { role = message.Role, content = message.Content }),
            ["max_tokens"] = maxTokens
        };
        if (request.Temperature.HasValue)
        {
            payload["temperature"] = request.Temperature.Value;
        }

        using HttpRequestMessage httpRequest = new(HttpMethod.Post, endpoint);
        httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.ApiKey);
        string bodyJson = JsonSerializer.Serialize(payload, PayloadJsonOptions);
        httpRequest.Content = new StringContent(bodyJson, Encoding.UTF8, "application/json");

        HttpResponseMessage httpResponse;
        try
        {
            httpResponse = await _httpClient.SendAsync(httpRequest, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // 非用户取消的等待超时，映射为可读超时错误
            _logger.LogWarning("调用 LLM 端点超时：{Endpoint}。", endpoint);
            return LlmChatResponse.Failure("timeout", "调用 LLM 超时，请稍后重试或检查网络。");
        }
        catch (HttpRequestException exception)
        {
            _logger.LogError(exception, "调用 LLM 端点网络失败：{Endpoint}。", endpoint);
            return LlmChatResponse.Failure("network", "网络请求失败，请检查网络连接。");
        }

        using (httpResponse)
        {
            string responseBody = await httpResponse.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (!httpResponse.IsSuccessStatusCode)
            {
                return MapHttpError(httpResponse.StatusCode, responseBody);
            }

            return ParseSuccessBody(responseBody);
        }
    }

    /// <summary>
    /// 释放自建 HttpClient 资源；外部传入的客户端交由创建方管理。
    /// </summary>
    public void Dispose()
    {
        if (_ownsHttpClient)
        {
            _httpClient.Dispose();
        }
    }

    /// <summary>
    /// 拼接对话补全端点，去除 BaseUrl 尾部斜杠后追加 /chat/completions。
    /// </summary>
    /// <param name="baseUrl">OpenAI 兼容端点。</param>
    /// <returns>对话补全端点绝对 URL。</returns>
    private static string BuildEndpoint(string baseUrl)
    {
        string normalized = baseUrl.Trim().TrimEnd('/');
        return normalized + "/chat/completions";
    }

    /// <summary>
    /// 将非成功 HTTP 状态码映射为结构化错误码与可读信息，优先透传服务端 error.message。
    /// </summary>
    /// <param name="statusCode">HTTP 状态码。</param>
    /// <param name="responseBody">响应体文本。</param>
    /// <returns>失败对话响应。</returns>
    private static LlmChatResponse MapHttpError(HttpStatusCode statusCode, string responseBody)
    {
        string? serverMessage = TryReadServerErrorMessage(responseBody);
        string message = statusCode switch
        {
            HttpStatusCode.Unauthorized => "认证失败，请检查 API Key 是否正确。",
            HttpStatusCode.Forbidden => "访问被拒绝，请检查 API Key 权限。",
            HttpStatusCode.BadRequest => "请求参数不合法，请调整后重试。",
            HttpStatusCode.NotFound => "请求端点不存在，请检查 BaseUrl 配置。",
            HttpStatusCode.TooManyRequests => "请求过于频繁，触发限流，请稍后重试。",
            _ => (int)statusCode >= 500
                ? "LLM 服务端错误，请稍后重试。"
                : $"请求失败（HTTP {(int)statusCode}）。"
        };

        if (!string.IsNullOrWhiteSpace(serverMessage))
        {
            // 服务端错误描述截断至 200 字符，避免超长文本进入用户可见错误信息
            string truncated = serverMessage.Length > 200 ? serverMessage[..200] + "…" : serverMessage;
            message += $" 服务端描述：{truncated}";
        }

        string errorCode = statusCode switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => "auth",
            HttpStatusCode.BadRequest or HttpStatusCode.NotFound => "invalid_request",
            HttpStatusCode.TooManyRequests => "rate_limit",
            _ when (int)statusCode >= 500 => "upstream",
            _ => "http_error"
        };

        return LlmChatResponse.Failure(errorCode, message);
    }

    /// <summary>
    /// 尝试读取 OpenAI 兼容错误体中的 error.message，body 非 JSON 或字段缺失时返回 null。
    /// </summary>
    /// <param name="responseBody">错误响应体文本。</param>
    /// <returns>服务端错误描述，无法读取时返回 null。</returns>
    private static string? TryReadServerErrorMessage(string responseBody)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(responseBody);
            if (document.RootElement.TryGetProperty("error", out JsonElement error)
                && error.TryGetProperty("message", out JsonElement message)
                && message.ValueKind == JsonValueKind.String)
            {
                return message.GetString();
            }
        }
        catch (JsonException)
        {
            // 错误体非 JSON 时忽略解析，走兜底错误文案
        }

        return null;
    }

    /// <summary>
    /// 解析成功响应体，抽取 choices[0].message.content；结构不满足时返回结构化错误。
    /// </summary>
    /// <param name="responseBody">成功响应体文本。</param>
    /// <returns>对话响应。</returns>
    private LlmChatResponse ParseSuccessBody(string responseBody)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(responseBody);
            if (document.RootElement.TryGetProperty("choices", out JsonElement choices)
                && choices.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement choice in choices.EnumerateArray())
                {
                    if (choice.TryGetProperty("message", out JsonElement message)
                        && message.TryGetProperty("content", out JsonElement content)
                        && content.ValueKind == JsonValueKind.String)
                    {
                        string text = content.GetString() ?? string.Empty;
                        if (text.Length > 0)
                        {
                            return LlmChatResponse.Success(text);
                        }
                    }
                }
            }

            return LlmChatResponse.Failure("invalid_response", "LLM 响应缺少有效内容。");
        }
        catch (JsonException exception)
        {
            _logger.LogWarning(exception, "LLM 成功响应体解析失败。");
            return LlmChatResponse.Failure("invalid_response", "LLM 响应不是合法 JSON。");
        }
    }
}

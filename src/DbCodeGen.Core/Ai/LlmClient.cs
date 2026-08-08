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

        // 消息列表兜底空集合，防止外部误置 null 导致装配请求体时空引用
        List<LlmChatMessage> messages = request.Messages ?? new List<LlmChatMessage>();

        // 组装 OpenAI 兼容请求体：model + messages + 可选 temperature；仅显式指定时携带 max_tokens，其余由服务端默认
        var payload = new Dictionary<string, object?>
        {
            ["model"] = model,
            ["messages"] = messages.Select(message => new { role = message.Role, content = message.Content })
        };
        if (request.MaxTokens.HasValue)
        {
            payload["max_tokens"] = request.MaxTokens.Value;
        }

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
            string detail = ExtractInnermostMessage(exception) ?? "请检查网络连接。";
            return LlmChatResponse.Failure("network", $"网络请求失败：{detail}");
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
    /// 测试 LLM 连接：以最小对话请求验证端点、鉴权与模型可用性，复用对话补全的鉴权与错误映射。
    /// </summary>
    /// <param name="options">瞬态调用配置，含端点、模型与明文 apiKey。</param>
    /// <param name="cancellationToken">取消标记。</param>
    /// <returns>对话响应，成功含内容，失败含结构化错误信息。</returns>
    public async Task<LlmChatResponse> TestConnectionAsync(
        LlmClientOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);

        // 最小对话请求验证端点/鉴权/模型，模型未显式指定时回退使用 options.Model；
        // 不设 max_tokens 上限，由服务端默认输出处理，避免推理模型思考阶段耗尽上限导致 content 为空
        var request = new LlmChatRequest
        {
            Model = string.Empty,
            Messages = new List<LlmChatMessage> { new() { Role = "user", Content = "ping" } }
        };
        return await ChatCompletionAsync(request, options, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 读取端点支持的模型列表，经 GET {baseUrl}/models 拉取 data[].id；端点未配置、不支持或调用失败时返回空集合。
    /// 模型列表拉取为尽力而为，不因失败抛出业务异常，供设置界面刷新模型下拉。
    /// </summary>
    /// <param name="options">瞬态调用配置，含端点与明文 apiKey。</param>
    /// <param name="cancellationToken">取消标记。</param>
    /// <returns>模型名清单，读取失败时为空集合。</returns>
    public async Task<IReadOnlyList<string>> ListModelsAsync(
        LlmClientOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);

        // 端点未配置或非合法绝对 URL 时无法拉取模型列表，直接返回空集合
        string endpoint = BuildEndpoint(options.BaseUrl, "/models");
        if (string.IsNullOrWhiteSpace(options.BaseUrl) || !Uri.TryCreate(endpoint, UriKind.Absolute, out _))
        {
            return Array.Empty<string>();
        }

        using HttpRequestMessage httpRequest = new(HttpMethod.Get, endpoint);
        if (!string.IsNullOrWhiteSpace(options.ApiKey))
        {
            httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.ApiKey);
        }

        try
        {
            HttpResponseMessage httpResponse = await _httpClient.SendAsync(httpRequest, cancellationToken).ConfigureAwait(false);
            using (httpResponse)
            {
                if (!httpResponse.IsSuccessStatusCode)
                {
                    return Array.Empty<string>();
                }

                string responseBody = await httpResponse.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                return ParseModelList(responseBody);
            }
        }
        catch (HttpRequestException exception)
        {
            // 网络层失败仅记调试日志，返回空集合由调用方按保留候选处理
            _logger.LogDebug(exception, "读取 LLM 模型列表网络失败，端点 {Endpoint}。", endpoint);
            return Array.Empty<string>();
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // 非用户取消的等待超时同样按拉取失败处理，返回空集合
            _logger.LogDebug("读取 LLM 模型列表超时，端点 {Endpoint}。", endpoint);
            return Array.Empty<string>();
        }
    }

    /// <summary>
    /// 解析 /models 响应体，抽取 data 数组中每项的 id 字段，非 JSON 或结构不满足时返回空集合。
    /// </summary>
    /// <param name="responseBody">模型列表响应体文本。</param>
    /// <returns>模型名清单，解析失败时为空集合。</returns>
    private static IReadOnlyList<string> ParseModelList(string responseBody)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(responseBody);
            if (!document.RootElement.TryGetProperty("data", out JsonElement data)
                || data.ValueKind != JsonValueKind.Array)
            {
                return Array.Empty<string>();
            }

            var models = new List<string>();
            foreach (JsonElement item in data.EnumerateArray())
            {
                if (item.TryGetProperty("id", out JsonElement id) && id.ValueKind == JsonValueKind.String)
                {
                    string modelId = id.GetString() ?? string.Empty;
                    if (!string.IsNullOrWhiteSpace(modelId))
                    {
                        models.Add(modelId.Trim());
                    }
                }
            }

            return models;
        }
        catch (JsonException)
        {
            return Array.Empty<string>();
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
    /// 读取异常链最内层的具体原因消息，HTTP 封装异常的 Message 常为通用文案，真实原因在内部异常。
    /// </summary>
    /// <param name="exception">HTTP 请求异常。</param>
    /// <returns>最内层原因文本；无法获取时返回 null。</returns>
    private static string? ExtractInnermostMessage(Exception exception)
    {
        Exception? current = exception;
        while (current.InnerException is not null)
        {
            current = current.InnerException;
        }

        string? message = current.Message;
        return string.IsNullOrWhiteSpace(message) ? null : message;
    }

    /// <summary>
    /// 拼接对话补全端点，去除 BaseUrl 尾部斜杠后追加 /chat/completions；BaseUrl 已含该路径时不重复拼接。
    /// </summary>
    /// <param name="baseUrl">OpenAI 兼容端点。</param>
    /// <returns>对话补全端点绝对 URL。</returns>
    private static string BuildEndpoint(string baseUrl)
    {
        return BuildEndpoint(baseUrl, "/chat/completions");
    }

    /// <summary>
    /// 拼接端点，去除 BaseUrl 尾部斜杠后追加目标路径；BaseUrl 已含目标路径时不重复拼接，避免拼出 404 路径。
    /// </summary>
    /// <param name="baseUrl">OpenAI 兼容端点。</param>
    /// <param name="suffix">目标路径，如 /chat/completions 或 /models。</param>
    /// <returns>端点绝对 URL。</returns>
    private static string BuildEndpoint(string baseUrl, string suffix)
    {
        string normalized = baseUrl.Trim().TrimEnd('/');
        if (normalized.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
        {
            return normalized;
        }

        return normalized + suffix;
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
    /// 解析成功响应体，抽取 choices 中首个非空 message 文本；content 为空时回退 reasoning_content
    /// （DeepSeek 推理模型可能返回 content 为 null、答案在 reasoning_content）。结构不满足时返回结构化错误。
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
                    if (!choice.TryGetProperty("message", out JsonElement message))
                    {
                        continue;
                    }

                    // 优先取 content；DeepSeek 推理模型的答案可能位于 reasoning_content
                    string? text = TryReadMessageText(message, "content") ?? TryReadMessageText(message, "reasoning_content");
                    if (!string.IsNullOrEmpty(text))
                    {
                        return LlmChatResponse.Success(text);
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

    /// <summary>
    /// 读取 message 对象中指定属性的非空字符串值，属性缺失或为空串时返回 null。
    /// </summary>
    /// <param name="message">message JSON 元素。</param>
    /// <param name="propertyName">目标属性名，如 content / reasoning_content。</param>
    /// <returns>非空文本；属性缺失或值为空串时返回 null。</returns>
    private static string? TryReadMessageText(JsonElement message, string propertyName)
    {
        if (message.TryGetProperty(propertyName, out JsonElement element)
            && element.ValueKind == JsonValueKind.String)
        {
            string text = element.GetString() ?? string.Empty;
            return text.Length > 0 ? text : null;
        }

        return null;
    }
}

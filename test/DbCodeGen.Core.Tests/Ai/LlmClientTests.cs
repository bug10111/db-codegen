using System.Collections.Concurrent;
using System.Net;
using System.Text;
using DbCodeGen.Core.Ai;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DbCodeGen.Core.Tests.Ai;

/// <summary>
/// LLM 客户端测试连接与模型列表方法单元测试，使用 FakeHttpMessageHandler mock LLM 端点。
/// </summary>
public class LlmClientTests
{
    /// <summary>
    /// 测试连接在端点返回正常对话响应时应判定成功。
    /// </summary>
    [Fact]
    public async Task TestConnectionAsync_ValidResponse_ReturnsSuccess()
    {
        FakeHttpMessageHandler handler = new(_ =>
            JsonResponse(HttpStatusCode.OK, """{"choices":[{"message":{"content":"pong"}}]}"""));
        LlmClient client = new(NullLogger<LlmClient>.Instance, new HttpClient(handler));

        LlmChatResponse response = await client.TestConnectionAsync(CreateOptions(), CancellationToken.None);

        Assert.True(response.IsSuccess);
        Assert.Equal("pong", response.Content);
    }

    /// <summary>
    /// 测试连接在鉴权失败（HTTP 401）时应返回可读失败信息。
    /// </summary>
    [Fact]
    public async Task TestConnectionAsync_AuthError_ReturnsFailureMessage()
    {
        FakeHttpMessageHandler handler = new(_ =>
            JsonResponse(HttpStatusCode.Unauthorized, """{"error":{"message":"invalid api key"}}"""));
        LlmClient client = new(NullLogger<LlmClient>.Instance, new HttpClient(handler));

        LlmChatResponse response = await client.TestConnectionAsync(CreateOptions(), CancellationToken.None);

        Assert.False(response.IsSuccess);
        Assert.Contains("API Key", response.ErrorMessage);
    }

    /// <summary>
    /// DeepSeek 推理模型可能返回 content 为 null、答案在 reasoning_content，解析应回退读取并判定连接成功。
    /// </summary>
    [Fact]
    public async Task TestConnectionAsync_ContentNullWithReasoningContent_ReturnsSuccess()
    {
        FakeHttpMessageHandler handler = new(_ =>
            JsonResponse(HttpStatusCode.OK, """{"choices":[{"message":{"role":"assistant","content":null,"reasoning_content":"thinking…"}}]}"""));
        LlmClient client = new(NullLogger<LlmClient>.Instance, new HttpClient(handler));

        LlmChatResponse response = await client.TestConnectionAsync(CreateOptions(), CancellationToken.None);

        Assert.True(response.IsSuccess);
        Assert.Equal("thinking…", response.Content);
    }

    /// <summary>
    /// 测试连接请求不应携带 max_tokens 限制，由服务端默认输出处理，避免推理模型输出被截断。
    /// </summary>
    [Fact]
    public async Task TestConnectionAsync_DoesNotSetMaxTokens()
    {
        var requestBodies = new ConcurrentQueue<string>();
        FakeHttpMessageHandler handler = new(request =>
        {
            requestBodies.Enqueue(ReadRequestBody(request));
            return JsonResponse(HttpStatusCode.OK, """{"choices":[{"message":{"content":"pong"}}]}""");
        });
        LlmClient client = new(NullLogger<LlmClient>.Instance, new HttpClient(handler));

        LlmChatResponse response = await client.TestConnectionAsync(CreateOptions(), CancellationToken.None);

        Assert.True(response.IsSuccess);
        Assert.DoesNotContain("max_tokens", Assert.Single(requestBodies));
    }

    /// <summary>
    /// 同步读取请求体文本，供测试处理器内联断言使用。
    /// </summary>
    private static string ReadRequestBody(HttpRequestMessage request)
    {
        return request.Content?.ReadAsStringAsync().GetAwaiter().GetResult() ?? string.Empty;
    }

    /// <summary>
    /// 网络层失败应携带底层具体原因（DNS/连接/TLS 等），便于用户据此定位。
    /// </summary>
    [Fact]
    public async Task TestConnectionAsync_NetworkError_IncludesUnderlyingDetail()
    {
        FakeHttpMessageHandler handler = new(_ =>
            throw new HttpRequestException(
                "An error occurred while sending the request.",
                new Exception("无法解析主机名 'api.deepseek.com'")));
        LlmClient client = new(NullLogger<LlmClient>.Instance, new HttpClient(handler));

        LlmChatResponse response = await client.TestConnectionAsync(CreateOptions(), CancellationToken.None);

        Assert.False(response.IsSuccess);
        Assert.Contains("无法解析主机名", response.ErrorMessage);
    }

    /// <summary>
    /// 模型列表应解析 /models 响应中 data 数组的 id 字段。
    /// </summary>
    [Fact]
    public async Task ListModelsAsync_ValidResponse_ParsesModelIds()
    {
        FakeHttpMessageHandler handler = new(request =>
        {
            Assert.Equal("https://example.com/v1/models", request.RequestUri?.ToString());
            return JsonResponse(HttpStatusCode.OK, """{"data":[{"id":"qwen-plus"},{"id":"qwen-max"}]}""");
        });
        LlmClient client = new(NullLogger<LlmClient>.Instance, new HttpClient(handler));

        IReadOnlyList<string> models = await client.ListModelsAsync(CreateOptions(), CancellationToken.None);

        Assert.Equal(new[] { "qwen-plus", "qwen-max" }, models);
    }

    /// <summary>
    /// 模型列表在端点返回错误状态码时应返回空集合，不抛异常。
    /// </summary>
    [Fact]
    public async Task ListModelsAsync_HttpError_ReturnsEmpty()
    {
        FakeHttpMessageHandler handler = new(_ => JsonResponse(HttpStatusCode.InternalServerError, "{}"));
        LlmClient client = new(NullLogger<LlmClient>.Instance, new HttpClient(handler));

        IReadOnlyList<string> models = await client.ListModelsAsync(CreateOptions(), CancellationToken.None);

        Assert.Empty(models);
    }

    /// <summary>
    /// 模型列表在端点未配置或非合法 URL 时应直接返回空集合，不发网络请求。
    /// </summary>
    [Fact]
    public async Task ListModelsAsync_InvalidBaseUrl_ReturnsEmptyWithoutCall()
    {
        FakeHttpMessageHandler handler = new(_ => throw new InvalidOperationException("不应发起网络请求"));
        LlmClient client = new(NullLogger<LlmClient>.Instance, new HttpClient(handler));

        IReadOnlyList<string> models = await client.ListModelsAsync(
            new LlmClientOptions { BaseUrl = "not-a-url", Model = "qwen-plus", ApiKey = "key" },
            CancellationToken.None);

        Assert.Empty(models);
    }

    /// <summary>
    /// 测试连接在接口地址已含 /chat/completions 时不应重复拼接，避免拼出 404 路径。
    /// </summary>
    [Fact]
    public async Task TestConnectionAsync_BaseUrlEndsWithChatCompletions_NoDoubleAppend()
    {
        FakeHttpMessageHandler handler = new(request =>
        {
            Assert.Equal("https://api.deepseek.com/chat/completions", request.RequestUri?.ToString());
            return JsonResponse(HttpStatusCode.OK, """{"choices":[{"message":{"content":"pong"}}]}""");
        });
        LlmClient client = new(NullLogger<LlmClient>.Instance, new HttpClient(handler));

        var options = new LlmClientOptions
        {
            BaseUrl = "https://api.deepseek.com/chat/completions",
            Model = "deepseek-chat",
            ApiKey = "test-key"
        };
        LlmChatResponse response = await client.TestConnectionAsync(options, CancellationToken.None);

        Assert.True(response.IsSuccess);
    }

    /// <summary>
    /// 模型列表在接口地址已含 /models 时不应重复拼接，避免拼出 404 路径。
    /// </summary>
    [Fact]
    public async Task ListModelsAsync_BaseUrlEndsWithModels_NoDoubleAppend()
    {
        FakeHttpMessageHandler handler = new(request =>
        {
            Assert.Equal("https://example.com/v1/models", request.RequestUri?.ToString());
            return JsonResponse(HttpStatusCode.OK, """{"data":[{"id":"qwen-plus"}]}""");
        });
        LlmClient client = new(NullLogger<LlmClient>.Instance, new HttpClient(handler));

        var options = new LlmClientOptions
        {
            BaseUrl = "https://example.com/v1/models",
            Model = "qwen-plus",
            ApiKey = "test-key"
        };
        IReadOnlyList<string> models = await client.ListModelsAsync(options, CancellationToken.None);

        Assert.Single(models);
    }

    /// <summary>
    /// 构造带默认端点、模型与密钥的瞬态调用配置。
    /// </summary>
    private static LlmClientOptions CreateOptions()
    {
        return new LlmClientOptions
        {
            BaseUrl = "https://example.com/v1",
            Model = "qwen-plus",
            ApiKey = "test-key"
        };
    }

    /// <summary>
    /// 构造 JSON 内容响应。
    /// </summary>
    private static HttpResponseMessage JsonResponse(HttpStatusCode statusCode, string json)
    {
        return new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
    }

    /// <summary>
    /// mock LLM 端点的 HTTP 消息处理器，按请求返回响应。
    /// </summary>
    private sealed class FakeHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;

        public FakeHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
        {
            _responder = responder;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(_responder(request));
        }
    }
}

using System.Net;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using DbCodeGen.Core.Ai;
using DbCodeGen.Core.Config;
using DbCodeGen.Core.Security;
using Microsoft.Extensions.Logging.Abstractions;

namespace DbCodeGen.Core.Tests.Ai;

/// <summary>
/// TemplateAiModifier AI 改模板对话服务单元测试，使用 FakeHttpMessageHandler mock LLM 端点，
/// 覆盖代码围栏剥离、内容非空校验、参考文件段落注入、多轮历史回放与鉴权端点等验收要点。
/// 目标守卫（目标文件比对 + 内容一致性确认）由改模板 Tab VM（T10）承载并单测，本测试不承载守卫逻辑。
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class TemplateAiModifierTests : IDisposable
{
    private const string TestApiKey = "test-secret-api-key";
    private const string TestModel = "test-model";
    private const string TestBaseUrl = "https://llm.test.example/v1";

    private const string TemplateSpec = "TEMPLATE_SPEC：表字段 table.rawName/table.className/table.variableName/table.comment，" +
        "列字段 column.propertyName/column.rawDbType/column.isPrimaryKey，tool 函数 firstLowerCase/firstUpperCase/" +
        "hump2Underline/hump3Underline/type，engine 固定 scriban。";

    private readonly string _tempRoot;
    private readonly CredentialProtector _protector = new();
    private readonly List<ConfigService> _configServices = new();

    /// <summary>
    /// 为每个测试实例创建独立临时目录，避免用例间配置与临时文件互相污染。
    /// </summary>
    public TemplateAiModifierTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "DbCodeGenTests", "AiModifier", Guid.NewGuid().ToString("N"));
    }

    /// <summary>
    /// 释放配置服务并递归删除测试临时目录。
    /// </summary>
    public void Dispose()
    {
        foreach (ConfigService configService in _configServices)
        {
            configService.Dispose();
        }

        if (Directory.Exists(_tempRoot))
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
    }

    /// <summary>
    /// 创建指向测试临时目录的配置服务，写入带密文 apiKey 的 LLM 配置。
    /// </summary>
    /// <returns>配置服务实例。</returns>
    private ConfigService CreateConfigService()
    {
        string configPath = Path.Combine(_tempRoot, "config", Guid.NewGuid().ToString("N"), "config.json");
        ConfigService config = new(_protector, NullLogger<ConfigService>.Instance, configPath);
        _configServices.Add(config);
        AppConfig appConfig = config.Load();
        appConfig.Llm = new LlmConfig
        {
            BaseUrl = TestBaseUrl,
            ApiKeyEncrypted = _protector.Encrypt(TestApiKey),
            Model = TestModel
        };
        config.Save();
        return config;
    }

    /// <summary>
    /// 创建 AI 改模板服务，注入 FakeHttpMessageHandler 支撑的 LLM 客户端。
    /// </summary>
    /// <param name="config">配置服务。</param>
    /// <param name="handler">mock LLM 端点的消息处理器。</param>
    /// <param name="templateSpecText">TEMPLATE_SPEC 文本，为空时走嵌入资源。</param>
    /// <returns>AI 改模板服务实例。</returns>
    private TemplateAiModifier CreateModifier(
        ConfigService config,
        FakeHttpMessageHandler handler,
        string? templateSpecText = null)
    {
        HttpClient httpClient = new(handler) { BaseAddress = new Uri(TestBaseUrl) };
        LlmClient llmClient = new(NullLogger<LlmClient>.Instance, httpClient);
        return new TemplateAiModifier(
            llmClient,
            config,
            NullLogger<TemplateAiModifier>.Instance,
            templateSpecText);
    }

    /// <summary>
    /// 构造改模板请求，含当前文件快照与修改指令。
    /// </summary>
    /// <returns>改模板请求实例。</returns>
    private static AiModifyTemplateRequest BuildRequest()
    {
        return new AiModifyTemplateRequest
        {
            CurrentTemplateFilePath = "entity.java.scriban",
            CurrentTemplateContent = "class {{table.className}} {\n  private Long id;\n}",
            ModificationInstruction = "给 id 字段加 @TableId 主键注解"
        };
    }

    /// <summary>
    /// 构造 OpenAI 兼容成功响应体。
    /// </summary>
    /// <param name="content">choices[0].message.content 内容。</param>
    /// <returns>响应体 JSON 文本。</returns>
    private static string BuildLlmSuccessBody(string content)
    {
        return JsonSerializer.Serialize(new
        {
            choices = new[]
            {
                new { message = new { role = "assistant", content } }
            }
        });
    }

    /// <summary>
    /// 构造指定状态码与响应体的 HTTP 响应。
    /// </summary>
    /// <param name="statusCode">HTTP 状态码。</param>
    /// <param name="body">响应体文本。</param>
    /// <returns>HTTP 响应消息。</returns>
    private static HttpResponseMessage CreateJsonResponse(HttpStatusCode statusCode, string body)
    {
        return new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
    }

    /// <summary>
    /// 从 LLM 请求体 JSON 中提取首条 user 消息内容，用于断言提示词注入结果。
    /// </summary>
    /// <param name="requestBody">LLM 请求体 JSON 文本。</param>
    /// <returns>用户消息内容文本。</returns>
    private static string GetUserPromptFromRequestBody(string requestBody)
    {
        using JsonDocument document = JsonDocument.Parse(requestBody);
        JsonElement messagesElement = document.RootElement.GetProperty("messages");

        // 提取 role 为 user 的首条消息
        JsonElement userMessageElement = messagesElement
            .EnumerateArray()
            .First(message => message.GetProperty("role").GetString() == "user");

        // 取消息 content 文本作为用户提示词
        return userMessageElement.GetProperty("content").GetString() ?? string.Empty;
    }

    /// <summary>
    /// 从 LLM 请求体 JSON 中提取全部消息的 (role, content) 有序清单，用于断言多轮历史回放顺序。
    /// </summary>
    /// <param name="requestBody">LLM 请求体 JSON 文本。</param>
    /// <returns>消息有序清单。</returns>
    private static List<(string Role, string Content)> GetMessagesFromRequestBody(string requestBody)
    {
        using JsonDocument document = JsonDocument.Parse(requestBody);
        var messages = new List<(string Role, string Content)>();
        foreach (JsonElement message in document.RootElement.GetProperty("messages").EnumerateArray())
        {
            string role = message.GetProperty("role").GetString() ?? string.Empty;
            string content = message.GetProperty("content").GetString() ?? string.Empty;
            messages.Add((role, content));
        }

        return messages;
    }

    /// <summary>
    /// LLM 返回带代码围栏的输出应剥离围栏后作为完整新文件返回。
    /// </summary>
    [Fact]
    public async Task ModifyAsync_ValidOutput_StripsCodeFenceAndReturnsNewContent()
    {
        ConfigService config = CreateConfigService();
        string fenced = "```scriban\nclass {{table.className}} {\n  @TableId(type = IdType.AUTO)\n  private Long id;\n}\n```";
        FakeHttpMessageHandler handler = new(_ => CreateJsonResponse(HttpStatusCode.OK, BuildLlmSuccessBody(fenced)));
        TemplateAiModifier modifier = CreateModifier(config, handler, TemplateSpec);

        AiModifyTemplateResult result = await modifier.ModifyAsync(BuildRequest(), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(
            "class {{table.className}} {\n  @TableId(type = IdType.AUTO)\n  private Long id;\n}",
            result.NewContent);
        Assert.Empty(result.Errors);
        Assert.Equal(fenced, result.RawLlmOutput);
    }

    /// <summary>
    /// 无围栏包裹的普通输出应原样保留并作为完整新文件返回。
    /// </summary>
    [Fact]
    public async Task ModifyAsync_OutputWithoutFence_KeepsContent()
    {
        ConfigService config = CreateConfigService();
        string plain = "class {{table.className}} {\n  private Long id;\n}";
        FakeHttpMessageHandler handler = new(_ => CreateJsonResponse(HttpStatusCode.OK, BuildLlmSuccessBody(plain)));
        TemplateAiModifier modifier = CreateModifier(config, handler, TemplateSpec);

        AiModifyTemplateResult result = await modifier.ModifyAsync(BuildRequest(), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(plain, result.NewContent);
    }

    /// <summary>
    /// 剥离代码围栏后内容为空应结构化失败并保留原始输出供人工查看。
    /// </summary>
    [Fact]
    public async Task ModifyAsync_FenceWrappedEmptyContent_FailsWithEmptyError()
    {
        ConfigService config = CreateConfigService();
        string empty = "```\n```";
        FakeHttpMessageHandler handler = new(_ => CreateJsonResponse(HttpStatusCode.OK, BuildLlmSuccessBody(empty)));
        TemplateAiModifier modifier = CreateModifier(config, handler, TemplateSpec);

        AiModifyTemplateResult result = await modifier.ModifyAsync(BuildRequest(), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Contains(result.Errors, error => error.Contains("内容为空"));
        Assert.Equal(empty, result.RawLlmOutput);
    }

    /// <summary>
    /// 全空白输出应视为内容为空结构化失败，不把空白当作有效新文件。
    /// </summary>
    [Fact]
    public async Task ModifyAsync_WhitespaceOnlyContent_FailsWithEmptyError()
    {
        ConfigService config = CreateConfigService();
        FakeHttpMessageHandler handler = new(_ => CreateJsonResponse(HttpStatusCode.OK, BuildLlmSuccessBody("   \r\n  ")));
        TemplateAiModifier modifier = CreateModifier(config, handler, TemplateSpec);

        AiModifyTemplateResult result = await modifier.ModifyAsync(BuildRequest(), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Contains(result.Errors, error => error.Contains("内容为空"));
    }

    /// <summary>
    /// 请求携带参考文件时，用户提示词应按文件名标记逐文件注入内容快照。
    /// </summary>
    [Fact]
    public async Task ModifyAsync_WithReferenceFiles_InjectsMarkedContent()
    {
        ConfigService config = CreateConfigService();
        FakeHttpMessageHandler handler = new(_ => CreateJsonResponse(HttpStatusCode.OK, BuildLlmSuccessBody("class A {}")));
        TemplateAiModifier modifier = CreateModifier(config, handler, TemplateSpec);

        AiModifyTemplateRequest request = BuildRequest();
        request.ReferenceFiles = new List<AiReferenceFileItem>
        {
            new("CommonMapper.cs", 42, "namespace Demo; class CommonMapper { }"),
            new("base.scriban", 20, "Hello from base template")
        };

        AiModifyTemplateResult result = await modifier.ModifyAsync(request, CancellationToken.None);

        Assert.True(result.IsSuccess);
        string userContent = GetUserPromptFromRequestBody(Assert.Single(handler.RequestBodies));
        Assert.Contains("### CommonMapper.cs", userContent);
        Assert.Contains("namespace Demo; class CommonMapper { }", userContent);
        Assert.Contains("### base.scriban", userContent);
        Assert.Contains("Hello from base template", userContent);
        Assert.Contains("当前模板文件：entity.java.scriban", userContent);
    }

    /// <summary>
    /// 请求不带参考文件时，用户提示词不含参考文件段落与文件名标记。
    /// </summary>
    [Fact]
    public async Task ModifyAsync_NoReferenceFiles_OmitsReferenceParagraph()
    {
        ConfigService config = CreateConfigService();
        FakeHttpMessageHandler handler = new(_ => CreateJsonResponse(HttpStatusCode.OK, BuildLlmSuccessBody("class A {}")));
        TemplateAiModifier modifier = CreateModifier(config, handler, TemplateSpec);

        AiModifyTemplateResult result = await modifier.ModifyAsync(BuildRequest(), CancellationToken.None);

        Assert.True(result.IsSuccess);
        string userContent = GetUserPromptFromRequestBody(Assert.Single(handler.RequestBodies));
        Assert.Contains("修改指令：", userContent);
        Assert.DoesNotContain("参考文件", userContent);
        Assert.DoesNotContain("### ", userContent);
    }

    /// <summary>
    /// 请求携带多轮历史时，消息列表应为 system + 历史全量回放 + 本轮 user，顺序与角色一致。
    /// </summary>
    [Fact]
    public async Task ModifyAsync_WithHistoryMessages_ReplaysAllTurnsInOrder()
    {
        ConfigService config = CreateConfigService();
        FakeHttpMessageHandler handler = new(_ => CreateJsonResponse(HttpStatusCode.OK, BuildLlmSuccessBody("class B {}")));
        TemplateAiModifier modifier = CreateModifier(config, handler, TemplateSpec);

        AiModifyTemplateRequest request = BuildRequest();
        request.HistoryMessages = new List<LlmChatMessage>
        {
            new() { Role = "user", Content = "第一轮指令：调整包名" },
            new() { Role = "assistant", Content = "第一轮结果：包名已调整" },
            new() { Role = "user", Content = "第二轮指令：补充注释" }
        };

        AiModifyTemplateResult result = await modifier.ModifyAsync(request, CancellationToken.None);

        Assert.True(result.IsSuccess);
        List<(string Role, string Content)> messages = GetMessagesFromRequestBody(Assert.Single(handler.RequestBodies));
        Assert.Equal(5, messages.Count);
        Assert.Equal("system", messages[0].Role);
        Assert.Equal("user", messages[1].Role);
        Assert.Equal("第一轮指令：调整包名", messages[1].Content);
        Assert.Equal("assistant", messages[2].Role);
        Assert.Equal("第一轮结果：包名已调整", messages[2].Content);
        Assert.Equal("user", messages[3].Role);
        Assert.Equal("第二轮指令：补充注释", messages[3].Content);
        Assert.Equal("user", messages[4].Role);
        Assert.Contains("当前模板文件：entity.java.scriban", messages[4].Content);
    }

    /// <summary>
    /// LLM 请求应使用 Bearer apiKey 鉴权并指向 /chat/completions 端点。
    /// </summary>
    [Fact]
    public async Task ModifyAsync_Request_UsesBearerAuthAndCompletionsEndpoint()
    {
        ConfigService config = CreateConfigService();
        string? capturedPath = null;
        string? capturedAuth = null;
        FakeHttpMessageHandler handler = new(request =>
        {
            capturedPath = request.RequestUri?.AbsolutePath;
            capturedAuth = request.Headers.Authorization?.ToString();
            return CreateJsonResponse(HttpStatusCode.OK, BuildLlmSuccessBody("class A {}"));
        });
        TemplateAiModifier modifier = CreateModifier(config, handler, TemplateSpec);

        AiModifyTemplateResult result = await modifier.ModifyAsync(BuildRequest(), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal("/v1/chat/completions", capturedPath);
        Assert.Equal($"Bearer {TestApiKey}", capturedAuth);
    }

    /// <summary>
    /// HTTP 认证失败应映射为可读错误，不发起重试。
    /// </summary>
    [Fact]
    public async Task ModifyAsync_HttpUnauthorized_ReturnsAuthError()
    {
        ConfigService config = CreateConfigService();
        FakeHttpMessageHandler handler = new(_ =>
            CreateJsonResponse(HttpStatusCode.Unauthorized, """{"error":{"message":"Invalid API key"}}"""));
        TemplateAiModifier modifier = CreateModifier(config, handler, TemplateSpec);

        AiModifyTemplateResult result = await modifier.ModifyAsync(BuildRequest(), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Contains(result.Errors, error => error.Contains("认证失败"));
        Assert.Equal(1, handler.CallCount);
    }

    /// <summary>
    /// LLM apiKey 未配置应直接失败，不发起网络请求。
    /// </summary>
    [Fact]
    public async Task ModifyAsync_ApiKeyNotConfigured_FailsWithoutRequest()
    {
        ConfigService config = CreateConfigService();
        config.Load().Llm.ApiKeyEncrypted = string.Empty;
        config.Save();
        FakeHttpMessageHandler handler = new(_ => CreateJsonResponse(HttpStatusCode.OK, BuildLlmSuccessBody("class A {}")));
        TemplateAiModifier modifier = CreateModifier(config, handler, TemplateSpec);

        AiModifyTemplateResult result = await modifier.ModifyAsync(BuildRequest(), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Contains(result.Errors, error => error.Contains("LLM 未配置"));
        Assert.Equal(0, handler.CallCount);
    }

    /// <summary>
    /// 修改指令为空应直接失败，不发起网络请求。
    /// </summary>
    [Fact]
    public async Task ModifyAsync_EmptyInstruction_FailsWithoutRequest()
    {
        ConfigService config = CreateConfigService();
        FakeHttpMessageHandler handler = new(_ => CreateJsonResponse(HttpStatusCode.OK, BuildLlmSuccessBody("class A {}")));
        TemplateAiModifier modifier = CreateModifier(config, handler, TemplateSpec);

        AiModifyTemplateRequest request = BuildRequest();
        request.ModificationInstruction = "   ";

        AiModifyTemplateResult result = await modifier.ModifyAsync(request, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Contains(result.Errors, error => error.Contains("修改指令不能为空"));
        Assert.Equal(0, handler.CallCount);
    }

    /// <summary>
    /// 当前文件内容为空应直接失败，不发起网络请求。
    /// </summary>
    [Fact]
    public async Task ModifyAsync_EmptyTemplateContent_FailsWithoutRequest()
    {
        ConfigService config = CreateConfigService();
        FakeHttpMessageHandler handler = new(_ => CreateJsonResponse(HttpStatusCode.OK, BuildLlmSuccessBody("class A {}")));
        TemplateAiModifier modifier = CreateModifier(config, handler, TemplateSpec);

        AiModifyTemplateRequest request = BuildRequest();
        request.CurrentTemplateContent = string.Empty;

        AiModifyTemplateResult result = await modifier.ModifyAsync(request, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Contains(result.Errors, error => error.Contains("当前模板文件内容为空"));
        Assert.Equal(0, handler.CallCount);
    }

    /// <summary>
    /// 未显式传入 TEMPLATE_SPEC 时应从嵌入资源加载规范文本注入 system 消息。
    /// </summary>
    [Fact]
    public async Task ModifyAsync_EmbeddedTemplateSpec_LoadedWhenNotProvided()
    {
        ConfigService config = CreateConfigService();
        FakeHttpMessageHandler handler = new(_ => CreateJsonResponse(HttpStatusCode.OK, BuildLlmSuccessBody("class A {}")));
        TemplateAiModifier modifier = CreateModifier(config, handler);

        AiModifyTemplateResult result = await modifier.ModifyAsync(BuildRequest(), CancellationToken.None);

        Assert.True(result.IsSuccess);
        List<(string Role, string Content)> messages = GetMessagesFromRequestBody(Assert.Single(handler.RequestBodies));
        Assert.Contains("table.className", messages[0].Content);
        Assert.Contains("tool", messages[0].Content);
    }

    /// <summary>
    /// 改模板请求不应携带 max_tokens 限制，由服务端按其默认输出上限处理，防止模型输出被截断。
    /// </summary>
    [Fact]
    public async Task ModifyAsync_RequestBody_DoesNotSetMaxTokens()
    {
        ConfigService config = CreateConfigService();
        FakeHttpMessageHandler handler = new(_ => CreateJsonResponse(HttpStatusCode.OK, BuildLlmSuccessBody("class A {}")));
        TemplateAiModifier modifier = CreateModifier(config, handler, TemplateSpec);

        AiModifyTemplateResult result = await modifier.ModifyAsync(BuildRequest(), CancellationToken.None);

        Assert.True(result.IsSuccess);
        string body = Assert.Single(handler.RequestBodies);
        using JsonDocument document = JsonDocument.Parse(body);
        Assert.False(document.RootElement.TryGetProperty("max_tokens", out _));
        Assert.Equal(TestModel, document.RootElement.GetProperty("model").GetString());
    }

    /// <summary>
    /// mock LLM 端点的 HTTP 消息处理器，记录调用次数与请求体。
    /// </summary>
    private sealed class FakeHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;

        /// <summary>
        /// 请求调用次数。
        /// </summary>
        public int CallCount { get; private set; }

        /// <summary>
        /// 每次请求体文本集合。
        /// </summary>
        public List<string> RequestBodies { get; } = new();

        /// <summary>
        /// 使用响应委托创建消息处理器。
        /// </summary>
        /// <param name="responder">按请求返回响应的委托。</param>
        public FakeHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
        {
            _responder = responder;
        }

        /// <summary>
        /// 记录请求体并委托响应，全程同步返回。
        /// </summary>
        /// <param name="request">HTTP 请求。</param>
        /// <param name="cancellationToken">取消标记。</param>
        /// <returns>委托生成的响应。</returns>
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            string body = request.Content is null
                ? string.Empty
                : request.Content.ReadAsStringAsync(cancellationToken).GetAwaiter().GetResult();
            RequestBodies.Add(body);
            return Task.FromResult(_responder(request));
        }
    }
}

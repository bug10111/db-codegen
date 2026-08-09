using System.Net;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using DbCodeGen.Core.Ai;
using DbCodeGen.Core.Config;
using DbCodeGen.Core.Model;
using DbCodeGen.Core.Security;
using DbCodeGen.Core.Templates.Packages;
using Microsoft.Extensions.Logging.Abstractions;

namespace DbCodeGen.Core.Tests.Ai;

/// <summary>
/// TemplateAiGenerator AI 模板生成服务单元测试，使用 FakeHttpMessageHandler mock LLM 端点，
/// 覆盖提示词组装、非法 JSON 重试、HTTP 错误映射、临时目录清理、校验落库与包名冲突等验收要点。
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class TemplateAiGeneratorTests : IDisposable
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
    private readonly List<TemplatePackageService> _services = new();

    /// <summary>
    /// 为每个测试实例创建独立临时目录，避免用例间模板库与临时文件互相污染。
    /// </summary>
    public TemplateAiGeneratorTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "DbCodeGenTests", "Ai", Guid.NewGuid().ToString("N"));
    }

    /// <summary>
    /// 释放配置服务与模板包服务并递归删除测试临时目录。
    /// </summary>
    public void Dispose()
    {
        foreach (ConfigService configService in _configServices)
        {
            configService.Dispose();
        }

        foreach (TemplatePackageService service in _services)
        {
            service.Dispose();
        }

        if (Directory.Exists(_tempRoot))
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
    }

    /// <summary>
    /// 创建指向测试临时目录的配置服务，写入带密文 apiKey 的 LLM 配置与用户模板库目录。
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
        appConfig.TemplateSearchDirectories.Clear();
        appConfig.TemplateSearchDirectories.Add(Path.Combine(_tempRoot, "user-library"));
        config.Save();
        return config;
    }

    /// <summary>
    /// 创建模板包管理服务，内置包根与导入临时目录均指向测试临时目录。
    /// </summary>
    /// <returns>模板包服务实例。</returns>
    private TemplatePackageService CreatePackageService()
    {
        TemplatePackageService service = new(
            _configServices[^1],
            NullLogger<TemplatePackageService>.Instance,
            Path.Combine(_tempRoot, "builtin-root"),
            Path.Combine(_tempRoot, "import-temp"));
        _services.Add(service);
        return service;
    }

    /// <summary>
    /// 创建 AI 生成服务，注入 FakeHttpMessageHandler 支撑的 LLM 客户端。
    /// </summary>
    /// <param name="config">配置服务。</param>
    /// <param name="service">模板包服务。</param>
    /// <param name="handler">mock LLM 端点的消息处理器。</param>
    /// <param name="templateSpecText">TEMPLATE_SPEC 文本，为空时走嵌入资源。</param>
    /// <returns>AI 生成服务实例。</returns>
    private TemplateAiGenerator CreateGenerator(
        ConfigService config,
        TemplatePackageService service,
        FakeHttpMessageHandler handler,
        string? templateSpecText = null)
    {
        HttpClient httpClient = new(handler) { BaseAddress = new Uri(TestBaseUrl) };
        LlmClient llmClient = new(NullLogger<LlmClient>.Instance, httpClient);
        return new TemplateAiGenerator(
            llmClient,
            config,
            service,
            NullLogger<TemplateAiGenerator>.Instance,
            templateSpecText,
            Path.Combine(_tempRoot, "generator-temp"));
    }

    /// <summary>
    /// 构造样例表元数据，含主键与普通列。
    /// </summary>
    /// <returns>样例表实体。</returns>
    private static TableInfo BuildSampleTable()
    {
        TableInfo table = new()
        {
            RawName = "sys_user",
            SchemaName = "test_db",
            ClassName = "SysUser",
            VariableName = "sysUser",
            Comment = "用户表"
        };
        table.SetColumns(new[]
        {
            new ColumnInfo
            {
                RawName = "id",
                PropertyName = "id",
                RawDbType = "bigint",
                IsPrimaryKey = true,
                AutoIncrement = true,
                IsNullable = false,
                Comment = "主键"
            },
            new ColumnInfo
            {
                RawName = "user_name",
                PropertyName = "userName",
                RawDbType = "varchar",
                IsNullable = false,
                Length = 64,
                Comment = "用户名"
            }
        });
        return table;
    }

    /// <summary>
    /// 构造 AI 生成请求。
    /// </summary>
    /// <returns>生成请求实例。</returns>
    private static AiTemplateGenerationRequest BuildRequest()
    {
        return new AiTemplateGenerationRequest
        {
            TechStackDescription = "Java + MyBatis-Plus，三层分层",
            SampleTable = BuildSampleTable()
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
    /// 构造 GeneratedPackageDocument JSON 文本。
    /// </summary>
    /// <param name="packageName">包名。</param>
    /// <param name="files">模板文件条目三元组。</param>
    /// <returns>包文档 JSON 文本。</returns>
    private static string BuildPackageDocumentJson(
        string packageName,
        params (string Name, string Output, string Content)[] files)
    {
        return JsonSerializer.Serialize(new
        {
            packageName,
            description = "AI 生成的测试模板包",
            basePackage = "com.example",
            typeMap = new Dictionary<string, string> { ["bigint"] = "Long" },
            files = files.Select(file => new
            {
                name = file.Name,
                relativeOutputPath = file.Output,
                enabled = true,
                content = file.Content
            })
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
    /// 从 LLM 请求体 JSON 中提取首条用户消息内容，用于断言提示词注入结果。
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
    /// 在指定根目录下创建含一个模板文件的合法模板包。
    /// </summary>
    /// <param name="rootDirectory">包根目录。</param>
    /// <param name="packageName">包名。</param>
    private static async Task CreatePackageDirAsync(string rootDirectory, string packageName)
    {
        string packageDir = Path.Combine(rootDirectory, packageName);
        Directory.CreateDirectory(packageDir);
        var manifest = new TemplateManifest
        {
            Name = packageName,
            Description = "测试包",
            Engine = "scriban",
            Files = new List<TemplateFileEntry>
            {
                new()
                {
                    Template = "entity.java.scriban",
                    Output = "{{package.dir}}/entity/{{table.className}}.java",
                    Enabled = true
                }
            }
        };
        await File.WriteAllTextAsync(
            Path.Combine(packageDir, TemplatePackageLoader.ManifestFileName),
            JsonSerializer.Serialize(manifest, TemplatePackageLoader.JsonOptions));
        await File.WriteAllTextAsync(Path.Combine(packageDir, "entity.java.scriban"), "class {{table.className}} {}");
    }

    /// <summary>
    /// 合法 LLM 输出应成功提交落库：manifest 映射正确、模板文件写入、临时目录清理。
    /// </summary>
    [Fact]
    public async Task GenerateAsync_ValidOutput_CommitsPackageAndCleansTemp()
    {
        ConfigService config = CreateConfigService();
        TemplatePackageService service = CreatePackageService();
        string packageJson = BuildPackageDocumentJson(
            "ai-test-pkg",
            ("entity.java.scriban", "{{package.dir}}/entity/{{table.className}}.java", "package {{package.basePackage}}; class {{table.className}} {}"));
        FakeHttpMessageHandler handler = new(_ => CreateJsonResponse(HttpStatusCode.OK, BuildLlmSuccessBody(packageJson)));
        TemplateAiGenerator generator = CreateGenerator(config, service, handler, TemplateSpec);

        AiTemplateGenerationResult result = await generator.GenerateAsync(BuildRequest(), overwrite: false, CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal("ai-test-pkg", result.PackageName);
        Assert.NotNull(result.TemplateDir);
        Assert.Empty(result.Errors);

        // 校验落库的 template.json 与模板文件，engine 固定 scriban
        string userLibrary = Path.Combine(_tempRoot, "user-library");
        string manifestPath = Path.Combine(userLibrary, "ai-test-pkg", "template.json");
        Assert.True(File.Exists(manifestPath));
        Assert.True(File.Exists(Path.Combine(userLibrary, "ai-test-pkg", "entity.java.scriban")));

        TemplateManifest manifest = JsonSerializer.Deserialize<TemplateManifest>(
            await File.ReadAllTextAsync(manifestPath), TemplatePackageLoader.JsonOptions)!;
        Assert.Equal("scriban", manifest.Engine);
        Assert.Equal("ai-test-pkg", manifest.Name);
        Assert.Equal("com.example", manifest.BasePackage);
        Assert.Single(manifest.Files);
        Assert.Equal("entity.java.scriban", manifest.Files[0].Template);
        Assert.Equal("{{package.dir}}/entity/{{table.className}}.java", manifest.Files[0].Output);

        // 临时目录已清理，不留脏数据
        Assert.False(Directory.Exists(Path.Combine(_tempRoot, "generator-temp", "ai-test-pkg")));
    }

    /// <summary>
    /// LLM 请求应使用 Bearer apiKey 鉴权并指向 /chat/completions 端点。
    /// </summary>
    [Fact]
    public async Task GenerateAsync_Request_UsesBearerAuthAndCompletionsEndpoint()
    {
        ConfigService config = CreateConfigService();
        TemplatePackageService service = CreatePackageService();
        string packageJson = BuildPackageDocumentJson("auth-pkg", ("entity.java.scriban", "{{package.dir}}/entity/{{table.className}}.java", "class {{table.className}} {}"));

        string? capturedPath = null;
        string? capturedAuth = null;
        FakeHttpMessageHandler handler = new(request =>
        {
            capturedPath = request.RequestUri?.AbsolutePath;
            capturedAuth = request.Headers.Authorization?.ToString();
            return CreateJsonResponse(HttpStatusCode.OK, BuildLlmSuccessBody(packageJson));
        });
        TemplateAiGenerator generator = CreateGenerator(config, service, handler, TemplateSpec);

        AiTemplateGenerationResult result = await generator.GenerateAsync(BuildRequest(), overwrite: false, CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal("/v1/chat/completions", capturedPath);
        Assert.Equal($"Bearer {TestApiKey}", capturedAuth);
    }

    /// <summary>
    /// 非法 JSON 前两次应携带错误重试，第三次合法则成功，重试轮含解析错误反馈。
    /// </summary>
    [Fact]
    public async Task GenerateAsync_InvalidJson_RetriesThenSucceeds()
    {
        ConfigService config = CreateConfigService();
        TemplatePackageService service = CreatePackageService();
        string validJson = BuildPackageDocumentJson(
            "retry-pkg",
            ("entity.java.scriban", "{{package.dir}}/entity/{{table.className}}.java", "class {{table.className}} {}"));

        int attempt = 0;
        FakeHttpMessageHandler handler = new(_ =>
        {
            attempt++;
            string body = attempt <= 2 ? "not-a-json" : validJson;
            return CreateJsonResponse(HttpStatusCode.OK, BuildLlmSuccessBody(body));
        });
        TemplateAiGenerator generator = CreateGenerator(config, service, handler, TemplateSpec);

        AiTemplateGenerationResult result = await generator.GenerateAsync(BuildRequest(), overwrite: false, CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal("retry-pkg", result.PackageName);
        Assert.Equal(3, handler.CallCount);

        // 重试轮携带上一轮解析错误文本
        string lastBody = handler.RequestBodies[^1];
        using JsonDocument bodyDocument = JsonDocument.Parse(lastBody);
        bool hasErrorFeedback = bodyDocument.RootElement
            .GetProperty("messages")
            .EnumerateArray()
            .Any(message => message.GetProperty("content").GetString()?.Contains("上一次输出解析失败") == true);
        Assert.True(hasErrorFeedback);
    }

    /// <summary>
    /// 非法 JSON 重试超限（≤2 次）应失败并保留原文，临时目录不落库。
    /// </summary>
    [Fact]
    public async Task GenerateAsync_InvalidJsonExceedsRetry_Fails()
    {
        ConfigService config = CreateConfigService();
        TemplatePackageService service = CreatePackageService();
        FakeHttpMessageHandler handler = new(_ => CreateJsonResponse(HttpStatusCode.OK, BuildLlmSuccessBody("still-not-json")));
        TemplateAiGenerator generator = CreateGenerator(config, service, handler, TemplateSpec);

        AiTemplateGenerationResult result = await generator.GenerateAsync(BuildRequest(), overwrite: false, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Contains(result.Errors, error => error.Contains("解析失败"));
        Assert.Equal(3, handler.CallCount);
        Assert.Equal("still-not-json", result.RawLlmOutput);
        Assert.False(Directory.Exists(Path.Combine(_tempRoot, "generator-temp", "retry-pkg")));
    }

    /// <summary>
    /// HTTP 认证失败应映射为可读错误且不消耗 JSON 重试额度。
    /// </summary>
    [Fact]
    public async Task GenerateAsync_HttpUnauthorized_ReturnsAuthErrorWithoutRetry()
    {
        ConfigService config = CreateConfigService();
        TemplatePackageService service = CreatePackageService();
        FakeHttpMessageHandler handler = new(_ =>
            CreateJsonResponse(HttpStatusCode.Unauthorized, """{"error":{"message":"Invalid API key"}}"""));
        TemplateAiGenerator generator = CreateGenerator(config, service, handler, TemplateSpec);

        AiTemplateGenerationResult result = await generator.GenerateAsync(BuildRequest(), overwrite: false, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Contains(result.Errors, error => error.Contains("认证失败"));
        Assert.Equal(1, handler.CallCount);
    }

    /// <summary>
    /// 生成包名与内置包同名应直接拒绝，overwrite 不生效。
    /// </summary>
    [Fact]
    public async Task GenerateAsync_BuiltinNameConflict_RejectsEvenWithOverwrite()
    {
        ConfigService config = CreateConfigService();
        TemplatePackageService service = CreatePackageService();
        await CreatePackageDirAsync(Path.Combine(_tempRoot, "builtin-root"), "conflict-pkg");

        string packageJson = BuildPackageDocumentJson(
            "conflict-pkg",
            ("entity.java.scriban", "{{package.dir}}/entity/{{table.className}}.java", "class {{table.className}} {}"));
        FakeHttpMessageHandler handler = new(_ => CreateJsonResponse(HttpStatusCode.OK, BuildLlmSuccessBody(packageJson)));
        TemplateAiGenerator generator = CreateGenerator(config, service, handler, TemplateSpec);

        AiTemplateGenerationResult result = await generator.GenerateAsync(BuildRequest(), overwrite: true, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Contains(result.Errors, error => error.Contains("只读"));
    }

    /// <summary>
    /// 生成包名与用户包同名：未确认覆盖失败提示冲突，确认覆盖后重新生成成功。
    /// </summary>
    [Fact]
    public async Task GenerateAsync_UserPackageConflict_RequiresOverwrite()
    {
        ConfigService config = CreateConfigService();
        TemplatePackageService service = CreatePackageService();
        string userLibrary = Path.Combine(_tempRoot, "user-library");
        Directory.CreateDirectory(userLibrary);
        await CreatePackageDirAsync(userLibrary, "user-pkg");

        string packageJson = BuildPackageDocumentJson(
            "user-pkg",
            ("entity.java.scriban", "{{package.dir}}/entity/{{table.className}}.java", "class {{table.className}} {}"));
        FakeHttpMessageHandler handler = new(_ => CreateJsonResponse(HttpStatusCode.OK, BuildLlmSuccessBody(packageJson)));
        TemplateAiGenerator generator = CreateGenerator(config, service, handler, TemplateSpec);

        AiTemplateGenerationResult first = await generator.GenerateAsync(BuildRequest(), overwrite: false, CancellationToken.None);

        Assert.False(first.IsSuccess);
        Assert.Contains(first.Errors, error => error.Contains("需确认覆盖"));

        AiTemplateGenerationResult second = await generator.GenerateAsync(BuildRequest(), overwrite: true, CancellationToken.None);

        Assert.True(second.IsSuccess);
        Assert.Equal("user-pkg", second.PackageName);
    }

    /// <summary>
    /// 生成包模板路径含 .. 段应被防目录穿越拒绝，不产生越界文件。
    /// </summary>
    [Fact]
    public async Task GenerateAsync_PathTraversalFile_Rejected()
    {
        ConfigService config = CreateConfigService();
        TemplatePackageService service = CreatePackageService();
        string packageJson = BuildPackageDocumentJson(
            "traversal-pkg",
            ("../evil.txt", "{{package.dir}}/evil.txt", "evil"));
        FakeHttpMessageHandler handler = new(_ => CreateJsonResponse(HttpStatusCode.OK, BuildLlmSuccessBody(packageJson)));
        TemplateAiGenerator generator = CreateGenerator(config, service, handler, TemplateSpec);

        AiTemplateGenerationResult result = await generator.GenerateAsync(BuildRequest(), overwrite: false, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.NotEmpty(result.Errors);
        Assert.False(File.Exists(Path.Combine(_tempRoot, "evil.txt")));
        Assert.False(Directory.Exists(Path.Combine(_tempRoot, "generator-temp", "traversal-pkg")));
    }

    /// <summary>
    /// 生成包输出相对路径含 .. 段应被拒绝，不落库不残留临时目录。
    /// </summary>
    [Fact]
    public async Task GenerateAsync_OutputPathTraversal_Rejected()
    {
        ConfigService config = CreateConfigService();
        TemplatePackageService service = CreatePackageService();
        string packageJson = BuildPackageDocumentJson(
            "out-traversal-pkg",
            ("entity.java.scriban", "../../../evil.java", "class Evil {}"));
        FakeHttpMessageHandler handler = new(_ => CreateJsonResponse(HttpStatusCode.OK, BuildLlmSuccessBody(packageJson)));
        TemplateAiGenerator generator = CreateGenerator(config, service, handler, TemplateSpec);

        AiTemplateGenerationResult result = await generator.GenerateAsync(BuildRequest(), overwrite: false, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Contains(result.Errors, error => error.Contains("输出路径不合法"));
        Assert.False(Directory.Exists(Path.Combine(_tempRoot, "generator-temp", "out-traversal-pkg")));
    }

    /// <summary>
    /// LLM 成功响应缺少 choices/content 应映射为结构化错误，不发起重试。
    /// </summary>
    [Fact]
    public async Task GenerateAsync_SuccessBodyMissingContent_ReturnsError()
    {
        ConfigService config = CreateConfigService();
        TemplatePackageService service = CreatePackageService();
        FakeHttpMessageHandler handler = new(_ => CreateJsonResponse(HttpStatusCode.OK, """{"choices":[]}"""));
        TemplateAiGenerator generator = CreateGenerator(config, service, handler, TemplateSpec);

        AiTemplateGenerationResult result = await generator.GenerateAsync(BuildRequest(), overwrite: false, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Contains(result.Errors, error => error.Contains("缺少有效内容"));
        Assert.Equal(1, handler.CallCount);
    }

    /// <summary>
    /// LLM apiKey 未配置应直接失败，不发起网络请求。
    /// </summary>
    [Fact]
    public async Task GenerateAsync_ApiKeyNotConfigured_Fails()
    {
        ConfigService config = CreateConfigService();
        config.Load().Llm.ApiKeyEncrypted = string.Empty;
        config.Save();
        TemplatePackageService service = CreatePackageService();
        FakeHttpMessageHandler handler = new(_ => CreateJsonResponse(HttpStatusCode.OK, BuildLlmSuccessBody("{}")));
        TemplateAiGenerator generator = CreateGenerator(config, service, handler, TemplateSpec);

        AiTemplateGenerationResult result = await generator.GenerateAsync(BuildRequest(), overwrite: false, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Contains(result.Errors, error => error.Contains("LLM 未配置"));
        Assert.Equal(0, handler.CallCount);
    }

    /// <summary>
    /// BaseUrl 非合法绝对 URL 应结构化失败，不抛异常也不发起网络请求。
    /// </summary>
    [Fact]
    public async Task GenerateAsync_InvalidBaseUrl_ReturnsStructuredErrorWithoutRequest()
    {
        ConfigService config = CreateConfigService();
        config.Load().Llm.BaseUrl = "not-a-url";
        config.Save();
        TemplatePackageService service = CreatePackageService();
        FakeHttpMessageHandler handler = new(_ => CreateJsonResponse(HttpStatusCode.OK, BuildLlmSuccessBody("{}")));
        TemplateAiGenerator generator = CreateGenerator(config, service, handler, TemplateSpec);

        AiTemplateGenerationResult result = await generator.GenerateAsync(BuildRequest(), overwrite: false, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Contains(result.Errors, error => error.Contains("URL 不合法"));
        Assert.Equal(0, handler.CallCount);
    }

    /// <summary>
    /// 模板生成请求不应携带 max_tokens 限制，由服务端按其默认输出上限处理，防止模型输出被截断。
    /// </summary>
    [Fact]
    public async Task GenerateAsync_RequestBody_DoesNotSetMaxTokens()
    {
        ConfigService config = CreateConfigService();
        TemplatePackageService service = CreatePackageService();
        string packageJson = BuildPackageDocumentJson(
            "token-pkg",
            ("entity.java.scriban", "{{package.dir}}/entity/{{table.className}}.java", "class {{table.className}} {}"));
        FakeHttpMessageHandler handler = new(_ => CreateJsonResponse(HttpStatusCode.OK, BuildLlmSuccessBody(packageJson)));
        TemplateAiGenerator generator = CreateGenerator(config, service, handler, TemplateSpec);

        AiTemplateGenerationResult result = await generator.GenerateAsync(BuildRequest(), overwrite: false, CancellationToken.None);

        Assert.True(result.IsSuccess);
        string body = Assert.Single(handler.RequestBodies);
        using JsonDocument document = JsonDocument.Parse(body);
        Assert.False(document.RootElement.TryGetProperty("max_tokens", out _));
        Assert.Equal(TestModel, document.RootElement.GetProperty("model").GetString());
    }

    /// <summary>
    /// 未显式传入 TEMPLATE_SPEC 时应从嵌入资源加载规范文本注入 system 消息。
    /// </summary>
    [Fact]
    public async Task GenerateAsync_EmbeddedTemplateSpec_LoadedWhenNotProvided()
    {
        ConfigService config = CreateConfigService();
        TemplatePackageService service = CreatePackageService();
        string packageJson = BuildPackageDocumentJson(
            "spec-pkg",
            ("entity.java.scriban", "{{package.dir}}/entity/{{table.className}}.java", "class {{table.className}} {}"));
        FakeHttpMessageHandler handler = new(_ => CreateJsonResponse(HttpStatusCode.OK, BuildLlmSuccessBody(packageJson)));
        TemplateAiGenerator generator = CreateGenerator(config, service, handler);

        AiTemplateGenerationResult result = await generator.GenerateAsync(BuildRequest(), overwrite: false, CancellationToken.None);

        Assert.True(result.IsSuccess);
        string body = Assert.Single(handler.RequestBodies);
        using JsonDocument document = JsonDocument.Parse(body);
        string systemContent = document.RootElement.GetProperty("messages")[0].GetProperty("content").GetString() ?? string.Empty;
        Assert.Contains("table.className", systemContent);
        Assert.Contains("tool", systemContent);
    }

    /// <summary>
    /// 请求携带参考文件时，用户提示词应按文件名标记逐文件注入内容快照，且不再出现 easycode 参考素材段落。
    /// </summary>
    [Fact]
    public async Task GenerateAsync_WithReferenceFiles_InjectsMarkedContent()
    {
        ConfigService config = CreateConfigService();
        TemplatePackageService service = CreatePackageService();
        string packageJson = BuildPackageDocumentJson(
            "ref-pkg",
            ("entity.java.scriban", "{{package.dir}}/entity/{{table.className}}.java", "class {{table.className}} {}"));
        FakeHttpMessageHandler handler = new(_ => CreateJsonResponse(HttpStatusCode.OK, BuildLlmSuccessBody(packageJson)));
        TemplateAiGenerator generator = CreateGenerator(config, service, handler, TemplateSpec);

        AiTemplateGenerationRequest request = BuildRequest();
        request.ReferenceFiles = new List<AiReferenceFileItem>
        {
            new("CommonMapper.cs", 42, "namespace Demo; class CommonMapper { }"),
            new("base.scriban", 20, "Hello from base template")
        };

        AiTemplateGenerationResult result = await generator.GenerateAsync(request, overwrite: false, CancellationToken.None);

        Assert.True(result.IsSuccess);
        string userContent = GetUserPromptFromRequestBody(Assert.Single(handler.RequestBodies));
        Assert.Contains("### CommonMapper.cs", userContent);
        Assert.Contains("namespace Demo; class CommonMapper { }", userContent);
        Assert.Contains("### base.scriban", userContent);
        Assert.Contains("Hello from base template", userContent);
        Assert.DoesNotContain("easycode", userContent);
    }

    /// <summary>
    /// 请求不带参考文件时，用户提示词不含参考文件段落与文件名标记。
    /// </summary>
    [Fact]
    public async Task GenerateAsync_NoReferenceFiles_OmitsReferenceParagraph()
    {
        ConfigService config = CreateConfigService();
        TemplatePackageService service = CreatePackageService();
        string packageJson = BuildPackageDocumentJson(
            "no-ref-pkg",
            ("entity.java.scriban", "{{package.dir}}/entity/{{table.className}}.java", "class {{table.className}} {}"));
        FakeHttpMessageHandler handler = new(_ => CreateJsonResponse(HttpStatusCode.OK, BuildLlmSuccessBody(packageJson)));
        TemplateAiGenerator generator = CreateGenerator(config, service, handler, TemplateSpec);

        AiTemplateGenerationResult result = await generator.GenerateAsync(BuildRequest(), overwrite: false, CancellationToken.None);

        Assert.True(result.IsSuccess);
        string userContent = GetUserPromptFromRequestBody(Assert.Single(handler.RequestBodies));
        Assert.DoesNotContain("参考文件", userContent);
        Assert.DoesNotContain("### ", userContent);
        Assert.DoesNotContain("easycode", userContent);
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

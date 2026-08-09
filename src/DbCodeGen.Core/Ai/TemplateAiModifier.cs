using System.Reflection;
using System.Text;
using DbCodeGen.Core.Config;
using Microsoft.Extensions.Logging;

namespace DbCodeGen.Core.Ai;

/// <summary>
/// AI 改模板对话服务实现：组装提示词（TEMPLATE_SPEC + 当前文件内容 + 修改指令 + 参考文件段落 + 多轮历史）→
/// 调用 LLM → 剥离 ``` 代码围栏 → 内容非空校验 → 返回完整新文件。
/// apiKey 经 IConfigService.GetLlmApiKey 解密为瞬态明文，仅内存短周期，不落盘不落日志；
/// 模板正文/指令/参考文件内容/LLM 原文不进日志，日志只记录相对路径、参考文件数量、状态与结果字符数。
/// </summary>
public sealed class TemplateAiModifier : ITemplateAiModifier
{
    private readonly ILlmClient _llmClient;
    private readonly IConfigService _configService;
    private readonly ILogger<TemplateAiModifier> _logger;
    private readonly string _templateSpec;

    /// <summary>
    /// 创建 AI 改模板对话服务。
    /// </summary>
    /// <param name="llmClient">LLM 对话客户端。</param>
    /// <param name="configService">配置服务，读取 LLM 配置并解密 apiKey。</param>
    /// <param name="logger">改模板服务日志器。</param>
    /// <param name="templateSpecText">TEMPLATE_SPEC 规范文本，为空时读取嵌入资源。</param>
    /// <exception cref="ArgumentNullException">任一核心依赖为 null 时抛出。</exception>
    public TemplateAiModifier(
        ILlmClient llmClient,
        IConfigService configService,
        ILogger<TemplateAiModifier> logger,
        string? templateSpecText = null)
    {
        ArgumentNullException.ThrowIfNull(llmClient);
        ArgumentNullException.ThrowIfNull(configService);
        ArgumentNullException.ThrowIfNull(logger);
        _llmClient = llmClient;
        _configService = configService;
        _logger = logger;
        _templateSpec = templateSpecText ?? LoadTemplateSpec();
    }

    /// <inheritdoc />
    public async Task<AiModifyTemplateResult> ModifyAsync(
        AiModifyTemplateRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        // 校验请求输入：当前文件内容与修改指令均必填，缺失直接结构化失败不发起网络请求
        if (string.IsNullOrWhiteSpace(request.CurrentTemplateContent))
        {
            return AiModifyTemplateResult.Failed(new List<string> { "当前模板文件内容为空，无法修改。" });
        }

        if (string.IsNullOrWhiteSpace(request.ModificationInstruction))
        {
            return AiModifyTemplateResult.Failed(new List<string> { "修改指令不能为空。" });
        }

        // 读取 LLM 配置：Current.Llm 提供端点与模型，GetLlmApiKey 解密返回瞬态明文，用后即弃
        LlmConfig? llmConfig = _configService.Current.Llm;
        if (llmConfig is null || string.IsNullOrWhiteSpace(llmConfig.ApiKeyEncrypted))
        {
            return AiModifyTemplateResult.Failed(new List<string> { "LLM 未配置，请先在设置中配置 API Key。" });
        }

        string? apiKey = _configService.GetLlmApiKey();
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return AiModifyTemplateResult.Failed(new List<string> { "LLM apiKey 读取失败，请重新配置。" });
        }

        var options = new LlmClientOptions
        {
            BaseUrl = llmConfig.BaseUrl,
            Model = llmConfig.Model,
            ApiKey = apiKey,
            TimeoutSeconds = llmConfig.TimeoutSeconds
        };

        // 日志只记录相对路径与参考文件数量，不记录模板正文、修改指令与参考文件内容
        _logger.LogInformation(
            "AI 改模板请求开始：目标文件 {FilePath}，参考文件 {ReferenceFileCount} 个。",
            request.CurrentTemplateFilePath,
            request.ReferenceFiles?.Count ?? 0);

        // 组装提示词：system 注入 TEMPLATE_SPEC，历史轮次全量回放后追加本轮 user 提示词
        List<LlmChatMessage> messages = BuildPromptMessages(request);

        var chatRequest = new LlmChatRequest
        {
            Model = options.Model,
            Messages = messages,
            Temperature = LlmChatRequest.DefaultTemperature
        };

        LlmChatResponse response = await _llmClient.ChatCompletionAsync(chatRequest, options, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccess)
        {
            // LLM 调用失败返回结构化错误，错误信息不含 apiKey 与连接串等敏感信息
            return AiModifyTemplateResult.Failed(new List<string> { response.ErrorMessage ?? "LLM 调用失败。" });
        }

        // 剥离 ``` 代码围栏后做内容非空校验，全空白视为失败并保留原文供人工查看
        string cleaned = StripCodeFence(response.Content);
        if (string.IsNullOrWhiteSpace(cleaned))
        {
            _logger.LogWarning("AI 改模板返回内容为空，目标文件 {FilePath}。", request.CurrentTemplateFilePath);
            return AiModifyTemplateResult.Failed(
                new List<string> { "LLM 返回内容为空，请调整修改指令后重试。" },
                response.Content);
        }

        // 日志只记录结果字符数，不记录模板正文与 LLM 原文
        _logger.LogInformation(
            "AI 改模板成功：目标文件 {FilePath}，结果字符数 {ContentLength}。",
            request.CurrentTemplateFilePath,
            cleaned.Length);
        return AiModifyTemplateResult.Success(cleaned, response.Content);
    }

    /// <summary>
    /// 组装提示词消息：system 注入 TEMPLATE_SPEC 规范文本，历史轮次按顺序全量回放后追加本轮 user 提示词。
    /// </summary>
    /// <param name="request">改模板请求。</param>
    /// <returns>对话消息列表。</returns>
    private List<LlmChatMessage> BuildPromptMessages(AiModifyTemplateRequest request)
    {
        var messages = new List<LlmChatMessage>
        {
            new() { Role = "system", Content = _templateSpec }
        };

        // 历史轮次（不含 system 与本轮指令）按顺序全量回放，保证多轮对话上下文连续
        if (request.HistoryMessages is { Count: > 0 })
        {
            foreach (LlmChatMessage history in request.HistoryMessages)
            {
                messages.Add(new LlmChatMessage { Role = history.Role, Content = history.Content });
            }
        }

        messages.Add(new LlmChatMessage { Role = "user", Content = BuildUserPrompt(request) });
        return messages;
    }

    /// <summary>
    /// 拼接用户提示词正文：当前文件内容 + 修改指令 + 参考文件段落（带文件名标记逐文件注入，空清单不注入）。
    /// </summary>
    /// <param name="request">改模板请求。</param>
    /// <returns>用户提示词正文。</returns>
    private static string BuildUserPrompt(AiModifyTemplateRequest request)
    {
        var builder = new StringBuilder();
        builder.AppendLine("请根据修改指令修改以下模板文件，直接输出修改后的完整模板文件内容。");
        builder.AppendLine();

        // 当前文件内容快照（含未保存编辑）作为修改对象注入，附带相对路径便于模型理解文件语义
        builder.AppendLine($"当前模板文件：{request.CurrentTemplateFilePath}");
        builder.AppendLine(request.CurrentTemplateContent);
        builder.AppendLine();

        builder.AppendLine("修改指令：");
        builder.AppendLine(request.ModificationInstruction.Trim());
        builder.AppendLine();

        // 参考文件内容快照逐文件注入提示词，带文件名标记区分来源；空清单不注入该段落
        if (request.ReferenceFiles is { Count: > 0 })
        {
            builder.AppendLine("参考文件内容（仅作参照，供了解既有命名与结构，请勿直接照搬文件内容）：");
            foreach (AiReferenceFileItem referenceFile in request.ReferenceFiles)
            {
                builder.AppendLine($"### {referenceFile.FileName}");
                builder.AppendLine(referenceFile.Content);
                builder.AppendLine();
            }
        }

        builder.AppendLine("只输出修改后的完整模板文件内容，不要包含 markdown 代码围栏或其它说明文字。");
        return builder.ToString();
    }

    /// <summary>
    /// 去除 markdown 代码围栏，LLM 常以 ``` 包裹输出，须剥离后才能作为模板文件内容应用。
    /// </summary>
    /// <param name="content">LLM 返回的原始文本。</param>
    /// <returns>去除围栏后的文本。</returns>
    private static string StripCodeFence(string content)
    {
        string trimmed = (content ?? string.Empty).Trim();
        if (!trimmed.StartsWith("```", StringComparison.Ordinal))
        {
            return trimmed;
        }

        int bodyStart = trimmed.IndexOf('\n');
        int fenceEnd = trimmed.LastIndexOf("```", StringComparison.Ordinal);
        if (bodyStart < 0 || fenceEnd <= bodyStart)
        {
            return trimmed;
        }

        return trimmed[(bodyStart + 1)..fenceEnd].Trim();
    }

    /// <summary>
    /// 从嵌入资源加载 TEMPLATE_SPEC 规范文本，供提示词组装使用；资源缺失时返回空串。
    /// </summary>
    /// <returns>TEMPLATE_SPEC 规范文本。</returns>
    private static string LoadTemplateSpec()
    {
        Assembly assembly = typeof(TemplateAiModifier).Assembly;
        string? resourceName = assembly.GetManifestResourceNames()
            .FirstOrDefault(name => name.EndsWith(".TEMPLATE_SPEC.md", StringComparison.Ordinal));
        if (resourceName is null)
        {
            return string.Empty;
        }

        using Stream? stream = assembly.GetManifestResourceStream(resourceName);
        if (stream is null)
        {
            return string.Empty;
        }

        using StreamReader reader = new(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }
}

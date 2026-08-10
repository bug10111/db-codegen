using System.Reflection;
using System.Text;
using DbCodeGen.Core.Config;
using Microsoft.Extensions.Logging;

namespace DbCodeGen.Core.Ai;

/// <summary>
/// AI 改模板对话服务实现：组装提示词（TEMPLATE_SPEC + 当前文件内容 + 修改指令 + 参考文件段落 + 多轮历史）→
/// 调用 LLM → 剥离 ``` 代码围栏 → 内容非空校验 → 返回完整新文件。
/// 批量修改把全部选中文件组装进同一条 user 提示词，单次 LLM 调用按 #FILE# 相对路径 标记一次返回全部文件修改结果；
/// apiKey 经 IConfigService.GetLlmApiKey 解密为瞬态明文，仅内存短周期，不落盘不落日志；
/// 模板正文/指令/参考文件内容/LLM 原文不进日志，日志只记录相对路径、文件数、状态与结果字符数。
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
        if (!TryBuildLlmOptions(out LlmClientOptions options, out string? llmError))
        {
            return AiModifyTemplateResult.Failed(new List<string> { llmError ?? "LLM 未配置。" });
        }

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

    /// <inheritdoc />
    public async Task<AiModifyMultipleResult> ModifyMultipleAsync(
        AiModifyMultipleRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        // 批量级校验：文件清单与修改指令必填非空，缺失直接结构化失败不发起任何网络请求
        if (request.Files is null || request.Files.Count == 0)
        {
            return AiModifyMultipleResult.Failed(new List<string> { "至少选择一个模板文件。" });
        }

        if (string.IsNullOrWhiteSpace(request.ModificationInstruction))
        {
            return AiModifyMultipleResult.Failed(new List<string> { "修改指令不能为空。" });
        }

        // 读取 LLM 配置：与单文件修改共用同一配置读取辅助，未配置直接失败不发起网络请求
        if (!TryBuildLlmOptions(out LlmClientOptions options, out string? llmError))
        {
            return AiModifyMultipleResult.Failed(new List<string> { llmError ?? "LLM 未配置。" });
        }

        // 文件内容前置校验：内容为空的文件标记失败且不进入提示词组装，其余文件参与同一次调用
        var emptyContentPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var filesToSend = new List<AiModifyFileItem>(request.Files.Count);
        foreach (AiModifyFileItem fileItem in request.Files)
        {
            if (string.IsNullOrWhiteSpace(fileItem.Content))
            {
                emptyContentPaths.Add(fileItem.RelativePath);
            }
            else
            {
                filesToSend.Add(fileItem);
            }
        }

        // 全部文件内容为空：不发起 LLM 调用，直接以各文件失败结果返回
        if (filesToSend.Count == 0)
        {
            return BuildAllEmptyContentFailure(request);
        }

        // 日志只记录包名、文件数与参考文件数量，不记录模板正文、修改指令与参考文件内容
        _logger.LogInformation(
            "AI 改模板批量请求开始：包 {PackageName}，文件数 {FileCount}，参考文件 {ReferenceFileCount} 个。",
            request.PackageName,
            request.Files.Count,
            request.ReferenceFiles?.Count ?? 0);

        // 组装一次对话消息：system 注入 TEMPLATE_SPEC 与历史全量回放，随后追加一条列出全部文件的 user 提示词
        List<LlmChatMessage> messages = BuildBaseMessages(request.HistoryMessages);
        messages.Add(new LlmChatMessage
        {
            Role = "user",
            Content = BuildMultipleFilesUserPrompt(request, filesToSend)
        });

        var chatRequest = new LlmChatRequest
        {
            Model = options.Model,
            Messages = messages,
            Temperature = LlmChatRequest.DefaultTemperature
        };

        // 单次 LLM 调用一次返回全部文件修改结果，取消由调用方捕获 OperationCanceledException 处理
        LlmChatResponse response = await _llmClient.ChatCompletionAsync(chatRequest, options, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccess)
        {
            // LLM 调用失败：全部请求文件标记失败，错误信息不含 apiKey 与连接串等敏感信息
            _logger.LogWarning("AI 改模板批量调用失败，包 {PackageName}。", request.PackageName);
            return BuildUniformFailure(request, response.ErrorMessage ?? "LLM 调用失败。");
        }

        // 解析响应：先剥离外层代码围栏（兼容 LLM 整段包裹），再扫描 #FILE# 相对路径 标记切分各文件块
        string cleaned = StripCodeFence(response.Content);
        List<ParsedFileBlock> blocks = ParseMultipleFileBlocks(cleaned);

        // 解析不出任何文件块：全部请求文件标记失败，提示无法解析批量修改结果
        if (blocks.Count == 0)
        {
            _logger.LogWarning("AI 改模板批量响应未解析出文件块，包 {PackageName}。", request.PackageName);
            return BuildUniformFailure(request, "未能解析批量修改结果。");
        }

        // 按请求文件顺序逐文件组装结果：空内容文件复用前置失败，其余匹配响应文件块
        var fileResults = new List<AiModifyFileResult>(request.Files.Count);
        foreach (AiModifyFileItem fileItem in request.Files)
        {
            // 内容为空的文件已在前置校验中标记失败，保持请求顺序直接组装失败结果
            if (emptyContentPaths.Contains(fileItem.RelativePath))
            {
                fileResults.Add(AiModifyFileResult.ForFailure(fileItem.RelativePath, "模板文件内容为空，无法修改。"));
                continue;
            }

            // 按规范化路径（忽略大小写）匹配响应文件块，未命中的文件单独记失败不中断其它文件
            ParsedFileBlock? matched = blocks.FirstOrDefault(block =>
                string.Equals(
                    NormalizePath(block.RelativePath),
                    NormalizePath(fileItem.RelativePath),
                    StringComparison.OrdinalIgnoreCase));

            if (matched is null)
            {
                fileResults.Add(AiModifyFileResult.ForFailure(fileItem.RelativePath, "未在 AI 响应中找到该文件的修改结果。"));
                continue;
            }

            // 响应文件块剥离后为空的文件视为该文件失败，与单文件返回内容为空语义一致
            if (string.IsNullOrWhiteSpace(matched.Content))
            {
                _logger.LogWarning("AI 改模板批量返回内容为空，目标文件 {FilePath}。", fileItem.RelativePath);
                fileResults.Add(AiModifyFileResult.ForFailure(fileItem.RelativePath, "LLM 返回内容为空，请调整修改指令后重试。"));
                continue;
            }

            // 日志只记录相对路径与结果字符数，不记录模板正文与 LLM 原文
            _logger.LogInformation(
                "AI 改模板批量成功：目标文件 {FilePath}，结果字符数 {ContentLength}。",
                fileItem.RelativePath,
                matched.Content.Length);
            fileResults.Add(AiModifyFileResult.ForSuccess(fileItem.RelativePath, matched.Content));
        }

        return AiModifyMultipleResult.Create(fileResults);
    }

    /// <summary>
    /// 构造全部文件内容为空时的批量失败结果，按请求文件顺序逐文件组装失败项，不发起 LLM 调用。
    /// </summary>
    /// <param name="request">批量修改请求。</param>
    /// <returns>批量失败结果。</returns>
    private static AiModifyMultipleResult BuildAllEmptyContentFailure(AiModifyMultipleRequest request)
    {
        var results = new List<AiModifyFileResult>(request.Files.Count);
        foreach (AiModifyFileItem fileItem in request.Files)
        {
            results.Add(AiModifyFileResult.ForFailure(fileItem.RelativePath, "模板文件内容为空，无法修改。"));
        }

        return AiModifyMultipleResult.Create(results);
    }

    /// <summary>
    /// 构造全部请求文件同一失败原因的批量结果，按请求文件顺序逐文件组装失败项，
    /// 供 LLM 调用失败与响应解析失败两种批量级失败共用。
    /// </summary>
    /// <param name="request">批量修改请求。</param>
    /// <param name="error">统一失败原因。</param>
    /// <returns>批量失败结果。</returns>
    private static AiModifyMultipleResult BuildUniformFailure(AiModifyMultipleRequest request, string error)
    {
        var results = new List<AiModifyFileResult>(request.Files.Count);
        foreach (AiModifyFileItem fileItem in request.Files)
        {
            results.Add(AiModifyFileResult.ForFailure(fileItem.RelativePath, error));
        }

        return AiModifyMultipleResult.Create(results);
    }

    /// <summary>
    /// 读取并组装 LLM 调用配置：读取配置快照的端点/模型/超时与解密的明文 apiKey，未配置或读取失败返回 false。
    /// 单文件与批量修改共用本辅助，保证 LLM 配置校验行为一致。
    /// </summary>
    /// <param name="options">成功时输出的 LLM 调用配置，含瞬态明文 apiKey，用后即弃。</param>
    /// <param name="error">失败时的可读错误信息，成功时为 null。</param>
    /// <returns>配置可用返回 true，未配置或读取失败返回 false。</returns>
    private bool TryBuildLlmOptions(out LlmClientOptions options, out string? error)
    {
        options = new LlmClientOptions();
        error = null;

        // 读取配置快照：Current.Llm 提供端点与模型，密文 apiKey 非空视为已配置
        LlmConfig? llmConfig = _configService.Current.Llm;
        if (llmConfig is null || string.IsNullOrWhiteSpace(llmConfig.ApiKeyEncrypted))
        {
            error = "LLM 未配置，请先在设置中配置 API Key。";
            return false;
        }

        // GetLlmApiKey 解密返回瞬态明文，仅内存短周期，用后即弃
        string? apiKey = _configService.GetLlmApiKey();
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            error = "LLM apiKey 读取失败，请重新配置。";
            return false;
        }

        options = new LlmClientOptions
        {
            BaseUrl = llmConfig.BaseUrl,
            Model = llmConfig.Model,
            ApiKey = apiKey,
            TimeoutSeconds = llmConfig.TimeoutSeconds
        };
        return true;
    }

    /// <summary>
    /// 组装批量修改的前导消息：system 注入 TEMPLATE_SPEC 规范文本，历史轮次按顺序全量回放。
    /// 各文件在副本上追加自己的 user 提示词，前导消息本身保持不变被共享复用。
    /// </summary>
    /// <param name="historyMessages">历史对话轮次，可空。</param>
    /// <returns>前导对话消息列表。</returns>
    private List<LlmChatMessage> BuildBaseMessages(IReadOnlyList<LlmChatMessage>? historyMessages)
    {
        var messages = new List<LlmChatMessage>
        {
            new() { Role = "system", Content = _templateSpec }
        };

        // 历史轮次（不含 system 与本轮指令）按顺序全量回放，保证多轮对话上下文连续
        if (historyMessages is { Count: > 0 })
        {
            foreach (LlmChatMessage history in historyMessages)
            {
                messages.Add(new LlmChatMessage { Role = history.Role, Content = history.Content });
            }
        }

        return messages;
    }

    /// <summary>
    /// 组装提示词消息：system 注入 TEMPLATE_SPEC 规范文本，历史轮次按顺序全量回放后追加本轮 user 提示词。
    /// </summary>
    /// <param name="request">改模板请求。</param>
    /// <returns>对话消息列表。</returns>
    private List<LlmChatMessage> BuildPromptMessages(AiModifyTemplateRequest request)
    {
        List<LlmChatMessage> messages = BuildBaseMessages(request.HistoryMessages);
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
        return BuildFileUserPrompt(
            request.CurrentTemplateFilePath,
            request.CurrentTemplateContent,
            request.ModificationInstruction,
            request.ReferenceFiles);
    }

    /// <summary>
    /// 拼接单个文件的用户提示词正文：目标文件内容 + 修改指令 + 参考文件段落（带文件名标记逐文件注入，空清单不注入）。
    /// 单文件修改与批量逐文件循环共用本辅助，保证提示词结构一致。
    /// </summary>
    /// <param name="filePath">目标文件相对路径，注入提示词供模型理解文件语义。</param>
    /// <param name="content">目标文件内容快照。</param>
    /// <param name="instruction">修改指令。</param>
    /// <param name="referenceFiles">参考文件内容快照清单，可空。</param>
    /// <returns>用户提示词正文。</returns>
    private static string BuildFileUserPrompt(
        string filePath,
        string content,
        string instruction,
        IReadOnlyList<AiReferenceFileItem>? referenceFiles)
    {
        var builder = new StringBuilder();
        builder.AppendLine("请根据修改指令修改以下模板文件，直接输出修改后的完整模板文件内容。");
        builder.AppendLine();

        // 当前文件内容快照（含未保存编辑）作为修改对象注入，附带相对路径便于模型理解文件语义
        builder.AppendLine($"当前模板文件：{filePath}");
        builder.AppendLine(content);
        builder.AppendLine();

        builder.AppendLine("修改指令：");
        builder.AppendLine(instruction.Trim());
        builder.AppendLine();

        // 参考文件内容快照逐文件注入提示词，带文件名标记区分来源；空清单不注入该段落
        if (referenceFiles is { Count: > 0 })
        {
            builder.AppendLine("参考文件内容（仅作参照，供了解既有命名与结构，请勿直接照搬文件内容）：");
            foreach (AiReferenceFileItem referenceFile in referenceFiles)
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
    /// 拼接批量修改的用户提示词正文：列出全部待修改文件（### 相对路径 标记 + 内容快照）+
    /// 修改指令 + 参考文件段落 + 严格 #FILE# 输出格式说明。所有文件组装进同一条 user 提示词，
    /// 供一次 LLM 调用按 #FILE# 标记返回全部文件修改结果。
    /// </summary>
    /// <param name="request">批量修改请求。</param>
    /// <param name="files">已前置过滤内容非空的待修改文件清单，与请求文件清单的空内容过滤保持一致。</param>
    /// <returns>用户提示词正文。</returns>
    private static string BuildMultipleFilesUserPrompt(
        AiModifyMultipleRequest request,
        IReadOnlyList<AiModifyFileItem> files)
    {
        var builder = new StringBuilder();
        builder.AppendLine("请根据修改指令依次修改以下多个模板文件，并一次性输出所有修改后的完整模板文件内容。");
        builder.AppendLine();

        // 待修改文件清单逐文件注入：### 相对路径 标记 + 内容快照，供模型逐个理解并修改
        builder.AppendLine("待修改模板文件：");
        foreach (AiModifyFileItem fileItem in files)
        {
            builder.AppendLine($"### {fileItem.RelativePath}");
            builder.AppendLine(fileItem.Content);
            builder.AppendLine();
        }

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

        // 严格输出格式约定：每个文件先输出 #FILE# 相对路径 行再紧跟完整内容，禁止代码围栏与说明文字
        builder.AppendLine("输出格式（严格遵守）：");
        builder.AppendLine("对每个修改后的文件，先单独一行输出 #FILE# 后跟该文件的相对路径，下一行开始输出该文件的完整内容；");
        builder.AppendLine("文件之间用下一个 #FILE# 行分隔；不要使用 markdown 代码围栏，不要输出任何说明文字。");
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
    /// 批量响应中按 #FILE# 标记切分出的单个文件块：相对路径与剥离分隔后的完整文件内容。
    /// </summary>
    private sealed record ParsedFileBlock(string RelativePath, string Content);

    /// <summary>
    /// 按 #FILE# 相对路径 标记切分批量响应为各文件块：#FILE# 标记行之后到下一个标记行之间的内容
    /// 即该文件完整内容，标记行路径缺失时该标记视为无效并丢弃当前块。
    /// </summary>
    /// <param name="content">剥离外层代码围栏后的 LLM 返回文本。</param>
    /// <returns>解析出的文件块清单，未解析出任何块时为空清单。</returns>
    private static List<ParsedFileBlock> ParseMultipleFileBlocks(string content)
    {
        var blocks = new List<ParsedFileBlock>();
        if (string.IsNullOrWhiteSpace(content))
        {
            return blocks;
        }

        // 按行扫描，#FILE# 相对路径 行作为文件块起点，其后内容归入该文件块直至下一个标记行；
        // 逐行追加保持原始换行风格，不引入平台默认换行符
        string[] lines = content.Split('\n');
        string? currentPath = null;
        var currentContent = new StringBuilder();

        foreach (string line in lines)
        {
            string trimmed = line.Trim();
            if (trimmed.StartsWith("#FILE#", StringComparison.Ordinal))
            {
                // 前一文件块收尾加入清单，再以当前标记行开启新文件块
                if (currentPath is not null)
                {
                    blocks.Add(new ParsedFileBlock(currentPath, currentContent.ToString().Trim()));
                }

                // 提取标记行中 #FILE# 之后的相对路径，路径缺失时该标记视为无效
                string path = trimmed["#FILE#".Length..].Trim();
                currentPath = string.IsNullOrWhiteSpace(path) ? null : path;
                currentContent.Clear();
                continue;
            }

            if (currentPath is not null)
            {
                currentContent.Append(line).Append('\n');
            }
        }

        // 收尾最后一个文件块：路径非空才计入结果清单
        if (currentPath is not null)
        {
            blocks.Add(new ParsedFileBlock(currentPath, currentContent.ToString().Trim()));
        }

        return blocks;
    }

    /// <summary>
    /// 规范化文件相对路径用于请求路径与响应块匹配：去除首尾空白、统一反斜杠为正斜杠并去除首尾斜杠。
    /// </summary>
    /// <param name="path">原始相对路径。</param>
    /// <returns>规范化后的相对路径。</returns>
    private static string NormalizePath(string path)
    {
        string trimmed = (path ?? string.Empty).Trim();
        return trimmed.Replace('\\', '/').Trim('/');
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

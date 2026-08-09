using System.Reflection;
using System.Text;
using System.Text.Json;
using DbCodeGen.Core.Config;
using DbCodeGen.Core.Templates.Packages;
using Microsoft.Extensions.Logging;

namespace DbCodeGen.Core.Ai;

/// <summary>
/// AI 模板生成服务实现：组装提示词（TEMPLATE_SPEC + 样例表 JSON + 生成说明 + 参考文件约定蓝本段落）→ 调用 LLM →
/// 解析模板包 → 按生成目标分流：新建模式写临时目录校验后提交落库，追加模式丢弃 AI 包级元数据直接写入目标用户包。
/// apiKey 经 IConfigService.GetLlmApiKey 解密为瞬态明文，仅内存短周期，不落盘不落日志；
/// 参考文件内容快照仅注入本次对话提示词，不写盘不进日志，日志只记录参考文件数量与文件名。
/// </summary>
public sealed class TemplateAiGenerator : ITemplateAiGenerator
{
    /// <summary>
    /// 非法 JSON 携带错误重试次数上限，超限直接失败。
    /// </summary>
    public const int MaxJsonRetryCount = 2;

    /// <summary>
    /// 默认临时目录根相对 %TEMP% 的路径段，完整默认根为 %TEMP%\DbCodeGen\Ai。
    /// </summary>
    internal const string DefaultTempRoot = "DbCodeGen\\Ai";

    private readonly ILlmClient _llmClient;
    private readonly IConfigService _configService;
    private readonly ITemplatePackageService _templatePackageService;
    private readonly ILogger<TemplateAiGenerator> _logger;
    private readonly string _tempRoot;
    private readonly string _templateSpec;

    private static readonly JsonSerializerOptions SampleTableJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    /// <summary>
    /// 创建 AI 模板生成服务。
    /// </summary>
    /// <param name="llmClient">LLM 对话客户端。</param>
    /// <param name="configService">配置服务，读取 LLM 配置并解密 apiKey。</param>
    /// <param name="templatePackageService">模板包管理服务，负责校验后提交落库。</param>
    /// <param name="logger">生成服务日志器。</param>
    /// <param name="templateSpecText">TEMPLATE_SPEC 规范文本，为空时读取嵌入资源。</param>
    /// <param name="tempRootOverride">临时目录根覆盖，为空时默认 %TEMP%\DbCodeGen\Ai。</param>
    /// <exception cref="ArgumentNullException">任一核心依赖为 null 时抛出。</exception>
    public TemplateAiGenerator(
        ILlmClient llmClient,
        IConfigService configService,
        ITemplatePackageService templatePackageService,
        ILogger<TemplateAiGenerator> logger,
        string? templateSpecText = null,
        string? tempRootOverride = null)
    {
        ArgumentNullException.ThrowIfNull(llmClient);
        ArgumentNullException.ThrowIfNull(configService);
        ArgumentNullException.ThrowIfNull(templatePackageService);
        ArgumentNullException.ThrowIfNull(logger);
        _llmClient = llmClient;
        _configService = configService;
        _templatePackageService = templatePackageService;
        _logger = logger;
        _tempRoot = tempRootOverride ?? Path.Combine(Path.GetTempPath(), "DbCodeGen", "Ai");
        _templateSpec = templateSpecText ?? LoadTemplateSpec();
    }

    /// <inheritdoc />
    public async Task<AiTemplateGenerationResult> GenerateAsync(
        AiTemplateGenerationRequest request,
        bool overwrite,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        // 校验向导输入：生成说明必填，样例表必选且须含表名
        if (string.IsNullOrWhiteSpace(request.TechStackDescription))
        {
            return AiTemplateGenerationResult.Failed(new List<string> { "生成说明不能为空。" });
        }

        if (request.SampleTable is null || string.IsNullOrWhiteSpace(request.SampleTable.RawName))
        {
            return AiTemplateGenerationResult.Failed(new List<string> { "请先选择样例表。" });
        }

        // 读取 LLM 配置：Current.Llm 提供端点与模型，GetLlmApiKey 解密返回瞬态明文，用后即弃
        LlmConfig? llmConfig = _configService.Current.Llm;
        if (llmConfig is null || string.IsNullOrWhiteSpace(llmConfig.ApiKeyEncrypted))
        {
            return AiTemplateGenerationResult.Failed(new List<string> { "LLM 未配置，请先在设置中配置 API Key。" });
        }

        string? apiKey = _configService.GetLlmApiKey();
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return AiTemplateGenerationResult.Failed(new List<string> { "LLM apiKey 读取失败，请重新配置。" });
        }

        var options = new LlmClientOptions
        {
            BaseUrl = llmConfig.BaseUrl,
            Model = llmConfig.Model,
            ApiKey = apiKey,
            TimeoutSeconds = llmConfig.TimeoutSeconds
        };

        // 参考文件内容仅注入提示词不进日志，日志只记录数量与文件名
        if (request.ReferenceFiles is { Count: > 0 })
        {
            _logger.LogInformation(
                "AI 模板生成请求携带 {ReferenceFileCount} 个参考文件：{ReferenceFileNames}。",
                request.ReferenceFiles.Count,
                string.Join(", ", request.ReferenceFiles.Select(file => file.FileName)));
        }

        // 追加模式目标预校验：目标包名必填、目标包存在且非内置，不满足直接失败避免浪费 LLM 调用
        if (request.TargetMode == AiGenerationTargetMode.AppendToPackage)
        {
            TemplatePackageOperationResult? targetError = await ValidateAppendTargetAsync(request.TargetPackageName, cancellationToken).ConfigureAwait(false);
            if (targetError is not null)
            {
                return AiTemplateGenerationResult.Failed(new List<string> { targetError.Message });
            }
        }

        // 组装首轮提示词：TEMPLATE_SPEC 注入 system，生成说明与样例表 JSON 注入 user
        List<LlmChatMessage> messages = BuildPromptMessages(request);

        // 调用 LLM 并解析模板包，非法 JSON 携带解析错误重试，超限失败
        GeneratedPackageDocument? document = null;
        string? rawLlmOutput = null;
        string? parseError = null;
        for (int attempt = 0; attempt <= MaxJsonRetryCount; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // 重试轮在对话尾部追加上一轮解析错误，引导模型修正输出为合法 JSON
            if (attempt > 0 && parseError is not null)
            {
                messages.Add(new LlmChatMessage
                {
                    Role = "user",
                    Content = $"上一次输出解析失败：{parseError}。请直接输出合法 JSON，不要包含 markdown 代码围栏或其它说明文字。"
                });
            }

            var chatRequest = new LlmChatRequest
            {
                Model = options.Model,
                Messages = messages,
                Temperature = LlmChatRequest.DefaultTemperature
            };

            LlmChatResponse response = await _llmClient.ChatCompletionAsync(chatRequest, options, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccess)
            {
                // HTTP 调用失败不消耗 JSON 重试额度，直接失败
                return AiTemplateGenerationResult.Failed(new List<string> { response.ErrorMessage ?? "LLM 调用失败。" });
            }

            rawLlmOutput = response.Content;
            try
            {
                document = GeneratedPackageDocument.Parse(rawLlmOutput);
                break;
            }
            catch (FormatException exception)
            {
                parseError = exception.Message;
                _logger.LogWarning(exception, "LLM 返回内容解析失败，第 {Attempt} 次。", attempt + 1);
            }
        }

        if (document is null)
        {
            // 重试超限仍失败，保留原文供人工修复，原文不进日志
            return AiTemplateGenerationResult.Failed(
                new List<string> { $"LLM 返回内容解析失败，已重试 {MaxJsonRetryCount} 次。{parseError}" },
                rawLlmOutput);
        }

        // 按生成目标分流：追加模式丢弃 AI 包级元数据直接写入目标包；新建模式校验包名后走临时包导入流程
        if (request.TargetMode == AiGenerationTargetMode.AppendToPackage)
        {
            return await AppendToPackageAsync(document, request, rawLlmOutput, cancellationToken).ConfigureAwait(false);
        }

        // 新建模式：用户显式指定包名时覆盖 AI 自定包名，仍须通过包名合法性校验
        if (!string.IsNullOrWhiteSpace(request.RequestedPackageName))
        {
            document.PackageName = request.RequestedPackageName.Trim();
        }

        // 包名合法性前置校验，不合法直接失败
        if (!TemplatePackageLoader.IsValidPackageName(document.PackageName))
        {
            return AiTemplateGenerationResult.Failed(
                new List<string> { $"包名不合法（须为字母/数字/中划线/下划线）：{document.PackageName}" },
                rawLlmOutput);
        }

        // 写临时目录 → 校验 → 提交落库 → 清理临时目录
        return await WriteValidateAndCommitAsync(document, overwrite, rawLlmOutput, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 组装提示词消息：system 注入 TEMPLATE_SPEC 规范文本，user 注入生成说明、样例表 JSON 与参考文件约定蓝本段落。
    /// </summary>
    /// <param name="request">生成请求。</param>
    /// <returns>对话消息列表。</returns>
    private List<LlmChatMessage> BuildPromptMessages(AiTemplateGenerationRequest request)
    {
        string sampleTableJson = JsonSerializer.Serialize(request.SampleTable, SampleTableJsonOptions);
        string userContent = BuildUserPrompt(request, sampleTableJson);
        return new List<LlmChatMessage>
        {
            new() { Role = "system", Content = _templateSpec },
            new() { Role = "user", Content = userContent }
        };
    }

    /// <summary>
    /// 拼接用户提示词正文：引言 + 生成说明（自由指令，最高优先级）+ 样例表 JSON + 输出 JSON 结构要求
    /// + 参考文件约定蓝本段落（逐文件镜像并翻译 Velocity 语法）+ 追加/新建目标说明。
    /// </summary>
    /// <param name="request">生成请求。</param>
    /// <param name="sampleTableJson">样例表序列化 JSON 文本。</param>
    /// <returns>用户提示词正文。</returns>
    private static string BuildUserPrompt(AiTemplateGenerationRequest request, string sampleTableJson)
    {
        var builder = new StringBuilder();
        builder.AppendLine("请根据以下生成说明与参考文件约定，生成可被本工具加载的 Scriban 模板。");
        builder.AppendLine();
        builder.AppendLine("生成说明（最高优先级，严格按照生成说明决定生成 1 个模板还是整套模板包）：");
        builder.AppendLine(request.TechStackDescription.Trim());
        builder.AppendLine();
        builder.AppendLine("样例表真实元数据（JSON，模板内字段名严格以 TEMPLATE_SPEC 变量表为准）：");
        builder.AppendLine(sampleTableJson);
        builder.AppendLine();
        builder.AppendLine("请按 TEMPLATE_SPEC 生成模板，并输出一个 GeneratedPackageDocument JSON，结构如下：");
        builder.AppendLine("{");
        builder.AppendLine("  \"packageName\": \"包名，仅字母/数字/中划线/下划线\",");
        builder.AppendLine("  \"description\": \"包说明\",");
        builder.AppendLine("  \"basePackage\": \"基础包名，可填完整包名（含模块段），如 com.example.common\",");
        builder.AppendLine("  \"typeMap\": { \"数据库原始类型\": \"目标语言类型\" },");
        builder.AppendLine("  \"files\": [");
        builder.AppendLine("    {");
        builder.AppendLine("      \"name\": \"模板相对路径，如 entity.java.scriban\",");
        builder.AppendLine("      \"relativeOutputPath\": \"输出相对路径，如 {{package.dir}}/entity/{{table.className}}.java\",");
        builder.AppendLine("      \"enabled\": true,");
        builder.AppendLine("      \"content\": \"模板文件内容（Scriban 语法）\"");
        builder.AppendLine("    }");
        builder.AppendLine("  ]");
        builder.AppendLine("}");
        builder.AppendLine("relativeOutputPath 是相对代码根（如 src/main/java）的相对路径，禁止携带 src/ 等绝对前缀；");
        builder.AppendLine("如需放到 src/main/resources 等代码根外目录，用 ../ 越级（如 ../resources/mapper/{{table.className}}.xml，解析后必须落在工作区根内）。");
        builder.AppendLine();

        // 追加模式下说明模板写入目标包，files[].name 用与参考文件对应的相对文件名即可，无需包级元数据
        if (request.TargetMode == AiGenerationTargetMode.AppendToPackage)
        {
            builder.AppendLine($"本次生成将把模板追加到目标用户包“{request.TargetPackageName}”，files[].name 用与参考文件对应的相对文件名即可，无需关心包级元数据。");
            builder.AppendLine();
        }

        // 参考文件内容快照逐文件注入，作为约定蓝本要求逐文件镜像并翻译 Velocity 语法；空清单不注入该段落
        if (request.ReferenceFiles is { Count: > 0 })
        {
            builder.AppendLine("参考文件是你的既有模板/代码约定，应作为蓝本逐文件镜像：每个参考文件尽量生成一个对应模板，文件名与相对结构对齐；");
            builder.AppendLine("参考文件为 Velocity/FreeMarker/EasyCode 模板配置时，把其 Velocity 语法翻译为 Scriban（$!{tableInfo.xxx}→{{table.xxx}}、#save(path,ext)→files[].name+files[].relativeOutputPath、#setPackageSuffix2 等宏按包结构约定），保持输出代码风格、注解、import、包结构一致；");
            builder.AppendLine("EasyCode 的 #save 绝对路径（如 /src/main/resources/mapper）不得照搬进 relativeOutputPath：输出路径相对代码根，目标资源目录用 ../ 越级（如 ../resources/mapper/xxx.xml），禁止 src/ 等绝对前缀；");
            builder.AppendLine("若为普通代码则提炼约定后覆盖同样范围。除非生成说明明确要求只生成部分模板，否则数量与参考文件对齐。");
            foreach (AiReferenceFileItem referenceFile in request.ReferenceFiles)
            {
                builder.AppendLine($"### {referenceFile.FileName}");
                builder.AppendLine(referenceFile.Content);
                builder.AppendLine();
            }
        }

        builder.AppendLine("只输出 JSON，不要包含 markdown 代码围栏或其它文字。");
        return builder.ToString();
    }

    /// <summary>
    /// 将解析后的模板包写入临时目录，经 TemplatePackageLoader 校验后提交到用户模板库，最后清理临时目录。
    /// </summary>
    /// <param name="document">解析后的模板包文档。</param>
    /// <param name="overwrite">与用户包同名时是否允许覆盖。</param>
    /// <param name="rawLlmOutput">原始 LLM 输出，失败时随结果返回。</param>
    /// <param name="cancellationToken">取消标记。</param>
    /// <returns>生成结果。</returns>
    private async Task<AiTemplateGenerationResult> WriteValidateAndCommitAsync(
        GeneratedPackageDocument document,
        bool overwrite,
        string? rawLlmOutput,
        CancellationToken cancellationToken)
    {
        string packageName = document.PackageName;
        string tempPackageDir = Path.Combine(_tempRoot, packageName);

        try
        {
            // 提交前查模板库归属：内置包同名直接拒绝，用户包同名未确认覆盖时返回冲突
            TemplatePackageOperationResult? conflict = await CheckPackageConflictAsync(packageName, overwrite, cancellationToken).ConfigureAwait(false);
            if (conflict is not null)
            {
                return AiTemplateGenerationResult.Failed(new List<string> { conflict.Message }, rawLlmOutput);
            }

            Directory.CreateDirectory(tempPackageDir);
            await WritePackageToDirectoryAsync(document, tempPackageDir, cancellationToken).ConfigureAwait(false);

            // 经 TemplatePackageLoader 完整校验，保证落库内容可加载
            try
            {
                await TemplatePackageLoader.LoadFromDirectoryAsync(tempPackageDir, isBuiltin: false, cancellationToken).ConfigureAwait(false);
            }
            catch (TemplatePackageException exception)
            {
                return AiTemplateGenerationResult.Failed(new List<string> { $"模板包校验失败：{exception.Message}" }, rawLlmOutput);
            }

            // 校验通过后提交落库，覆盖语义由模板包管理服务承载
            TemplatePackageOperationResult installResult = await _templatePackageService.ImportFromFolderAsync(tempPackageDir, overwrite, cancellationToken).ConfigureAwait(false);
            if (installResult.Status != TemplatePackageOperationStatus.Succeeded)
            {
                return AiTemplateGenerationResult.Failed(new List<string> { installResult.Message }, rawLlmOutput);
            }

            _logger.LogInformation("AI 生成模板包提交成功：{PackageName}，目录：{Directory}。", packageName, installResult.Package?.RootPath);
            return AiTemplateGenerationResult.Success(packageName, installResult.Package?.RootPath, rawLlmOutput);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (TemplatePackageException exception)
        {
            _logger.LogError(exception, "AI 生成模板包写入失败，包名：{PackageName}。", packageName);
            return AiTemplateGenerationResult.Failed(new List<string> { $"模板包写入失败：{exception.Message}" }, rawLlmOutput);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(exception, "AI 生成模板包写盘失败，包名：{PackageName}。", packageName);
            return AiTemplateGenerationResult.Failed(new List<string> { $"模板包写盘失败：{exception.Message}" }, rawLlmOutput);
        }
        finally
        {
            TryDeleteDirectory(tempPackageDir);
        }
    }

    /// <summary>
    /// 追加模式落库：丢弃 AI 返回的包级元数据（packageName/basePackage/typeMap），仅取 files[] 映射为
    /// 写入条目（模板相对路径加分组前缀，输出路径保持参考约定）批量追加到目标用户包；此路径不写临时目录。
    /// </summary>
    /// <param name="document">解析后的模板包文档，追加模式仅消费 files[]。</param>
    /// <param name="request">生成请求，读取目标包名与分组前缀。</param>
    /// <param name="rawLlmOutput">原始 LLM 输出，失败时随结果返回。</param>
    /// <param name="cancellationToken">取消标记。</param>
    /// <returns>生成结果，成功携带目标包名与目标包目录。</returns>
    private async Task<AiTemplateGenerationResult> AppendToPackageAsync(
        GeneratedPackageDocument document,
        AiTemplateGenerationRequest request,
        string? rawLlmOutput,
        CancellationToken cancellationToken)
    {
        string targetPackageName = request.TargetPackageName!;
        string? targetGroup = NormalizeGroupPrefix(request.TargetGroup);

        // 追加模式要求至少一个模板文件，空 files 明确提示失败
        if (document.Files.Count == 0)
        {
            return AiTemplateGenerationResult.Failed(
                new List<string> { "AI 未返回任何模板文件，无法追加到目标包。" },
                rawLlmOutput);
        }

        // 映射 files[] 为写入条目：模板相对路径加分组前缀，输出路径保持参考约定，丢弃 AI 包级元数据
        var entries = new List<TemplateFileWriteEntry>(document.Files.Count);
        foreach (PackageFile file in document.Files)
        {
            if (file is null)
            {
                return AiTemplateGenerationResult.Failed(
                    new List<string> { "AI 返回 files 中存在空条目。" },
                    rawLlmOutput);
            }

            // 模板相对路径为空时明确拒绝，避免空 name 在分组前缀下被折叠成以分组命名的文件
            if (string.IsNullOrWhiteSpace(file.Name))
            {
                return AiTemplateGenerationResult.Failed(
                    new List<string> { "AI 返回 files[].name 不能为空。" },
                    rawLlmOutput);
            }

            string relativePath = string.IsNullOrEmpty(targetGroup)
                ? file.Name
                : $"{targetGroup}/{file.Name}";

            entries.Add(new TemplateFileWriteEntry(relativePath, file.RelativeOutputPath, file.Content ?? string.Empty, file.Enabled));
        }

        // 批量追加到目标包：路径安全、已存在预检与失败回滚均由模板包管理服务承载
        TemplatePackageOperationResult result = await _templatePackageService.AppendTemplateFilesAsync(targetPackageName, entries, cancellationToken).ConfigureAwait(false);
        if (result.Status != TemplatePackageOperationStatus.Succeeded)
        {
            return AiTemplateGenerationResult.Failed(new List<string> { result.Message }, rawLlmOutput);
        }

        _logger.LogInformation(
            "AI 模板生成成功：追加 {FileCount} 个模板到用户包 {PackageName}，目录：{Directory}。",
            entries.Count,
            targetPackageName,
            result.Package?.RootPath);
        return AiTemplateGenerationResult.Success(targetPackageName, result.Package?.RootPath, rawLlmOutput);
    }

    /// <summary>
    /// 规范化分组目录前缀：统一正斜杠并折叠冗余分隔符；空输入返回 null 表示不分组。
    /// 含绝对路径或 .. 段的前缀由模板包管理服务在路径安全校验时拒绝。
    /// </summary>
    /// <param name="group">原始分组前缀，可空。</param>
    /// <returns>规范化后的分组前缀；空输入返回 null。</returns>
    private static string? NormalizeGroupPrefix(string? group)
    {
        if (string.IsNullOrWhiteSpace(group))
        {
            return null;
        }

        return TemplatePackageLoader.NormalizeRelativePath(group);
    }

    /// <summary>
    /// 提交前检查包名与模板库已有包的冲突：内置包同名直接拒绝，用户包同名未确认覆盖时返回冲突。
    /// </summary>
    /// <param name="packageName">模板包名。</param>
    /// <param name="overwrite">与用户包同名时是否允许覆盖。</param>
    /// <param name="cancellationToken">取消标记。</param>
    /// <returns>冲突结果，无冲突返回 null。</returns>
    private async Task<TemplatePackageOperationResult?> CheckPackageConflictAsync(string packageName, bool overwrite, CancellationToken cancellationToken)
    {
        IReadOnlyList<TemplatePackageInfo> packages = await _templatePackageService.ListPackagesAsync(cancellationToken).ConfigureAwait(false);
        TemplatePackageInfo? existing = packages.FirstOrDefault(package => string.Equals(package.Name, packageName, StringComparison.OrdinalIgnoreCase));
        if (existing is null)
        {
            return null;
        }

        if (existing.IsBuiltin)
        {
            return TemplatePackageOperationResult.BuiltinReadonly($"内置包 {packageName} 已存在且只读，请更换包名后重新生成。");
        }

        return overwrite ? null : TemplatePackageOperationResult.NameConflict($"同名用户包 {packageName} 已存在，需确认覆盖。");
    }

    /// <summary>
    /// 校验追加模式目标包：包名必填、目标包存在且非内置（内置包只读拒绝）；不满足返回错误结果，满足返回 null。
    /// </summary>
    /// <param name="targetPackageName">目标用户包名，追加模式必填。</param>
    /// <param name="cancellationToken">取消标记。</param>
    /// <returns>错误操作结果，校验通过返回 null。</returns>
    private async Task<TemplatePackageOperationResult?> ValidateAppendTargetAsync(string? targetPackageName, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(targetPackageName))
        {
            return TemplatePackageOperationResult.Failure("追加模式必须指定目标用户包。");
        }

        try
        {
            TemplatePackageInfo targetPackage = await _templatePackageService.LoadPackageAsync(targetPackageName, cancellationToken).ConfigureAwait(false);
            if (targetPackage.IsBuiltin)
            {
                return TemplatePackageOperationResult.BuiltinReadonly($"内置包 {targetPackageName} 只读，不可追加模板。");
            }
        }
        catch (Exception exception) when (exception is TemplatePackageException or IOException or UnauthorizedAccessException)
        {
            return TemplatePackageOperationResult.Failure($"目标模板包不存在或加载失败：{exception.Message}");
        }

        return null;
    }

    /// <summary>
    /// 将生成包文档映射为 template.json 结构与模板文件写入临时目录，engine 固定 scriban，路径双重防目录穿越。
    /// </summary>
    /// <param name="document">解析后的模板包文档。</param>
    /// <param name="tempPackageDir">临时包目录绝对路径。</param>
    /// <param name="cancellationToken">取消标记。</param>
    /// <exception cref="TemplatePackageException">路径穿越、文件缺失或映射不合法时抛出。</exception>
    private static async Task WritePackageToDirectoryAsync(GeneratedPackageDocument document, string tempPackageDir, CancellationToken cancellationToken)
    {
        var manifest = new TemplateManifest
        {
            Name = document.PackageName,
            Description = document.Description,
            Engine = TemplatePackageLoader.SupportedEngine,
            BasePackage = document.BasePackage,
            TypeMap = document.TypeMap ?? new Dictionary<string, string>(),
            Files = new List<TemplateFileEntry>()
        };

        foreach (PackageFile file in document.Files)
        {
            if (file is null)
            {
                throw new TemplatePackageException("生成包 files 中存在空条目。");
            }

            // 模板相对路径规范化并防目录穿越，解析到临时包目录内绝对路径
            string templateRelative = TemplatePackageLoader.NormalizeRelativePath(file.Name);
            if (templateRelative.Length == 0)
            {
                throw new TemplatePackageException("生成包 files[].name 不能为空。");
            }

            string templateFullPath = TemplatePackageLoader.ResolveWithinRoot(tempPackageDir, templateRelative);

            // 输出相对路径静态骨架防目录穿越，允许 .. 段（由生成侧解析时限定在工作区根内）
            string outputRelative = (file.RelativeOutputPath ?? string.Empty).Trim();
            if (!TemplatePackageLoader.IsSafeOutputPathSkeleton(outputRelative))
            {
                throw new TemplatePackageException($"生成包文件 {templateRelative} 的输出路径不合法，请修正 relativeOutputPath。");
            }

            manifest.Files.Add(new TemplateFileEntry
            {
                Template = templateRelative,
                Output = outputRelative,
                Enabled = file.Enabled
            });

            // 创建子目录并写入模板文件（UTF-8 无 BOM）
            string? parentDirectory = Path.GetDirectoryName(templateFullPath);
            if (!string.IsNullOrEmpty(parentDirectory))
            {
                Directory.CreateDirectory(parentDirectory);
            }

            await File.WriteAllTextAsync(templateFullPath, file.Content ?? string.Empty, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), cancellationToken).ConfigureAwait(false);
        }

        if (manifest.Files.Count == 0)
        {
            throw new TemplatePackageException("生成包 files 不能为空，至少需要一个模板文件。");
        }

        // 写入 template.json 清单
        string manifestJson = JsonSerializer.Serialize(manifest, TemplatePackageLoader.JsonOptions);
        await File.WriteAllTextAsync(Path.Combine(tempPackageDir, TemplatePackageLoader.ManifestFileName), manifestJson, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 尽力删除临时目录，失败仅记录警告不阻断主流程。
    /// </summary>
    /// <param name="directory">待删除目录绝对路径。</param>
    private void TryDeleteDirectory(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(exception, "清理 AI 生成临时目录失败：{Directory}。", directory);
        }
    }

    /// <summary>
    /// 从嵌入资源加载 TEMPLATE_SPEC 规范文本，供提示词组装使用；资源缺失时返回空串。
    /// </summary>
    /// <returns>TEMPLATE_SPEC 规范文本。</returns>
    private static string LoadTemplateSpec()
    {
        Assembly assembly = typeof(TemplateAiGenerator).Assembly;
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

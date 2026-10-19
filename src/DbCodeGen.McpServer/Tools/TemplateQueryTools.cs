using System.ComponentModel;
using DbCodeGen.Core.Templates;
using DbCodeGen.Core.Templates.Packages;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace DbCodeGen.McpServer.Tools;

/// <summary>
/// 模板只读查询工具组：模板包枚举、包内模板条目枚举（分组/语言段过滤）与模板正文读取。
/// 全部为只读薄壳，直接转发 Core 模板包服务与模板文件读写服务，不引入 MCP 侧独立状态。
/// </summary>
[McpServerToolType]
public sealed class TemplateQueryTools
{
    /// <summary>
    /// 包契约文档缺省文件名：manifest 未声明 contractPath 时按该路径发现传参契约；
    /// 即使未在 template.json files[] 声明，read_template 也可读取、list_templates 以文档型条目呈现。
    /// </summary>
    private const string DefaultContractFileName = TemplatePackageLoader.DefaultContractPath;

    private readonly ITemplatePackageService _packageService;
    private readonly TemplateFileWriter _templateFileWriter;
    private readonly ILogger<TemplateQueryTools> _logger;

    /// <summary>
    /// 以模板包服务、模板文件读写服务与日志器构造模板只读查询工具组。
    /// </summary>
    /// <param name="packageService">模板包服务，承载包枚举与单包加载校验。</param>
    /// <param name="templateFileWriter">模板文件读写服务，承载防目录穿越的模板正文读取。</param>
    /// <param name="logger">工具组日志器。</param>
    public TemplateQueryTools(
        ITemplatePackageService packageService,
        TemplateFileWriter templateFileWriter,
        ILogger<TemplateQueryTools> logger)
    {
        _packageService = packageService;
        _templateFileWriter = templateFileWriter;
        _logger = logger;
    }

    /// <summary>
    /// 列出全部可用模板包（内置包 + 用户包），携带内置只读标记、文件数、能力元数据（角色/技术栈/作用域/
    /// 后端 View/前端页面/契约路径/部署形态）与契约缺失告警，供生成前按用途选包与契约发现。
    /// </summary>
    /// <param name="cancellationToken">取消标记。</param>
    /// <returns>模板包摘要列表（结构化内容，输出结构由 ListTemplatePackagesResult 声明）。</returns>
    [McpServerTool(Name = "list_template_packages", Title = "列出模板包", ReadOnly = true, Idempotent = true, Destructive = false, UseStructuredContent = true)]
    [Description("列出全部模板包（内置包与用户包），含名称、说明、内置只读标记、文件数、修改时间与能力元数据：role（backend/scaffold/web/fullstack/utility）、stack（java/net/web/mixed）、scopes（table/module/project）、hasBackendView（是否含后端 View 层）、hasWebPage（是否含前端页面）、contractPath（传参契约文档，缺省 README.md.scriban）、deployModes（如 standalone/cloud）。包根契约缺失时回执 warnings 含 MissingContract（非阻断，提示包作者补契约）。")]
    public async Task<ListTemplatePackagesResult> ListTemplatePackagesAsync(CancellationToken cancellationToken)
    {
        try
        {
            IReadOnlyList<TemplatePackageInfo> packages = await _packageService.ListPackagesAsync(cancellationToken);

            // 投影为摘要 DTO：不透出 RootPath/ManifestPath 等本地磁盘细节，能力元数据供调用方判断包用途
            List<TemplatePackageSummary> items = new();
            foreach (TemplatePackageInfo package in packages)
            {
                bool hasContract = ContractExists(package);
                List<string> warnings = new();
                if (!hasContract)
                {
                    warnings.Add($"MissingContract：包 {package.Name} 未在包根找到契约文档 {package.ContractPath}；请补契约（传参键、命名与路径语义）或经 manifest.contractPath 指向实际契约文件。");
                }

                items.Add(new TemplatePackageSummary(
                    package.Name,
                    package.Description,
                    package.Engine,
                    package.BasePackage ?? string.Empty,
                    package.IsBuiltin,
                    package.Files.Count,
                    package.ModifiedTime,
                    package.Role,
                    package.Stack,
                    package.Scopes,
                    package.HasBackendView,
                    package.HasWebPage,
                    package.ContractPath,
                    package.DeployModes,
                    hasContract,
                    warnings));
            }

            _logger.LogInformation("list_template_packages 完成，包数量 {PackageCount}。", items.Count);
            return new ListTemplatePackagesResult(items);
        }
        catch (Exception exception)
        {
            // 强类型查询工具的错误走协议 isError 通道，避免错误负载混入结构化成功应答破坏输出契约
            _logger.LogError(exception, "list_template_packages 执行失败。");
            throw ToolError.AsMcpException(exception, cancellationToken);
        }
    }

    /// <summary>
    /// 列出指定模板包内的模板条目，可按分组（相对路径子目录前缀）与语言段过滤，
    /// 返回条目相对路径、输出路径模板、启用标记与推导语言；
    /// 包根存在且未在清单声明 README.md.scriban 时附带一条文档型条目（不进清单、不参与生成）。
    /// </summary>
    /// <param name="package">模板包包名。</param>
    /// <param name="group">可选分组前缀，匹配模板相对路径的子目录前缀，如 "controller"、子目录多级用正斜杠分隔。</param>
    /// <param name="type">可选语言段过滤，取值 java / csharp / xml / sql / json / plain，对应模板文件名语言段约定。</param>
    /// <param name="cancellationToken">取消标记。</param>
    /// <returns>模板条目列表（含过滤后总数；结构化内容，输出结构由 ListTemplatesResult 声明）。</returns>
    [McpServerTool(Name = "list_templates", Title = "列出模板文件", ReadOnly = true, Idempotent = true, Destructive = false, UseStructuredContent = true)]
    [Description("列出指定模板包内的模板条目（相对路径、输出路径模板、启用标记、语言、作用域 scope：table/module/project），可按分组前缀与语言段过滤。包根存在且未在 template.json files[] 声明 README.md.scriban 时附带文档型条目（enabled=false、output 空串、scope=project；不在清单中、不计入 fileCount、不参与生成）。")]
    public async Task<ListTemplatesResult> ListTemplatesAsync(
        string package,
        string? group = null,
        string? type = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            // 语言段过滤先行解析，非法取值直接结构化拒绝并列出合法值
            HighlightLanguage? languageFilter = null;
            if (type is not null)
            {
                languageFilter = ParseLanguageFilter(type);
            }

            // 分组前缀规范化：统一正斜杠、去首尾分隔符，匹配时补分隔符防 "a" 误命中 "ab/..."
            string? groupPrefix = NormalizeGroupPrefix(group);

            TemplatePackageInfo packageInfo = await _packageService.LoadPackageAsync(package, cancellationToken);
            List<TemplateEntry> entries = new();
            foreach (TemplateFileInfo file in packageInfo.Files)
            {
                if (!MatchesGroupPrefix(file.RelativeTemplatePath, groupPrefix))
                {
                    continue;
                }

                HighlightLanguage language = HighlightLanguageResolver.FromTemplateFileName(
                    Path.GetFileName(file.RelativeTemplatePath));
                if (languageFilter is not null && language != languageFilter)
                {
                    continue;
                }

                entries.Add(new TemplateEntry(
                    file.RelativeTemplatePath,
                    file.OutputPath,
                    file.IsEnabled,
                    language.ToString(),
                    TemplateScopeParser.ToContractText(file.Scope),
                    file.OutputRoot is null ? "legacy" : TemplateOutputRootParser.ToContractText(file.OutputRoot.Value),
                    file.Role,
                    IsContractPath(packageInfo, file.RelativeTemplatePath)));
            }

            // 包契约文档型条目：契约路径存在且未被清单声明时合成，供传参契约可被发现；
            // 受分组/语言过滤约束（根级契约仅在无分组过滤时命中、语言段按契约文件名推导），不进清单、不计入文件数、不参与生成
            if (groupPrefix is null && HasUndeclaredContract(packageInfo))
            {
                HighlightLanguage contractLanguage = HighlightLanguageResolver.FromTemplateFileName(
                    Path.GetFileName(packageInfo.ContractPath));
                if (languageFilter is null || contractLanguage == languageFilter)
                {
                    entries.Add(new TemplateEntry(
                        packageInfo.ContractPath,
                        string.Empty,
                        false,
                        contractLanguage.ToString(),
                        TemplateScopeParser.ToContractText(TemplateScope.Project),
                        "legacy",
                        TemplateFileRoleResolver.Contract,
                        true));
                }
            }

            // 契约缺失为非阻断告警：包仍可枚举与生成，但调用方无法从契约获知传参键与命名语义
            List<string> warnings = new();
            if (!ContractExists(packageInfo))
            {
                warnings.Add($"MissingContract：包 {packageInfo.Name} 未在包根找到契约文档 {packageInfo.ContractPath}；请补契约或经 manifest.contractPath 指向实际契约文件。");
            }

            _logger.LogInformation(
                "list_templates 完成，包 {PackageName}，返回 {EntryCount} 条（清单声明文件 {TotalCount} 个）。",
                package, entries.Count, packageInfo.Files.Count);
            return new ListTemplatesResult(
                packageInfo.Name,
                packageInfo.IsBuiltin,
                packageInfo.BasePackage ?? string.Empty,
                entries,
                warnings);
        }
        catch (Exception exception)
        {
            // 强类型查询工具的错误走协议 isError 通道，避免错误负载混入结构化成功应答破坏输出契约
            _logger.LogError(exception, "list_templates 执行失败，包名 {PackageName}。", package);
            throw ToolError.AsMcpException(exception, cancellationToken);
        }
    }

    /// <summary>
    /// 读取指定模板的正文全文：仅允许读取包清单声明的模板文件，路径经防目录穿越校验；
    /// 包契约文档（manifest.contractPath，缺省 README.md.scriban）为窄例外（可未声明），
    /// 按文档型条目返回、不参与生成。
    /// </summary>
    /// <param name="package">模板包包名。</param>
    /// <param name="path">模板文件相对包根路径（与 list_templates 返回的 path 一致，可含分组目录）。</param>
    /// <param name="cancellationToken">取消标记。</param>
    /// <returns>模板正文全文、条目元数据与内容摘要。</returns>
    [McpServerTool(Name = "read_template", Title = "读取模板", ReadOnly = true, Idempotent = true, Destructive = false)]
    [Description("读取指定模板文件的正文全文（Scriban 源文本）与正文内容摘要 contentHash（SHA256 前 16 位 hex 小写，供 save_template_edit 条件写作期望值）；path 取 list_templates 返回的相对路径。包契约文档（manifest.contractPath，缺省 README.md.scriban）即使未在 template.json files[] 声明也可读取（文档型条目：enabled=false、output 为空串、不参与生成）；其它未声明路径一律拒绝。契约缺失时 list_template_packages 回执 MissingContract 告警（非阻断）。")]
    public async Task<object> ReadTemplateAsync(string package, string path, CancellationToken cancellationToken)
    {
        try
        {
            TemplatePackageInfo packageInfo = await _packageService.LoadPackageAsync(package, cancellationToken);

            // 以包清单条目为读取白名单，杜绝任意文件读取；包契约文档为唯一窄例外（下方单独判定）
            TemplateFileInfo? entry = FindFileEntry(packageInfo, path);
            bool isContract = entry is null && IsContractPath(packageInfo, path);
            if (entry is null && !isContract)
            {
                throw new TemplatePackageException(
                    $"模板文件不存在：包 {package} 中未声明模板路径 {path}（契约路径为 {packageInfo.ContractPath}）。");
            }

            // 契约窄例外：调用方路径必须恰为 manifest.contractPath（缺省根 README.md.scriban），且包根实际存在该文件；
            // 读取仍走 TemplateFileWriter.ReadAsync 的防穿越校验 + UTF-8 去 BOM 解码通道
            string relativePath = entry?.RelativeTemplatePath ?? packageInfo.ContractPath;
            if (isContract && !ContractExists(packageInfo))
            {
                throw new TemplatePackageException($"模板文件不存在：包 {package} 的契约文档 {packageInfo.ContractPath} 不存在（MissingContract）。");
            }

            // 复用桌面端同款读取通道：防目录穿越 + UTF-8 去 BOM 解码
            string content = await _templateFileWriter.ReadAsync(packageInfo, relativePath, cancellationToken);
            HighlightLanguage language = HighlightLanguageResolver.FromTemplateFileName(
                Path.GetFileName(relativePath));

            _logger.LogInformation(
                "read_template 完成，包 {PackageName}，路径 {TemplatePath}，字符数 {CharCount}。",
                package, relativePath, content.Length);
            return new ReadTemplateResult(
                packageInfo.Name,
                relativePath,
                entry?.OutputPath ?? string.Empty,
                entry?.IsEnabled ?? false,
                language.ToString(),
                content,
                TemplateContentHash.Compute(content));
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "read_template 执行失败，包名 {PackageName}，路径 {TemplatePath}。", package, path);
            return ToolError.From(exception, cancellationToken);
        }
    }

    /// <summary>
    /// 解析语言段过滤入参为高亮语言枚举；大小写不敏感，非法取值抛参数异常并列出合法值。
    /// </summary>
    /// <param name="type">语言段文本。</param>
    /// <returns>解析后的高亮语言。</returns>
    private static HighlightLanguage ParseLanguageFilter(string type)
    {
        return type.Trim().ToLowerInvariant() switch
        {
            "java" => HighlightLanguage.Java,
            "csharp" or "cs" => HighlightLanguage.CSharp,
            "xml" => HighlightLanguage.Xml,
            "sql" => HighlightLanguage.Sql,
            "json" => HighlightLanguage.Json,
            "plain" => HighlightLanguage.Plain,
            _ => throw new ArgumentException($"无效的语言段过滤值：{type}；合法取值为 java / csharp / cs / xml / sql / json / plain。")
        };
    }

    /// <summary>
    /// 规范化分组前缀：统一为正斜杠并去首尾分隔符；空白输入视为不过滤返回 null。
    /// </summary>
    /// <param name="group">分组前缀原文。</param>
    /// <returns>规范化分组前缀；不过滤时为 null。</returns>
    private static string? NormalizeGroupPrefix(string? group)
    {
        if (string.IsNullOrWhiteSpace(group))
        {
            return null;
        }

        string trimmed = group.Trim();
        return trimmed.Replace('\\', '/').Trim('/');
    }

    /// <summary>
    /// 判断模板相对路径是否命中分组前缀：前缀需对齐到目录边界，避免 "a" 误命中 "ab/x"。
    /// </summary>
    /// <param name="relativePath">模板相对路径（正斜杠规范化）。</param>
    /// <param name="groupPrefix">规范化分组前缀；null 表示不过滤。</param>
    /// <returns>是否命中。</returns>
    private static bool MatchesGroupPrefix(string relativePath, string? groupPrefix)
    {
        if (groupPrefix is null)
        {
            return true;
        }

        return relativePath.StartsWith(groupPrefix + "/", StringComparison.OrdinalIgnoreCase)
            || string.Equals(relativePath, groupPrefix, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 在包清单文件条目中按相对路径查找目标模板；入参路径统一正斜杠后与条目忽略大小写比对。
    /// </summary>
    /// <param name="package">包运行时信息。</param>
    /// <param name="path">调用方传入的模板相对路径。</param>
    /// <returns>命中的文件条目；未命中返回 null。</returns>
    private static TemplateFileInfo? FindFileEntry(TemplatePackageInfo package, string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        // 调用方可能传反斜杠形态，统一正斜杠后比对
        string normalized = path.Trim().Replace('\\', '/');
        return package.Files.FirstOrDefault(
            file => string.Equals(file.RelativeTemplatePath, normalized, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// 判断调用方路径是否为包契约文档：去空白、反斜杠转正斜杠后与 manifest.contractPath（缺省 README.md.scriban）
    /// 忽略大小写全等；子目录形态（相对契约路径之外）、父级跳转形态与绝对路径均不命中，保持单一窄例外。
    /// </summary>
    /// <param name="package">包运行时信息，提供契约路径。</param>
    /// <param name="path">调用方传入的模板相对路径。</param>
    /// <returns>是否为包契约文档路径。</returns>
    private static bool IsContractPath(TemplatePackageInfo package, string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        string normalized = path.Trim().Replace('\\', '/');
        string contractPath = string.IsNullOrWhiteSpace(package.ContractPath)
            ? DefaultContractFileName
            : package.ContractPath;
        return string.Equals(normalized, contractPath, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 判断包契约文档是否存在：契约路径由加载器归一为安全相对路径（不含 .. 与盘符），
    /// 此处仅作存在性确认，正文读取仍统一经 TemplateFileWriter.ReadAsync 的防穿越通道。
    /// </summary>
    /// <param name="package">包运行时信息。</param>
    /// <returns>契约文档存在时为 true。</returns>
    private static bool ContractExists(TemplatePackageInfo package)
    {
        string contractPath = string.IsNullOrWhiteSpace(package.ContractPath)
            ? DefaultContractFileName
            : package.ContractPath;
        string fullPath = Path.Combine(package.RootPath, contractPath.Replace('/', Path.DirectorySeparatorChar));
        return File.Exists(fullPath);
    }

    /// <summary>
    /// 判断包契约文档是否为未在清单声明的文档文件：契约文档存在且清单 files 未声明同名条目时返回 true。
    /// </summary>
    /// <param name="package">包运行时信息。</param>
    /// <returns>存在未声明的契约文档时为 true。</returns>
    private static bool HasUndeclaredContract(TemplatePackageInfo package)
    {
        string contractPath = string.IsNullOrWhiteSpace(package.ContractPath)
            ? DefaultContractFileName
            : package.ContractPath;
        bool declared = package.Files.Any(
            file => string.Equals(file.RelativeTemplatePath, contractPath, StringComparison.OrdinalIgnoreCase));
        if (declared)
        {
            return false;
        }

        return ContractExists(package);
    }
}

/// <summary>
/// 模板包摘要 DTO。
/// </summary>
/// <param name="Name">包名。</param>
/// <param name="Description">包说明。</param>
/// <param name="Engine">模板引擎，当前固定 scriban。</param>
/// <param name="BasePackage">基础包名，未配置时为空串。</param>
/// <param name="IsBuiltin">是否内置包（只读，不可编辑删除）。</param>
/// <param name="FileCount">包内模板文件数。</param>
/// <param name="ModifiedTime">包目录最近修改时间。</param>
/// <param name="Role">包角色：backend / scaffold / web / fullstack / utility。</param>
/// <param name="Stack">技术栈：java / net / web / mixed。</param>
/// <param name="Scopes">文件作用域集合（table / module / project）。</param>
/// <param name="HasBackendView">是否具备后端 View 层产出。</param>
/// <param name="HasWebPage">是否具备前端页面产出。</param>
/// <param name="ContractPath">契约文档相对包根路径（缺省 README.md.scriban）。</param>
/// <param name="DeployModes">支持的部署形态清单。</param>
/// <param name="HasContract">契约文档是否实际存在。</param>
/// <param name="Warnings">能力/契约告警（如 MissingContract），无告警时为空列表。</param>
public sealed record TemplatePackageSummary(
    string Name,
    string Description,
    string Engine,
    string BasePackage,
    bool IsBuiltin,
    int FileCount,
    DateTime ModifiedTime,
    string Role,
    string Stack,
    IReadOnlyList<string> Scopes,
    bool HasBackendView,
    bool HasWebPage,
    string ContractPath,
    IReadOnlyList<string> DeployModes,
    bool HasContract,
    IReadOnlyList<string> Warnings);

/// <summary>
/// list_template_packages 应答 DTO。
/// </summary>
/// <param name="Packages">模板包摘要列表。</param>
public sealed record ListTemplatePackagesResult(IReadOnlyList<TemplatePackageSummary> Packages);

/// <summary>
/// 模板条目 DTO。
/// </summary>
/// <param name="Path">模板相对包根路径（可含分组目录）。</param>
/// <param name="Output">输出相对路径模板，支持 {{变量}} 占位。</param>
/// <param name="Enabled">是否默认参与生成。</param>
/// <param name="Language">按文件名语言段推导的目标语言（HighlightLanguage 口径）。</param>
/// <param name="Scope">模板作用域：table（按表集逐表渲染）/ module（按模块清单逐个）/ project（整次一次）；
/// manifest 缺省 scope 的条目显示 table。</param>
/// <param name="Root">输出根语义：legacy（未声明，按作用域沿用既有语义）/ output / workspace / web。</param>
/// <param name="Role">产出角色（manifest 声明或按输出路径推断）。</param>
/// <param name="IsContract">是否为包契约文档条目（manifest.contractPath，缺省 README.md.scriban）。</param>
public sealed record TemplateEntry(
    string Path,
    string Output,
    bool Enabled,
    string Language,
    string Scope,
    string Root,
    string Role,
    bool IsContract);

/// <summary>
/// list_templates 应答 DTO。
/// </summary>
/// <param name="Package">包名。</param>
/// <param name="IsBuiltin">是否内置包。</param>
/// <param name="BasePackage">基础包名，未配置时为空串。</param>
/// <param name="Templates">过滤后命中的模板条目列表。</param>
/// <param name="Warnings">契约缺失等非阻断告警，无告警时为空列表。</param>
public sealed record ListTemplatesResult(
    string Package,
    bool IsBuiltin,
    string BasePackage,
    IReadOnlyList<TemplateEntry> Templates,
    IReadOnlyList<string> Warnings);

/// <summary>
/// read_template 应答 DTO。
/// </summary>
/// <param name="Package">包名。</param>
/// <param name="Path">模板相对包根路径。</param>
/// <param name="Output">输出相对路径模板。</param>
/// <param name="Enabled">是否默认参与生成。</param>
/// <param name="Language">目标语言。</param>
/// <param name="Content">模板正文全文。</param>
/// <param name="ContentHash">正文内容摘要（SHA256 前 16 位 hex 小写），供 save_template_edit 条件写作期望值。</param>
internal sealed record ReadTemplateResult(
    string Package,
    string Path,
    string Output,
    bool Enabled,
    string Language,
    string Content,
    string ContentHash);

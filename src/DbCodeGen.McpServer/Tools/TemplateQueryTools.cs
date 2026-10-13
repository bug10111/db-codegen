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
    /// 列出全部可用模板包（内置包 + 用户包），携带内置只读标记与文件数，供生成前选择包。
    /// </summary>
    /// <param name="cancellationToken">取消标记。</param>
    /// <returns>模板包摘要列表（结构化内容，输出结构由 ListTemplatePackagesResult 声明）。</returns>
    [McpServerTool(Name = "list_template_packages", Title = "列出模板包", ReadOnly = true, Idempotent = true, Destructive = false, UseStructuredContent = true)]
    [Description("列出全部模板包（内置包与用户包），含名称、说明、内置只读标记、文件数与修改时间。")]
    public async Task<ListTemplatePackagesResult> ListTemplatePackagesAsync(CancellationToken cancellationToken)
    {
        try
        {
            IReadOnlyList<TemplatePackageInfo> packages = await _packageService.ListPackagesAsync(cancellationToken);

            // 投影为摘要 DTO：不透出 RootPath/ManifestPath 等本地磁盘细节
            List<TemplatePackageSummary> items = new();
            foreach (TemplatePackageInfo package in packages)
            {
                items.Add(new TemplatePackageSummary(
                    package.Name,
                    package.Description,
                    package.Engine,
                    package.BasePackage,
                    package.IsBuiltin,
                    package.Files.Count,
                    package.ModifiedTime));
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
    /// 返回条目相对路径、输出路径模板、启用标记与推导语言。
    /// </summary>
    /// <param name="package">模板包包名。</param>
    /// <param name="group">可选分组前缀，匹配模板相对路径的子目录前缀，如 "controller"、子目录多级用正斜杠分隔。</param>
    /// <param name="type">可选语言段过滤，取值 java / csharp / xml / sql / json / plain，对应模板文件名语言段约定。</param>
    /// <param name="cancellationToken">取消标记。</param>
    /// <returns>模板条目列表（含过滤后总数；结构化内容，输出结构由 ListTemplatesResult 声明）。</returns>
    [McpServerTool(Name = "list_templates", Title = "列出模板文件", ReadOnly = true, Idempotent = true, Destructive = false, UseStructuredContent = true)]
    [Description("列出指定模板包内的模板条目（相对路径、输出路径模板、启用标记、语言、作用域 scope：table/module/project），可按分组前缀与语言段过滤。")]
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
                    TemplateScopeParser.ToContractText(file.Scope)));
            }

            _logger.LogInformation(
                "list_templates 完成，包 {PackageName}，命中 {MatchedCount}/{TotalCount} 条。",
                package, entries.Count, packageInfo.Files.Count);
            return new ListTemplatesResult(
                packageInfo.Name,
                packageInfo.IsBuiltin,
                packageInfo.BasePackage,
                entries);
        }
        catch (Exception exception)
        {
            // 强类型查询工具的错误走协议 isError 通道，避免错误负载混入结构化成功应答破坏输出契约
            _logger.LogError(exception, "list_templates 执行失败，包名 {PackageName}。", package);
            throw ToolError.AsMcpException(exception, cancellationToken);
        }
    }

    /// <summary>
    /// 读取指定模板的正文全文，仅允许读取包清单声明的模板文件，路径经防目录穿越校验。
    /// </summary>
    /// <param name="package">模板包包名。</param>
    /// <param name="path">模板文件相对包根路径（与 list_templates 返回的 path 一致，可含分组目录）。</param>
    /// <param name="cancellationToken">取消标记。</param>
    /// <returns>模板正文全文、条目元数据与内容摘要。</returns>
    [McpServerTool(Name = "read_template", Title = "读取模板", ReadOnly = true, Idempotent = true, Destructive = false)]
    [Description("读取指定模板文件的正文全文（Scriban 源文本）与正文内容摘要 contentHash（SHA256 前 16 位 hex 小写，供 save_template_edit 条件写作期望值）；path 取 list_templates 返回的相对路径。")]
    public async Task<object> ReadTemplateAsync(string package, string path, CancellationToken cancellationToken)
    {
        try
        {
            TemplatePackageInfo packageInfo = await _packageService.LoadPackageAsync(package, cancellationToken);

            // 仅允许读取包清单声明的模板文件：以清单条目为白名单，杜绝任意文件读取
            TemplateFileInfo? entry = FindFileEntry(packageInfo, path);
            if (entry is null)
            {
                throw new TemplatePackageException($"模板文件不存在：包 {package} 中未声明模板路径 {path}。");
            }

            // 复用桌面端同款读取通道：防目录穿越 + UTF-8 去 BOM 解码
            string content = await _templateFileWriter.ReadAsync(packageInfo, entry.RelativeTemplatePath, cancellationToken);
            HighlightLanguage language = HighlightLanguageResolver.FromTemplateFileName(
                Path.GetFileName(entry.RelativeTemplatePath));

            _logger.LogInformation(
                "read_template 完成，包 {PackageName}，路径 {TemplatePath}，字符数 {CharCount}。",
                package, entry.RelativeTemplatePath, content.Length);
            return new ReadTemplateResult(
                packageInfo.Name,
                entry.RelativeTemplatePath,
                entry.OutputPath,
                entry.IsEnabled,
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

        return group.Trim().Replace('\\', '/').Trim('/');
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
}

/// <summary>
/// 模板包摘要 DTO。
/// </summary>
/// <param name="Name">包名。</param>
/// <param name="Description">包说明。</param>
/// <param name="Engine">模板引擎，当前固定 scriban。</param>
/// <param name="BasePackage">基础包名，未配置时为 null。</param>
/// <param name="IsBuiltin">是否内置包（只读，不可编辑删除）。</param>
/// <param name="FileCount">包内模板文件数。</param>
/// <param name="ModifiedTime">包目录最近修改时间。</param>
public sealed record TemplatePackageSummary(
    string Name,
    string Description,
    string Engine,
    string? BasePackage,
    bool IsBuiltin,
    int FileCount,
    DateTime ModifiedTime);

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
public sealed record TemplateEntry(string Path, string Output, bool Enabled, string Language, string Scope);

/// <summary>
/// list_templates 应答 DTO。
/// </summary>
/// <param name="Package">包名。</param>
/// <param name="IsBuiltin">是否内置包。</param>
/// <param name="BasePackage">基础包名，未配置时为 null。</param>
/// <param name="Templates">过滤后命中的模板条目列表。</param>
public sealed record ListTemplatesResult(
    string Package,
    bool IsBuiltin,
    string? BasePackage,
    IReadOnlyList<TemplateEntry> Templates);

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

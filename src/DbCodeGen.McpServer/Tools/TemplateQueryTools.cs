using System.ComponentModel;
using System.Text.Json.Serialization;
using DbCodeGen.Core.Templates;
using DbCodeGen.Core.Templates.Packages;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
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

    /// <summary>
    /// 单片字符上限护栏下界：低于该值的 maxChars 一律结构化拒绝，防止过小分片产生碎片化往返。
    /// </summary>
    private const int MinSliceChars = 1000;

    /// <summary>
    /// 单片字符上限护栏上界，同时是分片模式的缺省单片上限：与 doc-base 分片先例的单片上限口径一致。
    /// </summary>
    private const int MaxSliceChars = 100000;

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
    [Description("用途：列出全部模板包（内置包与用户包）及其能力元数据与命名预设，是选包第一步。\n"
        + "何时用：生成或模板维护前选包；需要确认包是否含后端 View/前端页面、支持哪些作用域与部署形态、有哪些 preset 时。\n"
        + "参数：无。\n"
        + "返回：packages[]——name（包名，=目录名）、description（说明）、engine（固定 scriban）、basePackage（基础包名，未配置为空串）、"
        + "isBuiltin（内置只读）、fileCount（清单模板文件数）、modifiedTime（目录最近修改时间）、role（backend/scaffold/web/fullstack/utility）、"
        + "stack（java/net/web/mixed）、scopes（table/module/project）、hasBackendView（含后端 View 层）、hasWebPage（含前端页面）、"
        + "contractPath（传参契约文档，缺省 README.md.scriban）、deployModes（如 standalone/cloud）、hasContract（契约文档实际存在）、"
        + "warnings（非阻断告警，如 MissingContract）、presets[]（name/description/fileCount，可直接用于 preview_generation/generate 的 preset 参数）。\n"
        + "常见错误：InternalError（包枚举底层异常，罕见）。")]
    public async Task<ListTemplatePackagesResult> ListTemplatePackagesAsync(CancellationToken cancellationToken)
    {
        try
        {
            IReadOnlyList<TemplatePackageInfo> packages = await _packageService.ListPackagesAsync(cancellationToken);

            // 投影为摘要 DTO：不透出 RootPath/ManifestPath 等本地磁盘细节，能力元数据与预设供调用方判断包用途
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
                    warnings,
                    TemplatePresetResolver.GetSummaries(package)));
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
    /// compact=true 时切换为紧凑单载荷回执：条目仅含 path/enabled 元数据、presets 折叠为计数，供大包在宿主单结果预算内完整送达。
    /// </summary>
    /// <param name="package">模板包包名。</param>
    /// <param name="group">可选分组前缀，匹配模板相对路径的子目录前缀，如 "controller"、子目录多级用正斜杠分隔。</param>
    /// <param name="type">可选语言段过滤，取值 java / csharp / xml / sql / json / plain，对应模板文件名语言段约定。</param>
    /// <param name="compact">可选紧凑模式；false（缺省）=完整回执行为不变，true=仅元数据最小集。</param>
    /// <param name="cancellationToken">取消标记。</param>
    /// <returns>compact=false 返回与既有 typed 返回逐字一致的完整回执（ListTemplatesResult），
    /// compact=true 返回紧凑单载荷回执（ListTemplatesCompactResult）；均为结构化内容，结构由两个应答 DTO 分别声明。</returns>
    [McpServerTool(Name = "list_templates", Title = "列出模板文件", ReadOnly = true, Idempotent = true, Destructive = false, UseStructuredContent = true)]
    [Description("用途：列出指定模板包内的模板条目与命名预设，确定生成时的 files 候选或维护目标。\n"
        + "何时用：生成前核对模板路径/作用域/输出路径；模板维护前定位文件；确认包提供哪些 preset；大包完整回执超宿主结果预算被截断时改用 compact=true。\n"
        + "参数：package(string,必填)=模板包包名；group(string,可选,默认 null)=分组前缀过滤（相对路径子目录前缀，目录边界对齐，大小写不敏感）；"
        + "type(string,可选,默认 null)=语言段过滤，取值 java/csharp(cs)/xml/sql/json/plain，非法取值拒绝并列出合法值；"
        + "compact(boolean,可选,默认 false)=紧凑模式：false=完整回执行为不变；true=条目仅含元数据 {path, enabled}，包级仅含 {package, isBuiltin, templateCount, presetCount}，"
        + "不含 output/language/scope/root/role/isContract/warnings/presets 细节；大包建议 compact=true（否则回执可能被宿主预算截断）。\n"
        + "返回（compact=false）：package（包名）、isBuiltin（是否内置）、basePackage（基础包名，未配置为空串）、templates[]（path 相对路径/output 输出路径模板/enabled 是否默认参与生成/"
        + "language/scope=table|module|project/root=legacy|output|workspace|web/role 产出角色/isContract 是否契约文档条目）、"
        + "warnings[]（MissingContract 等非阻断告警）、presets[]（name/description/fileCount）。\n"
        + "返回（compact=true，单载荷形态：structuredContent 唯一载荷、text 为摘要说明）：package、isBuiltin、templateCount（过滤后命中条目数）、templates[]（path/enabled）、"
        + "presetCount（命名预设数量）；group/type 过滤照常生效。\n"
        + "文档型条目：包根存在 contractPath 且未在清单声明时附带一条（enabled=false、output 空串、scope=project、isContract=true），不计入 fileCount、不参与生成；compact=true 时同样返回（仅 path/enabled）。\n"
        + "常见错误：TemplatePackageError（包不存在）；InvalidArgument（type 取值非法）。")]
    public async Task<CallToolResult> ListTemplatesAsync(
        [Description("模板包包名（必填）。")]
        string package,
        [Description("分组前缀过滤（可选，默认 null）：匹配模板相对路径的子目录前缀，目录边界对齐（controller 不会误命中 controllerx/），多级用正斜杠分隔，大小写不敏感。")]
        string? group = null,
        [Description("语言段过滤（可选，默认 null）：取值 java / csharp（或 cs）/ xml / sql / json / plain，大小写不敏感；非法取值被拒绝并列出合法值。")]
        string? type = null,
        [Description("紧凑模式（可选，默认 false）：false=完整回执行为不变；true=条目仅含 path/enabled、presets 折叠为 presetCount，大包建议 true 以免回执被宿主结果预算截断。")]
        bool compact = false,
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

            // 紧凑投影：仅保留定位 files 候选所需的最小元数据（path/enabled），presets 折叠为计数，
            // 供大包回执在宿主单结果预算内完整送达；group/type 过滤口径与完整模式完全一致
            if (compact)
            {
                var compactEntries = new List<TemplateEntryCompact>(entries.Count);
                foreach (TemplateEntry entry in entries)
                {
                    compactEntries.Add(new TemplateEntryCompact(entry.Path, entry.Enabled));
                }

                _logger.LogInformation(
                    "list_templates 完成（compact），包 {PackageName}，返回 {EntryCount} 条（清单声明文件 {TotalCount} 个）。",
                    package, compactEntries.Count, packageInfo.Files.Count);
                var compactResult = new ListTemplatesCompactResult(
                    packageInfo.Name,
                    packageInfo.IsBuiltin,
                    compactEntries.Count,
                    compactEntries,
                    TemplatePresetResolver.GetSummaries(packageInfo).Count);
                return SinglePayloadToolResult.Create(
                    compactResult,
                    "list_templates 紧凑回执承载于 structuredContent（本应答不重复输出载荷）；"
                    + $"package={compactResult.Package}，templateCount={compactResult.TemplateCount}，presetCount={compactResult.PresetCount}，"
                    + "完整条目细节请以 compact=false 或 read_template 查看。");
            }

            _logger.LogInformation(
                "list_templates 完成，包 {PackageName}，返回 {EntryCount} 条（清单声明文件 {TotalCount} 个）。",
                package, entries.Count, packageInfo.Files.Count);

            // 缺省回执形态保持：显式 CallToolResult 逐字复刻 SDK typed-object 返回的缺省应答
            // （text = DTO 全量 JSON + structuredContent = DTO 同口径序列化），保证 compact 缺省时回执逐字段不变
            return SinglePayloadToolResult.CreateDefaultShaped(new ListTemplatesResult(
                packageInfo.Name,
                packageInfo.IsBuiltin,
                packageInfo.BasePackage ?? string.Empty,
                entries,
                warnings,
                TemplatePresetResolver.GetSummaries(packageInfo)));
        }
        catch (Exception exception)
        {
            // 强类型查询工具的错误走协议 isError 通道，避免错误负载混入结构化成功应答破坏输出契约
            _logger.LogError(exception, "list_templates 执行失败，包名 {PackageName}。", package);
            throw ToolError.AsMcpException(exception, cancellationToken);
        }
    }

    /// <summary>
    /// 读取指定模板的正文（Scriban 源文本），支持大模板分片读取；仅允许读取包清单声明的模板文件，路径经防目录穿越校验；
    /// 包契约文档（manifest.contractPath，缺省 README.md.scriban）为窄例外（可未声明），按文档型条目返回、不参与生成。
    /// 应答为单载荷形态：正文只承载于 structuredContent，text 块仅为摘要说明，不重复输出正文。
    /// </summary>
    /// <param name="package">模板包包名。</param>
    /// <param name="path">模板文件相对包根路径（与 list_templates 返回的 path 一致，可含分组目录）。</param>
    /// <param name="offset">分片续读起点（0 基字符偏移）；传值含 0 即进入分片模式，不传=整读。</param>
    /// <param name="maxChars">单片/截停字符上限；分片模式为本片上限（缺省 100000），整读模式为截停阈值；护栏 [1000, 100000]。</param>
    /// <param name="cancellationToken">取消标记。</param>
    /// <returns>单载荷工具应答：structuredContent 承载正文全文/切片与条目元数据，text 块为摘要说明。</returns>
    [McpServerTool(Name = "read_template", Title = "读取模板", ReadOnly = true, Idempotent = true, Destructive = false,
        UseStructuredContent = true, OutputSchemaType = typeof(ReadTemplateResult))]
    [Description("用途：读取指定模板文件的正文（Scriban 源文本）与内容摘要，并透出包命名预设；大模板支持分片读取，正文被宿主结果预算截断时按 nextOffset 分片续读取全。\n"
        + "何时用：改模板前取当前内容与 contentHash；评估模板风格；读取包契约文档（manifest.contractPath）确认传参键与 preset 用法；大文件读取尾段。\n"
        + "参数：package(string,必填)=模板包包名；path(string,必填)=模板相对包根路径（取 list_templates 的 path；反斜杠规范化为正斜杠，大小写不敏感）；"
        + "offset(int,可选,默认 null)=分片续读起点（0 基字符偏移），传值含 0 即进入分片模式，不传=整读行为不变，负值或超出 charCount 拒绝为 InvalidArgument；"
        + "maxChars(int,可选,默认 null)=单片字符上限，护栏 [1000,100000] 越界拒绝为 InvalidArgument：分片模式=本片长度上限（缺省 100000），"
        + "整读模式=截停阈值（正文超限时 content 截停至该长度并置 truncated=true、nextOffset=该值），不传且整读=全文不截停。\n"
        + "返回：package（包名）、path（规范化相对路径）、output（输出路径模板）、enabled（是否默认参与生成）、language（目标语言）、"
        + "content（正文全文或当前分片，UTF-8 去 BOM 解码）、contentHash（全文 SHA256 前 16 位 hex 小写，分片模式仍为全文摘要，供 save_template_edit 条件写）、"
        + "charCount（正文总字符数）、nextOffset（下一分片 0 基字符偏移，null=已读全）、truncated（content 是否为截停片段：整读截停=true；分片模式恒 false，剩余由 nextOffset 表达）、"
        + "presets[]（name/description/fileCount）。\n"
        + "载荷口径：正文只承载于 structuredContent.content 一处，text 块仅为摘要说明（不重复输出正文）；续读循环：传 offset=上一回执 nextOffset 直至 nextOffset=null。\n"
        + "契约窄例外：manifest.contractPath（缺省 README.md.scriban）即使未在清单声明也可读取（文档型条目：enabled=false、output 空串、不参与生成）；其它未声明路径一律拒绝。\n"
        + "常见错误（经协议 isError=true 返回，消息带 [错误分类] 前缀）：TemplatePackageError（包不存在；path 未在清单声明且非契约路径；契约文件不存在）；"
        + "InvalidArgument（offset 负值或超界；maxChars 超出护栏区间）。")]
    public async Task<CallToolResult> ReadTemplateAsync(
        [Description("模板包包名（必填）。")]
        string package,
        [Description("模板相对包根路径（必填）：取 list_templates 返回的 path，可含分组目录；反斜杠会规范化为正斜杠比对。")]
        string path,
        [Description("分片续读起点（可选，0 基字符偏移）：传值含 0 即进入分片模式，返回该偏移起的切片；不传=整读行为不变；负值或超出 charCount 拒绝为 InvalidArgument。")]
        int? offset = null,
        [Description("单片字符上限（可选）：护栏 [1000,100000]（两种模式同护栏，越界拒绝）。分片模式=本片长度上限，缺省 100000；"
            + "整读模式=截停阈值，正文超限时 content 截停至该长度并置 truncated=true、nextOffset=该值；不传且整读=全文不截停。")]
        int? maxChars = null,
        CancellationToken cancellationToken = default)
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
            string fullContent = await _templateFileWriter.ReadAsync(packageInfo, relativePath, cancellationToken);
            HighlightLanguage language = HighlightLanguageResolver.FromTemplateFileName(
                Path.GetFileName(relativePath));

            // contentHash 恒为全文摘要：分片模式下仍是全文哈希，保证 save_template_edit 条件写在任意分片轮次可校验
            string contentHash = TemplateContentHash.Compute(fullContent);

            // 分片入参护栏：非法值结构化拒绝（InvalidArgument），禁止静默收口掩盖调用方错误
            ValidateSliceArguments(offset, maxChars, fullContent.Length);

            // 切片窗口：整读（offset 未传）自 0 起、缺省不设上限；分片（offset 传值含 0）自 offset 起、缺省单片上限 MaxSliceChars
            bool sharded = offset is not null;
            int start = offset ?? 0;
            int cap = sharded ? maxChars ?? MaxSliceChars : maxChars ?? int.MaxValue;
            int sliceLength = Math.Min(cap, fullContent.Length - start);
            int? nextOffset = start + sliceLength < fullContent.Length ? start + sliceLength : null;

            // 整读全文路径复用原字符串避免大正文复制；分片路径按窗口切片
            string content = start == 0 && sliceLength == fullContent.Length
                ? fullContent
                : fullContent.Substring(start, sliceLength);

            // truncated 口径：整读截停=true；分片模式切片恒完整、剩余由 nextOffset 表达，恒 false（与 doc-base 分片回执口径一致）
            bool truncated = !sharded && nextOffset is not null;

            ReadTemplateResult result = new(
                packageInfo.Name,
                relativePath,
                entry?.OutputPath ?? string.Empty,
                entry?.IsEnabled ?? false,
                language.ToString(),
                content,
                contentHash,
                TemplatePresetResolver.GetSummaries(packageInfo),
                fullContent.Length,
                nextOffset,
                truncated);

            _logger.LogInformation(
                "read_template 完成，包 {PackageName}，路径 {TemplatePath}，全文 {CharCount} 字符，模式 {SliceMode}（start={Start}，本片 {SliceLength}，nextOffset={NextOffset}）。",
                package, relativePath, fullContent.Length, sharded ? "sharded" : "whole", start, sliceLength, nextOffset);

            // 单载荷应答组装：正文只承载于 structuredContent 一处——SDK 缺省会对对象返回同时输出序列化 JSON 文本与
            // structuredContent 双份等价载荷，是大模板被宿主结果预算截断的根因；text 块降级为摘要说明。
            // 载荷组装与序列化口径由共享组装器承载，read_template 仅负责本工具的摘要说明文本
            string continuation = result.NextOffset is null
                ? "nextOffset=null（已读全）"
                : $"nextOffset={result.NextOffset}（续读传 offset={result.NextOffset}）";
            string summary = "read_template 正文承载于 structuredContent.content（本应答不重复输出正文）；"
                + $"package={result.Package}，path={result.Path}，charCount={result.CharCount}，"
                + $"truncated={result.Truncated.ToString().ToLowerInvariant()}，{continuation}，contentHash={result.ContentHash}。";
            return SinglePayloadToolResult.Create(result, summary);
        }
        catch (Exception exception)
        {
            // 声明 outputSchema 的工具错误必须走协议 isError 通道，避免错误负载混入结构化成功应答
            _logger.LogError(exception, "read_template 执行失败，包名 {PackageName}，路径 {TemplatePath}。", package, path);
            throw ToolError.AsMcpException(exception, cancellationToken);
        }
    }

    /// <summary>
    /// 校验分片入参护栏：offset 必须为非负且不超正文总长的 0 基字符偏移；maxChars 必须落在 [1000, 100000] 区间。
    /// 非法值结构化拒绝（经异常映射为 InvalidArgument），禁止静默收口掩盖调用方错误。
    /// </summary>
    /// <param name="offset">分片续读起点。</param>
    /// <param name="maxChars">单片/截停字符上限。</param>
    /// <param name="contentLength">正文总字符数。</param>
    private static void ValidateSliceArguments(int? offset, int? maxChars, int contentLength)
    {
        if (offset is < 0)
        {
            throw new ArgumentException($"offset 必须为非负 0 基字符偏移，收到 {offset}。");
        }

        if (offset > contentLength)
        {
            throw new ArgumentException($"offset 超出正文范围：offset={offset}，正文总字符数={contentLength}，请以回执 charCount 为界续读。");
        }

        if (maxChars is < MinSliceChars or > MaxSliceChars)
        {
            throw new ArgumentException($"maxChars 超出护栏区间 [{MinSliceChars}, {MaxSliceChars}]，收到 {maxChars}。");
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
/// <param name="Presets">命名预设摘要（name/description/fileCount），包未声明 presets 时为空列表。</param>
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
    IReadOnlyList<string> Warnings,
    IReadOnlyList<TemplatePresetSummary> Presets);

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
/// <param name="Presets">命名预设摘要（name/description/fileCount），包未声明 presets 时为空列表。</param>
public sealed record ListTemplatesResult(
    string Package,
    bool IsBuiltin,
    string BasePackage,
    IReadOnlyList<TemplateEntry> Templates,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<TemplatePresetSummary> Presets);

/// <summary>
/// list_templates 紧凑模式模板条目 DTO：仅保留定位 files 候选所需的最小元数据。
/// </summary>
/// <param name="Path">模板相对包根路径（可含分组目录）。</param>
/// <param name="Enabled">是否默认参与生成。</param>
public sealed record TemplateEntryCompact(
    string Path,
    bool Enabled);

/// <summary>
/// list_templates 紧凑模式应答 DTO：条目与预设折叠为最小元数据，供大包回执在宿主单结果预算内完整送达。
/// </summary>
/// <param name="Package">包名。</param>
/// <param name="IsBuiltin">是否内置包。</param>
/// <param name="TemplateCount">过滤后命中的模板条目数。</param>
/// <param name="Templates">紧凑模板条目列表（path/enabled）。</param>
/// <param name="PresetCount">命名预设数量；预设细节经完整模式或 read_template 查看。</param>
public sealed record ListTemplatesCompactResult(
    string Package,
    bool IsBuiltin,
    int TemplateCount,
    IReadOnlyList<TemplateEntryCompact> Templates,
    int PresetCount);

/// <summary>
/// read_template 应答 DTO（structuredContent 唯一载荷）。
/// </summary>
/// <param name="Package">包名。</param>
/// <param name="Path">模板相对包根路径。</param>
/// <param name="Output">输出相对路径模板。</param>
/// <param name="Enabled">是否默认参与生成。</param>
/// <param name="Language">目标语言。</param>
/// <param name="Content">模板正文全文（整读缺省）或当前分片切片。</param>
/// <param name="ContentHash">正文全文内容摘要（SHA256 前 16 位 hex 小写），供 save_template_edit 条件写作期望值；分片模式下仍为全文摘要。</param>
/// <param name="Presets">命名预设摘要（name/description/fileCount），包未声明 presets 时为空列表。</param>
/// <param name="CharCount">正文总字符数（整读与分片模式同值，作 offset 续读上界）。</param>
/// <param name="NextOffset">下一分片 0 基字符偏移；null=已读全；整读截停时为截停点。</param>
/// <param name="Truncated">content 是否为截停片段：整读截停=true；分片模式恒 false（切片恒完整，剩余由 nextOffset 表达）。</param>
internal sealed record ReadTemplateResult(
    string Package,
    string Path,
    string Output,
    bool Enabled,
    string Language,
    string Content,
    string ContentHash,
    IReadOnlyList<TemplatePresetSummary> Presets,
    int CharCount,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    int? NextOffset,
    bool Truncated);

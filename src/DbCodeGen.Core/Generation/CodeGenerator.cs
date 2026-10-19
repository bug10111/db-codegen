using System.Text;
using System.Text.RegularExpressions;
using DbCodeGen.Core.Config;
using DbCodeGen.Core.DataSource;
using DbCodeGen.Core.Model;
using DbCodeGen.Core.Templates;
using DbCodeGen.Core.Templates.Packages;
using Microsoft.Extensions.Logging;

namespace DbCodeGen.Core.Generation;

/// <summary>
/// 批量代码生成服务实现：表与模板文件笛卡尔积渲染，dry-run 分类，按同名文件策略覆盖/跳过并经文件写盘服务落盘。
/// 安全线：写盘前必走 dry-run 重算，同名文件处理策略由请求指定（无确认弹窗），渲染后相对路径做防目录穿越校验。
/// </summary>
public sealed class CodeGenerator : ICodeGenerator
{
    private readonly TemplateEngine _engine;
    private readonly TemplateFileWriter _templateFileWriter;
    private readonly IFileWriter _fileWriter;
    private readonly IConfigService _configService;
    private readonly ITypeMappingService? _typeMappingService;
    private readonly TableCatalogService? _tableCatalogService;
    private readonly ILogger<CodeGenerator> _logger;

    /// <summary>
    /// 使用共享渲染管线、模板文件读写、文件写盘服务、配置服务与可选类型映射服务创建批量生成服务。
    /// 类型映射服务为空时不做未映射预检，UnmappedTypes 返回空列表。
    /// </summary>
    /// <param name="engine">共享模板引擎，承载内容渲染与输出路径占位渲染。</param>
    /// <param name="templateFileWriter">模板文件读写服务，渲染时从包目录直读磁盘模板内容。</param>
    /// <param name="fileWriter">文件写盘服务，负责建目录与 UTF-8 无 BOM 异步写盘。</param>
    /// <param name="configService">配置服务，生成完成后回写最近相对输出根。</param>
    /// <param name="logger">生成服务日志器，日志不得输出模板正文或敏感信息。</param>
    /// <param name="typeMappingService">全局类型映射服务，用于生成前未映射类型预检，可为空。</param>
    /// <param name="tableCatalogService">表元数据编排服务，用于生成前补全表列元数据，可为空。</param>
    /// <exception cref="ArgumentNullException">engine、templateFileWriter、fileWriter、configService 或 logger 为 null 时抛出。</exception>
    public CodeGenerator(
        TemplateEngine engine,
        TemplateFileWriter templateFileWriter,
        IFileWriter fileWriter,
        IConfigService configService,
        ILogger<CodeGenerator> logger,
        ITypeMappingService? typeMappingService = null,
        TableCatalogService? tableCatalogService = null)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(templateFileWriter);
        ArgumentNullException.ThrowIfNull(fileWriter);
        ArgumentNullException.ThrowIfNull(configService);
        ArgumentNullException.ThrowIfNull(logger);
        _engine = engine;
        _templateFileWriter = templateFileWriter;
        _fileWriter = fileWriter;
        _configService = configService;
        _typeMappingService = typeMappingService;
        _tableCatalogService = tableCatalogService;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<GenerationPreview> BuildPreviewAsync(
        GenerationRequest request,
        IProgress<GenerationProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        return await BuildPreviewCoreAsync(request, progress, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<GenerationResult> GenerateAsync(
        GenerationRequest request,
        IProgress<GenerationProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        try
        {
            // 取消、渲染与写盘任一环节取消都统一转换为取消结果返回
            return await GenerateCoreAsync(request, progress, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 渲染或写盘任一环节取消，返回带取消日志的结果供底栏展示
            return new GenerationResult(0, 0, 0, 0, isCancelled: true, BuildCancelLogs("批量生成已取消。"));
        }
    }

    /// <summary>
    /// 生成写盘核心流程：内部重算 dry-run、逐文件写盘、统计与日志合并、回写最近生成路径。
    /// 同名文件覆盖/跳过按请求策略在 dry-run 分类阶段决定，写盘阶段无确认弹窗。
    /// </summary>
    /// <param name="request">生成请求，含同名文件处理策略。</param>
    /// <param name="progress">进度推送。</param>
    /// <param name="cancellationToken">取消标记。</param>
    /// <returns>写盘结果统计与生成日志。</returns>
    private async Task<GenerationResult> GenerateCoreAsync(
        GenerationRequest request,
        IProgress<GenerationProgress>? progress,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // 内部先重算 dry-run（同一渲染管线），保证安全线不因调用方绕过预览而失效
        GenerationPreview preview = await BuildPreviewCoreAsync(request, progress, cancellationToken).ConfigureAwait(false);

        // 待写条目为新增与覆盖（分类已按请求同名文件策略），跳过条目仅计数不进入写盘
        List<GenerationFileEntry> writeEntries = preview.Entries
            .Where(entry => entry.Action != GenerationAction.Skip)
            .ToList();

        // 逐文件写盘，单文件失败由写盘服务独立兜底后继续其余文件（部分失败）
        GenerationResult writeResult = await _fileWriter.WriteFilesAsync(writeEntries, progress, cancellationToken).ConfigureAwait(false);

        // 跳过日志与输出路径告警日志、平台表排除日志、覆盖告警日志、写盘日志合并为最终日志，
        // 跳过计数并入最终统计；坏路径与排除命中不阻断写盘，但必须在生成日志中可见
        List<GenerationLogEntry> mergedLogs = BuildSkipLogs(preview, request.DuplicateFileStrategy);
        mergedLogs.AddRange(BuildPlatformExclusionLogs(preview.PlatformTableExcluded));
        mergedLogs.AddRange(BuildPathWarningLogs(preview.PathWarnings));
        mergedLogs.AddRange(BuildCoverageWarningLogs(preview.CoverageWarnings));
        mergedLogs.AddRange(writeResult.Logs);

        // 记忆回写受请求开关控制：默认开保持桌面端"生成后记忆下次默认值"行为，MCP 等无记忆态调用方关闭
        if (!writeResult.IsCancelled && request.PersistLastUsedPaths)
        {
            WriteBackOutputRoot(request.WorkspaceRoot, request.CodeDirectory ?? request.RelativeOutputRoot, mergedLogs);
        }

        return new GenerationResult(
            writeResult.Generated,
            writeResult.Overwritten,
            preview.SkipCount,
            writeResult.Failed,
            writeResult.IsCancelled,
            mergedLogs,
            preview.NamingReport,
            preview.PathWarnings,
            preview.CoverageWarnings,
            preview.PlatformTableExcluded);
    }

    /// <summary>
    /// 执行 dry-run 渲染与分类核心管线：按模板作用域三路调度——
    /// table 作用域按（表 × 勾选模板）笛卡尔积（现状管线）；module 作用域按模块清单逐个渲染；
    /// project 作用域整次渲染一次。三种作用域同次调用可混跑，逐模板直读、内容渲染、
    /// 输出路径占位渲染、绝对路径解析与防目录穿越、按目标文件状态分类。
    /// </summary>
    /// <param name="request">生成请求。</param>
    /// <param name="progress">进度推送。</param>
    /// <param name="cancellationToken">取消标记。</param>
    /// <returns>dry-run 清单与分类计数。</returns>
    /// <exception cref="OperationCanceledException">渲染或分类过程被取消时抛出。</exception>
    /// <exception cref="GenerationException">勾选 module 作用域模板但未提供模块清单、模块名重复、模板渲染失败、输出路径模板渲染失败或路径越界时抛出。</exception>
    private async Task<GenerationPreview> BuildPreviewCoreAsync(
        GenerationRequest request,
        IProgress<GenerationProgress>? progress,
        CancellationToken cancellationToken)
    {
        // 过滤未勾选条目与空条目，保证只处理勾选模板文件与有效表
        List<TemplateFileSelection> selectedFiles = request.SelectedFiles
            .Where(file => file is not null && file.IsSelected)
            .ToList();
        List<TableInfo> tables = request.Tables
            .Where(table => table is not null)
            .ToList();

        // 平台表/排除表过滤先于一切渲染：命中平台表默认不生成（includePlatformTables=true 才放行），
        // 命中排除表一律不生成；命中记录随 dry-run 回执透出，保证「不写」是可见的显式行为
        (List<TableInfo> includedTables, List<PlatformTableExclusion> platformExclusions) = ApplyTableFilters(request, tables);
        tables = includedTables;

        var pathWarnings = new List<PathWarningInfo>();

        // 工作区根疑似 Maven 聚合根、权限模块派生源不一致属整次生成级预检，无论是否渲染都先收集
        await CollectWorkspaceRootWarningAsync(pathWarnings, request, cancellationToken).ConfigureAwait(false);
        CollectPermissionModuleWarning(pathWarnings, request);

        if (selectedFiles.Count == 0)
        {
            return BuildPreviewResult(
                Array.Empty<GenerationFileEntry>(), Array.Empty<UnmappedTypeInfo>(), pathWarnings,
                Array.Empty<NamingReportItem>(), BuildCoverageWarnings(request, selectedFiles), platformExclusions);
        }

        // 按模板作用域分组，各组保持勾选清单内相对顺序；无 scope 声明的条目全部落在表作用域组（既有包行为不变）
        List<TemplateFileSelection> tableFiles = new();
        List<TemplateFileSelection> moduleFiles = new();
        List<TemplateFileSelection> projectFiles = new();
        foreach (TemplateFileSelection file in selectedFiles)
        {
            switch (file.Scope)
            {
                case TemplateScope.Module:
                    moduleFiles.Add(file);
                    break;
                case TemplateScope.Project:
                    projectFiles.Add(file);
                    break;
                default:
                    tableFiles.Add(file);
                    break;
            }
        }

        // 表作用域无表可渲染且无其它作用域工作时整体空转（与既有"表×模板=0 条目"行为一致），
        // 但平台表排除、覆盖告警与工作区根告警等诊断仍随回执返回
        if ((tableFiles.Count == 0 || tables.Count == 0) && moduleFiles.Count == 0 && projectFiles.Count == 0)
        {
            return BuildPreviewResult(
                Array.Empty<GenerationFileEntry>(), Array.Empty<UnmappedTypeInfo>(), pathWarnings,
                Array.Empty<NamingReportItem>(), BuildCoverageWarnings(request, selectedFiles), platformExclusions);
        }

        // module 作用域被勾选但未提供模块清单时结构化报错；模块名重复同样拒绝（同名模块无法区分产出归属）
        IReadOnlyList<ModuleContext> modules = Array.Empty<ModuleContext>();
        if (moduleFiles.Count > 0)
        {
            if (request.Modules is null || request.Modules.Count == 0)
            {
                throw new GenerationException(
                    "勾选了 module 作用域模板但未提供模块清单（modules）；请传入 modules（如 [{\"name\":\"account\"}]）后重试。");
            }

            HashSet<string> seenModuleNames = new(StringComparer.Ordinal);
            foreach (ModuleContext module in request.Modules)
            {
                if (!seenModuleNames.Add(module.Name))
                {
                    throw new GenerationException($"模块清单存在重复模块名：{module.Name}；请去重后重试。");
                }
            }

            modules = request.Modules;
        }

        // 生成前补全表列元数据：表清单阶段不含列，实体与 mapper.xml 模板按列循环渲染，
        // 必须经表详情读取补全列集合，否则实体只剩类壳、insert 语句列清单为空
        tables = await EnrichTableColumnsAsync(request, tables, cancellationToken).ConfigureAwait(false);

        // 命名策略在列补全后统一应用到全部表：实体名/变量名/类名按「请求变量 > manifest > 缺省」派生，
        // 物理表名 rawName 永不变形；表对象按策略克隆，避免污染调用方复用的表实例
        ResolvedNamingPolicy namingPolicy = ResolvedNamingPolicy.Resolve(request.Package.Naming, request.Variables);
        tables = tables.Select(table => TableNamingResolver.Apply(table, namingPolicy)).ToList();

        TemplatePackageContext packageContext = TemplatePackageContext.From(
            request.Package, request.BasePackageOverride, request.Variables, request.Modules);

        // 作用域渲染输入构造：variables 键的保留命名空间冲突在此结构化拒绝；
        // 仅有表作用域且未传 variables 时保持 null（既有渲染路径零改动）；
        // project 作用域注入全模块清单（modules 根变量），project 模板聚合段与 module 作用域目录名同源同值
        bool hasScopeWork = moduleFiles.Count > 0 || projectFiles.Count > 0 || request.Variables is not null;
        ScopeRenderInput? tableScopeInput = hasScopeWork ? ScopeRenderInput.Create(request.Variables, null, tables) : null;
        ScopeRenderInput? projectScopeInput = projectFiles.Count > 0
            ? ScopeRenderInput.Create(request.Variables, null, tables, request.Modules)
            : null;

        var entries = new List<GenerationFileEntry>();
        int total = (tableFiles.Count > 0 ? tables.Count * tableFiles.Count : 0)
            + (moduleFiles.Count > 0 ? modules.Count * moduleFiles.Count : 0)
            + projectFiles.Count;
        int completed = 0;

        // 表作用域：表 × 模板笛卡尔积（现状管线不变）
        foreach (TableInfo table in tables)
        {
            foreach (TemplateFileSelection file in tableFiles)
            {
                cancellationToken.ThrowIfCancellationRequested();
                completed++;

                progress?.Report(new GenerationProgress(GenerationStage.Rendering, completed, total, file.TemplateFileName));

                // 从模板包目录直读模板内容，拾取模板编辑已保存的版本
                string templateText = await ReadTemplateContentAsync(request, file, cancellationToken).ConfigureAwait(false);

                // 模板正文命名反推预检：物理表名必须用 rawName、权限模块必须单一派生源（非阻断）
                CollectTemplateTextWarnings(pathWarnings, file, templateText);

                // 内容渲染，与模板编辑预览共用同一渲染管线，失败定位到模板名与行列
                PreviewResult renderResult = RenderContent(templateText, table, packageContext, file, cancellationToken, tableScopeInput);

                // 空迭代告警归并（渲染前静态分析，非阻断）
                CollectIterationWarnings(pathWarnings, file, renderResult);

                // 输出路径模板占位渲染，失败定位到具体模板文件
                string renderedRelativePath = RenderOutputPath(table, packageContext, file);

                // 输出路径坏路径预检（空包段/连续分隔符/非法包段/重复包段/非小写包段），归并进 dry-run 告警清单
                CollectPathWarnings(pathWarnings, file, packageContext, request, renderedRelativePath);

                // 解析绝对路径并校验防目录穿越：按条目的输出根语义（output/workspace/web）解析，越界即整单失败
                string absolutePath = ResolveAbsolutePath(request, renderedRelativePath, ResolveEffectiveOutputRoot(file), AllowsParentTraversal(file));

                progress?.Report(new GenerationProgress(GenerationStage.Previewing, completed, total, renderedRelativePath));

                // dry-run 分类：目标不存在为新增，其余按请求同名文件策略覆盖/跳过
                GenerationAction action = await ClassifyTargetAsync(absolutePath, renderResult.Output, request.DuplicateFileStrategy, cancellationToken).ConfigureAwait(false);
                entries.Add(new GenerationFileEntry(table.RawName, renderedRelativePath, absolutePath, action, renderResult.Output, null));
            }
        }

        // 模块作用域：模块清单 × 模块模板，渲染上下文含 module.* / vars.* / tables 汇总
        foreach (ModuleContext module in modules)
        {
            foreach (TemplateFileSelection file in moduleFiles)
            {
                cancellationToken.ThrowIfCancellationRequested();
                completed++;

                progress?.Report(new GenerationProgress(GenerationStage.Rendering, completed, total, file.TemplateFileName));

                ScopeRenderInput scopeInput = ScopeRenderInput.Create(request.Variables, module, tables);
                string templateText = await ReadTemplateContentAsync(request, file, cancellationToken).ConfigureAwait(false);

                // 模板正文命名反推预检（模块聚合模板同样不得由实体名反推物理表名）
                CollectTemplateTextWarnings(pathWarnings, file, templateText);

                // 内容渲染与表作用域同一渲染管线（共享函数文件、空行规整、结构化错误一致），仅上下文不同
                PreviewResult renderResult = RenderScopedContent(templateText, packageContext, scopeInput, file, cancellationToken);

                // 空迭代告警归并（渲染前静态分析，非阻断）
                CollectIterationWarnings(pathWarnings, file, renderResult);

                // 输出路径模板占位渲染，路径可引用 module.* / vars.*
                string renderedRelativePath = RenderScopedOutputPath(packageContext, scopeInput, file);

                // 输出路径坏路径预检，归并进 dry-run 告警清单，不阻断生成
                CollectPathWarnings(pathWarnings, file, packageContext, request, renderedRelativePath);

                string absolutePath = ResolveAbsolutePath(request, renderedRelativePath, ResolveEffectiveOutputRoot(file), AllowsParentTraversal(file));

                progress?.Report(new GenerationProgress(GenerationStage.Previewing, completed, total, renderedRelativePath));

                GenerationAction action = await ClassifyTargetAsync(absolutePath, renderResult.Output, request.DuplicateFileStrategy, cancellationToken).ConfigureAwait(false);
                entries.Add(new GenerationFileEntry(module.Name, renderedRelativePath, absolutePath, action, renderResult.Output, null));
            }
        }

        // 工程作用域：整次渲染一次，渲染上下文含 vars.* / tables 汇总 / modules 全模块清单（无单模块 module）
        foreach (TemplateFileSelection file in projectFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            completed++;

            progress?.Report(new GenerationProgress(GenerationStage.Rendering, completed, total, file.TemplateFileName));

            string templateText = await ReadTemplateContentAsync(request, file, cancellationToken).ConfigureAwait(false);

            // 模板正文命名反推预检（工程级契约/权限点模板同样纳入）
            CollectTemplateTextWarnings(pathWarnings, file, templateText);

            PreviewResult renderResult = RenderScopedContent(templateText, packageContext, projectScopeInput!, file, cancellationToken);

            // 空迭代告警归并（渲染前静态分析，非阻断）
            CollectIterationWarnings(pathWarnings, file, renderResult);

            string renderedRelativePath = RenderScopedOutputPath(packageContext, projectScopeInput!, file);

            // 输出路径坏路径预检，归并进 dry-run 告警清单，不阻断生成
            CollectPathWarnings(pathWarnings, file, packageContext, request, renderedRelativePath);

            string absolutePath = ResolveAbsolutePath(request, renderedRelativePath, ResolveEffectiveOutputRoot(file), AllowsParentTraversal(file));

            progress?.Report(new GenerationProgress(GenerationStage.Previewing, completed, total, renderedRelativePath));

            GenerationAction action = await ClassifyTargetAsync(absolutePath, renderResult.Output, request.DuplicateFileStrategy, cancellationToken).ConfigureAwait(false);
            entries.Add(new GenerationFileEntry(string.Empty, renderedRelativePath, absolutePath, action, renderResult.Output, null));
        }

        // 生成前未映射类型预检：全部表列解析链未命中的类型归并清单，供界面弹窗提示补映射
        IReadOnlyList<UnmappedTypeInfo> unmappedTypes = _typeMappingService is null
            ? Array.Empty<UnmappedTypeInfo>()
            : _typeMappingService.FindUnmappedTypes(tables, request.Package.TypeMap);

        // 逐表命名报告与覆盖告警随回执透出，供调用方在写盘前核对命名契约与生成面覆盖
        IReadOnlyList<NamingReportItem> namingReport = BuildNamingReport(tables, packageContext);
        IReadOnlyList<CoverageWarningInfo> coverageWarnings = BuildCoverageWarnings(request, selectedFiles);
        return BuildPreviewResult(entries, unmappedTypes, pathWarnings, namingReport, coverageWarnings, platformExclusions);
    }

    /// <summary>
    /// 组装 dry-run 结果：统一计算新增/覆盖/跳过计数并携带命名报告、覆盖告警与平台表排除清单。
    /// </summary>
    /// <param name="entries">待写条目。</param>
    /// <param name="unmappedTypes">未映射类型清单。</param>
    /// <param name="pathWarnings">路径与命名预检告警清单。</param>
    /// <param name="namingReport">逐表命名报告。</param>
    /// <param name="coverageWarnings">生成覆盖告警。</param>
    /// <param name="platformExclusions">平台表/排除表命中记录。</param>
    /// <returns>dry-run 结果。</returns>
    private static GenerationPreview BuildPreviewResult(
        IReadOnlyList<GenerationFileEntry> entries,
        IReadOnlyList<UnmappedTypeInfo> unmappedTypes,
        IReadOnlyList<PathWarningInfo> pathWarnings,
        IReadOnlyList<NamingReportItem> namingReport,
        IReadOnlyList<CoverageWarningInfo> coverageWarnings,
        IReadOnlyList<PlatformTableExclusion> platformExclusions)
    {
        int newCount = entries.Count(entry => entry.Action == GenerationAction.New);
        int overwriteCount = entries.Count(entry => entry.Action == GenerationAction.Overwrite);
        int skipCount = entries.Count(entry => entry.Action == GenerationAction.Skip);
        return new GenerationPreview(
            entries, newCount, overwriteCount, skipCount, unmappedTypes, pathWarnings, namingReport, coverageWarnings, platformExclusions);
    }

    /// <summary>
    /// 对单模板渲染结果执行空迭代告警归并：渲染前静态分析发现的 for 空集合零迭代告警按
    /// （同模板同种类归并计数）口径并入 dry-run 告警清单，非阻断；守卫文案设计场景按文案指引忽略。
    /// </summary>
    /// <param name="warnings">本次生成累计的告警清单，命中时就地归并。</param>
    /// <param name="file">勾选模板文件，提供模板名与输出路径模板定位。</param>
    /// <param name="renderResult">渲染结果，携带渲染前空迭代分析告警。</param>
    private static void CollectIterationWarnings(
        List<PathWarningInfo> warnings,
        TemplateFileSelection file,
        PreviewResult renderResult)
    {
        foreach (string message in renderResult.Warnings)
        {
            AppendPathWarning(
                warnings,
                PathWarningInfo.EmptyIteration,
                file.TemplateFileName,
                file.OutputPathTemplate,
                message);
        }
    }

    /// <summary>
    /// 对单条渲染后输出路径执行坏路径预检并归并告警：检测一为输出路径模板引用包段占位（package.dir）
    /// 但本次包段解析为空（典型诱因：codeDirectory 传 "." 等无包段输入，基础包名退化为空）；
    /// 检测二为渲染后路径含连续路径分隔符（如 src/main/java//X.cs，路径归一化后文件将丢失包层级直接落在上级目录）；
    /// 检测三为路径 src/main/java 之后的 Java 包段不是合法标识符（连字符/非法字符/数字开头/Java 关键字，
    /// 会导致 package 声明编译失败）。
    /// 告警不阻断生成，仅归并进 dry-run 回执供调用方可见。
    /// </summary>
    /// <param name="warnings">本次生成累计的告警清单，命中时就地归并。</param>
    /// <param name="file">勾选模板文件，提供输出路径模板与模板名定位。</param>
    /// <param name="packageContext">package 侧渲染上下文，提供包段占位解析值。</param>
    /// <param name="request">生成请求，提供相对输出根与 web 目录，用于组装工作区相对路径做 Java 包段预检。</param>
    /// <param name="renderedRelativePath">渲染后的相对输出路径。</param>
    private static void CollectPathWarnings(
        List<PathWarningInfo> warnings,
        TemplateFileSelection file,
        TemplatePackageContext packageContext,
        GenerationRequest request,
        string renderedRelativePath)
    {
        // 包段空检测：输出路径模板引用 package.dir 占位但包段解析为空，占位渲染为空串必然产生坏路径
        if (file.OutputPathTemplate.Contains("package.dir", StringComparison.Ordinal)
            && string.IsNullOrEmpty(packageContext.Dir))
        {
            AppendPathWarning(
                warnings,
                PathWarningInfo.EmptyPackageDir,
                file.TemplateFileName,
                renderedRelativePath,
                "输出路径引用了包段占位（package.dir），但本次基础包名解析为空；请检查 codeDirectory / 基础包名是否有效（如传 \".\" 会退化为空包段）。");
        }

        // 连续分隔符检测：渲染后相对路径含连续路径分隔符（正反斜杠均检测）
        if (renderedRelativePath.Contains("//", StringComparison.Ordinal)
            || renderedRelativePath.Contains("\\\\", StringComparison.Ordinal))
        {
            AppendPathWarning(
                warnings,
                PathWarningInfo.ConsecutiveSeparators,
                file.TemplateFileName,
                renderedRelativePath,
                "输出路径含连续路径分隔符，通常由空包段或路径模板拼接错误导致，路径归一化后文件将丢失包层级直接落在上级目录；请核对模板 outputPath 与包段配置。");
        }

        // Java 包段合法性检测：路径含 src/main/java 连续三元段时，其后各段（排除文件名段）必须是合法 Java 标识符，
        // 非法段（连字符/非法字符/数字开头/Java 关键字）会导致 package 声明编译失败；
        // 渲染路径本身不含源根标记（table 作用域相对输出根）时补输出根前缀并折叠 .. 后再定位，
        // 含源根标记时沿用渲染原文（保留空段等原始坏路径信号）
        string javaProbePath = ContainsJavaSourceMarker(renderedRelativePath)
            ? renderedRelativePath
            : BuildWorkspaceRelativePath(request, file, renderedRelativePath);
        CollectJavaPackageWarnings(warnings, file, javaProbePath);

        // 重复包段与大小写检测：包段连续重复（common/common）与非全小写（commonFile）同样归入坏路径预检
        CollectPackageSegmentShapeWarnings(warnings, file, javaProbePath);
    }

    /// <summary>
    /// 判断路径是否已含 src/main/java 连续三元段（忽略大小写、正反斜杠归一）。
    /// </summary>
    /// <param name="path">相对路径。</param>
    /// <returns>含源根标记时为 true。</returns>
    private static bool ContainsJavaSourceMarker(string path)
    {
        string[] segments = path.Replace('\\', '/').Split('/');
        for (int index = 0; index + 2 < segments.Length; index++)
        {
            if (string.Equals(segments[index], "src", StringComparison.OrdinalIgnoreCase)
                && string.Equals(segments[index + 1], "main", StringComparison.OrdinalIgnoreCase)
                && string.Equals(segments[index + 2], "java", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 组装工作区相对路径：按条目生效输出根补前缀（output=相对输出根；web=web 目录；workspace=无前缀），
    /// 并折叠 . 与 .. 段，供 Java 包段类预检定位 src/main/java 之后的真实包段。
    /// </summary>
    /// <param name="request">生成请求，提供相对输出根与 web 目录。</param>
    /// <param name="file">勾选模板文件，提供作用域与输出根声明。</param>
    /// <param name="renderedRelativePath">渲染后的相对输出路径。</param>
    /// <returns>折叠后的工作区相对路径（正斜杠分隔）。</returns>
    private static string BuildWorkspaceRelativePath(
        GenerationRequest request,
        TemplateFileSelection file,
        string renderedRelativePath)
    {
        string prefix = ResolveEffectiveOutputRoot(file) switch
        {
            TemplateOutputRoot.Workspace => string.Empty,
            TemplateOutputRoot.Web => request.WebDirectory,
            _ => request.RelativeOutputRoot
        };

        string combined = string.IsNullOrWhiteSpace(prefix)
            ? renderedRelativePath
            : prefix.Trim('/') + "/" + renderedRelativePath;

        // 词法折叠 . 与 ..：output 根允许 .. 越出相对输出根（如 ../resources），折叠后才能正确判定是否位于 Java 源根下
        var segments = new List<string>();
        foreach (string segment in combined.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == ".")
            {
                continue;
            }

            if (segment == "..")
            {
                if (segments.Count > 0)
                {
                    segments.RemoveAt(segments.Count - 1);
                }

                continue;
            }

            segments.Add(segment);
        }

        return string.Join("/", segments);
    }

    /// <summary>
    /// 对渲染后输出路径执行包段形态预检：src/main/java 之后的包段出现连续重复段时产出
    /// DuplicatePackageSegment 告警；包段含大写字母时产出 NonLowercasePackageSegment 告警。
    /// 每个模板每类告警只归并一条，后续出现仅累计次数。
    /// </summary>
    /// <param name="warnings">本次生成累计的告警清单，命中时就地归并。</param>
    /// <param name="file">勾选模板文件，提供模板名定位。</param>
    /// <param name="renderedRelativePath">渲染后的相对输出路径。</param>
    private static void CollectPackageSegmentShapeWarnings(
        List<PathWarningInfo> warnings,
        TemplateFileSelection file,
        string renderedRelativePath)
    {
        IReadOnlyList<string> segments = ExtractJavaPackageSegments(renderedRelativePath);

        // 连续重复段检测：忽略空段（空包段由 EmptyPackageDir/ConsecutiveSeparators 承载），大小写不敏感比较
        string? previous = null;
        foreach (string segment in segments)
        {
            if (segment.Length == 0)
            {
                continue;
            }

            if (previous is not null && string.Equals(previous, segment, StringComparison.OrdinalIgnoreCase))
            {
                AppendPathWarning(
                    warnings,
                    PathWarningInfo.DuplicatePackageSegment,
                    file.TemplateFileName,
                    renderedRelativePath,
                    $"Java 包段出现连续重复段 \"{segment}/{segment}\"，通常由模板无条件下硬拼层段（如 common）与基础包结尾重复导致；请改用 tool.packagePath 组装包路径（自动去重），禁止硬拼层段。");
                break;
            }

            previous = segment;
        }

        // 非小写包段检测：Java 包名约定全小写，大写段通常来自直接用 table.variableName 做包段
        foreach (string segment in segments)
        {
            if (segment.Length == 0)
            {
                continue;
            }

            if (!string.Equals(segment, segment.ToLowerInvariant(), StringComparison.Ordinal))
            {
                AppendPathWarning(
                    warnings,
                    PathWarningInfo.NonLowercasePackageSegment,
                    file.TemplateFileName,
                    renderedRelativePath,
                    $"Java 包段 \"{segment}\" 含大写字母，Java 包名约定全小写；请用 tool.packagePath(...) 或命名策略 packageSegmentCase=lower 生成包段，禁止直接用 table.variableName 作包段。");
                break;
            }
        }
    }

    /// <summary>
    /// Java 关键字与字面量集合（50 个关键字 + true/false/null + 下划线），供包段合法性判定。
    /// </summary>
    private static readonly HashSet<string> JavaKeywords = new(StringComparer.Ordinal)
    {
        "abstract", "assert", "boolean", "break", "byte", "case", "catch", "char", "class", "const",
        "continue", "default", "do", "double", "else", "enum", "extends", "final", "finally", "float",
        "for", "goto", "if", "implements", "import", "instanceof", "int", "interface", "long", "native",
        "new", "package", "private", "protected", "public", "return", "short", "static", "strictfp", "super",
        "switch", "synchronized", "this", "throw", "throws", "transient", "try", "void", "volatile", "while",
        "true", "false", "null", "_"
    };

    /// <summary>
    /// 对渲染后输出路径执行 Java 包段合法性预检：定位 src/main/java 连续三元段（忽略大小写、正反斜杠归一化），
    /// 其后所有段（排除最后一个文件名段）视为 Java 包段并逐段校验；不含该三元段的非 Java 路径不校验、不误报。
    /// 每个模板命中首个非法包段时归并一条告警（同模板同种类后续出现仅累计次数），非阻断。
    /// </summary>
    /// <param name="warnings">本次生成累计的告警清单，命中时就地归并。</param>
    /// <param name="file">勾选模板文件，提供模板名定位。</param>
    /// <param name="renderedRelativePath">渲染后的相对输出路径。</param>
    private static void CollectJavaPackageWarnings(
        List<PathWarningInfo> warnings,
        TemplateFileSelection file,
        string renderedRelativePath)
    {
        foreach (string segment in ExtractJavaPackageSegments(renderedRelativePath))
        {
            string? reason = DescribeInvalidJavaIdentifier(segment);
            if (reason is null)
            {
                continue;
            }

            AppendPathWarning(
                warnings,
                PathWarningInfo.InvalidJavaPackageSegment,
                file.TemplateFileName,
                renderedRelativePath,
                $"Java 包路径段 \"{segment}\" {reason}，不是合法 Java 标识符；请修正：kebab 模块名在 Java 包段/模块目录/artifactId 处用 tool.javaIdent 归一化（如 user-center→usercenter）；数字开头或 Java 关键字需改用合法模块名。");
            return;
        }
    }

    /// <summary>
    /// 从渲染后相对路径提取 Java 包段：定位 src/main/java 连续三元段（忽略大小写、正反斜杠归一化），
    /// 其后各段排除最后一个文件名段即为包段；路径不含该三元段时返回空集合（非 Java 路径不校验）。
    /// </summary>
    /// <param name="renderedRelativePath">渲染后的相对输出路径。</param>
    /// <returns>Java 包段清单，非 Java 路径为空集合。</returns>
    private static IReadOnlyList<string> ExtractJavaPackageSegments(string renderedRelativePath)
    {
        string[] segments = renderedRelativePath.Replace('\\', '/').Split('/');
        for (int index = 0; index + 2 < segments.Length; index++)
        {
            bool isSourceRoot = string.Equals(segments[index], "src", StringComparison.OrdinalIgnoreCase)
                && string.Equals(segments[index + 1], "main", StringComparison.OrdinalIgnoreCase)
                && string.Equals(segments[index + 2], "java", StringComparison.OrdinalIgnoreCase);
            if (!isSourceRoot)
            {
                continue;
            }

            // 三元段之后至倒数第二个段为包段；末段是文件名，不参与标识符校验
            int packageStart = index + 3;
            int packageEnd = segments.Length - 1;
            if (packageEnd <= packageStart)
            {
                return Array.Empty<string>();
            }

            var packageSegments = new List<string>(packageEnd - packageStart);
            for (int position = packageStart; position < packageEnd; position++)
            {
                packageSegments.Add(segments[position]);
            }

            return packageSegments;
        }

        return Array.Empty<string>();
    }

    /// <summary>
    /// 判定单个路径段是否为合法 Java 标识符：非空、首字符为字母/下划线/美元符、其余为字母数字/下划线/美元符，
    /// 且不是 Java 关键字或字面量；非法时返回原因描述，合法返回 null。
    /// </summary>
    /// <param name="segment">待校验的路径段。</param>
    /// <returns>非法原因描述；合法时为 null。</returns>
    private static string? DescribeInvalidJavaIdentifier(string segment)
    {
        if (segment.Length == 0)
        {
            return "为空";
        }

        if (JavaKeywords.Contains(segment))
        {
            return "为 Java 关键字或字面量";
        }

        char first = segment[0];
        if (char.IsDigit(first))
        {
            return "以数字开头";
        }

        if (!char.IsLetter(first) && first != '_' && first != '$')
        {
            return "以非法字符开头（仅允许字母、下划线、美元符）";
        }

        for (int index = 1; index < segment.Length; index++)
        {
            char current = segment[index];
            if (!char.IsLetterOrDigit(current) && current != '_' && current != '$')
            {
                return current == '-' ? "含连字符" : "含非法字符（仅允许字母、数字、下划线、美元符）";
            }
        }

        return null;
    }

    /// <summary>
    /// 归并一条路径告警：同一模板同一告警种类只保留首条记录，后续出现仅累计次数（与未映射类型清单同款归并口径）。
    /// </summary>
    /// <param name="warnings">告警清单。</param>
    /// <param name="kind">告警种类标识。</param>
    /// <param name="templateName">模板文件相对路径。</param>
    /// <param name="samplePath">首次出现的渲染后路径样例。</param>
    /// <param name="message">人类可读告警说明。</param>
    private static void AppendPathWarning(
        List<PathWarningInfo> warnings,
        string kind,
        string templateName,
        string samplePath,
        string message)
    {
        PathWarningInfo? existing = warnings.FirstOrDefault(warning => warning.Kind == kind && warning.TemplateName == templateName);
        if (existing is not null)
        {
            existing.Occurrences++;
            return;
        }

        warnings.Add(new PathWarningInfo
        {
            Kind = kind,
            TemplateName = templateName,
            SamplePath = samplePath,
            Occurrences = 1,
            Message = message
        });
    }

    /// <summary>
    /// 生成输出路径预检告警的日志条目：坏路径不阻断写盘，但必须在生成日志中可见。
    /// </summary>
    /// <param name="warnings">dry-run 预检归并后的告警清单。</param>
    /// <returns>警告级日志列表。</returns>
    private static List<GenerationLogEntry> BuildPathWarningLogs(IReadOnlyList<PathWarningInfo> warnings)
    {
        var logs = new List<GenerationLogEntry>(warnings.Count);
        foreach (PathWarningInfo warning in warnings)
        {
            logs.Add(GenerationLogEntry.Warning(
                $"输出路径告警（{warning.Kind}）：{warning.Message}（模板 {warning.TemplateName}，示例路径 {warning.SamplePath}，共 {warning.Occurrences} 处）"));
        }

        return logs;
    }

    /// <summary>
    /// 生成平台表/排除表命中日志：被排除的表以警告级日志可见（不写盘是显式行为），
    /// 平台表经 includePlatformTables 显式放行时以信息级日志标注。
    /// </summary>
    /// <param name="exclusions">平台表/排除表命中记录。</param>
    /// <returns>日志列表。</returns>
    private static List<GenerationLogEntry> BuildPlatformExclusionLogs(IReadOnlyList<PlatformTableExclusion> exclusions)
    {
        var logs = new List<GenerationLogEntry>(exclusions.Count);
        foreach (PlatformTableExclusion exclusion in exclusions)
        {
            bool isExcludeRule = exclusion.Rule.StartsWith("exclude:", StringComparison.Ordinal);
            if (exclusion.Excluded && isExcludeRule)
            {
                logs.Add(GenerationLogEntry.Warning(
                    $"表 {exclusion.TableName} 被排除，不参与生成（命中排除表规则 {exclusion.Rule}）；排除表规则不受 includePlatformTables 影响。"));
            }
            else if (exclusion.Excluded)
            {
                logs.Add(GenerationLogEntry.Warning(
                    $"表 {exclusion.TableName} 被排除，不参与生成（命中平台表规则 {exclusion.Rule}）；平台表禁止生成 CRUD，如确需生成请显式传 includePlatformTables=true。"));
            }
            else
            {
                logs.Add(GenerationLogEntry.Info(
                    $"表 {exclusion.TableName} 命中平台表规则（{exclusion.Rule}），已按 includePlatformTables=true 显式允许生成。"));
            }
        }

        return logs;
    }

    /// <summary>
    /// 生成覆盖告警日志：所选模板缺 View 层/缺前端页面/仅生成后端时以警告级日志可见。
    /// </summary>
    /// <param name="warnings">覆盖告警清单。</param>
    /// <returns>日志列表。</returns>
    private static List<GenerationLogEntry> BuildCoverageWarningLogs(IReadOnlyList<CoverageWarningInfo> warnings)
    {
        var logs = new List<GenerationLogEntry>(warnings.Count);
        foreach (CoverageWarningInfo warning in warnings)
        {
            logs.Add(GenerationLogEntry.Warning($"生成覆盖告警（{warning.Kind}）：{warning.Message}"));
        }

        return logs;
    }

    /// <summary>
    /// 补全生成请求中各表的列元数据：表清单阶段返回的表不含列，实体与 mapper.xml 模板依赖列集合渲染，
    /// 经表详情服务按表补全列集合；未提供表详情服务或数据源、表已含列时原样返回，兼容无连接直构请求的调用场景。
    /// </summary>
    /// <param name="request">生成请求，提供当前数据源配置。</param>
    /// <param name="tables">生成请求中的表集合，可能缺列。</param>
    /// <param name="ct">取消标记。</param>
    /// <returns>补全列元数据后的表集合，元素与入参顺序一致。</returns>
    private async Task<List<TableInfo>> EnrichTableColumnsAsync(
        GenerationRequest request,
        List<TableInfo> tables,
        CancellationToken ct)
    {
        // 未提供表详情服务或当前数据源时无法读取表详情，保持原表不补全
        if (_tableCatalogService is null || request.DataSource is null)
        {
            return tables;
        }

        var enriched = new List<TableInfo>(tables.Count);
        foreach (TableInfo table in tables)
        {
            // 表已含列元数据时直接复用，避免重复读库；否则按表名读取完整列详情
            if (table.Columns.Count > 0)
            {
                enriched.Add(table);
                continue;
            }

            TableInfo detail = await _tableCatalogService.GetTableDetailAsync(request.DataSource, table.RawName, ct).ConfigureAwait(false);
            enriched.Add(detail);
        }

        return enriched;
    }

    /// <summary>
    /// 经模板文件读写服务从包目录直读模板内容，读取异常统一转换为整单失败。
    /// </summary>
    /// <param name="request">生成请求，提供模板包信息。</param>
    /// <param name="file">勾选模板文件。</param>
    /// <param name="cancellationToken">取消标记。</param>
    /// <returns>模板文本。</returns>
    /// <exception cref="GenerationException">模板文件缺失、读取失败或路径越界时抛出。</exception>
    private async Task<string> ReadTemplateContentAsync(
        GenerationRequest request,
        TemplateFileSelection file,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _templateFileWriter.ReadAsync(request.Package, file.TemplateFileName, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or TemplatePackageException)
        {
            throw new GenerationException($"读取模板文件失败：{file.TemplateFileName}，原因：{exception.Message}", exception);
        }
    }

    /// <summary>
    /// 渲染模板内容，失败时抛整单失败异常并携带结构化错误信息。
    /// </summary>
    /// <param name="templateText">模板文本。</param>
    /// <param name="table">当前表元数据。</param>
    /// <param name="packageContext">package 侧渲染上下文。</param>
    /// <param name="file">勾选模板文件，提供模板名定位。</param>
    /// <param name="cancellationToken">取消标记。</param>
    /// <param name="scopeContext">可选的作用域渲染输入（vars / tables），为空时与既有行为一致。</param>
    /// <returns>渲染结果，成功时含真实代码。</returns>
    /// <exception cref="GenerationException">模板渲染失败时抛出。</exception>
    private PreviewResult RenderContent(
        string templateText,
        TableInfo table,
        TemplatePackageContext packageContext,
        TemplateFileSelection file,
        CancellationToken cancellationToken,
        ScopeRenderInput? scopeContext = null)
    {
        PreviewResult result = _engine.Render(templateText, table, null, packageContext, cancellationToken, file.TemplateFileName, scopeContext);
        if (!result.IsSuccess)
        {
            throw new GenerationException($"模板渲染失败：{result.ErrorMessage}");
        }

        return result;
    }

    /// <summary>
    /// 按作用域渲染模板正文（module / project 作用域），失败时抛整单失败异常并携带结构化错误信息。
    /// </summary>
    /// <param name="templateText">模板文本。</param>
    /// <param name="packageContext">package 侧渲染上下文。</param>
    /// <param name="scopeInput">作用域渲染输入（vars / module / tables）。</param>
    /// <param name="file">勾选模板文件，提供模板名定位。</param>
    /// <param name="cancellationToken">取消标记。</param>
    /// <returns>渲染结果，成功时含真实代码。</returns>
    /// <exception cref="GenerationException">模板渲染失败时抛出。</exception>
    private PreviewResult RenderScopedContent(
        string templateText,
        TemplatePackageContext packageContext,
        ScopeRenderInput scopeInput,
        TemplateFileSelection file,
        CancellationToken cancellationToken)
    {
        PreviewResult result = _engine.RenderScoped(templateText, packageContext, scopeInput, cancellationToken, file.TemplateFileName);
        if (!result.IsSuccess)
        {
            throw new GenerationException($"模板渲染失败：{result.ErrorMessage}");
        }

        return result;
    }

    /// <summary>
    /// 按作用域渲染输出路径模板中的占位变量（module / project 作用域），失败时抛整单失败异常并定位模板文件。
    /// </summary>
    /// <param name="packageContext">package 侧渲染上下文。</param>
    /// <param name="scopeInput">作用域渲染输入（vars / module）。</param>
    /// <param name="file">勾选模板文件，提供模板名定位。</param>
    /// <returns>渲染后的相对输出路径。</returns>
    /// <exception cref="GenerationException">输出路径模板渲染失败时抛出。</exception>
    private string RenderScopedOutputPath(
        TemplatePackageContext packageContext,
        ScopeRenderInput scopeInput,
        TemplateFileSelection file)
    {
        try
        {
            return _engine.RenderScopedPathTemplate(file.OutputPathTemplate, packageContext, scopeInput);
        }
        catch (TemplateRenderException exception)
        {
            throw new GenerationException($"输出路径模板渲染失败：{file.TemplateFileName}，原因：{exception.Message}", exception);
        }
    }

    /// <summary>
    /// 渲染输出路径模板中的占位变量，失败时抛整单失败异常并定位模板文件。
    /// </summary>
    /// <param name="table">当前表元数据。</param>
    /// <param name="packageContext">package 侧渲染上下文。</param>
    /// <param name="file">勾选模板文件，提供模板名定位。</param>
    /// <returns>渲染后的相对输出路径。</returns>
    /// <exception cref="GenerationException">输出路径模板渲染失败时抛出。</exception>
    private string RenderOutputPath(TableInfo table, TemplatePackageContext packageContext, TemplateFileSelection file)
    {
        try
        {
            var pathContext = new TemplateRenderContext(table, packageContext);
            return _engine.RenderPathTemplate(file.OutputPathTemplate, pathContext);
        }
        catch (TemplateRenderException exception)
        {
            throw new GenerationException($"输出路径模板渲染失败：{file.TemplateFileName}，原因：{exception.Message}", exception);
        }
    }

    /// <summary>
    /// 解析条目的生效输出根：manifest 显式声明（output/workspace/web）优先；
    /// 未声明时沿用既有作用域缺省语义——project 作用域以工作区根为基准，table/module 以相对输出根为基准。
    /// </summary>
    /// <param name="file">勾选模板文件。</param>
    /// <returns>生效输出根枚举。</returns>
    private static TemplateOutputRoot ResolveEffectiveOutputRoot(TemplateFileSelection file)
    {
        return file.OutputRoot
            ?? (file.Scope == TemplateScope.Project ? TemplateOutputRoot.Workspace : TemplateOutputRoot.Output);
    }

    /// <summary>
    /// 判断条目的输出路径是否允许 .. 段：显式声明 output 根或未声明根（legacy）沿用既有行为，
    /// 允许 .. 在工作区根内越出相对输出根；显式 workspace/web 根禁止 .. 段。
    /// </summary>
    /// <param name="file">勾选模板文件。</param>
    /// <returns>允许 .. 段时为 true。</returns>
    private static bool AllowsParentTraversal(TemplateFileSelection file)
    {
        return file.OutputRoot is null || file.OutputRoot == TemplateOutputRoot.Output;
    }

    /// <summary>
    /// 平台表/排除表过滤：排除表规则命中一律不生成；平台表规则命中默认不生成（includePlatformTables=true 时放行）。
    /// 命中记录随 dry-run 回执透出，保证「不写」是显式可见行为而非静默丢弃。
    /// </summary>
    /// <param name="request">生成请求，提供平台表开关与模板包规则。</param>
    /// <param name="tables">请求中的表集合。</param>
    /// <returns>实际参与生成的表集合与命中记录清单。</returns>
    private static (List<TableInfo> IncludedTables, List<PlatformTableExclusion> Exclusions) ApplyTableFilters(
        GenerationRequest request,
        List<TableInfo> tables)
    {
        var included = new List<TableInfo>(tables.Count);
        var exclusions = new List<PlatformTableExclusion>();
        foreach (TableInfo table in tables)
        {
            // 排除表规则优先：命中即不生成，includePlatformTables 不解除排除表
            string? excludeRule = request.Package.ExcludeTables.Match(table.RawName);
            if (excludeRule is not null)
            {
                exclusions.Add(new PlatformTableExclusion(table.RawName, $"exclude:{excludeRule}", excluded: true));
                continue;
            }

            string? platformRule = request.Package.PlatformTables.Match(table.RawName);
            if (platformRule is not null)
            {
                bool allowed = request.IncludePlatformTables;
                exclusions.Add(new PlatformTableExclusion(table.RawName, $"platform:{platformRule}", excluded: !allowed));
                if (!allowed)
                {
                    continue;
                }
            }

            included.Add(table);
        }

        return (included, exclusions);
    }

    /// <summary>
    /// 构建逐表命名报告：按生效包段与命名策略计算点号形态包路径（基础包 + layerPrefix + variableName，
    /// 包段已小写归一与连续去重），并附真实物理表名与权限模块，供调用方写盘前核对。
    /// </summary>
    /// <param name="tables">应用命名策略后的表集合。</param>
    /// <param name="packageContext">package 侧渲染上下文，提供包段、命名策略与权限模块。</param>
    /// <returns>命名报告条目清单。</returns>
    private static IReadOnlyList<NamingReportItem> BuildNamingReport(
        IReadOnlyList<TableInfo> tables,
        TemplatePackageContext packageContext)
    {
        var items = new List<NamingReportItem>(tables.Count);
        foreach (TableInfo table in tables)
        {
            IReadOnlyList<string> packageSegments = PackagePathBuilder.Compose(
                packageContext.Segments,
                packageContext.Naming.PackageSegmentCase,
                packageContext.Naming.LayerPrefix,
                table.VariableName);
            items.Add(new NamingReportItem(
                table.RawName,
                string.IsNullOrEmpty(table.EntityName) ? table.ClassName : table.EntityName,
                table.VariableName,
                PackagePathBuilder.ToDottedPath(packageSegments),
                table.RawName,
                packageContext.PermissionModule));
        }

        return items;
    }

    /// <summary>
    /// 按所选模板的产出角色集合判定生成覆盖告警：含后端产出但缺 View 层 → NoBackendView；
    /// 包具备前端页面能力但所选模板缺页面 → NoWebPage；仅选中后端产出 → OnlyBackendGenerated。
    /// </summary>
    /// <param name="request">生成请求，提供模板包能力元数据。</param>
    /// <param name="selectedFiles">勾选模板文件集合。</param>
    /// <returns>覆盖告警清单；无告警时为空列表。</returns>
    private static IReadOnlyList<CoverageWarningInfo> BuildCoverageWarnings(
        GenerationRequest request,
        IReadOnlyList<TemplateFileSelection> selectedFiles)
    {
        if (selectedFiles.Count == 0)
        {
            return Array.Empty<CoverageWarningInfo>();
        }

        var roles = new HashSet<string>(StringComparer.Ordinal);
        foreach (TemplateFileSelection file in selectedFiles)
        {
            string role = string.IsNullOrWhiteSpace(file.Role)
                ? TemplateFileRoleResolver.Infer(file.TemplateFileName, file.OutputPathTemplate)
                : file.Role.Trim();
            roles.Add(role);
        }

        bool hasBackend = roles.Any(TemplateFileRoleResolver.IsBackendRole);
        bool hasBackendView = roles.Contains(TemplateFileRoleResolver.BackendView);
        bool hasWeb = roles.Any(TemplateFileRoleResolver.IsWebRole);
        bool hasWebPage = roles.Contains(TemplateFileRoleResolver.WebPage);

        var warnings = new List<CoverageWarningInfo>();
        if (hasBackend && !hasBackendView)
        {
            warnings.Add(new CoverageWarningInfo(
                CoverageWarningInfo.NoBackendView,
                "所选模板含后端产出但缺后端 View 层（view/MPView/Repository）：代码生成至少须覆盖到 View 层，请补选 View 层模板。"));
        }

        if (request.Package.HasWebPage && hasBackend && !hasWebPage)
        {
            warnings.Add(new CoverageWarningInfo(
                CoverageWarningInfo.NoWebPage,
                "模板包具备前端页面能力（hasWebPage=true），但本次所选模板缺前端页面条目（列表页/表单弹窗）；前端业务页面纳入生成面时须同时选中 webPage 模板。"));
        }

        if (hasBackend && !hasWeb)
        {
            warnings.Add(new CoverageWarningInfo(
                CoverageWarningInfo.OnlyBackendGenerated,
                "本次只选择了后端模板，未选中任何前端 API/DTO/页面/路由/菜单模板：前端业务页面需由生成器产出时，请补选 web 业务模板（role=webApi/webDto/webPage/webRoute/webMenu）。"));
        }

        return warnings;
    }

    /// <summary>
    /// 工作区根疑似聚合根预检：workspaceRoot 含带 &lt;modules&gt; 的 pom.xml 且 codeDirectory 未落入其下模块目录时，
    /// 产出 WorkspaceRootMayBeAggregate 告警（提示应传工程父目录而非聚合根）。
    /// </summary>
    /// <param name="warnings">本次生成累计的告警清单，命中时就地归并。</param>
    /// <param name="request">生成请求，提供工作区根与代码目录。</param>
    /// <param name="cancellationToken">取消标记，贯穿 pom.xml 读取。</param>
    /// <returns>异步读取 pom.xml 的任务。</returns>
    private async Task CollectWorkspaceRootWarningAsync(
        List<PathWarningInfo> warnings,
        GenerationRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.WorkspaceRoot))
        {
            return;
        }

        string workspaceRootFull;
        try
        {
            workspaceRootFull = Path.GetFullPath(request.WorkspaceRoot);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return;
        }

        string pomPath = Path.Combine(workspaceRootFull, "pom.xml");
        if (!File.Exists(pomPath))
        {
            return;
        }

        string pomText;
        try
        {
            pomText = await File.ReadAllTextAsync(pomPath, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // 聚合根探测为辅助预检，读取失败记录调试日志后跳过；真实问题由后续模板渲染与写盘暴露
            _logger.LogDebug(exception, "聚合根预检读取 pom.xml 失败，已跳过该预检：{PomPath}", pomPath);
            return;
        }

        if (!pomText.Contains("<modules>", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        // 聚合模块目录清单：<module>xxx</module> 逐项提取，用于判断 codeDirectory 是否落入某个模块下
        MatchCollection moduleMatches = Regex.Matches(pomText, "<module>\\s*([^<]+?)\\s*</module>", RegexOptions.IgnoreCase);
        var moduleNames = new List<string>(moduleMatches.Count);
        foreach (Match match in moduleMatches)
        {
            string moduleName = match.Groups[1].Value.Trim().Replace('\\', '/');
            if (moduleName.Length > 0)
            {
                moduleNames.Add(moduleName);
            }
        }
        if (moduleNames.Count == 0)
        {
            return;
        }

        string codeDirectory = (request.CodeDirectory ?? string.Empty).Replace('\\', '/').Trim('/');
        string firstSegment = codeDirectory.Split('/', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? string.Empty;
        if (moduleNames.Any(name => string.Equals(name, firstSegment, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        AppendPathWarning(
            warnings,
            PathWarningInfo.WorkspaceRootMayBeAggregate,
            "workspaceRoot",
            request.WorkspaceRoot,
            "workspaceRoot 自身含带 <modules> 的 pom.xml（疑似 Maven 聚合根），而 codeDirectory 未落入其下模块目录；请把 workspaceRoot 指向工程父目录，并令 codeDirectory 形如 <模块目录>/src/main/java/<完整包路径>。");
    }

    /// <summary>
    /// 权限模块派生源一致性预检：仅当调用方显式声明权限模块（请求 variables.permissionModule 或 manifest.permissionModule）
    /// 且与 Hump3Underline(modules[0].name) 不一致时产出 PermissionKeyMismatch 告警；
    /// 由 modules 自动派生的情形与脚手架权限点清单同源，不告警。
    /// </summary>
    /// <param name="warnings">本次生成累计的告警清单，命中时就地归并。</param>
    /// <param name="request">生成请求，提供模块清单与权限派生输入。</param>
    private static void CollectPermissionModuleWarning(List<PathWarningInfo> warnings, GenerationRequest request)
    {
        if (request.Modules is null || request.Modules.Count == 0)
        {
            return;
        }

        // 仅显式声明参与一致性判定；未显式声明时 permissionModule 由 modules 自动派生，天然同源
        string? explicitModule = PolicyValueReader.GetString(request.Variables, PermissionModuleResolver.VariableKey);
        if (explicitModule is null && !string.IsNullOrWhiteSpace(request.Package.PermissionModule))
        {
            explicitModule = request.Package.PermissionModule.Trim();
        }

        if (explicitModule is null)
        {
            return;
        }

        string moduleKebab = ToolFunctions.Hump3Underline(request.Modules[0].Name);
        if (!string.Equals(moduleKebab, explicitModule, StringComparison.Ordinal))
        {
            AppendPathWarning(
                warnings,
                PathWarningInfo.PermissionKeyMismatch,
                "modules",
                request.Modules[0].Name,
                $"显式权限模块 \"{explicitModule}\"（请求变量/manifest）与模块名 kebab 形态 \"{moduleKebab}\" 不一致；"
                + "Controller 权限注解与权限点清单必须共用同一派生值（建议统一消费 package.permissionModule / tool.permissionKey），否则运行期 403。");
        }
    }

    /// <summary>
    /// 模板正文命名反推预检：静态出现 hump2Underline/hump3Underline(table.className) 时产出
    /// TableNameRoundTripMismatch（物理表名必须用 table.rawName）。同模板同种类仅归并一条，按渲染次数累计。
    /// </summary>
    /// <param name="warnings">本次生成累计的告警清单，命中时就地归并。</param>
    /// <param name="file">勾选模板文件，提供模板名与输出路径模板定位。</param>
    /// <param name="templateText">模板正文。</param>
    private static void CollectTemplateTextWarnings(
        List<PathWarningInfo> warnings,
        TemplateFileSelection file,
        string templateText)
    {
        if (string.IsNullOrEmpty(templateText))
        {
            return;
        }

        if (Regex.IsMatch(templateText, @"hump[23]Underline\s*\(\s*table\.className\s*\)", RegexOptions.CultureInvariant))
        {
            AppendPathWarning(
                warnings,
                PathWarningInfo.TableNameRoundTripMismatch,
                file.TemplateFileName,
                file.OutputPathTemplate,
                "模板正文用 hump2Underline/hump3Underline(table.className) 反推物理表名：表前缀剥离与单复数归一后反推会失真；物理表名（@TableName / DDL / RLS / @Schema）必须直接使用 table.rawName。");
        }
    }

    /// <summary>
    /// 解析渲染后相对路径为绝对路径并按条目输出根语义校验：
    /// output 根与未声明根的既有条目允许 .. 段在工作区根内越出相对输出根（既有行为）；
    /// 显式 workspace/web 根直接拒绝 .. 段；三种根解析后的最终路径都必须落在 workspaceRoot 内。
    /// </summary>
    /// <param name="request">生成请求，提供工作区根、相对输出根与 web 目录。</param>
    /// <param name="renderedRelativePath">渲染后的相对输出路径。</param>
    /// <param name="outputRoot">条目输出根语义。</param>
    /// <param name="allowParentTraversal">是否允许 .. 段（output 根与未声明根为 true，显式 workspace/web 根为 false）。</param>
    /// <returns>校验通过的目标绝对路径。</returns>
    /// <exception cref="GenerationException">路径含非法字符、含 .. 段（显式 workspace/web）或越出工作区根时抛出。</exception>
    private static string ResolveAbsolutePath(
        GenerationRequest request,
        string renderedRelativePath,
        TemplateOutputRoot outputRoot,
        bool allowParentTraversal)
    {
        // 渲染后产物路径非法字符检查：逐段核对 Windows 非法文件名字符（含 | * ? < > 与控制字符）
        EnsurePathHasNoInvalidChar(renderedRelativePath);

        // 渲染后相对路径为空时直接拒绝，避免以目录路径作为写盘目标产生误导性写盘失败
        if (string.IsNullOrWhiteSpace(renderedRelativePath))
        {
            throw new GenerationException($"输出路径渲染结果为空，已拒绝（目录穿越）：{renderedRelativePath}");
        }

        // 渲染后路径必须是相对路径：绝对路径与盘符前缀一律拒绝（渲染表达式同样不得绕过静态骨架校验）
        if (Path.IsPathRooted(renderedRelativePath) || renderedRelativePath.Contains(':'))
        {
            throw new GenerationException(
                $"输出路径必须为相对路径，禁止绝对路径与盘符前缀（产物路径校验）：{renderedRelativePath}");
        }

        // 显式 workspace / web 根为固定基准目录，禁止 .. 段逃离；output 根与未声明根维持既有「工作区根内可越出相对输出根」行为
        if (!allowParentTraversal)
        {
            string[] segments = renderedRelativePath.Replace('\\', '/').Split('/');
            if (segments.Any(segment => segment == ".."))
            {
                throw new GenerationException(
                    $"输出路径含 .. 段，root={TemplateOutputRootParser.ToContractText(outputRoot)} 的条目禁止越出基准目录（目录穿越）：{renderedRelativePath}");
            }
        }

        string workspaceRootFull = Path.GetFullPath(request.WorkspaceRoot)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        // 按输出根语义确定基准目录：output=工作区根+相对输出根；workspace=工作区根；web=工作区根+webDirectory
        string baseRoot = outputRoot switch
        {
            TemplateOutputRoot.Workspace => workspaceRootFull,
            TemplateOutputRoot.Web => Path.GetFullPath(Path.Combine(
                workspaceRootFull,
                request.WebDirectory.Replace('/', Path.DirectorySeparatorChar))),
            _ => Path.GetFullPath(Path.Combine(
                workspaceRootFull,
                request.RelativeOutputRoot.Replace('/', Path.DirectorySeparatorChar)))
        };

        // 渲染后相对路径解析到基准目录内；最终路径必须仍位于工作区根内，越界即整单失败
        string candidate = Path.GetFullPath(Path.Combine(baseRoot, renderedRelativePath.Replace('/', Path.DirectorySeparatorChar)));
        string prefix = workspaceRootFull + Path.DirectorySeparatorChar;
        if (!candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new GenerationException($"输出路径越出工作区根，已拒绝（目录穿越）：{renderedRelativePath}");
        }

        return candidate;
    }

    /// <summary>
    /// 渲染后产物路径非法字符检查：按正斜杠分段核对 Windows 非法文件名字符，命中即整单结构化失败。
    /// outputPath 模板（含 {{变量}} 占位与管道表达式）在清单写入阶段放行，渲染期表达式合法化后，
    /// 字面量非法字符经本单点拦截，非法路径不进入 dry-run 分类与写盘。
    /// </summary>
    /// <param name="renderedRelativePath">渲染后的相对输出路径。</param>
    /// <exception cref="GenerationException">路径含非法字符时抛出，携带完整被拒路径。</exception>
    private static void EnsurePathHasNoInvalidChar(string renderedRelativePath)
    {
        char[] invalidFileNameChars = Path.GetInvalidFileNameChars();
        foreach (string segment in renderedRelativePath.Split('/'))
        {
            if (segment.IndexOfAny(invalidFileNameChars) >= 0)
            {
                throw new GenerationException($"输出路径含 Windows 非法字符，已拒绝（产物路径校验）：{renderedRelativePath}");
            }
        }
    }

    /// <summary>
    /// 按目标文件状态与同名文件处理策略做 dry-run 分类：目标不存在为新增；
    /// 跳过策略下同名目标一律跳过；覆盖策略下内容相同跳过、内容不同覆盖，非 UTF-8 遗留文件按覆盖处理。
    /// </summary>
    /// <param name="absolutePath">目标文件绝对路径。</param>
    /// <param name="content">渲染后的文件内容。</param>
    /// <param name="strategy">同名文件处理策略。</param>
    /// <param name="cancellationToken">取消标记。</param>
    /// <returns>分类动作。</returns>
    private static async Task<GenerationAction> ClassifyTargetAsync(
        string absolutePath,
        string content,
        DuplicateFileStrategy strategy,
        CancellationToken cancellationToken)
    {
        // 目标不存在始终为新增
        if (!File.Exists(absolutePath))
        {
            return GenerationAction.New;
        }

        // 跳过策略：同名目标一律不写盘，无论内容是否相同
        if (strategy == DuplicateFileStrategy.Skip)
        {
            return GenerationAction.Skip;
        }

        // 覆盖策略：读取既有文件内容（UTF-8 去 BOM），与渲染内容做行尾归一化后比较，避免 CRLF/LF 差异误判为覆盖
        string existing;
        try
        {
            existing = await File.ReadAllTextAsync(absolutePath, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
        }
        catch (DecoderFallbackException)
        {
            // 既有文件非 UTF-8 编码（如 GBK 遗留文件），内容必然与渲染出的 UTF-8 结果不同，按覆盖处理
            return GenerationAction.Overwrite;
        }

        return string.Equals(NormalizeLineEndings(existing), NormalizeLineEndings(content), StringComparison.Ordinal)
            ? GenerationAction.Skip
            : GenerationAction.Overwrite;
    }

    /// <summary>
    /// 将文本行尾统一为换行符，供 dry-run 内容比较使用。
    /// </summary>
    /// <param name="text">原始文本。</param>
    /// <returns>行尾归一化后的文本。</returns>
    private static string NormalizeLineEndings(string text)
    {
        return text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
    }

    /// <summary>
    /// 生成跳过条目的日志，供最终日志合并展示；跳过原因文案随同名文件策略区分
    /// （覆盖策略下内容相同跳过，跳过策略下同名已存在跳过）。
    /// </summary>
    /// <param name="preview">dry-run 清单。</param>
    /// <param name="strategy">同名文件处理策略，决定跳过原因文案。</param>
    /// <returns>跳过条目日志列表。</returns>
    private static List<GenerationLogEntry> BuildSkipLogs(GenerationPreview preview, DuplicateFileStrategy strategy)
    {
        var logs = new List<GenerationLogEntry>();
        string skipReason = strategy == DuplicateFileStrategy.Skip ? "已跳过（同名文件已存在）" : "已跳过（内容相同）";
        foreach (GenerationFileEntry entry in preview.Entries)
        {
            if (entry.Action == GenerationAction.Skip)
            {
                logs.Add(GenerationLogEntry.Info($"{skipReason}：{entry.RelativePath}"));
            }
        }

        return logs;
    }

    /// <summary>
    /// 生成整单取消的日志列表。
    /// </summary>
    /// <param name="message">取消原因。</param>
    /// <returns>取消日志列表。</returns>
    private static List<GenerationLogEntry> BuildCancelLogs(string message)
    {
        return new List<GenerationLogEntry> { GenerationLogEntry.Warning(message) };
    }

    /// <summary>
    /// 生成完成后一并回写工作区根与最近代码目录并保存，回写失败仅记录警告日志，不阻断结果返回。
    /// </summary>
    /// <param name="workspaceRoot">本次生成使用的工作区根，写回为下次生成的默认值。</param>
    /// <param name="codeDirectory">本次生成的代码目录，作为下次生成的默认值。</param>
    /// <param name="logs">最终日志列表，回写失败时追加警告条目。</param>
    private void WriteBackOutputRoot(string workspaceRoot, string codeDirectory, List<GenerationLogEntry> logs)
    {
        try
        {
            // 一并回写工作区根与最近代码目录，供下次生成作为默认值
            _configService.Current.WorkspaceRoot = workspaceRoot;
            _configService.Current.LastRelativeOutputRoot = codeDirectory;
            _configService.Save();
        }
        catch (ConfigSaveException exception)
        {
            logs.Add(GenerationLogEntry.Warning($"最近生成路径回写失败：{exception.Message}"));
            _logger.LogWarning(exception, "最近生成路径回写失败。");
        }
    }
}

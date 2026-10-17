using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using DbCodeGen.Core.Generation;
using DbCodeGen.Core.Model;
using DbCodeGen.Core.Templates;
using DbCodeGen.Core.Templates.Packages;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace DbCodeGen.McpServer.Tools;

/// <summary>
/// 预览工具组：单模板内容渲染（合成表上下文，无需数据库连接）与批量生成 dry-run 文件计划（零写盘）。
/// 单模板渲染与桌面端实时预览共用同一 TemplateEngine 管线，批量预览与生成写盘共用同一 BuildPreviewAsync 管线，
/// 保证"所见即所得"；渲染失败按业务结果结构化返回（含行列定位），不按调用错误处理。
/// </summary>
[McpServerToolType]
public sealed class PreviewTools
{
    private readonly ITemplatePackageService _packageService;
    private readonly TemplateFileWriter _templateFileWriter;
    private readonly TemplateEngine _templateEngine;
    private readonly ICodeGenerator _codeGenerator;
    private readonly ILogger<PreviewTools> _logger;

    /// <summary>
    /// 以模板包服务、模板文件读写服务、共享渲染引擎、批量生成服务与日志器构造预览工具组。
    /// </summary>
    /// <param name="packageService">模板包服务，承载包加载与校验。</param>
    /// <param name="templateFileWriter">模板文件读写服务，承载防目录穿越的模板正文读取。</param>
    /// <param name="templateEngine">共享渲染引擎，与桌面端实时预览共用。</param>
    /// <param name="codeGenerator">批量生成服务，承载 dry-run 文件计划计算。</param>
    /// <param name="logger">工具组日志器。</param>
    public PreviewTools(
        ITemplatePackageService packageService,
        TemplateFileWriter templateFileWriter,
        TemplateEngine templateEngine,
        ICodeGenerator codeGenerator,
        ILogger<PreviewTools> logger)
    {
        _packageService = packageService;
        _templateFileWriter = templateFileWriter;
        _templateEngine = templateEngine;
        _codeGenerator = codeGenerator;
        _logger = logger;
    }

    /// <summary>
    /// 以合成表上下文渲染单个模板（无需数据库连接），返回渲染后全文或结构化语法错误（含行列定位）。
    /// 传入 table 时按表上下文渲染（现状行为）；省略 table 时按作用域片段渲染——传 modules 取首元素为 module 上下文，
    /// 未传 modules 按 project 型上下文（vars + 空 tables 汇总）；渲染返回文本不落盘。
    /// 与 generate/preview_generation 的 tables 分型必填语义不同，本工具 table 始终可省略（手写域/无表场景）。
    /// </summary>
    /// <param name="package">模板包包名。</param>
    /// <param name="path">模板文件相对包根路径（与 list_templates 返回的 path 一致）。</param>
    /// <param name="table">合成表上下文：原始表名必填，列集合可选；可省略（手写域/无表场景），省略时按作用域片段渲染。</param>
    /// <param name="column">可选的当前列上下文，供模板内 column 变量消费（列级模板用）；仅在传入 table 时可用。</param>
    /// <param name="basePackageOverride">本次渲染的基础包名覆盖值；为空时使用模板包 manifest 基础包名。</param>
    /// <param name="variables">可选的调用方变量对象，逐键注入渲染上下文 vars.*；值支持任意 JSON 类型（字符串/数组/布尔/对象），集合消费键必须传真实数组；键与保留命名空间冲突时结构化拒绝。</param>
    /// <param name="modules">可选的模块清单（如 [{"name":"account"}]）；片段渲染单次仅支持一个模块上下文，取首元素（name 必填，其余字段原样注入 module.*）；与 table 互斥。</param>
    /// <param name="cancellationToken">取消标记。</param>
    /// <returns>渲染结果全文（PreviewResult 全量字段）。</returns>
    [McpServerTool(Name = "render_template", Title = "渲染模板", ReadOnly = true, Idempotent = true, Destructive = false)]
    [Description("以合成表上下文渲染单个模板（无需数据库连接）：返回渲染后全文；模板语法错误时返回 errorMessage 与 errorLine/errorColumn 行列定位。table 可省略（手写域/无表场景）：不传 table 时传 modules 按 module 作用域渲染片段、未传 modules 按 project 上下文（vars + 空 tables 汇总）渲染——generate/preview_generation 仅在所选模板含 table 作用域条目时要求 tables 非空（纯 module/project 作用域集合可省略），本工具 table 始终可省略。支持可选 variables（注入 vars.*）与 modules（module 作用域片段渲染，返回文本不落盘）。")]
    public async Task<object> RenderTemplateAsync(
        string package,
        string path,
        SyntheticTableInput? table = null,
        SyntheticColumnInput? column = null,
        string? basePackageOverride = null,
        [Description("调用方变量对象，逐键注入渲染上下文 vars.*；值支持任意 JSON 类型（字符串/数值/布尔/数组/对象），集合消费键必须传真实数组（传字符串将被结构化拒绝），布尔键传布尔值；键与保留命名空间冲突时结构化拒绝。")]
        IReadOnlyDictionary<string, JsonElement>? variables = null,
        IReadOnlyList<Dictionary<string, object?>>? modules = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(package))
            {
                throw new ArgumentException("模板包名（package）不能为空。");
            }

            if (string.IsNullOrWhiteSpace(path))
            {
                throw new ArgumentException("模板路径（path）不能为空。");
            }

            // 片段渲染入参组合校验：table 与 modules 互斥；column 依赖 table
            if (table is not null && modules is not null && modules.Count > 0)
            {
                throw new ArgumentException("render_template 的 table 与 modules 互斥：表上下文渲染传 table，module 作用域片段渲染传 modules，二者不可同时提供。");
            }

            if (table is null && column is not null)
            {
                throw new ArgumentException("column（当前列上下文）依赖 table 提供；module/project 型片段渲染不支持 column。");
            }

            if (modules is not null && modules.Count > 1)
            {
                throw new ArgumentException("render_template 单次仅支持一个模块上下文：modules 清单最多一个元素；多模块请逐次调用。");
            }

            TemplatePackageInfo packageInfo = await _packageService.LoadPackageAsync(package.Trim(), cancellationToken);

            // 仅允许渲染包清单声明的模板文件：以清单条目为白名单，杜绝任意文件读取
            TemplateFileInfo? entry = FindFileEntry(packageInfo, path);
            if (entry is null)
            {
                throw new TemplatePackageException($"模板文件不存在：包 {package} 中未声明模板路径 {path}。");
            }

            // 复用桌面端同款读取通道（防目录穿越 + UTF-8 去 BOM），随后与实时预览共用同一渲染管线
            string templateText = await _templateFileWriter.ReadAsync(packageInfo, entry.RelativeTemplatePath, cancellationToken);
            TemplatePackageContext packageContext = TemplatePackageContext.From(packageInfo, basePackageOverride);

            // 变量与模块解析：键空白/模块缺 name 在此结构化拒绝；保留命名空间冲突由 Core 作用域输入校验
            IReadOnlyDictionary<string, object?>? parsedVariables = GenerationRequestFactory.ParseVariables(variables);
            IReadOnlyList<ModuleContext>? parsedModules = GenerationRequestFactory.ParseModules(modules);

            PreviewResult renderResult;
            if (table is not null)
            {
                // 表上下文渲染（现状行为），附带注入可选 vars / tables 汇总
                var tableInfo = SyntheticTableMapper.ToTableInfo(table);
                ColumnInfo? columnInfo = column is null ? null : SyntheticTableMapper.ToColumnInfo(column);
                ScopeRenderInput? scopeInput = parsedVariables is null
                    ? null
                    : ScopeRenderInput.Create(parsedVariables, null, new[] { tableInfo });

                renderResult = _templateEngine.Render(
                    templateText, tableInfo, columnInfo, packageContext, cancellationToken, templateName: entry.RelativeTemplatePath, scopeInput);
            }
            else
            {
                // 作用域片段渲染（module / project 型）：返回文本不落盘；tables 汇总按无表空清单注入，
                // modules 清单同步透传（module 作用域注入 module.*，project 作用域注入 modules 聚合清单）
                ScopeRenderInput scopeInput = ScopeRenderInput.Create(
                    parsedVariables,
                    parsedModules is null ? null : parsedModules[0],
                    Array.Empty<TableInfo>(),
                    parsedModules);

                renderResult = _templateEngine.RenderScoped(
                    templateText, packageContext, scopeInput, cancellationToken, templateName: entry.RelativeTemplatePath);
            }

            _logger.LogInformation(
                "render_template 完成，包 {PackageName}，路径 {TemplatePath}，成功 {IsSuccess}，耗时 {RenderDurationMs}ms，告警 {WarningCount}。",
                package, entry.RelativeTemplatePath, renderResult.IsSuccess, renderResult.RenderDurationMs, renderResult.Warnings.Count);
            return new RenderTemplateResult(
                renderResult.IsSuccess,
                renderResult.Output,
                renderResult.ErrorMessage,
                renderResult.ErrorLine,
                renderResult.ErrorColumn,
                renderResult.RenderDurationMs,
                renderResult.Warnings.Count == 0 ? null : renderResult.Warnings);
        }
        catch (GenerationException exception)
        {
            // 变量命名空间冲突等生成域结构化业务错误按参数错误返回，供调用方修正入参后重试
            _logger.LogError(exception, "render_template 生成域失败，包名 {PackageName}，路径 {TemplatePath}。", package, path);
            return new ToolErrorResult(ToolErrorResult.ErrorMarker, "InvalidArgument", exception.Message);
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "render_template 执行失败，包名 {PackageName}，路径 {TemplatePath}。", package, path);
            return ToolError.From(exception, cancellationToken);
        }
    }

    /// <summary>
    /// 构建批量生成 dry-run 文件计划（table 作用域按表×模板笛卡尔积、module 作用域按模块清单逐个、project 作用域整次一次，
    /// 三种作用域可混跑；渲染后按目标状态分类为 new/overwrite/skip），全程零写盘。
    /// tables 按生效模板集合分型：仅当所选模板（files 过滤后）含 table 作用域条目时必填非空。
    /// </summary>
    /// <param name="package">模板包包名。</param>
    /// <param name="workspaceRoot">工作区根目录绝对路径（输出文件的根前缀，须已存在或可创建）。</param>
    /// <param name="codeDirectory">代码目录（项目内完整相对路径含包名，如 src/main/java/com/example/common）；经源根标记拆分为相对输出根与基础包名，包间输出路径配方以包清单声明的输出路径形态为准。</param>
    /// <param name="tables">合成表集合；所选模板含 table 作用域条目时必填非空（至少一张表），纯 module/project 作用域集合可省略；离线合成场景直接内联表结构即可，无需连接数据库。</param>
    /// <param name="basePackageOverride">本次生成的基础包名覆盖值；为空时使用代码目录派生值。</param>
    /// <param name="files">模板过滤清单：传则仅预览清单内模板（包内不存在的路径直接拒绝），省略时默认选择包内全部启用条目。</param>
    /// <param name="duplicateStrategy">同名目标文件处理策略：overwrite / skip，影响 overwrite/skip 分类。</param>
    /// <param name="variables">可选的调用方变量对象，逐键注入渲染上下文 vars.*（如 {"appName":"demo"}）；值支持任意 JSON 类型（字符串/数组/布尔/对象），集合消费键必须传真实数组；键与保留命名空间冲突时结构化拒绝。</param>
    /// <param name="modules">可选的模块清单（如 [{"name":"account"}]），包内 module 作用域模板按此逐个渲染；元素 name 必填，其余字段原样注入 module.*；勾选 module 作用域模板但省略时结构化报错。</param>
    /// <param name="cancellationToken">取消标记。</param>
    /// <returns>文件计划条目、分类计数、未映射类型清单与输出路径告警清单。</returns>
    [McpServerTool(Name = "preview_generation", Title = "预览生成", ReadOnly = true, Idempotent = true, Destructive = false)]
    [Description("批量生成 dry-run 预览（零写盘）：返回文件计划（table/module/project 三作用域混跑，new/overwrite/skip 分类与目标路径）、未映射类型清单与预检告警清单（pathWarnings：EmptyPackageDir 包段为空 / ConsecutiveSeparators 连续分隔符 / EmptyIteration 模板空迭代零迭代信号 / InvalidJavaPackageSegment 非法 Java 包段——连字符/非法字符/数字开头/Java 关键字，非阻断但必须核对；坏路径类须先修正参数重新生成，非法 Java 包段应用 tool.javaIdent 归一化 kebab 模块名，空迭代告警若为守卫文案设计可按文案指引忽略），确认后再调 generate；tables 按所选模板分型——所选模板（files 过滤后）含 table 作用域条目时必填非空（至少一张表），纯 module/project 作用域集合可省略。files 为模板过滤清单：传则仅预览清单内模板，省略时选包内全部启用条目。")]
    public async Task<object> PreviewGenerationAsync(
        string package,
        string workspaceRoot,
        [Description("代码目录（项目内完整相对路径含包名，如 src/main/java/com/example/common）：按源根标记（java/kotlin/groovy/scala/src）拆分为相对输出根与基础包名，纯包名形态输出根固定 src/main/java；包间输出路径配方差异以包清单 files[].output 形态为准（经 list_templates 查看）。")]
        string codeDirectory,
        [Description("合成表集合；所选模板（files 过滤后）含 table 作用域条目时必填非空（至少一张表），纯 module/project 作用域集合可省略；离线合成场景直接内联表结构（列元数据自带），无需连接数据库。")]
        IReadOnlyList<SyntheticTableInput>? tables = null,
        string? basePackageOverride = null,
        [Description("模板过滤清单：传则仅预览清单内模板（包内不存在的路径直接拒绝），省略时默认选择包内全部启用条目。")]
        IReadOnlyList<string>? files = null,
        [Description("同名目标文件处理策略：overwrite（覆盖内容不同的同名文件）/ skip（同名一律跳过）；对含手写代码的输出目录用 skip 防覆盖手写文件。")]
        DuplicateFileStrategy duplicateStrategy = DuplicateFileStrategy.Overwrite,
        [Description("调用方变量对象，逐键注入渲染上下文 vars.*（如 {\"appName\":\"demo\"}）；值支持任意 JSON 类型（字符串/数值/布尔/数组/对象），集合消费键必须传真实数组（传字符串将被结构化拒绝），布尔键传布尔值；键与保留命名空间冲突时结构化拒绝。")]
        IReadOnlyDictionary<string, JsonElement>? variables = null,
        IReadOnlyList<Dictionary<string, object?>>? modules = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            GenerationRequest request = await GenerationRequestFactory.CreateAsync(
                _packageService, package, tables, files, workspaceRoot, codeDirectory,
                basePackageOverride, duplicateStrategy.ToString().ToLowerInvariant(), persistLastUsedPaths: false,
                variables, modules, cancellationToken);

            // dry-run 只计算文件计划，不产生任何写盘动作
            GenerationPreview preview = await _codeGenerator.BuildPreviewAsync(request, progress: null, cancellationToken);

            List<PreviewFilePlan> entries = new(preview.Entries.Count);
            foreach (var entry in preview.Entries)
            {
                entries.Add(new PreviewFilePlan(
                    entry.TableName,
                    entry.RelativePath,
                    entry.AbsolutePath,
                    ToActionText(entry.Action)));
            }

            List<UnmappedTypeItem> unmappedTypes = new(preview.UnmappedTypes.Count);
            foreach (var unmapped in preview.UnmappedTypes)
            {
                unmappedTypes.Add(new UnmappedTypeItem(unmapped.DbType, unmapped.TableName, unmapped.ColumnName, unmapped.Occurrences));
            }

            List<PathWarningItem> pathWarnings = new(preview.PathWarnings.Count);
            foreach (PathWarningInfo warning in preview.PathWarnings)
            {
                pathWarnings.Add(new PathWarningItem(warning.Kind, warning.TemplateName, warning.SamplePath, warning.Occurrences, warning.Message));
            }

            _logger.LogInformation(
                "preview_generation 完成，包 {PackageName}，条目 {EntryCount}（new {NewCount}/overwrite {OverwriteCount}/skip {SkipCount}），路径告警 {PathWarningCount}。",
                package, entries.Count, preview.NewCount, preview.OverwriteCount, preview.SkipCount, pathWarnings.Count);
            return new PreviewGenerationResult(
                entries,
                preview.NewCount,
                preview.OverwriteCount,
                preview.SkipCount,
                unmappedTypes,
                pathWarnings);
        }
        catch (GenerationException exception)
        {
            // 渲染失败与输出路径越界属生成域结构化业务错误（携带模板名行列或被拒路径），映射模板包域错误便于调用方定位模板问题
            _logger.LogError(exception, "preview_generation 生成域失败，包名 {PackageName}。", package);
            return new ToolErrorResult(ToolErrorResult.ErrorMarker, "TemplatePackageError", exception.Message);
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "preview_generation 执行失败，包名 {PackageName}。", package);
            return ToolError.From(exception, cancellationToken);
        }
    }

    /// <summary>
    /// 在包清单文件条目中按相对路径查找目标模板；入参路径统一正斜杠后与条目忽略大小写比对。
    /// </summary>
    /// <param name="package">包运行时信息。</param>
    /// <param name="path">调用方传入的模板相对路径。</param>
    /// <returns>命中的文件条目；未命中返回 null。</returns>
    private static TemplateFileInfo? FindFileEntry(TemplatePackageInfo package, string path)
    {
        string normalized = path.Trim().Replace('\\', '/');
        return package.Files.FirstOrDefault(
            file => string.Equals(file.RelativeTemplatePath, normalized, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// 将 dry-run 动作分类枚举转为小写动作文本。
    /// </summary>
    /// <param name="action">动作分类枚举。</param>
    /// <returns>动作文本：new / overwrite / skip。</returns>
    private static string ToActionText(GenerationAction action)
    {
        return action switch
        {
            GenerationAction.New => "new",
            GenerationAction.Overwrite => "overwrite",
            _ => "skip"
        };
    }
}

/// <summary>
/// render_template 应答 DTO，PreviewResult 全量字段透出。
/// </summary>
/// <param name="Success">渲染是否成功。</param>
/// <param name="Output">渲染后全文，仅成功时有值。</param>
/// <param name="ErrorMessage">结构化错误描述（含模板名与行列），仅失败时有值。</param>
/// <param name="ErrorLine">错误行号（从 1 开始），未知时为 null。</param>
/// <param name="ErrorColumn">错误列号（从 1 开始），未知时为 null。</param>
/// <param name="RenderDurationMs">渲染耗时（毫秒）。</param>
/// <param name="IterationWarnings">渲染前静态分析告警（空迭代零迭代信号，文案含守卫模式降噪指引）；
/// 无告警时为 null 且字段不序列化，保证无告警场景回执结构不变。</param>
internal sealed record RenderTemplateResult(
    bool Success,
    string Output,
    string ErrorMessage,
    int? ErrorLine,
    int? ErrorColumn,
    long RenderDurationMs,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? IterationWarnings);

/// <summary>
/// preview_generation 单个文件计划条目 DTO。
/// </summary>
/// <param name="Table">来源表名。</param>
/// <param name="RelativePath">渲染后相对输出根路径（已解析 {{变量}} 占位）。</param>
/// <param name="AbsolutePath">目标文件绝对路径（已通过防目录穿越校验）。</param>
/// <param name="Action">动作分类：new / overwrite / skip。</param>
internal sealed record PreviewFilePlan(string Table, string RelativePath, string AbsolutePath, string Action);

/// <summary>
/// 未映射类型条目 DTO，生成前预检结果。
/// </summary>
/// <param name="DbType">数据库原始类型名。</param>
/// <param name="Table">出现该类型的表名。</param>
/// <param name="Column">出现该类型的列名。</param>
/// <param name="Occurrences">出现次数。</param>
internal sealed record UnmappedTypeItem(string DbType, string Table, string Column, int Occurrences);

/// <summary>
/// 告警条目 DTO，dry-run 预检结果（非阻断，供调用方核对；含输出路径坏路径与模板空迭代）。
/// </summary>
/// <param name="Kind">告警种类：EmptyPackageDir（包段为空）/ ConsecutiveSeparators（连续分隔符）/ EmptyIteration（模板空迭代）/ InvalidJavaPackageSegment（非法 Java 包段）。</param>
/// <param name="Template">首次出现该告警的模板相对路径。</param>
/// <param name="SamplePath">首次出现的样例路径（路径类告警为渲染后路径；空迭代告警为模板输出路径模板原文）。</param>
/// <param name="Occurrences">该告警在本次生成范围内的总出现次数。</param>
/// <param name="Message">人类可读告警说明（中文），含修复指引；空迭代告警含守卫模式降噪指引。</param>
internal sealed record PathWarningItem(string Kind, string Template, string SamplePath, int Occurrences, string Message);

/// <summary>
/// preview_generation 应答 DTO。
/// </summary>
/// <param name="Entries">文件计划条目（表×模板笛卡尔积顺序）。</param>
/// <param name="NewCount">新增条目数。</param>
/// <param name="OverwriteCount">覆盖条目数。</param>
/// <param name="SkipCount">跳过条目数。</param>
/// <param name="UnmappedTypes">未映射类型清单，全部命中时为空列表。</param>
/// <param name="PathWarnings">预检告警清单（EmptyPackageDir 包段为空 / ConsecutiveSeparators 连续分隔符 / EmptyIteration 模板空迭代 / InvalidJavaPackageSegment 非法 Java 包段），无告警时为空列表。</param>
internal sealed record PreviewGenerationResult(
    IReadOnlyList<PreviewFilePlan> Entries,
    int NewCount,
    int OverwriteCount,
    int SkipCount,
    IReadOnlyList<UnmappedTypeItem> UnmappedTypes,
    IReadOnlyList<PathWarningItem> PathWarnings);

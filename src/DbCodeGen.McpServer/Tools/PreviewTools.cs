using System.ComponentModel;
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
    /// </summary>
    /// <param name="package">模板包包名。</param>
    /// <param name="path">模板文件相对包根路径（与 list_templates 返回的 path 一致）。</param>
    /// <param name="table">合成表上下文：原始表名必填，列集合可选。</param>
    /// <param name="column">可选的当前列上下文，供模板内 column 变量消费（列级模板用）。</param>
    /// <param name="basePackageOverride">本次渲染的基础包名覆盖值；为空时使用模板包 manifest 基础包名。</param>
    /// <param name="cancellationToken">取消标记。</param>
    /// <returns>渲染结果全文（PreviewResult 全量字段）。</returns>
    [McpServerTool(Name = "render_template", ReadOnly = true, Idempotent = true)]
    [Description("以合成表上下文渲染单个模板（无需数据库连接）：返回渲染后全文；模板语法错误时返回 errorMessage 与 errorLine/errorColumn 行列定位。")]
    public async Task<object> RenderTemplateAsync(
        string package,
        string path,
        SyntheticTableInput table,
        SyntheticColumnInput? column = null,
        string? basePackageOverride = null,
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

            ArgumentNullException.ThrowIfNull(table);

            TemplatePackageInfo packageInfo = await _packageService.LoadPackageAsync(package.Trim(), cancellationToken);

            // 仅允许渲染包清单声明的模板文件：以清单条目为白名单，杜绝任意文件读取
            TemplateFileInfo? entry = FindFileEntry(packageInfo, path);
            if (entry is null)
            {
                throw new TemplatePackageException($"模板文件不存在：包 {package} 中未声明模板路径 {path}。");
            }

            // 复用桌面端同款读取通道（防目录穿越 + UTF-8 去 BOM），随后与实时预览共用同一渲染管线
            string templateText = await _templateFileWriter.ReadAsync(packageInfo, entry.RelativeTemplatePath, cancellationToken);
            var tableInfo = SyntheticTableMapper.ToTableInfo(table);
            ColumnInfo? columnInfo = column is null ? null : SyntheticTableMapper.ToColumnInfo(column);
            TemplatePackageContext packageContext = TemplatePackageContext.From(packageInfo, basePackageOverride);

            PreviewResult renderResult = _templateEngine.Render(
                templateText, tableInfo, columnInfo, packageContext, cancellationToken, templateName: entry.RelativeTemplatePath);

            _logger.LogInformation(
                "render_template 完成，包 {PackageName}，路径 {TemplatePath}，成功 {IsSuccess}，耗时 {RenderDurationMs}ms。",
                package, entry.RelativeTemplatePath, renderResult.IsSuccess, renderResult.RenderDurationMs);
            return new RenderTemplateResult(
                renderResult.IsSuccess,
                renderResult.Output,
                renderResult.ErrorMessage,
                renderResult.ErrorLine,
                renderResult.ErrorColumn,
                renderResult.RenderDurationMs);
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "render_template 执行失败，包名 {PackageName}，路径 {TemplatePath}。", package, path);
            return ToolError.From(exception, cancellationToken);
        }
    }

    /// <summary>
    /// 构建批量生成 dry-run 文件计划（表 × 模板笛卡尔积渲染后按目标状态分类为 new/overwrite/skip），全程零写盘。
    /// </summary>
    /// <param name="package">模板包包名。</param>
    /// <param name="tables">合成表集合，至少一张表。</param>
    /// <param name="workspaceRoot">工作区根目录绝对路径（输出文件的根前缀，须已存在或可创建）。</param>
    /// <param name="codeDirectory">代码目录（项目内完整相对路径含包名，如 src/main/java/com/example/common）。</param>
    /// <param name="basePackageOverride">本次生成的基础包名覆盖值；为空时使用代码目录派生值。</param>
    /// <param name="files">模板相对路径清单；省略时默认选择包内全部启用条目。</param>
    /// <param name="duplicateStrategy">同名目标文件处理策略：overwrite / skip，影响 overwrite/skip 分类。</param>
    /// <param name="cancellationToken">取消标记。</param>
    /// <returns>文件计划条目与分类计数、未映射类型清单。</returns>
    [McpServerTool(Name = "preview_generation", ReadOnly = true, Idempotent = true)]
    [Description("批量生成 dry-run 预览（零写盘）：返回表×模板笛卡尔积的文件计划（new/overwrite/skip 分类与目标路径）及未映射类型清单，确认后再调 generate。")]
    public async Task<object> PreviewGenerationAsync(
        string package,
        IReadOnlyList<SyntheticTableInput> tables,
        string workspaceRoot,
        string codeDirectory,
        string? basePackageOverride = null,
        IReadOnlyList<string>? files = null,
        string duplicateStrategy = "overwrite",
        CancellationToken cancellationToken = default)
    {
        try
        {
            GenerationRequest request = await GenerationRequestFactory.CreateAsync(
                _packageService, package, tables, files, workspaceRoot, codeDirectory,
                basePackageOverride, duplicateStrategy, persistLastUsedPaths: false, cancellationToken);

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

            _logger.LogInformation(
                "preview_generation 完成，包 {PackageName}，条目 {EntryCount}（new {NewCount}/overwrite {OverwriteCount}/skip {SkipCount}）。",
                package, entries.Count, preview.NewCount, preview.OverwriteCount, preview.SkipCount);
            return new PreviewGenerationResult(
                entries,
                preview.NewCount,
                preview.OverwriteCount,
                preview.SkipCount,
                unmappedTypes);
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
internal sealed record RenderTemplateResult(
    bool Success,
    string Output,
    string ErrorMessage,
    int? ErrorLine,
    int? ErrorColumn,
    long RenderDurationMs);

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
/// preview_generation 应答 DTO。
/// </summary>
/// <param name="Entries">文件计划条目（表×模板笛卡尔积顺序）。</param>
/// <param name="NewCount">新增条目数。</param>
/// <param name="OverwriteCount">覆盖条目数。</param>
/// <param name="SkipCount">跳过条目数。</param>
/// <param name="UnmappedTypes">未映射类型清单，全部命中时为空列表。</param>
internal sealed record PreviewGenerationResult(
    IReadOnlyList<PreviewFilePlan> Entries,
    int NewCount,
    int OverwriteCount,
    int SkipCount,
    IReadOnlyList<UnmappedTypeItem> UnmappedTypes);

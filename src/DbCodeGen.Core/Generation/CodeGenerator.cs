using System.Text;
using DbCodeGen.Core.Config;
using DbCodeGen.Core.DataSource;
using DbCodeGen.Core.Model;
using DbCodeGen.Core.Templates;
using DbCodeGen.Core.Templates.Packages;
using Microsoft.Extensions.Logging;

namespace DbCodeGen.Core.Generation;

/// <summary>
/// 批量代码生成服务实现：表与模板文件笛卡尔积渲染，dry-run 分类，覆盖确认后经文件写盘服务落盘。
/// 安全线：写盘前必走 dry-run 重算，覆盖项必经确认回调，渲染后相对路径做防目录穿越校验。
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
        Func<IReadOnlyList<GenerationFileEntry>, Task<bool>>? confirmOverwriteAsync,
        IProgress<GenerationProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        try
        {
            // 取消、渲染、覆盖确认与写盘任一环节取消都统一转换为取消结果返回
            return await GenerateCoreAsync(request, confirmOverwriteAsync, progress, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 渲染/确认/写盘任一环节取消，返回带取消日志的结果供底栏展示
            return new GenerationResult(0, 0, 0, 0, isCancelled: true, BuildCancelLogs("批量生成已取消。"));
        }
    }

    /// <summary>
    /// 生成写盘核心流程：内部重算 dry-run、覆盖确认、逐文件写盘、统计与日志合并、回写最近输出根。
    /// </summary>
    /// <param name="request">生成请求。</param>
    /// <param name="confirmOverwriteAsync">覆盖确认回调。</param>
    /// <param name="progress">进度推送。</param>
    /// <param name="cancellationToken">取消标记。</param>
    /// <returns>写盘结果统计与生成日志。</returns>
    private async Task<GenerationResult> GenerateCoreAsync(
        GenerationRequest request,
        Func<IReadOnlyList<GenerationFileEntry>, Task<bool>>? confirmOverwriteAsync,
        IProgress<GenerationProgress>? progress,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // 内部先重算 dry-run（同一渲染管线），保证安全线不因调用方绕过预览而失效
        GenerationPreview preview = await BuildPreviewCoreAsync(request, progress, cancellationToken).ConfigureAwait(false);

        // 待写条目为新增与覆盖，跳过条目仅计数不进入写盘
        List<GenerationFileEntry> writeEntries = preview.Entries
            .Where(entry => entry.Action != GenerationAction.Skip)
            .ToList();
        List<GenerationFileEntry> overwriteEntries = writeEntries
            .Where(entry => entry.Action == GenerationAction.Overwrite)
            .ToList();

        if (overwriteEntries.Count > 0)
        {
            // 覆盖动作必经用户确认；未提供回调或用户取消均整单不写任何文件
            if (confirmOverwriteAsync is null)
            {
                return new GenerationResult(0, 0, 0, 0, isCancelled: true, BuildCancelLogs("覆盖确认回调未提供，已整单取消。"));
            }

            bool confirmed = await confirmOverwriteAsync(overwriteEntries).ConfigureAwait(false);
            if (!confirmed)
            {
                return new GenerationResult(0, 0, 0, 0, isCancelled: true, BuildCancelLogs("用户取消覆盖确认，已整单取消。"));
            }
        }

        // 逐文件写盘，单文件失败由写盘服务独立兜底后继续其余文件（部分失败）
        GenerationResult writeResult = await _fileWriter.WriteFilesAsync(writeEntries, progress, cancellationToken).ConfigureAwait(false);

        // 跳过日志与写盘日志合并为最终日志，跳过计数并入最终统计
        List<GenerationLogEntry> mergedLogs = BuildSkipLogs(preview);
        mergedLogs.AddRange(writeResult.Logs);

        if (!writeResult.IsCancelled)
        {
            WriteBackOutputRoot(request.CodeDirectory ?? request.RelativeOutputRoot, mergedLogs);
        }

        return new GenerationResult(
            writeResult.Generated,
            writeResult.Overwritten,
            preview.SkipCount,
            writeResult.Failed,
            writeResult.IsCancelled,
            mergedLogs);
    }

    /// <summary>
    /// 执行 dry-run 渲染与分类核心管线：逐（表 × 勾选模板文件）直读模板、内容渲染、
    /// 输出路径占位渲染、绝对路径解析与防目录穿越、按目标文件状态分类。
    /// </summary>
    /// <param name="request">生成请求。</param>
    /// <param name="progress">进度推送。</param>
    /// <param name="cancellationToken">取消标记。</param>
    /// <returns>dry-run 清单与分类计数。</returns>
    /// <exception cref="OperationCanceledException">渲染或分类过程被取消时抛出。</exception>
    /// <exception cref="GenerationException">模板渲染失败、输出路径模板渲染失败或路径越界时抛出。</exception>
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

        if (tables.Count == 0 || selectedFiles.Count == 0)
        {
            return new GenerationPreview(Array.Empty<GenerationFileEntry>(), 0, 0, 0);
        }

        // 生成前补全表列元数据：表清单阶段不含列，实体与 mapper.xml 模板按列循环渲染，
        // 必须经表详情读取补全列集合，否则实体只剩类壳、insert 语句列清单为空
        tables = await EnrichTableColumnsAsync(request, tables, cancellationToken).ConfigureAwait(false);

        TemplatePackageContext packageContext = TemplatePackageContext.From(request.Package, request.BasePackageOverride);
        var entries = new List<GenerationFileEntry>();
        int total = tables.Count * selectedFiles.Count;
        int completed = 0;

        foreach (TableInfo table in tables)
        {
            foreach (TemplateFileSelection file in selectedFiles)
            {
                cancellationToken.ThrowIfCancellationRequested();
                completed++;

                progress?.Report(new GenerationProgress(GenerationStage.Rendering, completed, total, file.TemplateFileName));

                // 从模板包目录直读模板内容，拾取模板编辑已保存的版本
                string templateText = await ReadTemplateContentAsync(request, file, cancellationToken).ConfigureAwait(false);

                // 内容渲染，与模板编辑预览共用同一渲染管线，失败定位到模板名与行列
                PreviewResult renderResult = RenderContent(templateText, table, packageContext, file, cancellationToken);

                // 输出路径模板占位渲染，失败定位到具体模板文件
                string renderedRelativePath = RenderOutputPath(table, packageContext, file);

                // 解析绝对路径并校验防目录穿越，越界即整单失败
                string absolutePath = ResolveAbsolutePath(request.WorkspaceRoot, request.RelativeOutputRoot, renderedRelativePath);

                progress?.Report(new GenerationProgress(GenerationStage.Previewing, completed, total, renderedRelativePath));

                // dry-run 分类：不存在为新增，存在且内容相同为跳过，否则为覆盖
                GenerationAction action = await ClassifyTargetAsync(absolutePath, renderResult.Output, cancellationToken).ConfigureAwait(false);
                entries.Add(new GenerationFileEntry(table.RawName, renderedRelativePath, absolutePath, action, renderResult.Output, null));
            }
        }

        // 生成前未映射类型预检：全部表列解析链未命中的类型归并清单，供界面弹窗提示补映射
        IReadOnlyList<UnmappedTypeInfo> unmappedTypes = _typeMappingService is null
            ? Array.Empty<UnmappedTypeInfo>()
            : _typeMappingService.FindUnmappedTypes(tables, request.Package.TypeMap);

        int newCount = entries.Count(entry => entry.Action == GenerationAction.New);
        int overwriteCount = entries.Count(entry => entry.Action == GenerationAction.Overwrite);
        int skipCount = entries.Count(entry => entry.Action == GenerationAction.Skip);
        return new GenerationPreview(entries, newCount, overwriteCount, skipCount, unmappedTypes);
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
    /// <returns>渲染结果，成功时含真实代码。</returns>
    /// <exception cref="GenerationException">模板渲染失败时抛出。</exception>
    private PreviewResult RenderContent(
        string templateText,
        TableInfo table,
        TemplatePackageContext packageContext,
        TemplateFileSelection file,
        CancellationToken cancellationToken)
    {
        PreviewResult result = _engine.Render(templateText, table, null, packageContext, cancellationToken, file.TemplateFileName);
        if (!result.IsSuccess)
        {
            throw new GenerationException($"模板渲染失败：{result.ErrorMessage}");
        }

        return result;
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
    /// 将工作区根、相对输出根与渲染后相对路径拼接为绝对路径，并做防目录穿越前缀校验。
    /// </summary>
    /// <param name="workspaceRoot">工作区根。</param>
    /// <param name="relativeOutputRoot">相对输出根。</param>
    /// <param name="renderedRelativePath">渲染后的相对输出路径。</param>
    /// <returns>校验通过的目标绝对路径。</returns>
    /// <exception cref="GenerationException">路径越出输出根目录时抛出（目录穿越）。</exception>
    private static string ResolveAbsolutePath(string workspaceRoot, string relativeOutputRoot, string renderedRelativePath)
    {
        // 工作区根与相对输出根拼接为本次输出的根目录
        string outputRoot = Path.GetFullPath(Path.Combine(workspaceRoot, relativeOutputRoot.Replace('/', Path.DirectorySeparatorChar)));

        // 渲染后相对路径解析到输出根内，规范化后做前缀校验防目录穿越
        string candidate = Path.GetFullPath(Path.Combine(outputRoot, renderedRelativePath.Replace('/', Path.DirectorySeparatorChar)));
        string prefix = outputRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new GenerationException($"输出路径越出工作区根，已拒绝（目录穿越）：{renderedRelativePath}");
        }

        return candidate;
    }

    /// <summary>
    /// 按目标文件状态做 dry-run 分类：不存在为新增，存在且内容相同为跳过，否则为覆盖。
    /// </summary>
    /// <param name="absolutePath">目标文件绝对路径。</param>
    /// <param name="content">渲染后的文件内容。</param>
    /// <param name="cancellationToken">取消标记。</param>
    /// <returns>分类动作。</returns>
    private static async Task<GenerationAction> ClassifyTargetAsync(string absolutePath, string content, CancellationToken cancellationToken)
    {
        if (!File.Exists(absolutePath))
        {
            return GenerationAction.New;
        }

        // 读取既有文件内容（UTF-8 去 BOM），与渲染内容做行尾归一化后比较，避免 CRLF/LF 差异误判为覆盖
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
    /// 生成跳过条目的日志，供最终日志合并展示。
    /// </summary>
    /// <param name="preview">dry-run 清单。</param>
    /// <returns>跳过条目日志列表。</returns>
    private static List<GenerationLogEntry> BuildSkipLogs(GenerationPreview preview)
    {
        var logs = new List<GenerationLogEntry>();
        foreach (GenerationFileEntry entry in preview.Entries)
        {
            if (entry.Action == GenerationAction.Skip)
            {
                logs.Add(GenerationLogEntry.Info($"已跳过（内容相同）：{entry.RelativePath}"));
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
    /// 生成完成后回写最近代码目录并保存，回写失败仅记录警告日志，不阻断结果返回。
    /// </summary>
    /// <param name="codeDirectory">本次生成的代码目录，作为下次生成的默认值。</param>
    /// <param name="logs">最终日志列表，回写失败时追加警告条目。</param>
    private void WriteBackOutputRoot(string codeDirectory, List<GenerationLogEntry> logs)
    {
        try
        {
            // 回写最近代码目录，供下次生成作为默认值
            _configService.Current.LastRelativeOutputRoot = codeDirectory;
            _configService.Save();
        }
        catch (ConfigSaveException exception)
        {
            logs.Add(GenerationLogEntry.Warning($"最近代码目录回写失败：{exception.Message}"));
            _logger.LogWarning(exception, "最近代码目录回写失败。");
        }
    }
}

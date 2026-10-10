using DbCodeGen.Core.Generation;
using DbCodeGen.Core.Model;
using DbCodeGen.Core.Templates.Packages;

namespace DbCodeGen.McpServer.Tools;

/// <summary>
/// 批量生成请求组装工厂：preview_generation 与 generate 共用的请求构造通道，
/// 保证两个阶段"同一渲染管线、同一入参口径"。组装语义与桌面端 GenerationViewModel.BuildRequest 一致：
/// 代码目录经 CodeDirectoryParser 拆为基础包名与相对输出根，模板文件按清单勾选；差异仅两点——
/// MCP 侧表元数据由调用方显式传入（不连接数据源补列）、记忆回写按调用方开关显式关闭（零记忆态副作用）。
/// </summary>
internal static class GenerationRequestFactory
{
    /// <summary>
    /// 组装批量生成请求：校验并解析全部显式入参，加载模板包、按清单解析文件选择、
    /// 由代码目录推导相对输出根与基础包名，构造 Core 生成请求。
    /// </summary>
    /// <param name="packageService">模板包服务，承载包加载与校验。</param>
    /// <param name="package">模板包包名。</param>
    /// <param name="tables">合成表输入集合，至少一张表。</param>
    /// <param name="files">模板相对路径清单；null 或空清单时默认选择包内全部启用条目。</param>
    /// <param name="workspaceRoot">工作区根目录绝对路径。</param>
    /// <param name="codeDirectory">代码目录（项目内完整相对路径含包名，如 src/main/java/com/example/common）。</param>
    /// <param name="basePackageOverride">本次生成的基础包名覆盖值；为空时使用代码目录派生值。</param>
    /// <param name="duplicateStrategy">同名目标文件处理策略文本：overwrite / skip。</param>
    /// <param name="persistLastUsedPaths">生成成功后是否回写最近生成路径记忆；MCP 调用方应传 false。</param>
    /// <param name="cancellationToken">取消标记。</param>
    /// <returns>可直接提交 Core 生成服务的请求。</returns>
    /// <exception cref="ArgumentException">任一必填入参为空、策略文本非法或文件清单含包内不存在的路径时抛出。</exception>
    public static async Task<GenerationRequest> CreateAsync(
        ITemplatePackageService packageService,
        string package,
        IReadOnlyList<SyntheticTableInput> tables,
        IReadOnlyList<string>? files,
        string workspaceRoot,
        string codeDirectory,
        string? basePackageOverride,
        string duplicateStrategy,
        bool persistLastUsedPaths,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(packageService);
        ArgumentNullException.ThrowIfNull(tables);
        if (string.IsNullOrWhiteSpace(package))
        {
            throw new ArgumentException("模板包名（package）不能为空。");
        }

        if (tables.Count == 0)
        {
            throw new ArgumentException("请至少提供一张表（tables）。");
        }

        if (string.IsNullOrWhiteSpace(workspaceRoot))
        {
            throw new ArgumentException("工作区根目录（workspaceRoot）不能为空。");
        }

        if (string.IsNullOrWhiteSpace(codeDirectory))
        {
            throw new ArgumentException("代码目录（codeDirectory）不能为空。");
        }

        // 同名文件策略先解析，非法取值直接结构化拒绝并列出合法值
        DuplicateFileStrategy strategy = ParseDuplicateStrategy(duplicateStrategy);

        TemplatePackageInfo packageInfo = await packageService.LoadPackageAsync(package.Trim(), cancellationToken);
        IReadOnlyList<TemplateFileSelection> selections = BuildSelections(packageInfo, files);

        // 代码目录拆分为相对输出根与基础包名（与桌面端同一解析器），显式覆盖值优先于派生值
        (string derivedBasePackage, string relativeOutputRoot) = CodeDirectoryParser.Split(codeDirectory);
        string? effectiveBasePackage = string.IsNullOrWhiteSpace(basePackageOverride)
            ? derivedBasePackage
            : basePackageOverride.Trim();

        List<TableInfo> tableInfos = new(tables.Count);
        foreach (SyntheticTableInput tableInput in tables)
        {
            tableInfos.Add(SyntheticTableMapper.ToTableInfo(tableInput));
        }

        // MCP 侧零记忆态：数据源不注入（表元数据由调用方显式传入，Core 对含列表原样使用），回写开关由调用方显式指定
        return new GenerationRequest(
            packageInfo,
            tableInfos,
            selections,
            workspaceRoot.Trim(),
            relativeOutputRoot,
            effectiveBasePackage,
            dataSource: null,
            codeDirectory.Trim(),
            strategy,
            persistLastUsedPaths);
    }

    /// <summary>
    /// 解析同名文件处理策略文本；大小写不敏感，非法取值抛参数异常并列出合法值。
    /// </summary>
    /// <param name="duplicateStrategy">策略文本。</param>
    /// <returns>同名文件处理策略枚举。</returns>
    private static DuplicateFileStrategy ParseDuplicateStrategy(string duplicateStrategy)
    {
        if (string.IsNullOrWhiteSpace(duplicateStrategy))
        {
            throw new ArgumentException("同名文件处理策略（duplicateStrategy）不能为空；合法取值为 overwrite / skip。");
        }

        return duplicateStrategy.Trim().ToLowerInvariant() switch
        {
            "overwrite" => DuplicateFileStrategy.Overwrite,
            "skip" => DuplicateFileStrategy.Skip,
            _ => throw new ArgumentException($"无效的同名文件处理策略：{duplicateStrategy}；合法取值为 overwrite / skip。")
        };
    }

    /// <summary>
    /// 按文件清单构建模板文件选择集合：清单为空时默认选择包内全部启用条目（与桌面端清单默认态一致）；
    /// 非空时逐条按清单白名单匹配，包内不存在的路径直接拒绝并列出提示。
    /// </summary>
    /// <param name="packageInfo">已加载的模板包运行时信息。</param>
    /// <param name="files">调用方传入的模板相对路径清单，可空。</param>
    /// <returns>参与生成的模板文件选择集合。</returns>
    /// <exception cref="ArgumentException">清单含空白路径或包内不存在的模板路径时抛出。</exception>
    private static IReadOnlyList<TemplateFileSelection> BuildSelections(
        TemplatePackageInfo packageInfo,
        IReadOnlyList<string>? files)
    {
        if (files is null || files.Count == 0)
        {
            List<TemplateFileInfo> enabledFiles = packageInfo.Files.Where(file => file.IsEnabled).ToList();
            List<TemplateFileSelection> defaultSelections = new(enabledFiles.Count);
            foreach (TemplateFileInfo file in enabledFiles)
            {
                defaultSelections.Add(new TemplateFileSelection(file.RelativeTemplatePath, file.OutputPath, isSelected: true));
            }

            if (defaultSelections.Count == 0)
            {
                throw new ArgumentException($"模板包 {packageInfo.Name} 内没有启用状态的模板文件，请显式传入 files 清单或先启用模板。");
            }

            return defaultSelections;
        }

        List<TemplateFileSelection> selections = new(files.Count);
        foreach (string? rawPath in files)
        {
            if (string.IsNullOrWhiteSpace(rawPath))
            {
                throw new ArgumentException("文件清单（files）含空白模板路径。");
            }

            // 调用方可能传反斜杠形态，统一正斜杠后与清单条目忽略大小写比对
            string normalized = rawPath.Trim().Replace('\\', '/');
            TemplateFileInfo? entry = packageInfo.Files.FirstOrDefault(
                file => string.Equals(file.RelativeTemplatePath, normalized, StringComparison.OrdinalIgnoreCase));
            if (entry is null)
            {
                throw new ArgumentException(
                    $"模板文件不存在：包 {packageInfo.Name} 中未声明模板路径 {normalized}；请用 list_templates 查看包内全部路径。");
            }

            selections.Add(new TemplateFileSelection(entry.RelativeTemplatePath, entry.OutputPath, isSelected: true));
        }

        return selections;
    }
}

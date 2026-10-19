using System.Text.Json;
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
    /// tables 按生效模板集合分型校验：仅当所选模板（files 过滤后）含 table 作用域条目时要求至少一张表，
    /// 纯 module/project 作用域集合允许省略 tables。
    /// </summary>
    /// <param name="packageService">模板包服务，承载包加载与校验。</param>
    /// <param name="package">模板包包名。</param>
    /// <param name="tables">合成表输入集合；所选模板含 table 作用域条目时至少一张，纯 module/project 作用域集合可为 null。</param>
    /// <param name="files">模板相对路径清单；null 或空清单时默认选择包内全部启用条目。</param>
    /// <param name="workspaceRoot">工作区根目录绝对路径。</param>
    /// <param name="codeDirectory">代码目录（项目内完整相对路径含包名，如 src/main/java/com/example/common）。</param>
    /// <param name="basePackageOverride">本次生成的基础包名覆盖值；为空时使用代码目录派生值。</param>
    /// <param name="duplicateStrategy">同名目标文件处理策略文本：overwrite / skip。</param>
    /// <param name="persistLastUsedPaths">生成成功后是否回写最近生成路径记忆；MCP 调用方应传 false。</param>
    /// <param name="variables">调用方注入变量对象，逐键注入渲染上下文 vars.* 命名空间；值支持任意 JSON 类型，可为省略。</param>
    /// <param name="modules">模块清单（如 [{"name":"account"}]），module 作用域模板按此逐个渲染；可省略。</param>
    /// <param name="cancellationToken">取消标记。</param>
    /// <param name="webDirectory">web 目录（相对 workspaceRoot，缺省 web），root=web 的模板输出以此为基准；非法值结构化拒绝。</param>
    /// <param name="includePlatformTables">是否显式允许生成平台表（缺省 false 表示命中平台表规则时排除）；不影响排除表规则。</param>
    /// <returns>可直接提交 Core 生成服务的请求。</returns>
    /// <exception cref="ArgumentException">任一必填入参为空、策略文本非法、文件清单含包内不存在的路径、所选模板含 table 作用域条目但未提供表、variables/modules 元素结构非法或 webDirectory 非法时抛出。</exception>
    public static async Task<GenerationRequest> CreateAsync(
        ITemplatePackageService packageService,
        string package,
        IReadOnlyList<SyntheticTableInput>? tables,
        IReadOnlyList<string>? files,
        string workspaceRoot,
        string codeDirectory,
        string? basePackageOverride,
        string duplicateStrategy,
        bool persistLastUsedPaths,
        IReadOnlyDictionary<string, JsonElement>? variables,
        IReadOnlyList<Dictionary<string, object?>>? modules,
        CancellationToken cancellationToken,
        string? webDirectory = null,
        bool includePlatformTables = false)
    {
        ArgumentNullException.ThrowIfNull(packageService);
        if (string.IsNullOrWhiteSpace(package))
        {
            throw new ArgumentException("模板包名（package）不能为空。");
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

        // 变量与模块清单解析：键空白、模块缺 name、模块名重复在此结构化拒绝（保留命名空间冲突由 Core 作用域输入校验）
        IReadOnlyDictionary<string, object?>? parsedVariables = ParseVariables(variables);
        IReadOnlyList<ModuleContext>? parsedModules = ParseModules(modules);

        TemplatePackageInfo packageInfo = await packageService.LoadPackageAsync(package.Trim(), cancellationToken);
        IReadOnlyList<TemplateFileSelection> selections = BuildSelections(packageInfo, files);

        // tables 非空要求按生效模板集合分型：仅当所选模板（files 过滤后）含 table 作用域条目时要求至少一张表，
        // 纯 module/project 作用域集合（模板不消费表）允许省略 tables
        IReadOnlyList<SyntheticTableInput> providedTables = tables ?? Array.Empty<SyntheticTableInput>();
        if (HasTableScopeSelection(selections) && providedTables.Count == 0)
        {
            throw new ArgumentException(
                "本次所选模板包含 table 作用域条目（按表集逐表渲染），必须提供至少一张合成表（tables，tables[].name 必填）；"
                + "仅当所选模板全部为 module/project 作用域时才可省略 tables。请补充 tables 后重试，或先经 list_templates 核对所选模板作用域。");
        }

        // 代码目录拆分为相对输出根与基础包名（与桌面端同一解析器），显式覆盖值优先于派生值
        (string derivedBasePackage, string relativeOutputRoot) = CodeDirectoryParser.Split(codeDirectory);
        string? effectiveBasePackage = string.IsNullOrWhiteSpace(basePackageOverride)
            ? derivedBasePackage
            : basePackageOverride.Trim();

        List<TableInfo> tableInfos = new(providedTables.Count);
        foreach (SyntheticTableInput tableInput in providedTables)
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
            persistLastUsedPaths,
            parsedVariables,
            parsedModules,
            webDirectory,
            includePlatformTables);
    }

    /// <summary>
    /// 解析调用方变量对象：拒绝空白键，逐键保留 JsonElement 原值（值经 Core 渲染侧 Scriban 值转换后供模板消费，
    /// JsonElement 按数组/对象/布尔/数值/字符串类别递归转换，天然支持非字符串类型）。
    /// </summary>
    /// <param name="variables">调用方传入的变量对象，可空。</param>
    /// <returns>归一化后的变量字典；入参为空或空对象时返回 null。</returns>
    /// <exception cref="ArgumentException">变量对象含空白键时抛出。</exception>
    internal static IReadOnlyDictionary<string, object?>? ParseVariables(IReadOnlyDictionary<string, JsonElement>? variables)
    {
        if (variables is null || variables.Count == 0)
        {
            return null;
        }

        var parsed = new Dictionary<string, object?>(variables.Count);
        foreach (KeyValuePair<string, JsonElement> pair in variables)
        {
            if (string.IsNullOrWhiteSpace(pair.Key))
            {
                throw new ArgumentException("变量对象（variables）含空白键。");
            }

            parsed[pair.Key.Trim()] = pair.Value;
        }

        return parsed;
    }

    /// <summary>
    /// 解析模块清单：每个元素 name 必填且非空字符串、模块名不得重复；name 之外的键值原样注入 module.*。
    /// </summary>
    /// <param name="modules">调用方传入的模块清单，可空。</param>
    /// <returns>模块上下文清单；入参为空或空清单时返回 null。</returns>
    /// <exception cref="ArgumentException">清单含空元素、元素缺 name、name 非字符串或模块名重复时抛出。</exception>
    internal static IReadOnlyList<ModuleContext>? ParseModules(IReadOnlyList<Dictionary<string, object?>>? modules)
    {
        if (modules is null || modules.Count == 0)
        {
            return null;
        }

        List<ModuleContext> parsed = new(modules.Count);
        HashSet<string> seenNames = new(StringComparer.Ordinal);
        foreach (Dictionary<string, object?>? element in modules)
        {
            if (element is null)
            {
                throw new ArgumentException("模块清单（modules）含空元素。");
            }

            // MCP 绑定下 object 值落地为 JsonElement：name 兼容 string 与 JSON 字符串元素两种形态
            if (!element.TryGetValue("name", out object? nameValue))
            {
                throw new ArgumentException("模块清单（modules）每个元素必须包含非空字符串 name 字段（如 {\"name\":\"account\"}）。");
            }

            string? name = nameValue switch
            {
                string text => text,
                JsonElement { ValueKind: JsonValueKind.String } jsonName => jsonName.GetString(),
                _ => null
            };

            if (string.IsNullOrWhiteSpace(name))
            {
                throw new ArgumentException("模块清单（modules）每个元素必须包含非空字符串 name 字段（如 {\"name\":\"account\"}）。");
            }

            if (!seenNames.Add(name.Trim()))
            {
                throw new ArgumentException($"模块清单（modules）存在重复模块名：{name.Trim()}。");
            }

            var properties = new Dictionary<string, object?>();
            foreach (KeyValuePair<string, object?> pair in element)
            {
                if (pair.Key != "name")
                {
                    properties[pair.Key] = pair.Value;
                }
            }

            parsed.Add(new ModuleContext(name, properties));
        }

        return parsed;
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
    /// 判定所选模板集合是否包含 table 作用域条目：缺省 scope 声明的条目按 table 作用域计
    /// （枚举缺省值即 table，与生成编排分组口径一致）。
    /// </summary>
    /// <param name="selections">files 过滤后的生效模板选择集合，非空。</param>
    /// <returns>含 table 作用域条目时为 true。</returns>
    private static bool HasTableScopeSelection(IReadOnlyList<TemplateFileSelection> selections)
    {
        foreach (TemplateFileSelection selection in selections)
        {
            if (selection.Scope == TemplateScope.Table)
            {
                return true;
            }
        }

        return false;
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
                defaultSelections.Add(new TemplateFileSelection(
                    file.RelativeTemplatePath, file.OutputPath, isSelected: true, file.Scope, file.OutputRoot, file.Role));
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

            selections.Add(new TemplateFileSelection(
                entry.RelativeTemplatePath, entry.OutputPath, isSelected: true, entry.Scope, entry.OutputRoot, entry.Role));
        }

        return selections;
    }
}

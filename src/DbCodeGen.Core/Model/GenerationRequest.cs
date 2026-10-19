using DbCodeGen.Core.Generation;
using DbCodeGen.Core.Templates.Packages;

namespace DbCodeGen.Core.Model;

/// <summary>
/// 一次批量代码生成的完整输入，承载勾选表集合、勾选模板文件集合、所属模板包、输出根路径与同名文件处理策略。
/// 工作区根与相对输出根默认取设置与配置的最近值，允许本次生成时临时修改。
/// </summary>
public sealed class GenerationRequest
{
    /// <summary>
    /// 使用完整字段构造一次生成请求。
    /// </summary>
    /// <param name="package">勾选模板文件所属的模板包运行时信息，渲染直读磁盘与类型映射都依赖它。</param>
    /// <param name="tables">勾选表集合，来源表浏览与选择。</param>
    /// <param name="selectedFiles">勾选模板文件集合，来源模板包管理勾选到层。</param>
    /// <param name="workspaceRoot">工作区根，绝对输出路径的根前缀，可本次修改。</param>
    /// <param name="relativeOutputRoot">相对输出根，可本次修改。</param>
    /// <param name="basePackageOverride">本次生成的基础包名覆盖值（如 com.example.common），可为空；为空时使用模板包 manifest 基础包名。</param>
    /// <param name="dataSource">当前数据源配置，用于生成前补全表列元数据；可为空，为空时表按传入元数据原样使用。</param>
    /// <param name="codeDirectory">本次生成的代码目录（项目内完整相对路径含包名，如 src/main/java/com/example/common），生成完成后写回为最近记忆；可为空。</param>
    /// <param name="duplicateFileStrategy">同名目标文件处理策略，默认覆盖；dry-run 分类与写盘按此策略执行。</param>
    /// <param name="persistLastUsedPaths">生成成功后是否回写最近生成路径记忆（工作区根与代码目录写回配置并落盘），默认 true 保持桌面端既有行为；MCP 等无记忆态调用方显式置 false 实现零配置副作用。</param>
    /// <param name="variables">本次生成的调用方变量集合，逐键注入渲染上下文 vars.* 命名空间；可为空，键与保留命名空间冲突在作用域输入构造时结构化拒绝。</param>
    /// <param name="modules">本次生成的模块清单，module 作用域模板按此清单逐个渲染；可为空，勾选 module 作用域模板但未提供时结构化报错。</param>
    /// <param name="webDirectory">web 目录（相对 workspaceRoot，缺省 web），root=web 的模板输出以此为基准；非法值结构化拒绝。</param>
    /// <param name="includePlatformTables">是否显式允许生成平台表（缺省 false 表示命中平台表规则时排除）；不影响排除表规则。</param>
    /// <exception cref="ArgumentNullException">package、tables 或 selectedFiles 为 null 时抛出。</exception>
    /// <exception cref="ArgumentException">webDirectory 非法时抛出。</exception>
    public GenerationRequest(
        TemplatePackageInfo package,
        IReadOnlyList<TableInfo> tables,
        IReadOnlyList<TemplateFileSelection> selectedFiles,
        string workspaceRoot,
        string relativeOutputRoot,
        string? basePackageOverride = null,
        DataSourceConfig? dataSource = null,
        string? codeDirectory = null,
        DuplicateFileStrategy duplicateFileStrategy = DuplicateFileStrategy.Overwrite,
        bool persistLastUsedPaths = true,
        IReadOnlyDictionary<string, object?>? variables = null,
        IReadOnlyList<ModuleContext>? modules = null,
        string? webDirectory = null,
        bool includePlatformTables = false)
    {
        ArgumentNullException.ThrowIfNull(package);
        ArgumentNullException.ThrowIfNull(tables);
        ArgumentNullException.ThrowIfNull(selectedFiles);
        Package = package;
        Tables = tables;
        SelectedFiles = selectedFiles;
        WorkspaceRoot = workspaceRoot;
        RelativeOutputRoot = relativeOutputRoot;
        BasePackageOverride = basePackageOverride;
        DataSource = dataSource;
        CodeDirectory = codeDirectory;
        DuplicateFileStrategy = duplicateFileStrategy;
        PersistLastUsedPaths = persistLastUsedPaths;
        Variables = variables;
        Modules = modules;
        WebDirectory = WebDirectoryResolver.Normalize(webDirectory);
        IncludePlatformTables = includePlatformTables;
    }

    /// <summary>
    /// 勾选模板文件所属的模板包运行时信息，提供包根目录、类型映射与包名等渲染所需上下文。
    /// </summary>
    public TemplatePackageInfo Package { get; }

    /// <summary>
    /// 勾选表集合，来源表浏览与选择。
    /// </summary>
    public IReadOnlyList<TableInfo> Tables { get; }

    /// <summary>
    /// 勾选模板文件集合，来源模板包管理勾选到层。
    /// </summary>
    public IReadOnlyList<TemplateFileSelection> SelectedFiles { get; }

    /// <summary>
    /// 工作区根，绝对输出路径的根前缀。
    /// </summary>
    public string WorkspaceRoot { get; }

    /// <summary>
    /// 相对输出根，与工作区根拼接后作为本次输出的根目录。
    /// </summary>
    public string RelativeOutputRoot { get; }

    /// <summary>
    /// 本次生成的基础包名覆盖值，可为空；为空时使用模板包 manifest 基础包名，供渲染上下文派生 package.dir。
    /// </summary>
    public string? BasePackageOverride { get; }

    /// <summary>
    /// 当前数据源配置，供生成服务补全表列元数据；可为空，为空时表按传入元数据原样使用。
    /// </summary>
    public DataSourceConfig? DataSource { get; }

    /// <summary>
    /// 本次生成的代码目录（项目内完整相对路径含包名，如 src/main/java/com/example/common），生成完成后写回为最近记忆；可为空。
    /// </summary>
    public string? CodeDirectory { get; }

    /// <summary>
    /// 同名目标文件处理策略：覆盖则替换内容不同的既有文件，跳过则只写新增文件。
    /// </summary>
    public DuplicateFileStrategy DuplicateFileStrategy { get; }

    /// <summary>
    /// 生成成功后是否回写最近生成路径记忆（工作区根与代码目录写回配置并落盘）。
    /// 默认 true 保持桌面端"生成后记忆下次默认值"的既有行为；MCP 等无记忆态调用方置 false，
    /// 生成全程不触碰 config.json 的最近路径字段。
    /// </summary>
    public bool PersistLastUsedPaths { get; }

    /// <summary>
    /// 本次生成的调用方变量集合，逐键注入渲染上下文 vars.* 命名空间；可为空，为空时不注入 vars。
    /// </summary>
    public IReadOnlyDictionary<string, object?>? Variables { get; }

    /// <summary>
    /// 本次生成的模块清单，module 作用域模板按此清单逐个渲染；可为空。
    /// 勾选 module 作用域模板但未提供清单时，生成编排结构化报错。
    /// </summary>
    public IReadOnlyList<ModuleContext>? Modules { get; }

    /// <summary>
    /// web 目录（相对 workspaceRoot，正斜杠规范化），root=web 的模板输出以此为基准；缺省 web。
    /// </summary>
    public string WebDirectory { get; }

    /// <summary>
    /// 是否显式允许生成平台表：false 时命中平台表规则的表中止生成并在 dry-run 回执中列出；
    /// true 时平台表参与生成（回执仍标注命中）。排除表规则不受本开关影响。
    /// </summary>
    public bool IncludePlatformTables { get; }
}

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
    /// <exception cref="ArgumentNullException">package、tables 或 selectedFiles 为 null 时抛出。</exception>
    public GenerationRequest(
        TemplatePackageInfo package,
        IReadOnlyList<TableInfo> tables,
        IReadOnlyList<TemplateFileSelection> selectedFiles,
        string workspaceRoot,
        string relativeOutputRoot,
        string? basePackageOverride = null,
        DataSourceConfig? dataSource = null,
        string? codeDirectory = null,
        DuplicateFileStrategy duplicateFileStrategy = DuplicateFileStrategy.Overwrite)
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
}

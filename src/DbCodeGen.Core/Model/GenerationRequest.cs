using DbCodeGen.Core.Templates.Packages;

namespace DbCodeGen.Core.Model;

/// <summary>
/// 一次批量代码生成的完整输入，承载勾选表集合、勾选模板文件集合、所属模板包与输出根路径。
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
    /// <exception cref="ArgumentNullException">package、tables 或 selectedFiles 为 null 时抛出。</exception>
    public GenerationRequest(
        TemplatePackageInfo package,
        IReadOnlyList<TableInfo> tables,
        IReadOnlyList<TemplateFileSelection> selectedFiles,
        string workspaceRoot,
        string relativeOutputRoot)
    {
        ArgumentNullException.ThrowIfNull(package);
        ArgumentNullException.ThrowIfNull(tables);
        ArgumentNullException.ThrowIfNull(selectedFiles);
        Package = package;
        Tables = tables;
        SelectedFiles = selectedFiles;
        WorkspaceRoot = workspaceRoot;
        RelativeOutputRoot = relativeOutputRoot;
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
}

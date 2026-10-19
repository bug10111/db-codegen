using DbCodeGen.Core.Templates.Packages;

namespace DbCodeGen.Core.Model;

/// <summary>
/// 勾选参与批量生成的模板文件选择项，对应模板包 manifest files[] 条目的勾选态。
/// 模板正文不在本实体承载，渲染时经 Core.Templates.TemplateFileWriter.ReadAsync 从模板包目录直读磁盘，
/// 因此模板编辑与预览已保存的版本必然被批量生成拾取。
/// </summary>
public sealed class TemplateFileSelection
{
    /// <summary>
    /// 使用模板相对文件名、输出路径模板、勾选态、作用域、输出根与产出角色构造选择项
    /// （缺省表作用域 + 输出根 + 无角色声明，既有调用行为不变）。
    /// </summary>
    /// <param name="templateFileName">模板包内相对文件名，对应 manifest files[].template。</param>
    /// <param name="outputPathTemplate">manifest files[].output 声明的相对输出路径模板，支持 {{变量}} 占位。</param>
    /// <param name="isSelected">是否勾选参与生成（勾选到层）。</param>
    /// <param name="scope">模板作用域，缺省 table（既有包行为不变）。</param>
    /// <param name="outputRoot">输出根语义，缺省 null（未声明，按作用域沿用既有语义：table/module=相对输出根，project=工作区根）。</param>
    /// <param name="role">产出角色声明，缺省 null（由生成管线按输出路径推断）。</param>
    /// <exception cref="ArgumentNullException">templateFileName 或 outputPathTemplate 为 null 时抛出。</exception>
    public TemplateFileSelection(
        string templateFileName,
        string outputPathTemplate,
        bool isSelected,
        TemplateScope scope = TemplateScope.Table,
        TemplateOutputRoot? outputRoot = null,
        string? role = null)
    {
        ArgumentNullException.ThrowIfNull(templateFileName);
        ArgumentNullException.ThrowIfNull(outputPathTemplate);
        TemplateFileName = templateFileName;
        OutputPathTemplate = outputPathTemplate;
        IsSelected = isSelected;
        Scope = scope;
        OutputRoot = outputRoot;
        Role = role;
    }

    /// <summary>
    /// 模板包内相对文件名（如 entity.java.scriban），对应 manifest files[].template。
    /// </summary>
    public string TemplateFileName { get; }

    /// <summary>
    /// manifest files[].output 声明的相对输出路径模板，支持 {{变量}} 占位。
    /// </summary>
    public string OutputPathTemplate { get; }

    /// <summary>
    /// 是否勾选参与生成（勾选到层），false 条目不进入渲染与写盘。
    /// </summary>
    public bool IsSelected { get; }

    /// <summary>
    /// 模板作用域：table（缺省）/ module / project，生成编排按作用域分组调度。
    /// </summary>
    public TemplateScope Scope { get; }

    /// <summary>
    /// 输出根语义：null 表示未声明（按作用域沿用既有语义）；非空时按 output/workspace/web 解析。
    /// </summary>
    public TemplateOutputRoot? OutputRoot { get; }

    /// <summary>
    /// 产出角色声明（如 backendView / webPage），缺省 null 时由生成管线按输出路径推断。
    /// </summary>
    public string? Role { get; }
}

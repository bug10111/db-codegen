namespace DbCodeGen.Core.Model;

/// <summary>
/// 勾选参与批量生成的模板文件选择项，对应模板包 manifest files[] 条目的勾选态。
/// 模板正文不在本实体承载，渲染时经 Core.Templates.TemplateFileWriter.ReadAsync 从模板包目录直读磁盘，
/// 因此模板编辑与预览已保存的版本必然被批量生成拾取。
/// </summary>
public sealed class TemplateFileSelection
{
    /// <summary>
    /// 使用模板相对文件名、输出路径模板与勾选态构造选择项。
    /// </summary>
    /// <param name="templateFileName">模板包内相对文件名，对应 manifest files[].template。</param>
    /// <param name="outputPathTemplate">manifest files[].output 声明的相对输出路径模板，支持 {{变量}} 占位。</param>
    /// <param name="isSelected">是否勾选参与生成（勾选到层）。</param>
    /// <exception cref="ArgumentNullException">templateFileName 或 outputPathTemplate 为 null 时抛出。</exception>
    public TemplateFileSelection(string templateFileName, string outputPathTemplate, bool isSelected)
    {
        ArgumentNullException.ThrowIfNull(templateFileName);
        ArgumentNullException.ThrowIfNull(outputPathTemplate);
        TemplateFileName = templateFileName;
        OutputPathTemplate = outputPathTemplate;
        IsSelected = isSelected;
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
}

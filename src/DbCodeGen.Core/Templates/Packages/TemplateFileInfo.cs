namespace DbCodeGen.Core.Templates.Packages;

/// <summary>
/// 包内模板文件的运行时信息，含相对路径、绝对路径、输出相对路径与默认勾选态。
/// </summary>
public sealed class TemplateFileInfo
{
    /// <summary>
    /// 模板文件相对包根的路径（正斜杠规范化，与 manifest files[].template 对应）。
    /// </summary>
    public string RelativeTemplatePath { get; set; } = string.Empty;

    /// <summary>
    /// 模板文件绝对路径，读取模板文本时使用。
    /// </summary>
    public string TemplatePath { get; set; } = string.Empty;

    /// <summary>
    /// manifest 声明的输出相对路径，支持 {{变量}} 占位，批量生成阶段解析。
    /// </summary>
    public string OutputPath { get; set; } = string.Empty;

    /// <summary>
    /// 是否默认勾选参与生成（勾选到层默认态）。
    /// </summary>
    public bool IsEnabled { get; set; }

    /// <summary>
    /// 模板文本，懒加载：默认 null，由模板读取方按需从磁盘加载后填充。
    /// </summary>
    public string? Content { get; set; }
}

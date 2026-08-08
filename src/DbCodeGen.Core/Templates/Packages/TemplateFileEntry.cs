namespace DbCodeGen.Core.Templates.Packages;

/// <summary>
/// 模板包 manifest files[] 条目，声明单个模板文件相对包根路径、输出相对路径与默认勾选态。
/// </summary>
public sealed class TemplateFileEntry
{
    /// <summary>
    /// 模板文件相对包根路径，禁止绝对路径与 .. 段（防目录穿越）。
    /// </summary>
    public string Template { get; set; } = string.Empty;

    /// <summary>
    /// 输出相对路径，支持 {{变量}} 占位（渲染阶段解析），同样禁止目录穿越。
    /// </summary>
    public string Output { get; set; } = string.Empty;

    /// <summary>
    /// 是否默认勾选参与生成（勾选到层默认态），缺省为 true。
    /// </summary>
    public bool Enabled { get; set; } = true;
}

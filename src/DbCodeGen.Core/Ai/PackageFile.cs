namespace DbCodeGen.Core.Ai;

/// <summary>
/// AI 生成模板包文件条目，声明模板相对路径、输出相对路径、默认勾选态与模板内容。
/// 落盘时映射为 template.json files[] 条目并写入模板文件内容。
/// </summary>
public sealed class PackageFile
{
    /// <summary>
    /// 模板文件相对包根路径，禁止绝对路径与 .. 段，防目录穿越。
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// 输出相对路径，支持 {{变量}} 占位，同样禁止目录穿越。
    /// </summary>
    public string RelativeOutputPath { get; set; } = string.Empty;

    /// <summary>
    /// 是否默认勾选参与生成，缺省为 true。
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// 模板文件内容，Scriban 语法文本。
    /// </summary>
    public string Content { get; set; } = string.Empty;
}

namespace DbCodeGen.Core.Templates.Packages;

/// <summary>
/// 包内模板文件的运行时信息，含相对路径、绝对路径、输出相对路径、输出根语义、产出角色与默认勾选态。
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
    /// 模板作用域，manifest files[].scope 解析结果，缺省 table（既有包行为不变）。
    /// </summary>
    public TemplateScope Scope { get; set; } = TemplateScope.Table;

    /// <summary>
    /// 输出根语义，manifest files[].root 解析结果；null 表示未声明（按作用域沿用既有缺省语义：
    /// table/module = 相对输出根，project = 工作区根）。
    /// </summary>
    public TemplateOutputRoot? OutputRoot { get; set; }

    /// <summary>
    /// 产出角色（声明值优先，缺省按输出路径推断），供能力元数据与覆盖告警判定。
    /// </summary>
    public string Role { get; set; } = TemplateFileRoleResolver.Other;

    /// <summary>
    /// 是否默认勾选参与生成（勾选到层默认态）。
    /// </summary>
    public bool IsEnabled { get; set; }

    /// <summary>
    /// 模板文本，懒加载：默认 null，由模板读取方按需从磁盘加载后填充。
    /// </summary>
    public string? Content { get; set; }
}

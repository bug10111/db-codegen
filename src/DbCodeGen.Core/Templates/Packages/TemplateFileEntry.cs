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
    /// 模板作用域声明：table（缺省，按表集逐表渲染）/ module（按模块清单逐个渲染）/ project（整次生成渲染一次）。
    /// manifest 缺省不写即 table，既有包行为不变；非法取值在包加载校验阶段结构化拒绝。
    /// </summary>
    public string? Scope { get; set; }

    /// <summary>
    /// 是否默认勾选参与生成（勾选到层默认态），缺省为 true。
    /// </summary>
    public bool Enabled { get; set; } = true;
}

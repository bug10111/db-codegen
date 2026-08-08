namespace DbCodeGen.Core.Templates.Packages;

/// <summary>
/// 模板包 manifest（template.json）反序列化模型，声明包名、说明、引擎、基础包名、类型映射与模板文件清单。
/// 字段 JSON 序列化使用 camelCase 命名，与 03 模板包管理 §6.2 外部契约一致。
/// </summary>
public sealed class TemplateManifest
{
    /// <summary>
    /// 包名，全局唯一（含内置与用户库），约定与目录名一致，须符合目录名规则。
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// 包说明，列表展示用。
    /// </summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>
    /// 模板引擎，当前固定支持 scriban。
    /// </summary>
    public string Engine { get; set; } = string.Empty;

    /// <summary>
    /// 基础包名（如 com.example），生成时注入渲染上下文 package 侧，可为空。
    /// </summary>
    public string? BasePackage { get; set; }

    /// <summary>
    /// 数据库原始类型到目标语言类型的映射表，供渲染侧 TypeMapper 消费。
    /// </summary>
    public Dictionary<string, string> TypeMap { get; set; } = new();

    /// <summary>
    /// 模板文件条目清单，每个条目声明模板相对路径与输出相对路径。
    /// </summary>
    public List<TemplateFileEntry> Files { get; set; } = new();
}

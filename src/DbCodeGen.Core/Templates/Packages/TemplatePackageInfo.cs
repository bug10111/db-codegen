namespace DbCodeGen.Core.Templates.Packages;

/// <summary>
/// 模板包加载后的运行时信息，是列表展示、单包加载、导入复制等操作的统一返回载体。
/// </summary>
public sealed class TemplatePackageInfo
{
    /// <summary>
    /// 包名，全局唯一，与包目录名一致。
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// 包说明，列表展示用。
    /// </summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>
    /// 模板引擎，当前固定 scriban。
    /// </summary>
    public string Engine { get; set; } = string.Empty;

    /// <summary>
    /// 基础包名，可为空，渲染上下文 package 侧注入。
    /// </summary>
    public string? BasePackage { get; set; }

    /// <summary>
    /// 包根目录绝对路径。
    /// </summary>
    public string RootPath { get; set; } = string.Empty;

    /// <summary>
    /// template.json 清单文件绝对路径。
    /// </summary>
    public string ManifestPath { get; set; } = string.Empty;

    /// <summary>
    /// 是否内置包，内置包只读：不可删、不可覆盖、不可直接修改清单。
    /// </summary>
    public bool IsBuiltin { get; set; }

    /// <summary>
    /// 包目录最新修改时间，列表展示用。
    /// </summary>
    public DateTime ModifiedTime { get; set; }

    /// <summary>
    /// 数据库原始类型到目标语言类型的映射表，取自 manifest，供渲染侧实时类型映射使用。
    /// </summary>
    public Dictionary<string, string> TypeMap { get; set; } = new();

    /// <summary>
    /// 校验通过的模板文件运行时信息集合。
    /// </summary>
    public List<TemplateFileInfo> Files { get; set; } = new();
}

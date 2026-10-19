namespace DbCodeGen.Core.Templates.Packages;

/// <summary>
/// 模板包加载后的运行时信息，是列表展示、单包加载、导入复制等操作的统一返回载体。
/// 能力元数据（角色/技术栈/作用域/View/前端页面/契约路径/部署形态/平台表规则/命名策略）
/// 由 manifest 声明派生，缺省按文件产物推断，供工具面能力可见与生成管线消费。
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

    /// <summary>
    /// 包角色：backend / scaffold / web / fullstack / utility（声明值优先，缺省按产物推断）。
    /// </summary>
    public string Role { get; set; } = "utility";

    /// <summary>
    /// 技术栈：java / net / web / mixed（声明值优先，缺省按产物扩展名推断）。
    /// </summary>
    public string Stack { get; set; } = "mixed";

    /// <summary>
    /// 文件作用域集合（从 files[].scope 去重派生，顺序固定 table / module / project）。
    /// </summary>
    public List<string> Scopes { get; set; } = new();

    /// <summary>
    /// 是否具备后端 View 层产出（声明值优先，缺省按 files[].role 与输出路径推断）。
    /// </summary>
    public bool HasBackendView { get; set; }

    /// <summary>
    /// 是否具备前端页面产出（声明值优先，缺省按 files[].role 与输出路径推断）。
    /// </summary>
    public bool HasWebPage { get; set; }

    /// <summary>
    /// 契约文档相对包根路径（缺省 README.md.scriban）。
    /// </summary>
    public string ContractPath { get; set; } = "README.md.scriban";

    /// <summary>
    /// 支持的部署形态清单（如 standalone / cloud）；未声明时为空列表。
    /// </summary>
    public List<string> DeployModes { get; set; } = new();

    /// <summary>
    /// 权限模块兜底声明；缺省空串表示由基础包名末段派生。
    /// </summary>
    public string PermissionModule { get; set; } = string.Empty;

    /// <summary>
    /// 平台表规则（命中后默认禁止生成 CRUD），未声明时为空规则集合。
    /// </summary>
    public TableFilterRules PlatformTables { get; set; } = new();

    /// <summary>
    /// 排除表规则（命中后一律不生成），未声明时为空规则集合。
    /// </summary>
    public TableFilterRules ExcludeTables { get; set; } = new();

    /// <summary>
    /// 命名策略声明（可为空，请求 variables 同名键优先）。
    /// </summary>
    public NamingPolicyOptions? Naming { get; set; }

    /// <summary>
    /// 命名预设集合（preset 名 → 归一化定义，大小写不敏感键）：供 preview_generation/generate 的
    /// preset 参数解析模板集合与默认变量；未声明时为空字典，调用行为与未引入预设前一致。
    /// </summary>
    public Dictionary<string, TemplatePresetDefinition> Presets { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

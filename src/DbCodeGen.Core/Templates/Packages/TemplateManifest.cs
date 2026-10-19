using System.Text.Json.Serialization;

namespace DbCodeGen.Core.Templates.Packages;

/// <summary>
/// 模板包 manifest（template.json）反序列化模型，声明包名、说明、引擎、基础包名、类型映射、模板文件清单
/// 与生成能力元数据（角色/技术栈/契约路径/部署形态/平台表规则/命名策略）。
/// 字段 JSON 序列化使用 camelCase 命名；能力元数据字段全部可选，缺省时按文件产物推断（既有包零改动可加载）。
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

    /// <summary>
    /// 命名预设集合（preset 名 → 定义）：把常用模板组合与默认变量固化，供 preview_generation/generate
    /// 以 preset 参数选择；与 files 参数互斥，未声明时不影响既有调用行为。
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, TemplatePresetDefinition>? Presets { get; set; }

    /// <summary>
    /// 包角色：backend / scaffold / web / fullstack / utility；缺省按产物扩展名推断
    /// （有 .java 产物→backend；仅 .ts/.tsx→web；否则 utility）。
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Role { get; set; }

    /// <summary>
    /// 技术栈：java / net / web / mixed；缺省按产物扩展名推断。
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Stack { get; set; }

    /// <summary>
    /// 是否具备后端 View 层产出；缺省按 files[].role 或输出路径含 /view/、View.java 推断。
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? HasBackendView { get; set; }

    /// <summary>
    /// 是否具备前端页面产出；缺省按 files[].role 为 webPage/webApi/webDto 或输出路径含 web/、/pages/ 推断。
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? HasWebPage { get; set; }

    /// <summary>
    /// 契约文档相对包根路径（缺省 README.md.scriban），供 read_template 契约发现与 list_templates 标记。
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ContractPath { get; set; }

    /// <summary>
    /// 支持的部署形态（如 standalone / cloud）；缺省空表示未声明。
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? DeployModes { get; set; }

    /// <summary>
    /// 权限模块兜底声明；缺省时由基础包名末段按 kebab 口径派生。
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PermissionModule { get; set; }

    /// <summary>
    /// 平台表规则：命中后默认禁止生成 CRUD（includePlatformTables=true 可显式覆盖）。
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public TableFilterRules? PlatformTables { get; set; }

    /// <summary>
    /// 排除表规则：命中后一律不生成（不受 includePlatformTables 影响）。
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public TableFilterRules? ExcludeTables { get; set; }

    /// <summary>
    /// 命名策略声明（全部可选）；请求 variables 同名键优先于本声明。
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public NamingPolicyOptions? Naming { get; set; }
}

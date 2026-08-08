using DbCodeGen.Core.Model;

namespace DbCodeGen.Core.Config;

/// <summary>
/// 应用配置根模型，配置共享容器的唯一权威定义，直接序列化为 config.json。
/// 任何功能读写配置都必须经 IConfigService，禁止各自解析或改写配置文件。
/// </summary>
public class AppConfig
{
    /// <summary>
    /// 配置结构版本号，当前版本 2；低于当前版本的文件加载时经 ConfigService 迁移补默认字段并升到 2。
    /// </summary>
    public int Version { get; set; } = 1;

    /// <summary>
    /// 工作区根默认路径，作为批量代码生成绝对输出路径的根前缀；默认空串表示未设置。
    /// </summary>
    public string WorkspaceRoot { get; set; } = string.Empty;

    /// <summary>
    /// 最近相对输出根，单值记忆上次语义（非历史列表），批量代码生成读作相对输出根默认值，生成后回写最新值。
    /// </summary>
    public string LastRelativeOutputRoot { get; set; } = string.Empty;

    /// <summary>
    /// LLM 配置，包含 OpenAI 兼容端点、apiKey 密文与模型名。
    /// </summary>
    public LlmConfig Llm { get; set; } = new();

    /// <summary>
    /// 模板搜索目录列表，默认包含 %AppData%\DbCodeGen\Templates，供模板包管理扫描用户级模板包。
    /// </summary>
    public List<string> TemplateSearchDirectories { get; set; } = new();

    /// <summary>
    /// 数据源连接列表，由数据源管理功能承载维护，经共享容器读写同一配置文件。
    /// </summary>
    public List<DataSourceConfig> DataSources { get; set; } = new();

    /// <summary>
    /// 全局类型映射表，数据库原始类型到目标语言类型，由"类型映射"窗口维护，
    /// 生成时经 TypeMappingService 优先于模板包 typeMap 命中，随配置导入导出与备份恢复持久化。
    /// </summary>
    public List<TypeMappingEntry> TypeMappings { get; set; } = new();
}

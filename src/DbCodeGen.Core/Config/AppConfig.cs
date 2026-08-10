using DbCodeGen.Core.Model;

namespace DbCodeGen.Core.Config;

/// <summary>
/// 应用配置根模型，配置共享容器的唯一权威定义，直接序列化为 config.json。
/// 任何功能读写配置都必须经 IConfigService，禁止各自解析或改写配置文件。
/// </summary>
public class AppConfig
{
    /// <summary>
    /// 配置结构版本号，当前版本 3；低于当前版本的文件加载时经 ConfigService 迁移并按库重灌默认映射，同时升到 3。
    /// 初始值 1 用于反序列化无版本字段的旧文件时触发迁移。
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
    /// AI 参考文件限制配置，写模板与改模板上传参考文件时按数量/单文件/总大小上限共用校验。
    /// </summary>
    public AiReferenceFileLimits AiReferenceFileLimits { get; set; } = new();

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

    /// <summary>
    /// 按包名记忆的模板文件勾选态，键为包名，值为该包各模板文件的勾选态清单，
    /// 由②模板区"勾选到层" checkbox 变化时写入，下次加载该包时覆盖 manifest 默认勾选态。
    /// </summary>
    public Dictionary<string, List<TemplateFileState>> TemplateFileStates { get; set; } = new();

    /// <summary>
    /// 包展示顺序记忆，值为按展示顺序排列的包名清单；空集合视为默认排序（内置优先+包名升序），
    /// 由模板包管理窗口包排序操作写入，加载包列表时覆盖默认顺序。
    /// </summary>
    public List<string> TemplatePackageOrder { get; set; } = new();

    /// <summary>
    /// 按包名记忆的包内模板文件展示顺序，键为包名，值为该包模板文件相对包根路径按展示顺序排列的清单，
    /// 相对路径为正斜杠规范化（与 manifest files[].template 对应）；空集合视为默认排序（manifest files[] 声明顺序）。
    /// </summary>
    public Dictionary<string, List<string>> TemplateFileOrder { get; set; } = new();

    /// <summary>
    /// 最近一次选中的模板包名，启动时按此恢复②区包下拉选中项；模板包重命名后同步为最新名。
    /// </summary>
    public string LastSelectedPackage { get; set; } = string.Empty;

    /// <summary>
    /// 最近一次选中的模板文件相对包根路径，模板包加载后按此恢复文件树选中项。
    /// </summary>
    public string LastSelectedTemplateFile { get; set; } = string.Empty;

    /// <summary>
    /// 统一数据目录，集中存放 config.json 与 Templates 等数据；为空表示未设置，回退 %AppData%\DbCodeGen。
    /// 由设置窗口数据目录功能切换，定位文件 config-location.json 引导配置所在地。
    /// </summary>
    public string DataDirectory { get; set; } = string.Empty;

    /// <summary>
    /// 主窗口布局记忆，记录上区三栏列宽与纵向行高，供启动恢复与关闭回写。
    /// </summary>
    public MainLayoutState MainLayout { get; set; } = new();
}

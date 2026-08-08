using DbCodeGen.Core.Model;

namespace DbCodeGen.Core.BackupRestore;

/// <summary>
/// 备份配置快照模型，用于迁移场景还原配置的非密文字段。
/// 密码与 apiKey 密文一律不进入快照，仅记录是否存在以支持恢复后引导重输。
/// </summary>
public sealed class BackupManifestConfig
{
    /// <summary>
    /// 数据源连接的非密字段快照，密码以 PasswordConfigured 布尔标记替代，绝不包含密文。
    /// </summary>
    public sealed class DataSourceSnapshot
    {
        /// <summary>
        /// 连接名称，在数据源列表内唯一，作为下拉与引用标识。
        /// </summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>
        /// 数据库类型（MySql / PostgreSql）。
        /// </summary>
        public DataSourceType Type { get; set; }

        /// <summary>
        /// 主机名或 IP 地址。
        /// </summary>
        public string Host { get; set; } = string.Empty;

        /// <summary>
        /// 端口号，取值 1-65535。
        /// </summary>
        public int Port { get; set; }

        /// <summary>
        /// 数据库名。
        /// </summary>
        public string Database { get; set; } = string.Empty;

        /// <summary>
        /// 用户名。
        /// </summary>
        public string UserId { get; set; } = string.Empty;

        /// <summary>
        /// 备份时该连接是否已配置密码；恢复后密码被清空，为 true 时引导用户重输。
        /// </summary>
        public bool PasswordConfigured { get; set; }

        /// <summary>
        /// 创建时间。
        /// </summary>
        public DateTime CreatedAt { get; set; }

        /// <summary>
        /// 最近更新时间。
        /// </summary>
        public DateTime UpdatedAt { get; set; }
    }

    /// <summary>
    /// 配置结构版本号，来自 AppConfig.Version。
    /// </summary>
    public int Version { get; set; } = 1;

    /// <summary>
    /// 工作区根默认路径。
    /// </summary>
    public string WorkspaceRoot { get; set; } = string.Empty;

    /// <summary>
    /// 最近相对输出根。
    /// </summary>
    public string LastRelativeOutputRoot { get; set; } = string.Empty;

    /// <summary>
    /// LLM 端点地址。
    /// </summary>
    public string LlmBaseUrl { get; set; } = string.Empty;

    /// <summary>
    /// LLM 模型名。
    /// </summary>
    public string LlmModel { get; set; } = string.Empty;

    /// <summary>
    /// 备份时是否已配置 LLM apiKey；恢复后 apiKey 密文被清空，为 true 时引导用户重新配置。
    /// </summary>
    public bool LlmApiKeyConfigured { get; set; }

    /// <summary>
    /// 模板搜索目录列表快照，非密字段按备份还原。
    /// </summary>
    public List<string> TemplateSearchDirectories { get; set; } = new();

    /// <summary>
    /// 数据源连接非密字段快照列表。
    /// </summary>
    public List<DataSourceSnapshot> DataSources { get; set; } = new();

    /// <summary>
    /// 全局类型映射表快照，非密字段按备份还原，用户自定义的数据库类型到目标类型映射随备份携带。
    /// </summary>
    public List<TypeMappingEntry> TypeMappings { get; set; } = new();
}

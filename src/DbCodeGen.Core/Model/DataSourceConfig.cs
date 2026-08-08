namespace DbCodeGen.Core.Model;

/// <summary>
/// 数据源连接配置，持久化到 config.json 的核心实体，密码以 DPAPI 密文形式保存。
/// </summary>
public class DataSourceConfig
{
    /// <summary>
    /// 连接名称，在 DataSources 列表内唯一，作为下拉与引用标识。
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// 数据库类型（MySql / PostgreSql），JSON 序列化为成员名字符串。
    /// </summary>
    public DataSourceType Type { get; set; }

    /// <summary>
    /// 主机名或 IP 地址。
    /// </summary>
    public string Host { get; set; } = string.Empty;

    /// <summary>
    /// 端口号，取值 1-65535，MySql 默认 3306，PostgreSql 默认 5432。
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
    /// 密码经 Windows DPAPI 加密后的 Base64 密文，绝不存储明文。
    /// </summary>
    public string PasswordCipher { get; set; } = string.Empty;

    /// <summary>
    /// 创建时间。
    /// </summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>
    /// 最近更新时间。
    /// </summary>
    public DateTime UpdatedAt { get; set; }
}

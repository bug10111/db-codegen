namespace DbCodeGen.Core.Model;

/// <summary>
/// 测试连接输入，承载明文/密文二选一的密码契约。
/// </summary>
public class TestConnectionInput
{
    /// <summary>
    /// 数据库类型。
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
    /// 表单输入的新密码（明文），非空时优先用于测试，对应新增连接或编辑时输入新密码。
    /// </summary>
    public string? PlainPassword { get; set; }

    /// <summary>
    /// 已保存连接的密码密文，编辑且密码框留空时传入；与 PlainPassword 二选一，二者皆空按空密码尝试。
    /// </summary>
    public string? SavedPasswordCipher { get; set; }
}

using DbCodeGen.Core.Model;

namespace DbCodeGen.Core.DataSource;

/// <summary>
/// 数据源连接保存草稿：连接新增/更新的调用方输入形态，密码以明文瞬态传入由服务加密后持久化，
/// 编辑场景以 OriginalName 定位既有连接，密码留空表示保持原密文。
/// </summary>
public sealed class DataSourceConnectionDraft
{
    /// <summary>
    /// 使用连接字段构造保存草稿。
    /// </summary>
    /// <param name="name">连接名称，在连接列表内唯一（忽略大小写）。</param>
    /// <param name="type">数据库类型。</param>
    /// <param name="host">主机名或 IP 地址。</param>
    /// <param name="port">端口号，取值 1-65535。</param>
    /// <param name="database">数据库名。</param>
    /// <param name="userId">用户名。</param>
    /// <param name="plainPassword">密码明文瞬态值；null 或空白表示编辑场景保持原密文、新增场景落空密码。</param>
    /// <param name="originalName">编辑场景被编辑连接的原始名称，null 或空白表示新增连接。</param>
    public DataSourceConnectionDraft(
        string name,
        DataSourceType type,
        string host,
        int port,
        string database,
        string userId,
        string? plainPassword = null,
        string? originalName = null)
    {
        Name = name;
        Type = type;
        Host = host;
        Port = port;
        Database = database;
        UserId = userId;
        PlainPassword = plainPassword;
        OriginalName = originalName;
    }

    /// <summary>
    /// 连接名称，在连接列表内唯一（忽略大小写比较）。
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// 数据库类型（MySql / PostgreSql）。
    /// </summary>
    public DataSourceType Type { get; }

    /// <summary>
    /// 主机名或 IP 地址。
    /// </summary>
    public string Host { get; }

    /// <summary>
    /// 端口号，取值 1-65535。
    /// </summary>
    public int Port { get; }

    /// <summary>
    /// 数据库名。
    /// </summary>
    public string Database { get; }

    /// <summary>
    /// 用户名。
    /// </summary>
    public string UserId { get; }

    /// <summary>
    /// 密码明文瞬态值：非空白时由服务加密覆盖原密文；null 或空白表示编辑场景保持原密文、新增场景落空密码。
    /// </summary>
    public string? PlainPassword { get; }

    /// <summary>
    /// 编辑场景被编辑连接的原始名称（按该名称定位既有连接）；null 或空白表示新增连接。
    /// </summary>
    public string? OriginalName { get; }
}

using System.Security.Cryptography;
using System.Text;

namespace DbCodeGen.Core.Threading;

/// <summary>
/// 跨进程命名锁名称构造器：以当前用户域标识加业务用途生成同机同用户域内唯一的内核命名对象名，
/// 桌面端与 MCP 服务端两个进程对同一份 config.json 与用户模板库以相同名称互斥。
/// Global 前缀使锁在 Windows 上覆盖同一用户跨会话（终端会话、服务宿主）场景；用户域标识段保证不同用户的进程互不关联。
/// </summary>
internal static class CrossProcessLockNames
{
    /// <summary>
    /// 构造当前用户域下模板包变更操作共用的跨进程锁名。
    /// 与进程内串行门"整个服务一把门"的口径一致，整个用户模板库共用一把跨进程锁。
    /// </summary>
    /// <returns>内核命名互斥对象名。</returns>
    internal static string ForPackageOperations()
    {
        return $"Global\\DbCodeGen.PackageOps.{BuildUserScopeToken()}";
    }

    /// <summary>
    /// 构造保护指定配置文件的跨进程锁名：按配置文件完整路径派生指纹，
    /// 不同数据目录下的 config.json 各自独立互斥，测试注入的临时配置路径互不干扰真实配置。
    /// </summary>
    /// <param name="configFilePath">配置文件绝对路径。</param>
    /// <returns>内核命名互斥对象名。</returns>
    internal static string ForConfigFile(string configFilePath)
    {
        // 大小写归一后取路径指纹，Windows 路径大小写不敏感但字符串异同会影响锁名一致性
        string fullPath = Path.GetFullPath(configFilePath);
        string normalized = fullPath
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .ToUpperInvariant();
        return $"Global\\DbCodeGen.Config.{BuildUserScopeToken()}.{BuildHashFingerprint(normalized)}";
    }

    /// <summary>
    /// 构建当前用户域标识段：域/机器名加用户名组成同机内区分用户的稳定标识，
    /// 同一用户在桌面端与 MCP 服务端进程取值一致，不同用户互不关联。
    /// </summary>
    /// <returns>仅含字母数字与下划线的用户域标识文本。</returns>
    private static string BuildUserScopeToken()
    {
        string scope = $"{Environment.UserDomainName}_{Environment.UserName}";

        var builder = new StringBuilder(scope.Length);
        foreach (char character in scope)
        {
            // 域名与用户名可能含非字母数字字符（如中划线），统一替换为下划线保证内核对象名合法
            builder.Append(char.IsLetterOrDigit(character) ? character : '_');
        }

        return builder.ToString();
    }

    /// <summary>
    /// 计算文本的 SHA256 指纹（前 8 字节十六进制小写），把完整路径压缩为定长短段，
    /// 避免长路径使内核对象名超过命名上限。
    /// </summary>
    /// <param name="value">待指纹的文本。</param>
    /// <returns>16 位十六进制指纹文本。</returns>
    private static string BuildHashFingerprint(string value)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(hash, 0, 8).ToLowerInvariant();
    }
}

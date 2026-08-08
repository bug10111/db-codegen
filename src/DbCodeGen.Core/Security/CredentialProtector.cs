using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace DbCodeGen.Core.Security;

/// <summary>
/// 使用 Windows DPAPI 对敏感凭据进行加密与解密，作用域限定当前用户。
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class CredentialProtector
{
    /// <summary>
    /// 加密明文并返回 Base64 密文。
    /// </summary>
    /// <param name="plainText">需要加密的明文，例如连接密码或 LLM apiKey。</param>
    /// <returns>DPAPI 加密后的 Base64 密文字符串。</returns>
    /// <exception cref="ArgumentNullException">plainText 为 null 时抛出。</exception>
    public string Encrypt(string plainText)
    {
        if (plainText is null)
        {
            throw new ArgumentNullException(nameof(plainText));
        }

        byte[] plainBytes = Encoding.UTF8.GetBytes(plainText);

        // 以当前用户作用域加密，仅当前 Windows 用户可解密，免密钥管理
        byte[] encryptedBytes = ProtectedData.Protect(plainBytes, null, DataProtectionScope.CurrentUser);

        return Convert.ToBase64String(encryptedBytes);
    }

    /// <summary>
    /// 解密 Base64 密文并返回明文。
    /// </summary>
    /// <param name="cipherBase64">DPAPI 加密后的 Base64 密文。</param>
    /// <returns>解密后的明文字符串，调用方负责用完即弃。</returns>
    /// <exception cref="ArgumentException">cipherBase64 为 null、空白或非法时抛出。</exception>
    /// <exception cref="CryptographicException">密文损坏或无法解密时抛出。</exception>
    public string Decrypt(string cipherBase64)
    {
        if (string.IsNullOrWhiteSpace(cipherBase64))
        {
            throw new ArgumentException("密文不能为空。", nameof(cipherBase64));
        }

        byte[] encryptedBytes;
        try
        {
            encryptedBytes = Convert.FromBase64String(cipherBase64);
        }
        catch (FormatException exception)
        {
            // 非法 Base64 视为无法解密的密文，统一转为加密异常便于调用方按同一类型处理
            throw new CryptographicException("密文不是合法的 Base64 字符串。", exception);
        }

        byte[] plainBytes = ProtectedData.Unprotect(encryptedBytes, null, DataProtectionScope.CurrentUser);

        return Encoding.UTF8.GetString(plainBytes);
    }
}

using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using DbCodeGen.Core.Security;

namespace DbCodeGen.Core.Tests.Security;

/// <summary>
/// CredentialProtector 的单元测试，覆盖加解密还原、CurrentUser 作用域与密文形态。
/// </summary>
[SupportedOSPlatform("windows")]
public class CredentialProtectorTests
{
    private readonly CredentialProtector _protector = new CredentialProtector();

    /// <summary>
    /// 加密后再解密应还原出与原文一致的明文。
    /// </summary>
    [Fact]
    public void Encrypt_Decrypt_RoundTripsOriginalPlainText()
    {
        const string plainText = "MyP@ssw0rd!";

        string cipher = _protector.Encrypt(plainText);
        string decrypted = _protector.Decrypt(cipher);

        Assert.Equal(plainText, decrypted);
    }

    /// <summary>
    /// 空密码也应能完成加密与解密还原。
    /// </summary>
    [Fact]
    public void Encrypt_Decrypt_EmptyPlainText_RoundTrips()
    {
        string cipher = _protector.Encrypt(string.Empty);
        string decrypted = _protector.Decrypt(cipher);

        Assert.Equal(string.Empty, decrypted);
    }

    /// <summary>
    /// 相同明文两次加密产生的密文不同，密文不可预测且无固定明文头部。
    /// </summary>
    [Fact]
    public void Encrypt_SamePlainTextTwice_ProducesDifferentCipherTexts()
    {
        const string plainText = "MyP@ssw0rd!";

        string cipher1 = _protector.Encrypt(plainText);
        string cipher2 = _protector.Encrypt(plainText);

        Assert.NotEqual(cipher1, cipher2);
    }

    /// <summary>
    /// 加密结果应为 Base64 密文，可被 Convert.FromBase64String 成功解码。
    /// </summary>
    [Fact]
    public void Encrypt_ReturnsBase64EncodedCipherText()
    {
        string cipher = _protector.Encrypt("Secret");

        byte[] decoded = Convert.FromBase64String(cipher);

        Assert.NotEmpty(decoded);
    }

    /// <summary>
    /// 加密与解密应绑定当前用户 DPAPI 密钥：与 ProtectedData 的 CurrentUser 作用域双向互操作。
    /// </summary>
    [Fact]
    public void Encrypt_Decrypt_UseCurrentUserDpapiScope()
    {
        const string plainText = "ScopeCheckSecret";
        byte[] plainBytes = Encoding.UTF8.GetBytes(plainText);

        // 以 CurrentUser 作用域手工保护的数据，CredentialProtector 应能解密，证明解密使用当前用户密钥
        string manualCipher = Convert.ToBase64String(
            ProtectedData.Protect(plainBytes, null, DataProtectionScope.CurrentUser));
        Assert.Equal(plainText, _protector.Decrypt(manualCipher));

        // CredentialProtector 加密的数据，以 CurrentUser 作用域手工解保护应还原，证明加密使用当前用户密钥
        string protectorCipher = _protector.Encrypt(plainText);
        byte[] manualPlainBytes = ProtectedData.Unprotect(
            Convert.FromBase64String(protectorCipher), null, DataProtectionScope.CurrentUser);
        Assert.Equal(plainText, Encoding.UTF8.GetString(manualPlainBytes));
    }

    /// <summary>
    /// 解密非 Base64 的输入应抛出加密相关异常。
    /// </summary>
    [Fact]
    public void Decrypt_InvalidBase64_ThrowsCryptographicException()
    {
        Assert.Throws<CryptographicException>(() => _protector.Decrypt("not-a-base64!!!"));
    }

    /// <summary>
    /// 解密空密文应抛出参数异常。
    /// </summary>
    [Fact]
    public void Decrypt_EmptyCipher_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() => _protector.Decrypt(string.Empty));
    }

    /// <summary>
    /// 加密 null 明文应抛出参数异常。
    /// </summary>
    [Fact]
    public void Encrypt_NullPlainText_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => _protector.Encrypt(null!));
    }

    /// <summary>
    /// 解密合法 Base64 但非 DPAPI 密文的数据应抛出加密异常，验证损坏密文处理契约。
    /// </summary>
    [Fact]
    public void Decrypt_CorruptedCipher_ThrowsCryptographicException()
    {
        string corrupted = Convert.ToBase64String(Encoding.UTF8.GetBytes("not a dpapi blob"));

        Assert.Throws<CryptographicException>(() => _protector.Decrypt(corrupted));
    }
}

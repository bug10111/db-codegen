using System.Security.Cryptography;
using System.Text;

namespace DbCodeGen.Core.Templates.Packages;

/// <summary>
/// 模板正文内容摘要计算单点：SHA256 前 16 位 hex 小写，供模板局部编辑的条件写乐观锁
/// （调用方以 read_template 返回的 contentHash 为期望值，编辑时校验正文未被并发改写）与
/// 编辑成功后的新摘要回执共用，保证算法与精度全链路唯一。
/// </summary>
public static class TemplateContentHash
{
    /// <summary>
    /// 计算模板正文内容摘要：UTF-8 编码取 SHA256，输出前 16 位 hex 小写。
    /// </summary>
    /// <param name="content">模板正文原文。</param>
    /// <returns>16 位 hex 小写摘要。</returns>
    public static string Compute(string content)
    {
        ArgumentNullException.ThrowIfNull(content);
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(content));
        return Convert.ToHexString(hash, 0, 8).ToLowerInvariant();
    }
}

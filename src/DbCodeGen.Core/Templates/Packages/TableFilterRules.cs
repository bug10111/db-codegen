using System.Text.RegularExpressions;

namespace DbCodeGen.Core.Templates.Packages;

/// <summary>
/// 表过滤规则集合，承载模板包 manifest 的 platformTables / excludeTables 声明。
/// 三种匹配方式可并存，命中任一即视为命中；exact 为表名精确匹配，prefixes 为前缀匹配，
/// regex 为正则匹配，全部大小写不敏感。
/// </summary>
public sealed class TableFilterRules
{
    /// <summary>
    /// 精确匹配的表名集合（大小写不敏感）。
    /// </summary>
    public List<string> Exact { get; set; } = new();

    /// <summary>
    /// 前缀匹配的表名集合（大小写不敏感，如 sys_）。
    /// </summary>
    public List<string> Prefixes { get; set; } = new();

    /// <summary>
    /// 正则匹配的表名模式集合（大小写不敏感，如 ^sys_）。
    /// </summary>
    public List<string> Regex { get; set; } = new();

    /// <summary>
    /// 规则集合是否为空：三项均为空时不做任何过滤。
    /// </summary>
    public bool IsEmpty => Exact.Count == 0 && Prefixes.Count == 0 && Regex.Count == 0;

    /// <summary>
    /// 判断表名是否命中任一规则。
    /// </summary>
    /// <param name="tableName">数据库原始表名。</param>
    /// <returns>命中时返回命中规则描述（如 exact:sys_user / prefix:sys_ / regex:^sys_），未命中返回 null。</returns>
    public string? Match(string tableName)
    {
        if (string.IsNullOrWhiteSpace(tableName) || IsEmpty)
        {
            return null;
        }

        string name = tableName.Trim();

        // 精确匹配优先，命中最具体的声明规则便于回执定位
        foreach (string exact in Exact)
        {
            if (string.Equals(name, exact, StringComparison.OrdinalIgnoreCase))
            {
                return $"exact:{exact}";
            }
        }

        foreach (string prefix in Prefixes)
        {
            if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return $"prefix:{prefix}";
            }
        }

        foreach (string pattern in Regex)
        {
            // 正则带超时，防声明侧写出灾难性回溯模式阻塞生成；此处用全限定名避免与 Regex 属性同名遮蔽
            if (System.Text.RegularExpressions.Regex.IsMatch(
                name, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2)))
            {
                return $"regex:{pattern}";
            }
        }

        return null;
    }
}

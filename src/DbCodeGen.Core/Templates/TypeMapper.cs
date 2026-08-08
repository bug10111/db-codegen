namespace DbCodeGen.Core.Templates;

/// <summary>
/// 数据库原始类型到目标语言类型的映射工具，按当前模板包 manifest typeMap 实时计算映射结果。
/// 映射责任归渲染侧，多套模板包各按各自 typeMap 计算。
/// 全局映射表的解析由 TypeMappingService 承载，本类保留旧字典匹配能力供兼容与测试使用。
/// </summary>
public static class TypeMapper
{
    /// <summary>
    /// 按类型映射表将数据库原始类型映射为目标语言类型；未命中或输入为空时返回默认类型。
    /// 匹配大小写不敏感，并自动去除长度/精度/无符号等修饰（如 varchar(255)、bigint unsigned）。
    /// </summary>
    /// <param name="rawDbType">数据库原始类型，如 varchar、bigint、timestamp。</param>
    /// <param name="typeMap">manifest 声明的类型映射表，键为数据库原始类型。</param>
    /// <param name="fallback">未命中时返回的默认目标类型。</param>
    /// <returns>映射后的目标语言类型。</returns>
    public static string MapType(string? rawDbType, IReadOnlyDictionary<string, string>? typeMap, string fallback = "String")
    {
        if (string.IsNullOrWhiteSpace(rawDbType))
        {
            return fallback;
        }

        string normalized = NormalizeType(rawDbType);
        if (typeMap is not null)
        {
            foreach (KeyValuePair<string, string> pair in typeMap)
            {
                // 仅命中非空映射值，键与值均经规范化后比较
                if (!string.IsNullOrWhiteSpace(pair.Value) && NormalizeType(pair.Key) == normalized)
                {
                    return pair.Value.Trim();
                }
            }
        }

        return fallback;
    }

    /// <summary>
    /// 规范化数据库原始类型键：转小写并去除长度/精度括号后缀，保留多词类型（如 timestamp with time zone）。
    /// 供全局映射表匹配与映射表重复校验使用，与旧 NormalizeType 的空格剥离策略分离，避免破坏多词类型键。
    /// </summary>
    /// <param name="rawType">原始类型文本。</param>
    /// <returns>规范化后的类型键。</returns>
    public static string Normalize(string rawType)
    {
        string type = rawType.Trim().ToLowerInvariant();

        // 去除括号内长度/精度，如 varchar(255) → varchar、numeric(10,2) → numeric
        int parentIndex = type.IndexOf('(');
        if (parentIndex > 0)
        {
            type = type[..parentIndex];
        }

        // 合并连续空白并去首尾空白，多词类型保持原词序
        return string.Join(' ', type.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }

    /// <summary>
    /// 规范化数据库原始类型：转小写并去除长度/精度后缀与多余修饰。
    /// </summary>
    /// <param name="rawType">原始类型文本。</param>
    /// <returns>规范化后的类型键。</returns>
    private static string NormalizeType(string rawType)
    {
        string type = rawType.Trim().ToLowerInvariant();

        // 去除括号内长度/精度，如 varchar(255) → varchar
        int parentIndex = type.IndexOf('(');
        if (parentIndex > 0)
        {
            return type[..parentIndex].Trim();
        }

        // 去除空格修饰，如 bigint unsigned → bigint
        int spaceIndex = type.IndexOf(' ');
        if (spaceIndex > 0)
        {
            return type[..spaceIndex].Trim();
        }

        return type;
    }
}

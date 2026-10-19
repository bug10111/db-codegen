using DbCodeGen.Core.Generation;
using DbCodeGen.Core.Model;

namespace DbCodeGen.Core.Templates.Packages;

/// <summary>
/// 模板包 manifest 的命名策略声明（全部字段可选，缺省保持既有命名行为）。
/// 请求 variables 同名键优先于本声明，本声明优先于全局缺省。
/// </summary>
public sealed class NamingPolicyOptions
{
    /// <summary>
    /// 是否剥离表前缀；缺省 false（不剥离，保持既有类名派生行为）。
    /// </summary>
    public bool? StripTablePrefix { get; set; }

    /// <summary>
    /// 可剥离的表前缀清单（如 t_、sys_），剥离时按最长前缀优先匹配（大小写不敏感）。
    /// </summary>
    public List<string>? TablePrefixes { get; set; }

    /// <summary>
    /// 是否对实体名做基本单复数归一；缺省 false。
    /// </summary>
    public bool? Singularize { get; set; }

    /// <summary>
    /// 实体类名后缀（如 Entity），拼接在 PascalCase 实体名之后；缺省空串。
    /// </summary>
    public string? EntitySuffix { get; set; }

    /// <summary>
    /// Java 包段大小写口径：lower（缺省，全部小写）/ preserve（保留原大小写）。
    /// </summary>
    public string? PackageSegmentCase { get; set; }

    /// <summary>
    /// 分层路径段前缀（如 common），供模板用 tool.packagePath 组装包路径；缺省空串。
    /// </summary>
    public string? LayerPrefix { get; set; }
}

/// <summary>
/// 生效命名策略：由请求 variables 同名键、manifest naming 声明与全局缺省按优先级合并而成，
/// 承载实体名派生与包段归一规则，供生成管线与模板渲染上下文共同消费。
/// </summary>
public sealed class ResolvedNamingPolicy
{
    /// <summary>
    /// 包段大小写口径取值：全部小写。
    /// </summary>
    public const string PackageSegmentCaseLower = "lower";

    /// <summary>
    /// 包段大小写口径取值：保留原大小写。
    /// </summary>
    public const string PackageSegmentCasePreserve = "preserve";

    private static readonly string[] KnownPackageSegmentCases = { PackageSegmentCaseLower, PackageSegmentCasePreserve };

    private ResolvedNamingPolicy(
        bool stripTablePrefix,
        IReadOnlyList<string> tablePrefixes,
        bool singularize,
        string entitySuffix,
        string packageSegmentCase,
        string layerPrefix)
    {
        StripTablePrefix = stripTablePrefix;
        TablePrefixes = tablePrefixes;
        Singularize = singularize;
        EntitySuffix = entitySuffix;
        PackageSegmentCase = packageSegmentCase;
        LayerPrefix = layerPrefix;
    }

    /// <summary>
    /// 是否剥离表前缀。
    /// </summary>
    public bool StripTablePrefix { get; }

    /// <summary>
    /// 可剥离的表前缀清单，按最长前缀优先匹配。
    /// </summary>
    public IReadOnlyList<string> TablePrefixes { get; }

    /// <summary>
    /// 是否做基本单复数归一。
    /// </summary>
    public bool Singularize { get; }

    /// <summary>
    /// 实体类名后缀。
    /// </summary>
    public string EntitySuffix { get; }

    /// <summary>
    /// Java 包段大小写口径（lower / preserve）。
    /// </summary>
    public string PackageSegmentCase { get; }

    /// <summary>
    /// 分层路径段前缀。
    /// </summary>
    public string LayerPrefix { get; }

    /// <summary>
    /// 按「请求 variables &gt; manifest naming &gt; 全局缺省」优先级解析生效命名策略。
    /// </summary>
    /// <param name="manifestOptions">模板包 manifest 的 naming 声明，可空。</param>
    /// <param name="variables">本次请求的调用方变量，可空；同名键优先于 manifest。</param>
    /// <returns>合并后的生效命名策略。</returns>
    /// <exception cref="GenerationException">packageSegmentCase 取值非法时抛出。</exception>
    public static ResolvedNamingPolicy Resolve(
        NamingPolicyOptions? manifestOptions,
        IReadOnlyDictionary<string, object?>? variables)
    {
        bool stripTablePrefix = PolicyValueReader.GetBool(variables, "stripTablePrefix")
            ?? manifestOptions?.StripTablePrefix
            ?? false;

        IReadOnlyList<string>? variablePrefixes = PolicyValueReader.GetStringList(variables, "tablePrefixes");
        IReadOnlyList<string> tablePrefixes = variablePrefixes
            ?? (IReadOnlyList<string>?)NormalizePrefixes(manifestOptions?.TablePrefixes)
            ?? Array.Empty<string>();

        bool singularize = PolicyValueReader.GetBool(variables, "singularize")
            ?? manifestOptions?.Singularize
            ?? false;

        string entitySuffix = PolicyValueReader.GetString(variables, "entitySuffix")
            ?? (string.IsNullOrWhiteSpace(manifestOptions?.EntitySuffix) ? string.Empty : manifestOptions.EntitySuffix.Trim());

        string packageSegmentCase = PolicyValueReader.GetString(variables, "packageSegmentCase")
            ?? (string.IsNullOrWhiteSpace(manifestOptions?.PackageSegmentCase)
                ? PackageSegmentCaseLower
                : manifestOptions.PackageSegmentCase.Trim().ToLowerInvariant());
        if (!KnownPackageSegmentCases.Contains(packageSegmentCase, StringComparer.Ordinal))
        {
            throw new GenerationException(
                $"packageSegmentCase 取值非法：{packageSegmentCase}；合法取值为 lower / preserve。");
        }

        string layerPrefix = PolicyValueReader.GetString(variables, "layerPrefix")
            ?? (string.IsNullOrWhiteSpace(manifestOptions?.LayerPrefix) ? string.Empty : manifestOptions.LayerPrefix.Trim());

        return new ResolvedNamingPolicy(stripTablePrefix, tablePrefixes, singularize, entitySuffix, packageSegmentCase, layerPrefix);
    }

    /// <summary>
    /// 按生效策略派生实体名：先按最长前缀剥离表前缀（stripTablePrefix=true 时），再按基本单复数规则归一
    /// 末词（singularize=true 时），再 PascalCase，最后拼接 entitySuffix。
    /// </summary>
    /// <param name="rawName">数据库真实物理表名，永不变形。</param>
    /// <returns>派生后的实体名（PascalCase + 后缀）。</returns>
    public string DeriveEntityName(string rawName)
    {
        if (string.IsNullOrWhiteSpace(rawName))
        {
            return string.Empty;
        }

        string working = rawName.Trim();
        if (StripTablePrefix)
        {
            working = StripLongestPrefix(working);
        }

        if (Singularize)
        {
            working = SingularizeLastWord(working);
        }

        return TableInfo.ToPascalCase(working) + EntitySuffix;
    }

    /// <summary>
    /// 按最长前缀优先剥离表前缀；无命中时原样返回。
    /// </summary>
    /// <param name="name">原始表名。</param>
    /// <returns>剥离后的表名。</returns>
    private string StripLongestPrefix(string name)
    {
        string? matched = null;
        foreach (string prefix in TablePrefixes)
        {
            if (prefix.Length > 0
                && name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                && (matched is null || prefix.Length > matched.Length))
            {
                matched = prefix;
            }
        }

        // 前缀剥离后为空串时保留原名，避免派生空实体名
        if (matched is null || name.Length == matched.Length)
        {
            return name;
        }

        return name[matched.Length..];
    }

    /// <summary>
    /// 对名称末词做基本单复数归一（仅安全规则：ies→y；ses/xes/zes/ches/shes→去 es；s→去 s；
    /// 不破坏 ss/us/is 等结尾）；无末词或未命中规则时原样返回。
    /// </summary>
    /// <param name="name">待归一名称。</param>
    /// <returns>归一后的名称。</returns>
    private static string SingularizeLastWord(string name)
    {
        int wordStart = name.Length;
        while (wordStart > 0 && char.IsLetterOrDigit(name[wordStart - 1]))
        {
            wordStart--;
        }

        if (wordStart >= name.Length)
        {
            return name;
        }

        string head = name[..wordStart];
        string word = name[wordStart..];
        string lower = word.ToLowerInvariant();

        // 逐条安全规则判定，命中即返回；缩写保护（ss/us/is）先排除
        if (lower.Length > 3 && lower.EndsWith("ies", StringComparison.Ordinal))
        {
            return head + word[..^3] + "y";
        }

        if (lower.Length > 3
            && (lower.EndsWith("ses", StringComparison.Ordinal)
                || lower.EndsWith("xes", StringComparison.Ordinal)
                || lower.EndsWith("zes", StringComparison.Ordinal)
                || lower.EndsWith("ches", StringComparison.Ordinal)
                || lower.EndsWith("shes", StringComparison.Ordinal)))
        {
            return head + word[..^2];
        }

        bool protectedEnding = lower.EndsWith("ss", StringComparison.Ordinal)
            || lower.EndsWith("us", StringComparison.Ordinal)
            || lower.EndsWith("is", StringComparison.Ordinal);
        if (!protectedEnding && lower.Length > 1 && lower.EndsWith("s", StringComparison.Ordinal))
        {
            return head + word[..^1];
        }

        return name;
    }

    /// <summary>
    /// 归一前缀清单：去空白、剔除空项、保持声明顺序。
    /// </summary>
    /// <param name="prefixes">原始前缀清单，可空。</param>
    /// <returns>归一后的前缀清单；无有效项时为 null。</returns>
    private static List<string>? NormalizePrefixes(List<string>? prefixes)
    {
        if (prefixes is null || prefixes.Count == 0)
        {
            return null;
        }

        var normalized = new List<string>(prefixes.Count);
        foreach (string prefix in prefixes)
        {
            if (!string.IsNullOrWhiteSpace(prefix))
            {
                normalized.Add(prefix.Trim());
            }
        }

        return normalized.Count == 0 ? null : normalized;
    }
}

namespace DbCodeGen.Core.Templates.Packages;

/// <summary>
/// 包路径组装器：把基础包段与追加段归一为合法 Java 包段并组装为目录/点号形态。
/// 归一规则：按点号与连字符切段，逐段经 tool.javaIdent 归一（kebab→小写无连字符）并按口径决定大小写，
/// 剔除空段与连续重复段（如基础包已以 common 结尾、分层再拼 common 时只保留一个）。
/// </summary>
public static class PackagePathBuilder
{
    /// <summary>
    /// 归一基础包段：对每个点号分隔的段执行 Java 包段归一，并按 packageSegmentCase 决定大小写。
    /// </summary>
    /// <param name="basePackage">基础包名（点号分隔，可为空）。</param>
    /// <param name="packageSegmentCase">包段大小写口径（lower / preserve）。</param>
    /// <returns>归一后的包段清单，不含空段与连续重复段。</returns>
    public static IReadOnlyList<string> NormalizeBaseSegments(string? basePackage, string packageSegmentCase)
    {
        if (string.IsNullOrWhiteSpace(basePackage))
        {
            return Array.Empty<string>();
        }

        var segments = new List<string>();
        foreach (string raw in basePackage.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            AppendSegment(segments, raw, packageSegmentCase);
        }

        return segments;
    }

    /// <summary>
    /// 在基础包段之后追加若干层段并返回归一后的完整包段清单（连续重复段去重）。
    /// 追加段同样按点号/连字符切分后逐段归一；空值段直接跳过。
    /// </summary>
    /// <param name="baseSegments">基础包段清单。</param>
    /// <param name="packageSegmentCase">包段大小写口径（lower / preserve）。</param>
    /// <param name="extraSegments">追加段清单，元素可为空。</param>
    /// <returns>完整包段清单。</returns>
    public static IReadOnlyList<string> Compose(
        IEnumerable<string> baseSegments,
        string packageSegmentCase,
        params string?[] extraSegments)
    {
        var segments = new List<string>(baseSegments);
        foreach (string? extra in extraSegments)
        {
            if (string.IsNullOrWhiteSpace(extra))
            {
                continue;
            }

            foreach (string raw in extra.Split(new[] { '.', '/' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                AppendSegment(segments, raw, packageSegmentCase);
            }
        }

        return segments;
    }

    /// <summary>
    /// 将包段清单组装为目录形态（正斜杠分隔）。
    /// </summary>
    /// <param name="segments">包段清单。</param>
    /// <returns>目录形态路径；空清单返回空串。</returns>
    public static string ToDirectoryPath(IEnumerable<string> segments)
    {
        return string.Join("/", segments);
    }

    /// <summary>
    /// 将包段清单组装为点号形态（Java package 声明）。
    /// </summary>
    /// <param name="segments">包段清单。</param>
    /// <returns>点号形态包名；空清单返回空串。</returns>
    public static string ToDottedPath(IEnumerable<string> segments)
    {
        return string.Join(".", segments);
    }

    /// <summary>
    /// 追加一个原始段：按 packageSegmentCase 归一后剔除空段，且与上一个已落段相同时跳过（连续重复段去重）。
    /// </summary>
    /// <param name="segments">目标包段清单，就地追加。</param>
    /// <param name="rawSegment">原始段（可能含 kebab 连字符）。</param>
    /// <param name="packageSegmentCase">包段大小写口径。</param>
    private static void AppendSegment(List<string> segments, string rawSegment, string packageSegmentCase)
    {
        string normalized = NormalizeSegment(rawSegment, packageSegmentCase);
        if (normalized.Length == 0)
        {
            return;
        }

        if (segments.Count > 0 && string.Equals(segments[^1], normalized, StringComparison.Ordinal))
        {
            return;
        }

        segments.Add(normalized);
    }

    /// <summary>
    /// 单段归一：去空白，kebab 连字符删除，lower 口径统一小写、preserve 口径保留原大小写；
    /// 其余非法字符保留交由预检告警。
    /// </summary>
    /// <param name="rawSegment">原始段。</param>
    /// <param name="packageSegmentCase">包段大小写口径。</param>
    /// <returns>归一后的段；空串表示无效段。</returns>
    private static string NormalizeSegment(string rawSegment, string packageSegmentCase)
    {
        string trimmed = rawSegment.Trim();
        if (trimmed.Length == 0)
        {
            return string.Empty;
        }

        bool preserveCase = string.Equals(
            packageSegmentCase, ResolvedNamingPolicy.PackageSegmentCasePreserve, StringComparison.Ordinal);
        var builder = new System.Text.StringBuilder(trimmed.Length);
        foreach (char character in trimmed)
        {
            // kebab 连字符是包段分隔符，归一化后删除；其余字符按口径决定大小写
            if (character == '-')
            {
                continue;
            }

            builder.Append(preserveCase ? character : char.ToLowerInvariant(character));
        }

        return builder.ToString();
    }
}

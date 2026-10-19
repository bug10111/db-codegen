using DbCodeGen.Core.Model;

namespace DbCodeGen.McpServer.Tools;

/// <summary>
/// 生成诊断投影器：把 Core 生成结果的命名报告、路径告警、覆盖告警与平台表排除记录
/// 投影为 MCP 工具面 DTO，供 preview_generation 与 generate 共用同一结构化回执口径。
/// </summary>
internal static class GenerationDiagnosticMapper
{
    /// <summary>
    /// 投影逐表命名报告。
    /// </summary>
    /// <param name="items">Core 命名报告条目。</param>
    /// <returns>工具面命名报告 DTO 清单。</returns>
    public static List<NamingReportEntry> ToNamingReportEntries(IReadOnlyList<NamingReportItem> items)
    {
        var entries = new List<NamingReportEntry>(items.Count);
        foreach (NamingReportItem item in items)
        {
            entries.Add(new NamingReportEntry(
                item.RawName, item.EntityName, item.VariableName, item.PackagePath, item.TableNameInCode, item.PermissionKey));
        }

        return entries;
    }

    /// <summary>
    /// 投影预检告警清单。
    /// </summary>
    /// <param name="warnings">Core 预检告警清单。</param>
    /// <returns>工具面告警 DTO 清单。</returns>
    public static List<PathWarningItem> ToPathWarningItems(IReadOnlyList<PathWarningInfo> warnings)
    {
        var items = new List<PathWarningItem>(warnings.Count);
        foreach (PathWarningInfo warning in warnings)
        {
            items.Add(new PathWarningItem(
                warning.Kind, warning.TemplateName, warning.SamplePath, warning.Occurrences, warning.Message));
        }

        return items;
    }

    /// <summary>
    /// 投影生成覆盖告警清单。
    /// </summary>
    /// <param name="warnings">Core 覆盖告警清单。</param>
    /// <returns>工具面覆盖告警 DTO 清单。</returns>
    public static List<CoverageWarningItem> ToCoverageWarningItems(IReadOnlyList<CoverageWarningInfo> warnings)
    {
        var items = new List<CoverageWarningItem>(warnings.Count);
        foreach (CoverageWarningInfo warning in warnings)
        {
            items.Add(new CoverageWarningItem(warning.Kind, warning.Message));
        }

        return items;
    }

    /// <summary>
    /// 投影平台表/排除表命中记录。
    /// </summary>
    /// <param name="exclusions">Core 平台表/排除表命中记录。</param>
    /// <returns>工具面命中记录 DTO 清单。</returns>
    public static List<PlatformTableExcludedItem> ToPlatformTableExcludedItems(IReadOnlyList<PlatformTableExclusion> exclusions)
    {
        var items = new List<PlatformTableExcludedItem>(exclusions.Count);
        foreach (PlatformTableExclusion exclusion in exclusions)
        {
            items.Add(new PlatformTableExcludedItem(exclusion.TableName, exclusion.Rule, exclusion.Excluded));
        }

        return items;
    }
}

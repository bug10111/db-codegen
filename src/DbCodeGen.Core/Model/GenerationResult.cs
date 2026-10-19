namespace DbCodeGen.Core.Model;

/// <summary>
/// 批量生成写盘结果统计，承载生成/覆盖/跳过/失败四计数、取消标记与生成日志。
/// 生成等于新增成功写盘文件数，覆盖等于覆盖成功文件数，跳过等于未写盘文件数（覆盖策略内容相同或跳过策略同名已存在），失败等于写盘异常文件数。
/// </summary>
public sealed class GenerationResult
{
    /// <summary>
    /// 使用完整字段构造写盘结果。
    /// </summary>
    /// <param name="generated">新增成功写盘文件数。</param>
    /// <param name="overwritten">覆盖成功写盘文件数。</param>
    /// <param name="skipped">跳过未写文件数。</param>
    /// <param name="failed">写盘失败文件数。</param>
    /// <param name="isCancelled">是否被用户取消，取消时部分文件可能已写盘。</param>
    /// <param name="logs">生成日志，底栏展示。</param>
    /// <param name="namingReport">逐表命名报告，缺省为空清单。</param>
    /// <param name="pathWarnings">预检告警清单（坏路径/命名反推/权限键/聚合根等），缺省为空清单。</param>
    /// <param name="coverageWarnings">生成覆盖告警（缺 View 层/缺前端页面/仅后端），缺省为空清单。</param>
    /// <param name="platformTableExcluded">平台表/排除表命中记录，缺省为空清单。</param>
    public GenerationResult(
        int generated,
        int overwritten,
        int skipped,
        int failed,
        bool isCancelled,
        IReadOnlyList<GenerationLogEntry>? logs = null,
        IReadOnlyList<NamingReportItem>? namingReport = null,
        IReadOnlyList<PathWarningInfo>? pathWarnings = null,
        IReadOnlyList<CoverageWarningInfo>? coverageWarnings = null,
        IReadOnlyList<PlatformTableExclusion>? platformTableExcluded = null)
    {
        Generated = generated;
        Overwritten = overwritten;
        Skipped = skipped;
        Failed = failed;
        IsCancelled = isCancelled;
        Logs = logs ?? Array.Empty<GenerationLogEntry>();
        NamingReport = namingReport ?? Array.Empty<NamingReportItem>();
        PathWarnings = pathWarnings ?? Array.Empty<PathWarningInfo>();
        CoverageWarnings = coverageWarnings ?? Array.Empty<CoverageWarningInfo>();
        PlatformTableExcluded = platformTableExcluded ?? Array.Empty<PlatformTableExclusion>();
    }

    /// <summary>
    /// 新增成功写盘文件数。
    /// </summary>
    public int Generated { get; }

    /// <summary>
    /// 覆盖成功写盘文件数。
    /// </summary>
    public int Overwritten { get; }

    /// <summary>
    /// 跳过未写文件数，覆盖策略下内容相同或跳过策略下同名已存在。
    /// </summary>
    public int Skipped { get; }

    /// <summary>
    /// 写盘失败文件数。
    /// </summary>
    public int Failed { get; }

    /// <summary>
    /// 是否被用户取消，渲染或写盘过程取消时为 true。
    /// </summary>
    public bool IsCancelled { get; }

    /// <summary>
    /// 生成日志列表，底栏展示。
    /// </summary>
    public IReadOnlyList<GenerationLogEntry> Logs { get; }

    /// <summary>
    /// 逐表命名报告（真实表名/实体名/变量名/包路径/代码内表名/权限键），仅含实际参与生成的表；无表时为空列表。
    /// </summary>
    public IReadOnlyList<NamingReportItem> NamingReport { get; }

    /// <summary>
    /// 生成前预检发现的告警清单（坏路径/命名反推/权限键/聚合根等），非阻断、不改变写盘结果；无告警时为空列表。
    /// </summary>
    public IReadOnlyList<PathWarningInfo> PathWarnings { get; }

    /// <summary>
    /// 生成覆盖告警（NoBackendView / NoWebPage / OnlyBackendGenerated），按所选模板 role 集合判定；无告警时为空列表。
    /// </summary>
    public IReadOnlyList<CoverageWarningInfo> CoverageWarnings { get; }

    /// <summary>
    /// 平台表/排除表命中记录；includePlatformTables=true 时平台表记录 Excluded=false（已显式允许生成）。
    /// </summary>
    public IReadOnlyList<PlatformTableExclusion> PlatformTableExcluded { get; }
}

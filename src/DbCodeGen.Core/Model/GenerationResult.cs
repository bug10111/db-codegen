namespace DbCodeGen.Core.Model;

/// <summary>
/// 批量生成写盘结果统计，承载生成/覆盖/跳过/失败四计数、取消标记与生成日志。
/// 生成等于新增成功写盘文件数，覆盖等于覆盖成功文件数，跳过等于内容相同未写文件数，失败等于写盘异常文件数。
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
    public GenerationResult(
        int generated,
        int overwritten,
        int skipped,
        int failed,
        bool isCancelled,
        IReadOnlyList<GenerationLogEntry>? logs = null)
    {
        Generated = generated;
        Overwritten = overwritten;
        Skipped = skipped;
        Failed = failed;
        IsCancelled = isCancelled;
        Logs = logs ?? Array.Empty<GenerationLogEntry>();
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
    /// 跳过未写文件数，内容与目标文件相同。
    /// </summary>
    public int Skipped { get; }

    /// <summary>
    /// 写盘失败文件数。
    /// </summary>
    public int Failed { get; }

    /// <summary>
    /// 是否被用户取消，覆盖确认取消或渲染/写盘过程取消时为 true。
    /// </summary>
    public bool IsCancelled { get; }

    /// <summary>
    /// 生成日志列表，底栏展示。
    /// </summary>
    public IReadOnlyList<GenerationLogEntry> Logs { get; }
}

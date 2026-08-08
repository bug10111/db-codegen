using DbCodeGen.Core.Model;

namespace DbCodeGen.Core.Generation;

/// <summary>
/// 批量生成文件写盘服务统一接口，负责自动建目录、UTF-8 无 BOM 异步写盘与写盘结果统计。
/// 单文件写盘失败以独立 try/catch 兜底，记录条目级错误后继续其余文件，形成"部分失败"终态。
/// </summary>
public interface IFileWriter
{
    /// <summary>
    /// 将待写条目逐文件写盘：新增与覆盖写盘，跳过仅计数；每文件失败独立记录并继续，支持取消。
    /// </summary>
    /// <param name="entries">待写条目集合，通常为新增与已确认覆盖的条目。</param>
    /// <param name="progress">写盘进度推送，报告 Writing 阶段。</param>
    /// <param name="cancellationToken">取消标记，取消时返回取消结果并携带已完成统计。</param>
    /// <returns>写盘结果统计与生成日志。</returns>
    /// <exception cref="ArgumentNullException">entries 为 null 时抛出。</exception>
    Task<GenerationResult> WriteFilesAsync(
        IReadOnlyList<GenerationFileEntry> entries,
        IProgress<GenerationProgress>? progress,
        CancellationToken cancellationToken);
}

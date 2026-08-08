using System.Text;
using DbCodeGen.Core.Model;
using Microsoft.Extensions.Logging;

namespace DbCodeGen.Core.Generation;

/// <summary>
/// 批量生成文件写盘服务实现，逐文件自动建目录并以 UTF-8 无 BOM 编码异步写盘。
/// 单文件写盘异常独立捕获后继续其余文件，条目级异常写入 GenerationFileEntry.Error 供界面逐条展示。
/// </summary>
public sealed class FileWriter : IFileWriter
{
    private readonly ILogger<FileWriter> _logger;

    /// <summary>
    /// 使用日志器创建文件写盘服务。
    /// </summary>
    /// <param name="logger">写盘服务日志器，日志不得输出模板正文或敏感信息。</param>
    /// <exception cref="ArgumentNullException">logger 为 null 时抛出。</exception>
    public FileWriter(ILogger<FileWriter> logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<GenerationResult> WriteFilesAsync(
        IReadOnlyList<GenerationFileEntry> entries,
        IProgress<GenerationProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entries);
        if (entries.Any(entry => entry is null))
        {
            throw new ArgumentException("待写条目集合不能包含空条目。", nameof(entries));
        }

        int generated = 0;
        int overwritten = 0;
        int skipped = 0;
        int failed = 0;
        var logs = new List<GenerationLogEntry>();
        int total = entries.Count;
        int completed = 0;

        try
        {
            foreach (GenerationFileEntry entry in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                completed++;

                try
                {
                    // 按动作分类写盘：跳过仅计数，新增与覆盖写盘后分别累计
                    switch (entry.Action)
                    {
                        case GenerationAction.Skip:
                            skipped++;
                            logs.Add(GenerationLogEntry.Info($"已跳过（内容相同）：{entry.RelativePath}"));
                            break;
                        case GenerationAction.New:
                            await WriteSingleFileAsync(entry, cancellationToken).ConfigureAwait(false);
                            generated++;
                            logs.Add(GenerationLogEntry.Info($"已生成：{entry.RelativePath}"));
                            break;
                        case GenerationAction.Overwrite:
                            await WriteSingleFileAsync(entry, cancellationToken).ConfigureAwait(false);
                            overwritten++;
                            logs.Add(GenerationLogEntry.Info($"已覆盖：{entry.RelativePath}"));
                            break;
                        default:
                            throw new ArgumentOutOfRangeException(nameof(entry.Action), entry.Action, "未知的生成动作。");
                    }
                }
                catch (OperationCanceledException)
                {
                    // 取消信号原样上抛，交由外层统一返回取消结果，不把取消当作单文件失败
                    throw;
                }
                catch (Exception exception)
                {
                    // 单文件写盘失败：累计失败计数并记录条目级追溯视图后继续其余文件（部分失败）
                    failed++;
                    entry.Error = exception.Message;
                    logs.Add(GenerationLogEntry.Error($"写盘失败：{entry.RelativePath}，原因：{exception.Message}"));
                    _logger.LogWarning("批量生成单文件写盘失败，路径：{Path}。", entry.RelativePath);
                }

                progress?.Report(new GenerationProgress(GenerationStage.Writing, completed, total, entry.RelativePath));
            }
        }
        catch (OperationCanceledException)
        {
            // 写盘过程被取消：返回已完成统计与取消标记，已写盘文件保持不动
            _logger.LogInformation("批量生成写盘已被取消，已完成条目：{Completed}。", completed);
            return new GenerationResult(generated, overwritten, skipped, failed, isCancelled: true, logs);
        }

        _logger.LogInformation(
            "批量生成写盘完成：生成 {Generated} · 覆盖 {Overwritten} · 跳过 {Skipped} · 失败 {Failed}。",
            generated,
            overwritten,
            skipped,
            failed);
        return new GenerationResult(generated, overwritten, skipped, failed, isCancelled: false, logs);
    }

    /// <summary>
    /// 写盘单个文件：自动创建父目录后以 UTF-8 无 BOM 编码异步写入内容。
    /// </summary>
    /// <param name="entry">待写盘条目，绝对路径已校验防目录穿越。</param>
    /// <param name="cancellationToken">取消标记。</param>
    private static async Task WriteSingleFileAsync(GenerationFileEntry entry, CancellationToken cancellationToken)
    {
        // 自动创建父目录，避免目标目录不存在时写盘失败
        string? directory = Path.GetDirectoryName(entry.AbsolutePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        // UTF-8 无 BOM 异步写盘，与模板包保存写盘保持一致的编码约定
        await File.WriteAllTextAsync(
            entry.AbsolutePath,
            entry.Content,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            cancellationToken).ConfigureAwait(false);
    }
}

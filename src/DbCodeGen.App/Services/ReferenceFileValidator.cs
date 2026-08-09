using System.IO;
using System.Text;
using DbCodeGen.Core.Ai;
using DbCodeGen.Core.Config;

namespace DbCodeGen.App.Services;

/// <summary>
/// 参考文件校验器（App.Services，写模板与改模板两 Tab 共用）：对候选参考文件路径清单一次性校验——
/// 数量合计 ≤ MaxFileCount、各文件 ≤ MaxSingleFileBytes、总大小合计 ≤ MaxTotalBytes，
/// 全部通过才整体加入共享上下文，任一失败整体拒绝并逐文件列出原因；
/// 校验通过后按严格 UTF-8 读取文本生成 AiReferenceFileItem 内容快照，二进制/不可解码文件拒绝。
/// 校验与内容读取均不写盘、不进日志，内容快照仅注入本次对话提示词。
/// </summary>
public sealed class ReferenceFileValidator
{
    /// <summary>
    /// 校验并读取候选参考文件：先按 F04 限制配置逐项校验数量合计/单文件/总大小，
    /// 再按严格 UTF-8 读取文本快照；任一文件失败返回逐文件错误清单（整体拒绝），
    /// 全部通过返回内容快照清单供共享上下文整体加入。
    /// </summary>
    /// <param name="candidatePaths">本次待添加的文件绝对路径清单。</param>
    /// <param name="currentCount">共享上下文当前参考文件数量，用于数量合计校验。</param>
    /// <param name="currentTotalBytes">共享上下文当前总字节数，用于总大小合计校验。</param>
    /// <param name="limits">F04 参考文件限制配置，数量/单文件/总大小上限。</param>
    /// <param name="cancellationToken">取消标记，读取大文件时可中断。</param>
    /// <returns>校验结果：IsValid 为 true 时 Items 含全部内容快照，否则 Errors 含逐文件拒绝原因。</returns>
    public async Task<ReferenceFileValidationResult> ValidateAndReadAsync(
        IReadOnlyList<string> candidatePaths,
        int currentCount,
        long currentTotalBytes,
        AiReferenceFileLimits limits,
        CancellationToken cancellationToken = default)
    {
        var errors = new List<string>();
        var items = new List<AiReferenceFileItem>();
        long newTotalBytes = 0;

        // 数量校验：现有数量与本次新增合计不得超过配置的数量上限，超限整体拒绝
        if (currentCount + candidatePaths.Count > limits.MaxFileCount)
        {
            errors.Add($"参考文件数量将超过上限 {limits.MaxFileCount} 个（当前 {currentCount} 个，本次新增 {candidatePaths.Count} 个）。");
        }

        // 逐文件读取大小与文本快照，按 F04 配置校验单文件与总大小上限，二进制或不可解码文件拒绝
        foreach (string path in candidatePaths)
        {
            cancellationToken.ThrowIfCancellationRequested();

            string fileName = Path.GetFileName(path);
            try
            {
                FileInfo fileInfo = new(path);
                if (!fileInfo.Exists)
                {
                    errors.Add($"文件不存在：{fileName}。");
                    continue;
                }

                long size = fileInfo.Length;
                if (size > limits.MaxSingleFileBytes)
                {
                    errors.Add($"{fileName} 大小 {FormatFileSize(size)} 超过单文件上限 {FormatFileSize(limits.MaxSingleFileBytes)}。");
                    continue;
                }

                // 总大小按累计已通过校验的文件字节数核算，与共享上下文现有总大小相加后不得超上限
                if (currentTotalBytes + newTotalBytes + size > limits.MaxTotalBytes)
                {
                    errors.Add($"{fileName} 加入后总大小将超过上限 {FormatFileSize(limits.MaxTotalBytes)}。");
                    continue;
                }

                // 按严格 UTF-8 读取文本快照：非法字节抛异常拒绝，含空字节视为二进制内容拒绝
                string content = await ReadTextWithStrictUtf8Async(path, cancellationToken);
                if (content.IndexOf('\0') >= 0)
                {
                    errors.Add($"{fileName} 含二进制内容，无法作为参考文件。");
                    continue;
                }

                newTotalBytes += size;
                items.Add(new AiReferenceFileItem(fileName, size, content));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or DecoderFallbackException)
            {
                errors.Add($"读取失败：{fileName}（{exception.Message}）。");
            }
        }

        // 任一失败整体拒绝：全部通过才由调用方整体加入共享上下文
        if (errors.Count > 0)
        {
            return new ReferenceFileValidationResult { Errors = errors };
        }

        return new ReferenceFileValidationResult { Items = items };
    }

    /// <summary>
    /// 发送时复核参考文件内容快照：数量合计 ≤ MaxFileCount、总大小合计 ≤ MaxTotalBytes，
    /// 任一超限返回错误清单（对应状态机“配置检查中 → 失败”）；与上传时校验共用同一限制配置。
    /// </summary>
    /// <param name="items">共享上下文内容快照，发送写模板/改模板请求前复核。</param>
    /// <param name="limits">F04 参考文件限制配置。</param>
    /// <returns>复核错误清单，为空表示复核通过。</returns>
    public IReadOnlyList<string> ValidateSnapshot(IReadOnlyList<AiReferenceFileItem> items, AiReferenceFileLimits limits)
    {
        var errors = new List<string>();
        if (items.Count > limits.MaxFileCount)
        {
            errors.Add($"参考文件数量 {items.Count} 超过上限 {limits.MaxFileCount} 个。");
        }

        long totalBytes = items.Sum(item => item.SizeBytes);
        if (totalBytes > limits.MaxTotalBytes)
        {
            errors.Add($"参考文件总大小 {FormatFileSize(totalBytes)} 超过上限 {FormatFileSize(limits.MaxTotalBytes)}。");
        }

        return errors;
    }

    /// <summary>
    /// 按严格 UTF-8 读取文件全部文本：BOM 自动剥离，非法字节抛 DecoderFallbackException。
    /// </summary>
    /// <param name="path">文件绝对路径。</param>
    /// <param name="cancellationToken">取消标记。</param>
    /// <returns>文件文本内容。</returns>
    private static async Task<string> ReadTextWithStrictUtf8Async(string path, CancellationToken cancellationToken)
    {
        using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using StreamReader reader = new(
            stream,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true),
            detectEncodingFromByteOrderMarks: true);
        return await reader.ReadToEndAsync(cancellationToken);
    }

    /// <summary>
    /// 将字节数格式化为可读文件大小文本，供校验错误提示展示。
    /// </summary>
    /// <param name="bytes">字节数。</param>
    /// <returns>带单位的可读大小文本。</returns>
    private static string FormatFileSize(long bytes)
    {
        if (bytes < 1024)
        {
            return $"{bytes} B";
        }

        if (bytes < 1024 * 1024)
        {
            return $"{bytes / 1024.0:0.#} KB";
        }

        return $"{bytes / (1024.0 * 1024.0):0.##} MB";
    }

    /// <summary>
    /// 参考文件校验结果：全部通过时 Items 为内容快照清单，任一失败时 Errors 为逐文件原因清单。
    /// </summary>
    public sealed class ReferenceFileValidationResult
    {
        /// <summary>
        /// 校验通过的参考文件内容快照清单，IsValid 为 true 时有效。
        /// </summary>
        public IReadOnlyList<AiReferenceFileItem> Items { get; init; } = Array.Empty<AiReferenceFileItem>();

        /// <summary>
        /// 整体拒绝时逐文件列出的失败原因清单，IsValid 为 false 时有效。
        /// </summary>
        public IReadOnlyList<string> Errors { get; init; } = Array.Empty<string>();

        /// <summary>
        /// 是否全部通过校验。
        /// </summary>
        public bool IsValid => Errors.Count == 0;
    }
}

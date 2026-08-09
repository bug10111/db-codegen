namespace DbCodeGen.Core.Ai;

/// <summary>
/// 参考文件项（Core.Ai 权威类型）：参考文件校验通过后按 UTF-8 读取生成的不可变内容快照。
/// 写模板（AiTemplateGenerationRequest.ReferenceFiles）、改模板（AiModifyTemplateRequest.ReferenceFiles）
/// 与窗口级共享参考文件上下文统一引用本类型，不另定义同类模型。
/// 内容快照仅内存短周期，用于注入本次对话提示词，不写盘、不进日志。
/// </summary>
public sealed class AiReferenceFileItem
{
    /// <summary>
    /// 文件名（Path.GetFileName(路径)），仅展示与提示词标记用，不保留完整路径。
    /// </summary>
    public string FileName { get; }

    /// <summary>
    /// 文件字节数，供总大小校验与清单展示。
    /// </summary>
    public long SizeBytes { get; }

    /// <summary>
    /// 文本内容快照（UTF-8 读取），仅注入本次对话提示词，不写盘、不进日志。
    /// </summary>
    public string Content { get; }

    /// <summary>
    /// 创建参考文件项。
    /// </summary>
    /// <param name="fileName">文件名，仅保留基础文件名，不包含目录路径。</param>
    /// <param name="sizeBytes">文件字节数。</param>
    /// <param name="content">文本内容快照。</param>
    /// <exception cref="ArgumentNullException">fileName 或 content 为 null 时抛出。</exception>
    public AiReferenceFileItem(string fileName, long sizeBytes, string content)
    {
        ArgumentNullException.ThrowIfNull(fileName);
        ArgumentNullException.ThrowIfNull(content);
        FileName = fileName;
        SizeBytes = sizeBytes;
        Content = content;
    }
}
